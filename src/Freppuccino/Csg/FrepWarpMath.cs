using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Csg;

/// Warp ref resolution + param derivation, shared by the shader (FrepScene/FrepShaderGen) and the CPU mesher (FrepField).
public static class FrepWarpMath
{
    /// Live warp: visible node whose ref is an undeleted line (Twist/Bend) / box (Repeat). Hidden refs count.
    /// Axis/region are canonical: the object's geometry with its applied root transforms removed (A⁻¹ × geometry,
    /// see <see cref="Placement"/>), so a root transform that also moved the real object isn't applied twice.
    public static bool TryResolve(RhinoDoc doc, FrepNode n, out RhinoObject? obj, out Line axis, out Box region) =>
        TryResolve(doc, n, out obj, out axis, out region, out _);

    /// <paramref name="applied"/>: the object's A (identity if none).
    public static bool TryResolve(RhinoDoc doc, FrepNode n, out RhinoObject? obj, out Line axis, out Box region, out Transform applied)
    {
        axis = default;
        region = default;
        obj = null;
        applied = Transform.Identity;
        if (!n.Op.IsWarp() || n.Hidden || doc.Objects.FindId(n.Ref) is not { IsDeleted: false } o) return false;
        var g = o.Geometry;
        switch (Placement.ReadApplied(o.Attributes, out var a))
        {
            case Placement.Stored.Invalid:
                return false; // unreadable: treated like a missing reference, never as unmoved geometry
            case Placement.Stored.Valid:
                if (!a.TryGetInverse(out var inv)) return false;
                applied = a;
                g = g.Duplicate();
                if (!g.Transform(inv)) return false;
                break;
        }
        if (!(n.Op == FrepOp.Repeat ? FrepRefs.TryBox(g, out region) : FrepRefs.TryLine(g, out axis))) return false;
        obj = o;
        return true;
    }

    /// <paramref name="child"/>: child's world bbox. Extent = max corner distance from the (infinite) axis; Anchor = its center;
    /// Half = its half-extents along the box axes (Repeat).
    public static SdWarp Make(FrepNode n, Line axis, Box region, BoundingBox child)
    {
        double extent = 0;
        var anchor = child.IsValid ? child.Center : region.Center;
        Vector3d half = default;
        if (child.IsValid)
            foreach (var c in child.GetCorners())
            {
                extent = Math.Max(extent, axis.DistanceTo(c, false));
                var v = c - anchor;
                var f = region.Plane;
                half = new Vector3d(Math.Max(half.X, Math.Abs(v * f.XAxis)), Math.Max(half.Y, Math.Abs(v * f.YAxis)),
                    Math.Max(half.Z, Math.Abs(v * f.ZAxis)));
            }
        return new SdWarp(n.Op, axis, region, anchor, extent, new Vector3d(n.Param(0), n.Param(1), n.Param(2))) { Half = half };
    }

    /// Max child half-extent, in cells, that Repeat searches for (overlapping copies); past it distances may overshoot.
    public const double MaxReach = 2;

    /// Uniform rows, layout per opTwist/opBend/opRepeat (FrepShaderGen.WarpRows(op) of them). Also fed to FrepField.AddWarp.
    public static V4[] Rows(SdWarp w)
    {
        var a = w.Axis.From;
        var d = w.Axis.Direction;
        double len = d.Length;
        d.Unitize();
        switch (w.Op)
        {
            case FrepOp.Twist:
                return [new(a.X, a.Y, a.Z, RhinoMath.ToRadians(w.P.X) / len), new(d.X, d.Y, d.Z, w.Extent)];
            case FrepOp.Bend:
            {
                var (n, beta, radius) = BendFrame(w);
                return [new(a.X, a.Y, a.Z, len), new(d.X, d.Y, d.Z, beta), new(n.X, n.Y, n.Z, Math.Max(radius - w.Extent, 0.05 * radius))];
            }
            case FrepOp.Repeat:
            {
                var f = w.Region.Plane;
                var c = w.Anchor;
                f.RemapToPlaneSpace(c, out var cl);
                var s = w.P;
                static (double lo, double hi) Range(double sp, Interval iv, double at) => sp <= 0 ? (0, 0)
                    : (Math.Min(0, Math.Ceiling((iv.Min - at) / sp - 1e-9)), Math.Max(0, Math.Floor((iv.Max - at) / sp + 1e-9)));
                var (x0, x1) = Range(s.X, w.Region.X, cl.X);
                var (y0, y1) = Range(s.Y, w.Region.Y, cl.Y);
                var (z0, z1) = Range(s.Z, w.Region.Z, cl.Z);
                static double Reach(double sp, double h) => sp <= 0 ? 0 : Math.Min(h / sp, MaxReach);
                return [
                    new(c.X, c.Y, c.Z, s.X),
                    new(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, s.Y),
                    new(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, s.Z),
                    new(x0, y0, z0, 0),
                    new(x1, y1, z1, 0),
                    new(Reach(s.X, w.Half.X), Reach(s.Y, w.Half.Y), Reach(s.Z, w.Half.Z), 0),
                ];
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(w));
        }
    }

    /// Bend plane normal n (toward the bend center), |angle| in rad, radius (huge when straight).
    public static (Vector3d N, double Beta, double Radius) BendFrame(SdWarp w)
    {
        var d = w.Axis.Direction;
        double len = d.Length;
        d.Unitize();
        double beta = RhinoMath.ToRadians(w.P.X), phi = RhinoMath.ToRadians(w.P.Y);
        var n0 = BendReference(d);
        var n = n0 * Math.Cos(phi) + Vector3d.CrossProduct(d, n0) * Math.Sin(phi);
        if (beta < 0) (n, beta) = (-n, -beta);
        return (n, beta, beta > 1e-9 ? len / beta : double.MaxValue / 4);
    }

    /// Bend direction 0°: world +Z projected perpendicular to the axis (world +X for a vertical axis).
    public static Vector3d BendReference(Vector3d d)
    {
        var r = Math.Abs(d.Z) > 0.99 ? Vector3d.XAxis : Vector3d.ZAxis;
        r -= d * (r * d);
        r.Unitize();
        return r;
    }
}
