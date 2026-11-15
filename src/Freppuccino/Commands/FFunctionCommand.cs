using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Freppuccino.Sdf;
using Freppuccino.UserFn;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppuccino.Commands;

/// <summary>
/// FFunction: a user-written SDF (UserFn DSL) clipped by a box. Same interaction as FBox.
///   SDF.Type = sdUser, frame origin = box centre, SDF.b.x/y/z = half-extents (the clip box),
///   SDF.Code = source, SDF.L = Lipschitz scale (missing = 1), SDF.u.&lt;name&gt; = param values (always written; missing = declared default).
/// Field = max(f(q) / L, sdBox(q, b)), q = point in the box's local frame (world units).
/// Transforms: rigid moves the frame; anything else (incl. scale) goes to SDF.Linear.* (b and the code stay as they are).
/// </summary>
[Guid("9169f0f8-712a-4696-bf63-e7a1f7149db6")]
public class FFunctionCommand : Command
{
    public const string SdfType = SdfUserText.UserType;

    /// Compiled by tests/UserFn.Tests/DocSampleTests.
    public const string DefaultCode = """
        // Gyroid lattice, clipped by the FFunction box.
        // p = point in the box's local frame (world units, origin = box centre).
        param float cell = 10.0;      // period of the lattice
        param float thickness = 1.0;  // wall thickness

        float sdf(vec3 p) {
            float w = 6.2831853 / cell;
            vec3 q = p * w;
            float g = sin(q.x) * cos(q.y) + sin(q.y) * cos(q.z) + sin(q.z) * cos(q.x);
            return abs(g) / w - 0.5 * thickness;
        }
        """;

    /// Gyroid gradient peaks near 1.5 (in q space), so f / 1.5 stays 1-Lipschitz.
    public const double DefaultL = 1.5;

    public override string EnglishName => "FFunction";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var rc = RhinoGet.GetBox(out Box box, GetBoxMode.All, Point3d.Unset, null, null, null);
        if (rc != Result.Success) return rc;
        if (!box.IsValid)
        {
            RhinoApp.WriteLine("FFunction: box is not valid.");
            return Result.Failure;
        }
        var frame = box.Plane;
        frame.Origin = box.Center;
        var b = new Vector3d(Math.Abs(box.X.Length), Math.Abs(box.Y.Length), Math.Abs(box.Z.Length)) * 0.5;
        if (!(b.X > 0 && b.Y > 0 && b.Z > 0))
        {
            RhinoApp.WriteLine("FFunction: box is flat.");
            return Result.Failure;
        }
        // Params scaled to the box, so the sample reads at any size / unit system: ~4 cells along the longest side.
        double cell = Math.Max(b.X, Math.Max(b.Y, b.Z)) * 0.5;
        var raw = new Dictionary<string, string> { ["cell"] = Format(cell), ["thickness"] = Format(cell * 0.1) };
        return FrepInput.AddPrimitive(doc, "FFunction", "function box", box.ToBrep(),
            a => WriteSdf(a, frame, b, Instance(DefaultCode, DefaultL, raw), doc.ModelUnitSystem, raw));
    }

    /// <summary>
    /// Header + b + code + L + every param value (always stored, defaults included).
    /// <paramref name="raw"/>: SDF.u.* written verbatim when the code doesn't compile (names unknown); ignored otherwise.
    /// </summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, Vector3d b, UserFnInstance fn, UnitSystem units,
        IReadOnlyDictionary<string, string>? raw = null)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteVec3(attributes, "b", b.X, b.Y, b.Z);
        attributes.SetUserString(SdfUserText.CodeKey, fn.Code);
        SdfUserText.WriteFloatExact(attributes, "L", fn.L);
        if (fn.Program is { } program)
        {
            for (int i = 0; i < program.Params.Length; i++)
            {
                var prm = program.Params[i];
                SdfUserText.WriteFloatExact(attributes, "u." + prm.Name, i < fn.Values.Length ? fn.Values[i] : prm.Default);
            }
        }
        else if (raw != null)
            foreach (var (name, value) in raw) attributes.SetUserString(SdfUserText.UserParamPrefix + name, value);
    }

    /// <summary>Frame, b and the compiled instance. True even if the code doesn't compile (Program null); false if b / L are bad.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out Vector3d b, out UserFnInstance? fn)
    {
        fn = null;
        b = Vector3d.Unset;
        if (!SdfUserText.TryReadFrame(attributes, out frame) || !SdfUserText.TryReadVec3(attributes, "b", out b)) return false;
        if (!(b.X > 0 && b.Y > 0 && b.Z > 0)) return false;
        if (!TryReadL(attributes, out var L)) return false;
        fn = Instance(attributes.GetUserString(SdfUserText.CodeKey) ?? "", L, RawParams(attributes));
        return true;
    }

    /// SDF.L: missing = 1; false if present but not a finite number > 0 (the object then isn't drawn).
    public static bool TryReadL(ObjectAttributes attributes, out double L)
    {
        L = 1;
        if (attributes.GetUserString(SdfUserText.LipschitzKey) is { Length: > 0 } && !SdfUserText.TryReadFloat(attributes, "L", out L)) return false;
        return L > 0 && double.IsFinite(L);
    }

    /// Compiles (cached) and resolves param values: raw[name] if it parses, else the declared default.
    public static UserFnInstance Instance(string code, double L, IReadOnlyDictionary<string, string> raw)
    {
        var result = UserFnCompiler.Compile(code);
        var values = ImmutableArray<double>.Empty;
        if (result.Program is { } program)
            values = [.. program.Params.Select(p =>
                raw.TryGetValue(p.Name, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                    ? v
                    : p.Default)];
        return new UserFnInstance(code, result, values, L);
    }

    /// SDF.u.* as name → text.
    public static Dictionary<string, string> RawParams(ObjectAttributes attributes)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = attributes.GetUserStrings();
        foreach (string? key in all.AllKeys)
            if (key != null && key.StartsWith(SdfUserText.UserParamPrefix, StringComparison.Ordinal) && all[key] is { } value)
                raw[key[SdfUserText.UserParamPrefix.Length..]] = value;
        return raw;
    }

    /// Rigid only: a function can't absorb scale into b (scaling stretches it), so scale goes to SDF.Linear.*.
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!SdfUserText.TryReadFrame(attributes, out var frame))
        {
            reason = "missing or unreadable SDF.Frame.*";
            return false;
        }
        return SdfTransforms.CommitAffine(attributes, frame, xform, IsUnitScale, out reason);
    }

    public static bool IsUnitScale(Vector3d scale) =>
        SdfTransforms.NearlyEqual(scale.X, 1) && SdfTransforms.NearlyEqual(scale.Y, 1) && SdfTransforms.NearlyEqual(scale.Z, 1);

    static string Format(double v) => SdfUserText.FormatExact(v);
}
