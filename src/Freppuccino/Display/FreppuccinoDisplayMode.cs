using Rhino;
using Rhino.Display;

namespace Freppuccino.Display;

/// "Freppuccino" = the Rhino mode chosen in <see cref="FrepDisplaySettings.Geometry"/> (Ghosted at <see cref="Transparency"/> by
/// default), as the underlay for the shader layer. Created once per machine and then only edited in place, so its Id, and
/// every view showing it, survives a change of setting. Persisted via UpdateDisplayMode.
///
/// "Freppuccino Capture" (hidden from menus) is the same with the proxy Breps invisible (fully transparent, no edges) and curves hidden (warp axes), for the
/// AI's screenshots (Ai/AiCapture.cs): only the shader's CSG result, grid and axes, so subtracted tools don't show as ghosts.
public static class FreppuccinoDisplayMode
{
    public const string Name = "Freppuccino";
    public const string CaptureName = "Freppuccino Capture";
    /// Ghosted surface transparency, 0 opaque .. 1 transparent (Rhino's 80%).
    public const double Transparency = 0.8;
    public static Guid Id { get; private set; }
    public static Guid CaptureId { get; private set; }

    public static void Ensure()
    {
        var m = DisplayModeDescription.FindByName(Name);
        if (m == null && DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.GhostedId, Name) is var id && id != Guid.Empty)
            m = DisplayModeDescription.GetDisplayMode(id);
        Id = m?.Id ?? Guid.Empty;
        Apply(FrepDisplaySettings.Geometry);
        CaptureId = EnsureMode(CaptureName, IsCurrentCapture, a =>
        {
            a.FrontMaterialTransparency = 1;
            a.BackMaterialTransparency = 1;
            a.ShowSurfaceEdges = false;
            a.ShowIsoCurves = false;
#if RHINO9
            a.ShowSilhouttes = false;
#endif
            a.ShowTangentEdges = false;
            a.ShowTangentSeams = false;
            a.ShowCurves = false; // warp axis lines
        }, inMenu: false);
    }

    static Guid EnsureMode(string name, Func<DisplayModeDescription, bool> isCurrent, Action<DisplayPipelineAttributes> setup, bool inMenu = true)
    {
        var m = DisplayModeDescription.FindByName(name);
        if (m != null && !isCurrent(m))
        {
            DisplayModeDescription.DeleteDisplayMode(m.Id);
            m = null;
        }
        if (m == null)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.GhostedId, name);
            m = DisplayModeDescription.GetDisplayMode(id);
            if (m != null)
            {
                setup(m.DisplayAttributes);
                m.InMenu = inMenu;
                DisplayModeDescription.UpdateDisplayMode(m);
            }
        }
        return m?.Id ?? Guid.Empty;
    }

    static Guid SourceId(GeometryDisplay g) => g switch
    {
        GeometryDisplay.Shaded => DisplayModeDescription.ShadedId,
        GeometryDisplay.Wireframe => DisplayModeDescription.WireframeId,
        GeometryDisplay.Rendered => DisplayModeDescription.RenderedId,
        GeometryDisplay.Arctic => DisplayModeDescription.FindByName("Arctic")?.Id ?? DisplayModeDescription.AmbientOcclusionId,
        _ => DisplayModeDescription.GhostedId,
    };

    /// Makes the Freppuccino mode look like <paramref name="g"/>, keeping its Id and name: the built-in mode is exported,
    /// re-labelled as ours and imported over it. Views showing it are switched through the capture mode and back, since
    /// Rhino ignores a reassignment of the mode a view already shows.
    public static bool Apply(GeometryDisplay g)
    {
        if (Id == Guid.Empty || DisplayModeDescription.GetDisplayMode(SourceId(g)) is not { } src) return false;
        var views = RhinoDoc.OpenDocuments().SelectMany(d => d.Views.GetViewList(ViewTypeFilter.All))
            .Where(v => v.ActiveViewport.DisplayMode?.Id == Id).ToList();
        var path = Path.Combine(Path.GetTempPath(), $"freppuccino-mode-{Guid.NewGuid():N}.ini");
        Guid imported = Guid.Empty;
        try
        {
            if (DisplayModeDescription.ExportToFile(src, path))
            {
                var text = File.ReadAllText(path);
                foreach (var f in new[] { "D", "B" })
                    text = text.Replace(src.Id.ToString(f), Id.ToString(f), StringComparison.OrdinalIgnoreCase);
                text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^Name=.*$", "Name=" + Name);
                File.WriteAllText(path, text);
                imported = DisplayModeDescription.ImportFromFile(path);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
        if (imported != Id || DisplayModeDescription.GetDisplayMode(Id) is not { } m)
        {
            RhinoApp.WriteLine($"Freppuccino: could not switch the display to {g}.");
            return false;
        }
        if (g == GeometryDisplay.Ghosted)
        {
            m.DisplayAttributes.FrontMaterialTransparency = Transparency;
            m.DisplayAttributes.BackMaterialTransparency = Transparency;
        }
        m.InMenu = true;
        DisplayModeDescription.UpdateDisplayMode(m);
        var capture = DisplayModeDescription.GetDisplayMode(CaptureId);
        foreach (var v in views)
        {
            if (capture != null) v.ActiveViewport.DisplayMode = capture;
            v.ActiveViewport.DisplayMode = m;
            v.Redraw();
        }
        return true;
    }

    static bool IsCurrentCapture(DisplayModeDescription m) =>
        m.DisplayAttributes.ShadingEnabled && m.DisplayAttributes.FrontMaterialTransparency > 0.999 && !m.DisplayAttributes.ShowSurfaceEdges && !m.DisplayAttributes.ShowCurves && !m.InMenu;

    public static bool IsActive(DisplayPipeline p) =>
        p.DisplayPipelineAttributes?.Id is { } id && id != Guid.Empty && (id == Id || id == CaptureId);
}
