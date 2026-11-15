using System.Text.Json.Nodes;
using Freppuccino.Commands;
using Freppuccino.Scene;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// LLM-friendly primitive descriptions ⇄ <see cref="SdPrimitive"/>. World coordinates, model units, Z up.
/// The same field names are used by create_primitive, update_primitive and get_scene, so a description read
/// from the scene can be edited and sent back. Keep in sync with the tool schemas in src/ui/src/ai/tools.ts.
///
///   sphere     center, radius
///   box        center, size [x,y,z] (full lengths along x_axis / y_axis / their cross product), x_axis?, y_axis?
///   ellipsoid  center, radii [x,y,z] (semi-axes along x_axis / y_axis / cross), x_axis?, y_axis?
///   cylinder   start, end (centers of the two caps), radius
///   cone       start, end, start_radius, end_radius (one may be 0 for a point; both > 0 = frustum)
///   torus      center, axis? (normal of the ring plane, default [0,0,1]), major_radius, minor_radius
///   pyramid    base_center, axis? (base → apex, default [0,0,1]), height, base_size (square side), x_axis? (a base edge)
///   function   center, size, x_axis?, y_axis? (the clip box, like box) + ok / error / lipschitz / params (read-only here;
///              code and params are edited with the function tools, Ai/AiFunctions.cs)
/// </summary>
public static class AiShapes
{
    public static readonly string[] Types = ["sphere", "box", "ellipsoid", "cylinder", "cone", "torus", "pyramid"];

    public static string TypeName(SdKind kind) => kind switch
    {
        SdKind.Sphere => "sphere",
        SdKind.Box => "box",
        SdKind.Ellipsoid => "ellipsoid",
        SdKind.Cylinder => "cylinder",
        SdKind.Cone => "cone",
        SdKind.Torus => "torus",
        SdKind.Pyramid => "pyramid",
        SdKind.User => "function",
        _ => kind.ToString().ToLowerInvariant(),
    };

    static readonly Vector3d WorldX = Vector3d.XAxis, WorldY = Vector3d.YAxis, WorldZ = Vector3d.ZAxis;

    /// <summary>Spec → primitive. Throws <see cref="AiToolException"/> with a message the model can act on.</summary>
    public static SdPrimitive FromSpec(string type, JsonObject s)
    {
        switch (type)
        {
            case "sphere":
            {
                var c = Point(s, "center");
                return new(SdKind.Sphere, new Plane(c, WorldX, WorldY), new Vector3d(Positive(s, "radius"), 0, 0));
            }
            case "box":
            {
                var size = Vec(s, "size");
                RequirePositive("size", size);
                return new(SdKind.Box, Frame(s), size * 0.5);
            }
            case "ellipsoid":
            {
                var r = Vec(s, "radii");
                RequirePositive("radii", r);
                return new(SdKind.Ellipsoid, Frame(s), r);
            }
            case "cylinder":
            {
                var (frame, h) = Segment(s);
                return new(SdKind.Cylinder, frame, new Vector3d(h, Positive(s, "radius"), 0));
            }
            case "cone":
            {
                var (frame, h) = Segment(s);
                double r1 = NonNegative(s, "start_radius"), r2 = NonNegative(s, "end_radius");
                if (r1 + r2 <= 0) throw new AiToolException("cone needs start_radius or end_radius > 0");
                return new(SdKind.Cone, frame, new Vector3d(h, r1, r2));
            }
            case "torus":
            {
                var c = Point(s, "center");
                var axis = Direction(s, "axis", WorldZ);
                double major = Positive(s, "major_radius"), minor = Positive(s, "minor_radius");
                return new(SdKind.Torus, FrepInput.AxisFrame(c, axis, WorldX), new Vector3d(major, minor, 0));
            }
            case "pyramid":
            {
                var c = Point(s, "base_center");
                var axis = Direction(s, "axis", WorldZ);
                var x = Direction(s, "x_axis", WorldX);
                return new(SdKind.Pyramid, FrepInput.AxisFrame(c, axis, x), new Vector3d(Positive(s, "height"), Positive(s, "base_size"), 0));
            }
            case "function": // clip box only; Fn (code / params / L) is the caller's (create_function, or kept by update_primitive)
            {
                var size = Vec(s, "size");
                RequirePositive("size", size);
                return new(SdKind.User, Frame(s), size * 0.5);
            }
            default:
                throw new AiToolException($"unknown type '{type}'; use one of: {string.Join(", ", Types)}");
        }
    }

    /// <summary>Primitive → spec (type + the fields above). Inverse of <see cref="FromSpec"/>.</summary>
    public static JsonObject ToSpec(SdPrimitive p)
    {
        var f = p.Frame;
        var v = p.P;
        var o = new JsonObject { ["type"] = TypeName(p.Kind) };
        switch (p.Kind)
        {
            case SdKind.Sphere:
                o["center"] = Json(f.Origin);
                o["radius"] = Round(v.X);
                break;
            case SdKind.Box:
                o["center"] = Json(f.Origin);
                o["size"] = Json(v * 2);
                AddAxes(o, f);
                break;
            case SdKind.Ellipsoid:
                o["center"] = Json(f.Origin);
                o["radii"] = Json(v);
                AddAxes(o, f);
                break;
            case SdKind.Cylinder:
                o["start"] = Json(f.Origin - f.YAxis * v.X);
                o["end"] = Json(f.Origin + f.YAxis * v.X);
                o["radius"] = Round(v.Y);
                break;
            case SdKind.Cone:
                o["start"] = Json(f.Origin - f.YAxis * v.X);
                o["end"] = Json(f.Origin + f.YAxis * v.X);
                o["start_radius"] = Round(v.Y);
                o["end_radius"] = Round(v.Z);
                break;
            case SdKind.Torus:
                o["center"] = Json(f.Origin);
                o["axis"] = Json(f.YAxis);
                o["major_radius"] = Round(v.X);
                o["minor_radius"] = Round(v.Y);
                break;
            case SdKind.Pyramid:
                o["base_center"] = Json(f.Origin);
                o["axis"] = Json(f.YAxis);
                o["height"] = Round(v.X);
                o["base_size"] = Round(v.Y);
                o["x_axis"] = Json(f.XAxis);
                break;
            case SdKind.User:
                o["center"] = Json(f.Origin);
                o["size"] = Json(v * 2);
                AddAxes(o, f);
                if (p.Fn is { } fn)
                {
                    o["ok"] = fn.Result.Ok;
                    if (!fn.Result.Ok && fn.Result.Diagnostics.Length > 0) o["error"] = fn.Result.Diagnostics[0].ToString();
                    if (fn.L != 1) o["lipschitz"] = Round(fn.L);
                    if (fn.Program is { Params.Length: > 0 } program)
                    {
                        var prms = new JsonObject();
                        for (int i = 0; i < program.Params.Length; i++)
                            prms[program.Params[i].Name] = Round(i < fn.Values.Length ? fn.Values[i] : program.Params[i].Default);
                        o["params"] = prms;
                    }
                }
                if (p.Basis != null) o["stretched"] = true; // scaled / sheared after creation (SDF.Linear): the function is stretched too
                break;
        }
        return o;
    }

    /// <summary>Only report axes when they aren't the world axes, to keep get_scene short.</summary>
    static void AddAxes(JsonObject o, Plane f)
    {
        if (f.XAxis.IsParallelTo(WorldX, 1e-6) == 1 && f.YAxis.IsParallelTo(WorldY, 1e-6) == 1) return;
        o["x_axis"] = Json(f.XAxis);
        o["y_axis"] = Json(f.YAxis);
    }

    static Plane Frame(JsonObject s)
    {
        var c = Point(s, "center");
        var x = Direction(s, "x_axis", WorldX);
        var y = Direction(s, "y_axis", x.IsParallelTo(WorldY, 1e-3) != 0 ? -WorldX : WorldY);
        var frame = new Plane(c, x, y); // y is made perpendicular to x
        if (!frame.IsValid) throw new AiToolException("x_axis and y_axis must not be parallel");
        return frame;
    }

    /// <summary>Axial frame from start/end (origin halfway, local Y = start → end) and the half length.</summary>
    static (Plane Frame, double HalfLength) Segment(JsonObject s)
    {
        var a = Point(s, "start");
        var b = Point(s, "end");
        var axis = b - a;
        if (axis.Length <= 1e-9) throw new AiToolException("start and end must be different points");
        return (FrepInput.AxisFrame((a + b) * 0.5, axis, WorldX), axis.Length * 0.5);
    }

    // ── field readers ──

    static Point3d Point(JsonObject s, string key)
    {
        if (!s.ContainsKey(key)) throw new AiToolException($"missing '{key}' ([x, y, z])");
        return new Point3d(Vec(s, key));
    }

    static Vector3d Direction(JsonObject s, string key, Vector3d fallback)
    {
        if (!s.ContainsKey(key) || s[key] == null) return fallback;
        var v = Vec(s, key);
        if (!v.Unitize()) throw new AiToolException($"'{key}' must not be a zero vector");
        return v;
    }

    public static Vector3d Vec(JsonObject s, string key)
    {
        if (s[key] is not JsonArray a || a.Count != 3)
            throw new AiToolException($"'{key}' must be an array of 3 numbers [x, y, z]");
        if (ToDouble(a[0]) is { } x && ToDouble(a[1]) is { } y && ToDouble(a[2]) is { } z) return new Vector3d(x, y, z);
        throw new AiToolException($"'{key}' must be an array of 3 numbers [x, y, z]");
    }

    public static double Number(JsonObject s, string key)
    {
        if (s[key] is not JsonValue v) throw new AiToolException($"missing '{key}' (number)");
        return ToDouble(v) ?? throw new AiToolException($"'{key}' must be a number");
    }

    /// Any JSON number, whether parsed (JsonElement) or built in code (int / double values); null otherwise.
    static double? ToDouble(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
        double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    /// <summary>Optional "color": "#RRGGBB" or a CSS / .NET color name ("tan", "HotPink"); null when absent.</summary>
    public static System.Drawing.Color? Color(JsonObject s)
    {
        if (s["color"] is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        try
        {
            if (!text.StartsWith('#') && text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out _))
                text = "#" + text;
            var c = System.Drawing.ColorTranslator.FromHtml(text);
            if (c.IsEmpty || (c.A == 0 && !text.StartsWith('#'))) throw new FormatException();
            return System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
        }
        catch (Exception)
        {
            throw new AiToolException($"'color' must be \"#RRGGBB\" or a color name (got \"{text}\")");
        }
    }

    static double Positive(JsonObject s, string key)
    {
        var x = Number(s, key);
        if (!(x > 0)) throw new AiToolException($"'{key}' must be > 0 (got {x})");
        return x;
    }

    static double NonNegative(JsonObject s, string key)
    {
        if (!s.ContainsKey(key)) return 0;
        var x = Number(s, key);
        if (!(x >= 0)) throw new AiToolException($"'{key}' must be ≥ 0 (got {x})");
        return x;
    }

    static void RequirePositive(string key, Vector3d v)
    {
        if (!(v.X > 0 && v.Y > 0 && v.Z > 0)) throw new AiToolException($"all components of '{key}' must be > 0");
    }

    // ── output ──

    public static double Round(double x)
    {
        var r = Math.Round(x, 4);
        return r == 0 ? 0 : r; // no -0
    }

    public static JsonArray Json(Point3d p) => [Round(p.X), Round(p.Y), Round(p.Z)];
    public static JsonArray Json(Vector3d v) => [Round(v.X), Round(v.Y), Round(v.Z)];
}

/// <summary>A tool failure whose message goes back to the model verbatim.</summary>
public sealed class AiToolException(string message) : Exception(message);
