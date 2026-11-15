using Freppuccino.Meshing;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Csg;

/// Uniform rows for Shell / Gyroid / Pipe nodes and CSG blend codes, shared by the shader (FrepShaderGen) and the CPU mesher
/// (FrepField), like <see cref="FrepWarpMath"/> for warps. Layouts are documented on the GLSL (opShell / opGyroid / sdPipe).
public static class FrepNodeMath
{
    // ───────────── CSG blend style ─────────────

    /// One float per CSG node, next to its k: style + 4 · steps (decoded by bmin/bmax in the shader and FrepField.Combine).
    public static double BlendCode(FrepNode n) => (int)n.Style() + 4 * n.Steps();

    public static (BlendStyle Style, int Steps) DecodeBlend(double code)
    {
        int c = (int)Math.Round(code);
        return ((c & 3) switch { 1 => BlendStyle.Chamfer, 2 => BlendStyle.Stairs, _ => BlendStyle.Round }, Math.Max(1, c >> 2));
    }

    /// How far a union of <paramref name="operands"/> can sink below the plain minimum (bounds padding, in field units).
    ///   round:   k/4 for two operands, compounding towards k for more (see FrepField.Bounds);
    ///   chamfer: (a + b − k)/2 ≥ min − k/2 per join; stairs: same lower bound.
    public static double UnionGrowth(BlendStyle style, double k, int operands)
    {
        if (k <= 0 || operands < 2) return 0;
        return style == BlendStyle.Round ? k * (operands > 2 ? 1.0 : 0.25) : 0.5 * k * (operands - 1);
    }

    // ───────────── Shell ─────────────

    /// (thickness ≥ 0, unused ×3).
    public static V4[] ShellRows(FrepNode n) => [new(Math.Max(0, n.Param(0)), 0, 0, 0)];

    // ───────────── Gyroid ─────────────

    /// g(q) = sin x cos y + sin y cos z + sin z cos x. Max |∇g| = √3 (its Lipschitz constant in lattice space).
    public const double GyroidLip = 1.7320508075688772;
    /// Mean |∇g| on the surface g = 0 (numerically ≈ 1.528): sets the |g| level so the sheet is about `thickness` thick.
    public const double GyroidMeanGrad = 1.528;

    /// (ω = 2π / cell, level = ω · mean|∇g| · thickness / 2, 1 / (ω · √3), skin). ω = 0: lattice off (child passes through).
    public static V4[] GyroidRows(FrepNode n)
    {
        double cell = n.Param(0), t = Math.Max(0, n.Param(1)), skin = Math.Max(0, n.Param(2));
        if (!(cell > 0) || !double.IsFinite(cell)) return [new(0, 0, 0, skin)];
        double w = 2 * Math.PI / cell;
        return [new(w, w * GyroidMeanGrad * t * 0.5, 1 / (w * GyroidLip), skin)];
    }

    /// Sensible defaults for a child of this size: ~5 cells across, walls a tenth of a cell, no skin.
    public static double[] GyroidDefaults(BoundingBox child)
    {
        double size = child.IsValid ? child.Diagonal.MaximumCoordinate : 10;
        double cell = Math.Max(size / 5, 1e-3);
        return [cell, cell * 0.1, 0];
    }

    // ───────────── Pipe ─────────────

    /// Polyline points per pipe (fixed uniform budget: header + this many rows). Curves are sampled down to fit.
    public const int PipePoints = 32;
    public const int PipeRows = 1 + PipePoints;

    /// Live Pipe: visible node whose ref is an undeleted curve (hidden curves count).
    public static bool TryResolve(RhinoDoc doc, FrepNode n, out RhinoObject? obj, out Curve curve) =>
        TryResolve(doc, n, out obj, out curve, out _);

    /// <paramref name="curve"/> in canonical space: the real curve mapped by A⁻¹, A = the root transforms applied to it
    /// (<see cref="Placement.AppliedKey"/>, as for warp references). <paramref name="applied"/>: A (identity if none).
    public static bool TryResolve(RhinoDoc doc, FrepNode n, out RhinoObject? obj, out Curve curve, out Transform applied)
    {
        obj = null;
        curve = null!;
        applied = Transform.Identity;
        if (n.Op != FrepOp.Pipe || n.Hidden || doc.Objects.FindId(n.Ref) is not { IsDeleted: false } o) return false;
        GeometryBase g = o.Geometry;
        switch (Placement.ReadApplied(o.Attributes, out var a))
        {
            case Placement.Stored.Invalid:
                return false; // unreadable: treated like a missing curve, never as unmoved geometry
            case Placement.Stored.Valid:
                if (!a.TryGetInverse(out var inv)) return false;
                applied = a;
                g = g.Duplicate();
                if (!g.Transform(inv)) return false;
                break;
        }
        if (!FrepRefs.TryCurve(g, out curve)) return false;
        obj = o;
        return true;
    }

    /// Samples the curve into ≤ <see cref="PipePoints"/> points with radii interpolated by length. Color = the curve's display color.
    public static SdPipe Make(FrepNode n, Curve curve, System.Drawing.Color color)
    {
        double r0 = Math.Max(0, n.Param(0)), r1 = n.ParamCount < 2 ? r0 : Math.Max(0, n.Param(1));
        var pts = Sample(curve, Math.Max(Math.Max(r0, r1), 1e-6));
        var radii = new double[pts.Count];
        double total = 0;
        var at = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) at[i] = total += pts[i].DistanceTo(pts[i - 1]);
        for (int i = 0; i < pts.Count; i++) radii[i] = total > 0 ? r0 + (r1 - r0) * at[i] / total : r0;
        return new SdPipe([.. pts], radii, color);
    }

    /// Polylines with few enough vertices are used exactly (sharp corners stay sharp); smooth curves are refined by angle and
    /// chord error, loosened until they fit; anything else is divided evenly by count.
    static List<Point3d> Sample(Curve c, double radius)
    {
        if (c.TryGetPolyline(out var pl) && pl.Count >= 2 && pl.Count <= PipePoints) return [.. pl];
        double tol = radius * 0.02, angle = RhinoMath.ToRadians(5);
        for (int i = 0; i < 16; i++, tol *= 2, angle = Math.Min(angle * 1.5, Math.PI / 2))
        {
            using var poly = c.ToPolyline(tol, angle, 0, 0);
            if (poly != null && poly.PointCount >= 2 && poly.PointCount <= PipePoints)
                return [.. Enumerable.Range(0, poly.PointCount).Select(poly.Point)];
        }
        // Open: count + 1 parameters; closed: count (the start isn't repeated, so close it below).
        var ts = c.DivideByCount(PipePoints - 1, true) ?? [c.Domain.Min, c.Domain.Max];
        var pts = ts.Select(c.PointAt).ToList();
        if (c.IsClosed) pts.Add(pts[0]);
        return pts;
    }

    /// Header (Lipschitz bound, point count, 0, packed color) + one row per point (xyz, radius), padded with the last point.
    /// A segment whose radius changes by Δr over length ℓ has gradient up to √(1 + (Δr/ℓ)²); the field is divided by the max.
    public static V4[] PipeRowsOf(SdPipe pipe)
    {
        var rows = new V4[PipeRows];
        var p = pipe.Points;
        int n = Math.Min(p.Length, PipePoints);
        double lip = 1;
        for (int i = 1; i < n; i++)
        {
            double len = p[i].DistanceTo(p[i - 1]);
            if (len > 1e-12) lip = Math.Max(lip, Math.Sqrt(1 + Math.Pow((pipe.Radii[i] - pipe.Radii[i - 1]) / len, 2)));
        }
        rows[0] = new(lip, n, 0, FrepShaderGen.PackColor(pipe.Color));
        for (int i = 0; i < PipePoints; i++)
        {
            int j = Math.Min(i, n - 1);
            rows[1 + i] = n > 0 ? new(p[j].X, p[j].Y, p[j].Z, pipe.Radii[j]) : default;
        }
        return rows;
    }

    /// World box of the pipe: points ± largest radius.
    public static BoundingBox Box(SdPipe pipe)
    {
        if (pipe.Points.Length == 0) return BoundingBox.Empty;
        var b = new BoundingBox(pipe.Points);
        b.Inflate(pipe.Radii.Max());
        return b;
    }
}
