using Rhino;

namespace Freppuccino.Display;

public enum CutterDisplay { Silhouette, Wireframe, Hidden }

public enum GeometryDisplay { Ghosted, Shaded, Wireframe, Arctic, Rendered }

public static class FrepDisplaySettings
{
    const string CuttersKey = "DisplayCutters", GeometryKey = "DisplayGeometry";

    public static event Action? Changed;

    public static CutterDisplay Cutters
    {
        get => Read(CuttersKey, CutterDisplay.Silhouette);
        set
        {
            if (value == Cutters) return;
            Write(CuttersKey, value);
            Changed?.Invoke();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
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

    static T Read<T>(string key, T fallback) where T : struct, Enum =>
        FreppuccinoPlugin.Instance?.Settings.GetString(key, fallback.ToString()) is { } s && Enum.TryParse<T>(s, out var v) && Enum.IsDefined(v) ? v : fallback;

    static void Write<T>(string key, T value) where T : struct, Enum => FreppuccinoPlugin.Instance?.Settings.SetString(key, value.ToString());
}
