using System;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Sdf;

/// <summary>
/// Writes F-rep primitive metadata as Attribute User Text using the parameter names from
/// Inigo Quilez's distance functions (https://iquilezles.org/articles/distfunctions/).
///
/// Schema (shared by every primitive):
///   SDF.Version       schema version
///   SDF.Type          IQ function name, e.g. "sdBox", "sdSphere", "sdCappedCylinder"
///   SDF.Units         model unit system the values are expressed in
///   SDF.Frame.Origin  "x,y,z"  world position of the primitive's local origin
///   SDF.Frame.XAxis   "x,y,z"  local X axis (unit vector, world space)
///   SDF.Frame.YAxis   "x,y,z"  local Y axis (unit vector, world space); Z = X × Y
///   SDF.Color         "#RRGGBB"  viewport shader color, per object. New primitives start white (#FCFCFC);
///                     edited from the Freppuccino panel. Missing = white. Independent of the layer color.
///   SDF.Linear.X/Y/Z  optional columns of the canonical-to-world map, when a gumball transform
///                     is not just a rotate/scale along those axes (shear, or scale off-axis).
///                     Absent means the frame axes. Parameters stay in the frame's coordinates.
///   SDF.&lt;param&gt;       IQ parameters, vectors split per component (SDF.b.x, SDF.b.y, ...)
///
/// IQ functions are defined in local space with the primitive centered at the origin,
/// so evaluate as sdXxx(worldToLocal(p), params...).
/// </summary>
public static class SdfUserText
{
    public const string Prefix = "SDF.";
    public const string SchemaVersion = "1";

    public const string VersionKey = Prefix + "Version";
    public const string TypeKey = Prefix + "Type";
    public const string UnitsKey = Prefix + "Units";
    public const string OriginKey = Prefix + "Frame.Origin";
    public const string XAxisKey = Prefix + "Frame.XAxis";
    public const string YAxisKey = Prefix + "Frame.YAxis";
    public const string LinearXKey = Prefix + "Linear.X";
    public const string LinearYKey = Prefix + "Linear.Y";
    public const string LinearZKey = Prefix + "Linear.Z";

    /// <summary>FFunction (SDF.Type = sdUser): GLSL-subset source, see UserFn/.</summary>
    public const string UserType = "sdUser";
    public const string CodeKey = Prefix + "Code";
    /// <summary>FFunction Lipschitz scale: field = f / L. Missing = 1.</summary>
    public const string LipschitzKey = Prefix + "L";
    /// <summary>FFunction param values: SDF.u.&lt;name&gt;. Missing = the param's declared default.</summary>
    public const string UserParamPrefix = Prefix + "u.";

    /// <summary>Per-object display color "#RRGGBB" the viewport shader uses. Set from the Freppuccino panel.</summary>
    public const string ColorKey = Prefix + "Color";

    /// <summary>White: the color every new primitive starts with, and the color of objects without SDF.Color.</summary>
    public static readonly System.Drawing.Color DefaultColor = System.Drawing.Color.FromArgb(0xFC, 0xFC, 0xFC);

    /// <summary>Writes SDF.Color as "#RRGGBB".</summary>
    public static void WriteColor(ObjectAttributes attributes, System.Drawing.Color color) =>
        attributes.SetUserString(ColorKey, FormatColor(color));

    /// <summary>Writes the default (white) SDF.Color. Call when creating a primitive.</summary>
    public static void WriteDefaultColor(ObjectAttributes attributes) => WriteColor(attributes, DefaultColor);

    /// <summary>"#RRGGBB" (uppercase hex).</summary>
    public static string FormatColor(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Reads SDF.Color ("#RRGGBB", '#' optional).</summary>
    public static bool TryReadColor(ObjectAttributes attributes, out System.Drawing.Color color) =>
        TryParseColor(attributes.GetUserString(ColorKey), out color);

    /// <summary>SDF.Color, or <see cref="DefaultColor"/> (white) if it is missing or malformed.</summary>
    public static System.Drawing.Color ReadColorOrDefault(ObjectAttributes attributes) =>
        TryReadColor(attributes, out var color) ? color : DefaultColor;

    /// <summary>Parses "#RRGGBB" / "RRGGBB" (any case).</summary>
    public static bool TryParseColor(string? text, out System.Drawing.Color color)
    {
        color = System.Drawing.Color.Empty;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] == '#') text = text[1..];
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    /// <summary>Writes the common header and local frame. Call once per primitive, then add parameters.</summary>
    public static void WriteHeader(ObjectAttributes attributes, string sdfType, Plane frame, UnitSystem units)
    {
        attributes.SetUserString(VersionKey, SchemaVersion);
        attributes.SetUserString(TypeKey, sdfType);
        attributes.SetUserString(UnitsKey, units.ToString());
        WriteFrame(attributes, frame);
    }

    /// <summary>Columns of the canonical-to-world map (world space). Omitted when they are the frame axes.</summary>
    public static void WriteLinear(ObjectAttributes attributes, Vector3d x, Vector3d y, Vector3d z)
    {
        // Round-trip precision: a small but healthy map (e.g. a sheared shape scaled by 1e-7) must read back the same.
        static string R(Vector3d v) => string.Join(",", v.X.ToString("R", CultureInfo.InvariantCulture), v.Y.ToString("R", CultureInfo.InvariantCulture), v.Z.ToString("R", CultureInfo.InvariantCulture));
        attributes.SetUserString(LinearXKey, R(x));
        attributes.SetUserString(LinearYKey, R(y));
        attributes.SetUserString(LinearZKey, R(z));
    }

    public static void ClearLinear(ObjectAttributes attributes)
    {
        attributes.DeleteUserString(LinearXKey);
        attributes.DeleteUserString(LinearYKey);
        attributes.DeleteUserString(LinearZKey);
    }

    public static bool TryReadLinear(ObjectAttributes attributes, out Vector3d x, out Vector3d y, out Vector3d z) =>
        ReadLinear(attributes, out x, out y, out z) == LinearState.Valid;

    public enum LinearState { Absent, Valid, Invalid }

    /// <summary>
    /// Absent: no SDF.Linear.* keys (the frame axes). Invalid: keys present but unparsable, non-finite, or not
    /// <see cref="SdfTransforms.IsUsableBasis"/> (the same test the write path applies): callers treat the primitive as
    /// degenerate rather than silently unsheared.
    /// </summary>
    public static LinearState ReadLinear(ObjectAttributes attributes, out Vector3d x, out Vector3d y, out Vector3d z)
    {
        x = y = z = Vector3d.Unset;
        string? sx = attributes.GetUserString(LinearXKey), sy = attributes.GetUserString(LinearYKey), sz = attributes.GetUserString(LinearZKey);
        if (string.IsNullOrEmpty(sx) && string.IsNullOrEmpty(sy) && string.IsNullOrEmpty(sz)) return LinearState.Absent;
        if (!TryParse(sx, out var xx, out var xy, out var xz) ||
            !TryParse(sy, out var yx, out var yy, out var yz) ||
            !TryParse(sz, out var zx, out var zy, out var zz))
            return LinearState.Invalid;
        x = new Vector3d(xx, xy, xz);
        y = new Vector3d(yx, yy, yz);
        z = new Vector3d(zx, zy, zz);
        if (!x.IsValid || !y.IsValid || !z.IsValid || !SdfTransforms.IsUsableBasis(new SdfBasis(x, y, z))) return LinearState.Invalid;
        return LinearState.Valid;
    }

    /// <summary>Writes SDF.Frame.Origin / XAxis / YAxis only (used when a transform moves the primitive).</summary>
    public static void WriteFrame(ObjectAttributes attributes, Plane frame)
    {
        attributes.SetUserString(OriginKey, Format(frame.Origin.X, frame.Origin.Y, frame.Origin.Z));
        attributes.SetUserString(XAxisKey, Format(frame.XAxis.X, frame.XAxis.Y, frame.XAxis.Z));
        attributes.SetUserString(YAxisKey, Format(frame.YAxis.X, frame.YAxis.Y, frame.YAxis.Z));
    }

    /// <summary>Scalar parameter, e.g. WriteFloat(a, "r", radius) -> SDF.r</summary>
    public static void WriteFloat(ObjectAttributes attributes, string name, double value) =>
        attributes.SetUserString(Prefix + name, Format(value));

    /// <summary>Like <see cref="WriteFloat"/> but round-trip exact (no rounding / tiny-value snapping): SDF.L, SDF.u.*.</summary>
    public static void WriteFloatExact(ObjectAttributes attributes, string name, double value) =>
        attributes.SetUserString(Prefix + name, FormatExact(value));

    /// <summary>Shortest round-trip text, invariant (e.g. "0.1", "1E-15").</summary>
    public static string FormatExact(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>vec2 parameter, e.g. WriteVec2(a, "t", R, r) -> SDF.t.x, SDF.t.y</summary>
    public static void WriteVec2(ObjectAttributes attributes, string name, double x, double y)
    {
        WriteFloat(attributes, name + ".x", x);
        WriteFloat(attributes, name + ".y", y);
    }

    /// <summary>vec3 parameter, e.g. WriteVec3(a, "b", hx, hy, hz) -> SDF.b.x, SDF.b.y, SDF.b.z</summary>
    public static void WriteVec3(ObjectAttributes attributes, string name, double x, double y, double z)
    {
        WriteFloat(attributes, name + ".x", x);
        WriteFloat(attributes, name + ".y", y);
        WriteFloat(attributes, name + ".z", z);
    }

    /// <summary>SDF.Type, or null if the object carries no F-rep metadata.</summary>
    public static string? ReadType(ObjectAttributes attributes)
    {
        var type = attributes.GetUserString(TypeKey);
        return string.IsNullOrEmpty(type) ? null : type;
    }

    /// <summary>Reads SDF.Frame.*; Z axis is X × Y.</summary>
    public static bool TryReadFrame(ObjectAttributes attributes, out Plane frame)
    {
        frame = Plane.Unset;
        if (!TryParse(attributes.GetUserString(OriginKey), out var ox, out var oy, out var oz) ||
            !TryParse(attributes.GetUserString(XAxisKey), out var xx, out var xy, out var xz) ||
            !TryParse(attributes.GetUserString(YAxisKey), out var yx, out var yy, out var yz))
            return false;

        frame = new Plane(new Point3d(ox, oy, oz), new Vector3d(xx, xy, xz), new Vector3d(yx, yy, yz));
        return frame.IsValid;
    }

    /// <summary>Reads SDF.&lt;name&gt;.</summary>
    public static bool TryReadFloat(ObjectAttributes attributes, string name, out double value) =>
        TryParse(attributes.GetUserString(Prefix + name), out value);

    /// <summary>Reads SDF.&lt;name&gt;.x / .y.</summary>
    public static bool TryReadVec2(ObjectAttributes attributes, string name, out double x, out double y)
    {
        y = 0.0;
        return TryReadFloat(attributes, name + ".x", out x) & TryReadFloat(attributes, name + ".y", out y);
    }

    /// <summary>Reads SDF.&lt;name&gt;.x / .y / .z.</summary>
    public static bool TryReadVec3(ObjectAttributes attributes, string name, out Vector3d value)
    {
        value = Vector3d.Unset;
        if (!TryReadFloat(attributes, name + ".x", out var x) ||
            !TryReadFloat(attributes, name + ".y", out var y) ||
            !TryReadFloat(attributes, name + ".z", out var z))
            return false;
        value = new Vector3d(x, y, z);
        return true;
    }

    private static bool TryParse(string? text, out double value)
    {
        value = 0.0;
        return !string.IsNullOrWhiteSpace(text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParse(string? text, out double x, out double y, out double z)
    {
        x = y = z = 0.0;
        var parts = text?.Split(',');
        return parts is { Length: 3 } &&
               TryParse(parts[0], out x) && TryParse(parts[1], out y) && TryParse(parts[2], out z);
    }

    private static string Format(double x, double y, double z) =>
        Format(x) + "," + Format(y) + "," + Format(z);

    private static string Format(double value)
    {
        // Avoid "-0" and float noise; invariant culture so values parse identically on any locale.
        if (Math.Abs(value) < 1e-12)
            value = 0.0;
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
