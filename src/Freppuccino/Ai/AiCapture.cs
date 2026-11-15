using System.Drawing;
using System.Drawing.Imaging;
using Freppuccino.Display;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// Offscreen screenshots for the AI's visual check: renders a model view from standard angles in the Freppuccino
/// display mode (so the F-rep shader draws the CSG result), framed on a bounding box. The user's camera is pushed
/// and restored, so their view doesn't move.
/// </summary>
public static class AiCapture
{
    /// True while capturing (an AI screenshot, or a <see cref="CleanCapture"/> scope): the shader conduit then draws
    /// just the model, without its "FREP MODE" badge or the control outlines.
    public static bool Active => cleanDepth > 0;
    static int cleanDepth;

    /// <summary>
    /// For code that captures views itself (e.g. the sizzle-reel capture scripts, through reflection): while the returned
    /// scope is alive, captures show just the model, like the AI's screenshots. Nestable; dispose it to end.
    /// </summary>
    public static IDisposable CleanCapture()
    {
        cleanDepth++;
        return new CleanScope();
    }

    sealed class CleanScope : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cleanDepth--;
        }
    }

    /// Camera direction (from target toward the camera) for the named views; null = Rhino's defined projection.
    static readonly Dictionary<string, (DefinedViewportProjection Projection, Vector3d? Direction)> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["perspective"] = (DefinedViewportProjection.Perspective, new Vector3d(1, -1.3, 0.9)),
        ["perspective_back"] = (DefinedViewportProjection.Perspective, new Vector3d(-1, 1.3, 0.9)),
        ["perspective_below"] = (DefinedViewportProjection.Perspective, new Vector3d(1, -1.3, -0.7)),
        ["front"] = (DefinedViewportProjection.Front, null),
        ["back"] = (DefinedViewportProjection.Back, null),
        ["left"] = (DefinedViewportProjection.Left, null),
        ["right"] = (DefinedViewportProjection.Right, null),
        ["top"] = (DefinedViewportProjection.Top, null),
        ["bottom"] = (DefinedViewportProjection.Bottom, null),
    };

    public static IEnumerable<string> ViewNames => Views.Keys;

    public static bool IsView(string name) => Views.ContainsKey(name);

    /// <summary>One PNG data URL per view name, in order, framed on <paramref name="boxes"/> (empty = keep the current zoom).</summary>
    public static List<string> Capture(RhinoDoc doc, IReadOnlyList<string> views, int width, int height, IReadOnlyList<BoundingBox> boxes)
    {
        var frame = BoundingBox.Empty;
        foreach (var b in boxes) frame.Union(b);
        var corners = boxes.SelectMany(b => b.GetCorners()).ToArray();
        var view = ModelView(doc) ?? throw new AiToolException("no model view to capture from");
        var mode = DisplayModeDescription.GetDisplayMode(FreppuccinoDisplayMode.CaptureId)
                   ?? DisplayModeDescription.GetDisplayMode(FreppuccinoDisplayMode.Id)
                   ?? throw new AiToolException("Freppuccino display mode is missing");

        var vp = view.ActiveViewport;
        var images = new List<string>(views.Count);
        vp.PushViewProjection();
        var clean = CleanCapture();
        try
        {
            foreach (var name in views)
            {
                SetCamera(vp, name, frame, corners, (double)width / height);
                using var bitmap = view.CaptureToBitmap(new Size(width, height), mode)
                                   ?? throw new AiToolException($"capture failed for view '{name}'");
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                images.Add("data:image/png;base64," + Convert.ToBase64String(png.ToArray()));
            }
        }
        finally
        {
            clean.Dispose(); // before the redraw, so the user's view gets its badge and outlines back
            vp.PopViewProjection();
            view.Redraw();
        }
        return images;
    }

    static RhinoView? ModelView(RhinoDoc doc)
    {
        if (doc.Views.ActiveView is { } active and not RhinoPageView) return active;
        return doc.Views.GetViewList(ViewTypeFilter.Model).FirstOrDefault();
    }

    /// Fraction of the image the framed box should span (the rest is margin for blends that bulge past the proxy Breps).
    const double Fill = 0.85;

    static void SetCamera(RhinoViewport vp, string name, BoundingBox frame, Point3d[] corners, double captureAspect)
    {
        if (!Views.TryGetValue(name, out var v)) throw new AiToolException($"unknown view '{name}'; use: {string.Join(", ", Views.Keys)}");
        vp.SetProjection(v.Projection, null, false);
        if (!frame.IsValid) return;

        if (frame.Diagonal.Length < 1e-6) frame.Inflate(1e-3);
        if (v.Direction is { } dir)
        {
            var target = frame.Center;
            vp.SetCameraLocations(target, target + dir * frame.Diagonal.Length * 2);
        }
        vp.ZoomBoundingBox(frame);
        FitForCapture(vp, corners, captureAspect);
    }

    /// <summary>
    /// ZoomBoundingBox fits the on-screen viewport's aspect, but a capture at another aspect keeps the vertical extent
    /// (a square shot of a wide view crops its sides), and perspective zoom is loose. So measure the objects' boxes on screen and
    /// dolly until it spans <see cref="Fill"/> of the part the capture keeps.
    /// </summary>
    static void FitForCapture(RhinoViewport vp, Point3d[] corners, double captureAspect)
    {
        for (int i = 0; i < 4; i++)
        {
            var size = vp.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
            var toScreen = vp.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Screen);
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var c in corners)
            {
                var p = c;
                p.Transform(toScreen);
                x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
                y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
            }
            double keptWidth = size.Height * captureAspect, keptHeight = size.Height;
            double s = Math.Max((x1 - x0) / keptWidth, (y1 - y0) / keptHeight) / Fill;
            if (!(s > 0) || double.IsInfinity(s) || Math.Abs(s - 1) < 0.02) return;
            vp.Magnify(1 / s, false);
        }
    }
}
