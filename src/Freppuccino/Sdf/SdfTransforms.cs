using System;
using System.Collections.Generic;
using Freppuccino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Sdf;

/// <summary>
/// Rewrites a primitive's SDF.* values so they describe the primitive after <paramref name="xform"/>.
/// A rotate, mirror, or scale along the primitive's own axes is folded into the frame and parameters.
/// Anything else (shear, scale off those axes) is kept as <see cref="SdfUserText.LinearXKey"/> and the parameters
/// stay in the original frame. Fails only if the map flattens the primitive.
/// <paramref name="attributes"/> is a copy; only commit it on success.
/// </summary>
public delegate bool SdfTransformHandler(ObjectAttributes attributes, Transform xform, out string? reason);

/// <summary>Per-SDF.Type transform rules. Each new primitive command registers its handler here.</summary>
public static class SdfTransforms
{
    private static readonly Dictionary<string, SdfTransformHandler> Handlers = new(StringComparer.Ordinal)
    {
        [FBoxCommand.SdfType] = FBoxCommand.TransformSdf,
        [FSphereCommand.SdfType] = FSphereCommand.TransformSdf,
        [FTorusCommand.SdfType] = FTorusCommand.TransformSdf,
        [FCylinderCommand.SdfType] = FCylinderCommand.TransformSdf,
        [FConeCommand.SdfType] = FConeCommand.TransformSdf,
        [FEllipsoidCommand.SdfType] = FEllipsoidCommand.TransformSdf,
        [FPyramidCommand.SdfType] = FPyramidCommand.TransformSdf,
        [FFunctionCommand.SdfType] = FFunctionCommand.TransformSdf,
    };

    public static void Register(string sdfType, SdfTransformHandler handler) => Handlers[sdfType] = handler;

    /// <summary>True if two scale factors match within a relative tolerance (e.g. a round cross-section stays round).</summary>
    public static bool NearlyEqual(double a, double b)
    {
        const double uniformTolerance = 1e-6;
        return Math.Abs(a - b) <= uniformTolerance * Math.Max(Math.Abs(a), Math.Abs(b));
    }

    /// <summary>
    /// Moves a local frame by <paramref name="xform"/>. <paramref name="scale"/> is how much each local axis
    /// was stretched. Fails if an axis collapses, or the axes stop being perpendicular (shear, or
    /// non-uniform scale along directions other than the frame's own axes).
    /// </summary>
    public static bool TryTransformFrame(Plane frame, Transform xform, out Plane result, out Vector3d scale, out string? reason)
    {
        result = Plane.Unset;
        scale = Vector3d.Unset;

        var origin = frame.Origin;
        origin.Transform(xform);

        // Vector3d.Transform applies only the linear part, so translation doesn't affect the axes.
        var x = frame.XAxis; x.Transform(xform);
        var y = frame.YAxis; y.Transform(xform);
        var z = frame.ZAxis; z.Transform(xform);

        double sx = x.Length, sy = y.Length, sz = z.Length;
        const double degenerate = 1e-12;
        if (sx < degenerate || sy < degenerate || sz < degenerate)
        {
            reason = "transform flattens the primitive";
            return false;
        }

        x /= sx; y /= sy; z /= sz;

        const double perpendicularTolerance = 1e-6;
        if (Math.Abs(x * y) > perpendicularTolerance ||
            Math.Abs(y * z) > perpendicularTolerance ||
            Math.Abs(z * x) > perpendicularTolerance)
        {
            reason = "transform skews the primitive (shear, or non-uniform scale off its axes)";
            return false;
        }

        // Mirroring can flip X × Y relative to the transformed Z; fine for the symmetric primitives we support.
        result = new Plane(origin, x, y);
        scale = new Vector3d(sx, sy, sz);
        reason = null;
        return true;
    }

    /// <summary>
    /// Ellipsoid (frame + semi-axes r) after <paramref name="xform"/>. Any non-degenerate affine map takes an ellipsoid to an
    /// ellipsoid, including shear and non-uniform scale off its axes, so this only fails if the result is flat.
    /// Keeps the existing axes when they stay perpendicular; otherwise reads the new principal axes off A·Aᵀ,
    /// where A = linear part × frame axes × diag(r) maps the unit ball onto the ellipsoid.
    /// </summary>
    public static bool TryTransformEllipsoid(Plane frame, Vector3d r, Transform xform, out Plane result, out Vector3d radii, out string? reason)
    {
        if (TryTransformFrame(frame, xform, out result, out var scale, out reason))
        {
            radii = new Vector3d(r.X * scale.X, r.Y * scale.Y, r.Z * scale.Z);
            return true;
        }

        radii = Vector3d.Unset;
        var origin = frame.Origin;
        origin.Transform(xform);
        var cols = new[] { frame.XAxis * r.X, frame.YAxis * r.Y, frame.ZAxis * r.Z };
        for (int i = 0; i < 3; i++) cols[i].Transform(xform);

        var m = new double[3, 3]; // A·Aᵀ = sum of outer products of A's columns
        foreach (var c in cols)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] += c[i] * c[j];

        SymmetricEigen(m, out var w, out var v);
        double max = Math.Max(w[0], Math.Max(w[1], w[2]));
        if (!(max > 0) || Math.Min(w[0], Math.Min(w[1], w[2])) <= 1e-18 * max)
        {
            reason = "transform flattens the primitive";
            return false;
        }

        var x = new Vector3d(v[0, 0], v[1, 0], v[2, 0]);
        var y = new Vector3d(v[0, 1], v[1, 1], v[2, 1]);
        result = new Plane(origin, x, y);
        radii = new Vector3d(Math.Sqrt(w[0]), Math.Sqrt(w[1]), Math.Sqrt(w[2]));
        reason = null;
        return result.IsValid;
    }

    /// <summary>Cyclic Jacobi on a symmetric 3×3 (destroys <paramref name="a"/>). Eigenvalues w, eigenvectors in v's columns.</summary>
    internal static void SymmetricEigen(double[,] a, out double[] w, out double[,] v)
    {
        v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double scale = Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]);
        for (int sweep = 0; sweep < 32; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off <= 1e-15 * scale) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (a[p, q] == 0) continue;
                    // Rotation in the (p, q) plane that zeroes a[p, q] (Numerical Recipes 11.1).
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        w = [a[0, 0], a[1, 1], a[2, 2]];
    }

    public static bool TryApply(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        var type = SdfUserText.ReadType(attributes);
        if (type == null)
        {
            reason = "object has no " + SdfUserText.TypeKey;
            return false;
        }

        if (!Handlers.TryGetValue(type, out var handler))
        {
            reason = "no transform rule for " + type;
            return false;
        }

        if (!xform.IsValid || !xform.IsAffine)
        {
            reason = "transform is not affine";
            return false;
        }

        return handler(attributes, xform, out reason);
    }

    /// <summary>
    /// Applies <paramref name="xform"/> to the primitive's canonical-to-world map.
    /// When <paramref name="absorb"/> accepts <paramref name="scale"/>, the frame becomes that rigid pose and the linear map is cleared
    /// (the callback writes the scaled parameters). Otherwise the frame axes stay put, the origin moves, and the full map is stored.
    /// </summary>
    public static bool CommitAffine(ObjectAttributes attributes, Plane frame, Transform xform, Func<Vector3d, bool> absorb, out string? reason)
    {
        SdfBasis current;
        switch (SdfUserText.ReadLinear(attributes, out var lx, out var ly, out var lz))
        {
            case SdfUserText.LinearState.Valid:
                current = new SdfBasis(lx, ly, lz);
                break;
            case SdfUserText.LinearState.Invalid:
                // Never compose onto an unreadable map as if it were the frame axes.
                reason = "unreadable or degenerate " + SdfUserText.LinearXKey;
                return false;
            default:
                current = SdfBasis.FromPlane(frame);
                break;
        }
        if (!TryAdvance(frame, current, xform, out var origin, out var factored, out var next, out var scale, out var rigid, out reason))
            return false;

        if (rigid && absorb(scale))
        {
            SdfUserText.WriteFrame(attributes, factored);
            SdfUserText.ClearLinear(attributes);
            reason = null;
            return true;
        }

        var frozen = new Plane(origin, frame.XAxis, frame.YAxis);
        SdfUserText.WriteFrame(attributes, frozen);
        if (next.Matches(frozen)) SdfUserText.ClearLinear(attributes);
        else SdfUserText.WriteLinear(attributes, next.X, next.Y, next.Z);
        reason = null;
        return true;
    }

    /// <summary>
    /// The one validity test for a primitive's canonical-to-world map, on write (<see cref="TryAdvance"/>) and on read
    /// (<see cref="SdfUserText.ReadLinear"/>): invertible with condition number below 1e9. Scale-relative, so a healthy
    /// map of any absolute size passes.
    /// </summary>
    public static bool IsUsableBasis(SdfBasis basis) =>
        TryMetrics(basis, out _, out _, out _, out var sigmaMin, out var sigmaMax) && sigmaMin > 1e-9 * Math.Max(sigmaMax, 1e-300);

    /// <summary>
    /// <paramref name="next"/> is the map's columns after <paramref name="xform"/>.
    /// <paramref name="rigid"/> means those columns are perpendicular, so <paramref name="scale"/> is their lengths
    /// and <paramref name="factored"/> is the unit frame. <paramref name="origin"/> is the transformed frame origin either way.
    /// </summary>
    public static bool TryAdvance(Plane frame, SdfBasis current, Transform xform, out Point3d origin, out Plane factored, out SdfBasis next, out Vector3d scale, out bool rigid, out string? reason)
    {
        origin = frame.Origin;
        origin.Transform(xform);
        next = current.Apply(xform);
        factored = Plane.Unset;
        scale = Vector3d.Unset;
        rigid = false;
        if (!IsUsableBasis(next))
        {
            reason = "transform flattens the primitive";
            return false;
        }
        rigid = TryFactor(next, origin, out factored, out scale);
        reason = null;
        return true;
    }

    /// <summary>Perpendicular columns: <paramref name="scale"/> is their lengths, <paramref name="pose"/> their unit directions.</summary>
    public static bool TryFactor(SdfBasis basis, Point3d origin, out Plane pose, out Vector3d scale)
    {
        pose = Plane.Unset;
        scale = Vector3d.Unset;
        double sx = basis.X.Length, sy = basis.Y.Length, sz = basis.Z.Length;
        const double degenerate = 1e-12;
        if (sx < degenerate || sy < degenerate || sz < degenerate) return false;
        var x = basis.X / sx;
        var y = basis.Y / sy;
        var z = basis.Z / sz;
        const double perpendicularTolerance = 1e-6;
        if (Math.Abs(x * y) > perpendicularTolerance ||
            Math.Abs(y * z) > perpendicularTolerance ||
            Math.Abs(z * x) > perpendicularTolerance)
            return false;
        pose = new Plane(origin, x, y);
        scale = new Vector3d(sx, sy, sz);
        // A reflection has perpendicular columns but the opposite Z. Baking it would drop the mirror.
        return pose.IsValid && pose.ZAxis * z > 1 - perpendicularTolerance;
    }

    /// <summary>
    /// Rows of the inverse (what the shader dots with) and the singular values of <paramref name="basis"/>.
    /// <paramref name="sigmaMin"/> keeps an affine-transformed distance conservative: sd(A⁻¹p) · σ_min changes by at most 1 per world unit.
    /// </summary>
    public static bool TryMetrics(SdfBasis basis, out Vector3d r0, out Vector3d r1, out Vector3d r2, out double sigmaMin, out double sigmaMax)
    {
        r0 = r1 = r2 = Vector3d.Unset;
        sigmaMin = sigmaMax = 0;
        var c0 = basis.X;
        var c1 = basis.Y;
        var c2 = basis.Z;
        double det = c0 * Vector3d.CrossProduct(c1, c2);
        double volume = c0.Length * c1.Length * c2.Length;
        // Scale-relative: the determinant of the normalized columns (no absolute floor, so a healthy map of any size passes).
        if (!(volume > 0 && double.IsFinite(det / volume) && Math.Abs(det / volume) > 1e-12)) return false;
        double invDet = 1.0 / det;
        r0 = Vector3d.CrossProduct(c1, c2) * invDet;
        r1 = Vector3d.CrossProduct(c2, c0) * invDet;
        r2 = Vector3d.CrossProduct(c0, c1) * invDet;

        var m = new double[3, 3];
        Vector3d[] cols = [c0, c1, c2];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = cols[i] * cols[j];
        SymmetricEigen(m, out var w, out _);
        sigmaMin = Math.Sqrt(Math.Max(0, Math.Min(w[0], Math.Min(w[1], w[2]))));
        sigmaMax = Math.Sqrt(Math.Max(0, Math.Max(w[0], Math.Max(w[1], w[2]))));
        return sigmaMin > 0 && sigmaMax > 0;
    }
}

/// <summary>Columns of a primitive's canonical-to-world linear map. The frame axes when no gumball residual is stored.</summary>
public readonly record struct SdfBasis(Vector3d X, Vector3d Y, Vector3d Z)
{
    public static SdfBasis FromPlane(Plane frame) => new(frame.XAxis, frame.YAxis, frame.ZAxis);

    public SdfBasis Apply(Transform xform)
    {
        var x = X; x.Transform(xform);
        var y = Y; y.Transform(xform);
        var z = Z; z.Transform(xform);
        return new SdfBasis(x, y, z);
    }

    public bool Matches(Plane frame)
    {
        const double tol = 1e-6;
        return (X - frame.XAxis).Length <= tol && (Y - frame.YAxis).Length <= tol && (Z - frame.ZAxis).Length <= tol;
    }
}
