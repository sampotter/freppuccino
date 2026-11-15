using Freppuccino.Commands;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// <summary>
/// Non-interactive primitive I/O: <see cref="SdPrimitive"/> values ⇄ SDF.* user text + the Rhino proxy Brep.
/// The SDF values are the source of truth; the Brep is regenerated from them so selection, zoom and bounding
/// boxes match. Used by the AI tools (Ai/AiTools.cs); the interactive F* commands write the same schema.
/// </summary>
public static class FrepPrimitives
{
    public static string SdfType(SdKind kind) => kind switch
    {
        SdKind.Sphere => FSphereCommand.SdfType,
        SdKind.Box => FBoxCommand.SdfType,
        SdKind.Torus => FTorusCommand.SdfType,
        SdKind.Cylinder => FCylinderCommand.SdfType,
        SdKind.Cone => FConeCommand.SdfType,
        SdKind.Ellipsoid => FEllipsoidCommand.SdfType,
        SdKind.Pyramid => FPyramidCommand.SdfType,
        SdKind.User => FFunctionCommand.SdfType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Reads a drawable primitive from SDF.* user text (same validation as the viewport).</summary>
    public static bool TryRead(ObjectAttributes attributes, out SdPrimitive prim) => FrepScene.TryRead(attributes, out prim);

    /// <summary>
    /// Replaces every SDF.* shape key with the values of <paramref name="p"/> (P packing as documented on <see cref="SdPrimitive"/>).
    /// SDF.Color is not shape data and is kept. An FFunction writes every declared param; if its code doesn't compile, SDF.u.*
    /// come from <paramref name="rawParams"/> (default: the ones already on <paramref name="attributes"/>), since names are unknown.
    /// </summary>
    public static void WriteSdf(ObjectAttributes attributes, SdPrimitive p, UnitSystem units, IReadOnlyDictionary<string, string>? rawParams = null)
    {
        if (p.Kind == SdKind.User) rawParams ??= FFunctionCommand.RawParams(attributes);
        var old = attributes.GetUserStrings();
        foreach (string? key in old.AllKeys)
            if (key != null && key.StartsWith(SdfUserText.Prefix, StringComparison.Ordinal) && key != SdfUserText.ColorKey)
                attributes.DeleteUserString(key);

        var f = p.Frame;
        var v = p.P;
        switch (p.Kind)
        {
            case SdKind.Sphere: FSphereCommand.WriteSdf(attributes, f, v.X, units); break;
            case SdKind.Box: FBoxCommand.WriteSdf(attributes, BoxOf(f, v), units); break;
            case SdKind.Torus: FTorusCommand.WriteSdf(attributes, f, v.X, v.Y, units); break;
            case SdKind.Cylinder: FCylinderCommand.WriteSdf(attributes, f, v.X, v.Y, units); break;
            case SdKind.Cone: FConeCommand.WriteSdf(attributes, f, v.X, v.Y, v.Z, units); break;
            case SdKind.Ellipsoid: FEllipsoidCommand.WriteSdf(attributes, f, v, units); break;
            case SdKind.Pyramid: FPyramidCommand.WriteSdf(attributes, f, v.X, v.Y, units); break;
            case SdKind.User:
                FFunctionCommand.WriteSdf(attributes, f, v, p.Fn ?? throw new ArgumentException("FFunction without Fn", nameof(p)), units, rawParams);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(p));
        }
        // The kind writers only know frame + P; keep a shear / off-axis (or, for functions, any) scale.
        if (p.Basis is { } basis && !basis.Matches(f)) SdfUserText.WriteLinear(attributes, basis.X, basis.Y, basis.Z);
    }

    static Box BoxOf(Plane frame, Vector3d b) =>
        new(frame, new Interval(-b.X, b.X), new Interval(-b.Y, b.Y), new Interval(-b.Z, b.Z));

    /// <summary>The Rhino proxy geometry for a primitive, or null if it can't be built. <see cref="SdPrimitive.Basis"/> applied.</summary>
    public static Brep? CreateBrep(SdPrimitive p, double tolerance)
    {
        var brep = CreateCanonicalBrep(p, tolerance);
        if (brep != null && p.Basis is { } basis && !basis.Matches(p.Frame)) brep.Transform(FrameToBasis(p.Frame, basis));
        return brep;
    }

    /// world = o + B · Rᵀ (x − o): frame coordinates re-mapped through the canonical-to-world columns.
    static Transform FrameToBasis(Plane f, Sdf.SdfBasis b)
    {
        Vector3d[] B = [b.X, b.Y, b.Z], R = [f.XAxis, f.YAxis, f.ZAxis];
        var a = Transform.Identity;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                a[i, j] = B[0][i] * R[0][j] + B[1][i] * R[1][j] + B[2][i] * R[2][j];
        var o = f.Origin - Point3d.Origin;
        return Transform.Translation(o) * a * Transform.Translation(-o);
    }

    static Brep? CreateCanonicalBrep(SdPrimitive p, double tolerance)
    {
        var f = p.Frame;
        var v = p.P;
        // Plane at the frame origin whose normal is the frame's Y axis (the axis of IQ's Y-up primitives).
        var axial = new Plane(f.Origin, f.ZAxis, f.XAxis);
        switch (p.Kind)
        {
            case SdKind.Sphere:
                return new Sphere(f, v.X).ToBrep();
            case SdKind.Box:
                return BoxOf(f, v).ToBrep();
            case SdKind.Torus:
                return new Torus(axial, v.X, v.Y).ToBrep();
            case SdKind.Cylinder:
            {
                var basePlane = axial;
                basePlane.Origin = f.Origin - f.YAxis * v.X;
                return new Cylinder(new Circle(basePlane, v.Y), 2 * v.X).ToBrep(true, true);
            }
            case SdKind.Cone:
                return RevolvedFrustum(f, v.X, v.Y, v.Z);
            case SdKind.Ellipsoid:
                return FEllipsoidCommand.CreateBrep(f, v);
            case SdKind.Pyramid:
                return FPyramidCommand.CreateBrep(f, v.Y, v.X, tolerance);
            case SdKind.User:
                return BoxOf(f, v).ToBrep(); // the clip box
            default:
                return null;
        }
    }

    /// <summary>Closed solid of revolution about the frame's Y axis: radius r1 at y = -h, r2 at y = +h (either may be 0).</summary>
    static Brep? RevolvedFrustum(Plane f, double h, double r1, double r2)
    {
        var pts = new List<Point3d> { f.PointAt(0, -h, 0) };
        if (r1 > RhinoMath.ZeroTolerance) pts.Add(f.PointAt(r1, -h, 0));
        if (r2 > RhinoMath.ZeroTolerance) pts.Add(f.PointAt(r2, h, 0));
        pts.Add(f.PointAt(0, h, 0));
        var axis = new Line(f.PointAt(0, -h, 0), f.PointAt(0, h, 0));
        var rev = RevSurface.Create(new Polyline(pts), axis);
        return rev == null ? null : Brep.CreateFromRevSurface(rev, false, false);
    }

    /// <summary>Adds a primitive (proxy Brep + SDF.* user text). Caller handles the undo record and redraw.</summary>
    public static Guid Add(RhinoDoc doc, SdPrimitive p, string? name, System.Drawing.Color? color = null,
        IReadOnlyDictionary<string, string>? rawParams = null)
    {
        var brep = CreateBrep(p, doc.ModelAbsoluteTolerance) ?? throw new InvalidOperationException($"couldn't build {p.Kind} geometry");
        var attributes = doc.CreateDefaultAttributes();
        if (!string.IsNullOrEmpty(name)) attributes.Name = name;
        WriteSdf(attributes, p, doc.ModelUnitSystem, rawParams);
        // Like the F* commands: every new primitive carries an SDF.Color (default white).
        SetColor(attributes, color ?? SdfUserText.DefaultColor);
        var id = doc.Objects.AddBrep(brep, attributes);
        if (id == Guid.Empty) throw new InvalidOperationException($"couldn't add {p.Kind} to the document");
        return id;
    }

    /// <summary>Rewrites an existing primitive in place (same object id, so CSG references stay valid).</summary>
    public static void Replace(RhinoDoc doc, RhinoObject obj, SdPrimitive p, string? name = null, System.Drawing.Color? color = null)
    {
        var brep = CreateBrep(p, doc.ModelAbsoluteTolerance) ?? throw new InvalidOperationException($"couldn't build {p.Kind} geometry");
        var attributes = obj.Attributes.Duplicate();
        if (name != null) attributes.Name = name;
        WriteSdf(attributes, p, doc.ModelUnitSystem);
        if (color is { } c) SetColor(attributes, c);
        var id = obj.Id;
        // Attributes before geometry: ModifyAttributes after Replace, with attributes duplicated from the pre-Replace
        // object, silently drops some user-string edits (e.g. newly added SDF.Linear.*).
        if (!doc.Objects.ModifyAttributes(id, attributes, true)) throw new InvalidOperationException("couldn't update the object's SDF values");
        if (!doc.Objects.Replace(id, brep)) throw new InvalidOperationException("couldn't replace the object's geometry");
    }

    /// <summary>
    /// Shader color: SDF.Color, the same key the panel's color picker writes (the viewport shader draws each primitive in
    /// it; blends mix them). Not the Rhino object / layer color, which only tints the proxy wireframe.
    /// </summary>
    public static void SetColor(ObjectAttributes attributes, System.Drawing.Color color) => SdfUserText.WriteColor(attributes, color);

    /// <summary>Sets SDF.Color on an existing primitive. Caller handles the undo record and redraw.</summary>
    public static void SetColor(RhinoDoc doc, RhinoObject obj, System.Drawing.Color color)
    {
        var attributes = obj.Attributes.Duplicate();
        SetColor(attributes, color);
        if (!doc.Objects.ModifyAttributes(obj.Id, attributes, true)) throw new InvalidOperationException("couldn't update the object's color");
    }
}
