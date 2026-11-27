using Rhino;

namespace Freppuccino.Display;

public enum ShapeDisplay { Silhouette, Wireframe, Hidden }

public enum GeometryDisplay { Ghosted, Shaded, Wireframe, Arctic, Rendered }

public enum HiddenLineDisplay { Faint, Off }

public static class FrepDisplaySettings
{
    const string CuttersKey = "DisplayCutters", OtherShapesKey = "DisplayOtherShapes", GeometryKey = "DisplayGeometry", HiddenLinesKey = "DisplayHiddenLines";

    public const string CuttersLabel = "Cutters", OtherShapesLabel = "Other shapes", GeometryLabel = "Rhino geometry";

    public static string OptionName(string label) => string.Concat(label.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public static event Action? Changed;

    public static ShapeDisplay Cutters
    {
        get => Read(CuttersKey, ShapeDisplay.Silhouette);
        set => SetShapes(CuttersKey, Cutters, value);
    }

    public static ShapeDisplay OtherShapes
    {
        get => Read(OtherShapesKey, ShapeDisplay.Silhouette);
        set => SetShapes(OtherShapesKey, OtherShapes, value);
    }

    public static ShapeDisplay For(bool cutter) => cutter ? Cutters : OtherShapes;

    public static bool AnyHidden => Cutters == ShapeDisplay.Hidden || OtherShapes == ShapeDisplay.Hidden;

    static void SetShapes(string key, ShapeDisplay old, ShapeDisplay value)
    {
        if (value == old) return;
        Write(key, value);
        Changed?.Invoke();
        RhinoDoc.ActiveDoc?.Views.Redraw();
    }

    public static GeometryDisplay Geometry
    {
        get => Read(GeometryKey, GeometryDisplay.Ghosted);
        set
        {
            if (value == Geometry) return;
            if (FreppuccinoDisplayMode.Apply(value)) Write(GeometryKey, value);
            Changed?.Invoke();
        }
    }

    public static HiddenLineDisplay HiddenLines
    {
        get => Read(HiddenLinesKey, HiddenLineDisplay.Faint);
        set
        {
            if (value == HiddenLines) return;
            Write(HiddenLinesKey, value);
            Changed?.Invoke();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
    }

    static T Read<T>(string key, T fallback) where T : struct, Enum =>
        FreppuccinoPlugin.Instance?.Settings.GetString(key, fallback.ToString()) is { } s && Enum.TryParse<T>(s, out var v) && Enum.IsDefined(v) ? v : fallback;

    static void Write<T>(string key, T value) where T : struct, Enum => FreppuccinoPlugin.Instance?.Settings.SetString(key, value.ToString());
}
