using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Csg;

/// Displace node → uniform rows (layout in <see cref="FrepNoise"/>), shared by the shader (FrepShaderGen) and the CPU mesher
/// (FrepField), like <see cref="FrepWarpMath"/> for warps.
public static class FrepDisplaceMath
{
    /// Knurl axis: the node's live Ref line, else vertical (world Z) through the child's bounding-box centre.
    /// Ref set but deleted / not a line also falls back (the panel says so).
    public static Line Axis(RhinoDoc doc, FrepNode n, BoundingBox child, out RhinoObject? refObj)
    {
        refObj = null;
        if (n.Ref != Guid.Empty && doc.Objects.FindId(n.Ref) is { IsDeleted: false } o && FrepRefs.TryLine(o.Geometry, out var line))
        {
            refObj = o;
            return line;
        }
        var c = child.IsValid ? child.Center : Point3d.Origin;
        return new Line(c, c + Vector3d.ZAxis);
    }

    /// <paramref name="child"/>: child's world bbox (sizes the knurl: radius ≈ bbox corner distance from the axis / √2, exact
    /// for a cylinder or sphere on the axis).
    public static SdDisplace Make(FrepNode n, Line axis, BoundingBox child)
    {
        double extent = 0;
        if (child.IsValid)
            foreach (var c in child.GetCorners())
                extent = Math.Max(extent, axis.DistanceTo(c, false));
        return new SdDisplace((int)n.Pattern(), n.Param(1), n.Param(2), n.Param(3), n.Mask != null, axis, extent / Math.Sqrt(2));
    }

    /// Smallest scale / fade accepted, as a fraction of the other sizes, so 1 / scale and 1 / fade stay finite.
    const double MinSize = 1e-6;

    public static V4[] Rows(SdDisplace s)
    {
        double a = double.IsFinite(s.Amplitude) ? s.Amplitude : 0;
        double scale = Math.Max(Math.Abs(s.Scale), MinSize), invScale = 1 / scale;
        // Mask fade: no mask → 0 (weight 1 everywhere). A fade of 0 would be a step (unbounded gradient): at least 1% of scale.
        double invFade = s.HasMask ? 1 / Math.Max(s.Fade, 0.01 * scale) : 0;

        double k = 0, rho0 = 0, invRamp = 0, lipH;
        var d = s.Axis.Direction;
        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        d = len > 1e-12 ? d / len : Vector3d.ZAxis;
        switch (s.Pattern)
        {
            case 1:
                lipH = FrepNoise.VoronoiLip * invScale;
                break;
            case 2:
            {
                // Whole teeth around the knurl radius R, about one pitch apart; faded in between 0.4 R and 0.7 R.
                double r = Math.Max(s.Radius, scale);
                k = Math.Max(3, Math.Round(2 * Math.PI * r / scale)) / (2 * Math.PI);
                rho0 = 0.4 * r;
                invRamp = 1 / (0.3 * r);
                // |∇u| = √((K/ρ)² + (1/s)²) is largest at ρ0; triangle waves have slope 2; the ramp adds its own slope.
                lipH = 2 * Math.Sqrt(k / rho0 * (k / rho0) + invScale * invScale) + invRamp;
                break;
            }
            default:
                lipH = FrepNoise.PerlinLip * invScale;
                break;
        }
        double lip = 1 + Math.Abs(a) * (lipH + invFade);
        var o = s.Axis.From;
        return
        [
            new(s.Pattern, a, invScale, 1 / lip),
            new(invFade, k, rho0, invRamp),
            new(o.X, o.Y, o.Z, 0),
            new(d.X, d.Y, d.Z, 0),
        ];
    }

    /// Defaults for a child of this size: Perlin, height 2% of the size, features a tenth of it, fade = one feature.
    public static double[] Defaults(BoundingBox child)
    {
        double size = child.IsValid ? Math.Max(child.Diagonal.MaximumCoordinate, 1e-3) : 10;
        return [0, 0.02 * size, 0.1 * size, 0.1 * size];
    }
}
