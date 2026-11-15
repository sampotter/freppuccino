using System.Globalization;
using Freppuccino.Meshing;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// General affine transforms for root nodes and warp references, stored as 12 numbers (top three rows of the 4x4,
/// row-major). Leaf primitives carry theirs as SDF.Linear.* (main's SdfTransforms.CommitAffine), not here.
///
/// Node transform M (FrepNode.Matrix): field = node(M⁻¹ p) · s(M), s = smallest singular value, so it still changes by
/// at most 1 per unit distance. Shader / FrepField rows: <see cref="TryRows"/>.
///
/// Applied root transforms A (FREP.Applied) on a warp reference object (axis line / repeat box): the accumulated root
/// transforms Rhino has applied to that object's real geometry. Its canonical geometry is A⁻¹ × geometry. Absent = identity.
public static class Placement
{
    public const string AppliedKey = "FREP.Applied";

    /// Rows a transformed node adds to the shader data (and FrepField takes): 3 rows of M⁻¹ (xyz linear row,
    /// w translation), then (s(M), 0, 0, 0).
    public const int ShaderRows = 4;

    public enum Stored { Absent, Valid, Invalid }

    /// A transformed copy of <paramref name="g"/>, or null. Rhino keeps revolved/rational surfaces (sphere, cylinder,
    /// cone, torus breps) exact under a similarity but does not deform them under a non-uniform scale or shear, so for
    /// those transforms the copy is made deformable first (as Rhino's own Scale1D/Shear do).
    public static GeometryBase? Mapped(GeometryBase g, Transform t)
    {
        var copy = g.Duplicate();
        if (t.SimilarityType == TransformSimilarityType.NotSimilarity)
        {
            if (copy is Extrusion e) copy = e.ToBrep();
            if (copy is Brep b) b.MakeDeformable();
            else if (copy is Surface s) copy = s.ToNurbsSurface();
        }
        return copy.Transform(t) ? copy : null;
    }

    /// Absent = identity. Invalid = present but unparsable or not <see cref="IsUsable"/>: the reference counts as missing.
    public static Stored ReadApplied(ObjectAttributes a, out Transform applied) => Read(a.GetUserString(AppliedKey), out applied);
    public static bool TryReadApplied(ObjectAttributes a, out Transform applied) => ReadApplied(a, out applied) == Stored.Valid;

    /// Identity (or null) removes the key. Callers write only <see cref="IsUsable"/> transforms.
    public static void WriteApplied(ObjectAttributes a, Transform? applied) => Write(a, AppliedKey, applied);

    /// Largest condition number (largest / smallest stretch) accepted. Scale-relative, so a well-shaped placement of any
    /// absolute size passes (a determinant test would reject a tiny but healthy one); beyond this, P⁻¹ loses precision.
    public const double MaxCondition = 1e10;

    /// The single validity test for reading and writing: finite affine with full rank, condition <= MaxCondition.
    public static bool IsUsable(Transform t)
    {
        if (!t.IsValid || !t.IsAffine) return false;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                if (!double.IsFinite(t[r, c])) return false;
        var (lo, hi) = Singular(t);
        return lo > 0 && hi / lo <= MaxCondition && t.TryGetInverse(out _);
    }

    public static bool IsIdentity(Transform t, double tol = 1e-12)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(t[r, c] - (r == c ? 1 : 0)) > tol) return false;
        return true;
    }

    /// Smallest singular value of the linear part (sqrt of AᵀA's smallest eigenvalue).
    public static double MinScale(Transform t) => Singular(t).Min;
    public static double MaxScale(Transform t) => Singular(t).Max;

    /// Largest / smallest singular value of the linear part (1 for a similarity); infinity if singular.
    public static double Condition(Transform t)
    {
        var (lo, hi) = Singular(t);
        return lo > 0 ? hi / lo : double.PositiveInfinity;
    }

    static (double Min, double Max) Singular(Transform t)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    m[i, j] += t[k, i] * t[k, j];
        Sdf.SdfTransforms.SymmetricEigen(m, out var w, out _);
        double lo = Math.Min(w[0], Math.Min(w[1], w[2])), hi = Math.Max(w[0], Math.Max(w[1], w[2]));
        return (Math.Sqrt(Math.Max(0, lo)), Math.Sqrt(Math.Max(0, hi)));
    }

    /// Shader / FrepField rows for a leaf placement; false if P is singular.
    public static bool TryRows(Transform place, out V4[] rows)
    {
        rows = [];
        if (!IsUsable(place) || !place.TryGetInverse(out var inv)) return false;
        double s = MinScale(place);
        rows = [
            new(inv.M00, inv.M01, inv.M02, inv.M03),
            new(inv.M10, inv.M11, inv.M12, inv.M13),
            new(inv.M20, inv.M21, inv.M22, inv.M23),
            new(s, 0, 0, 0),
        ];
        return true;
    }

    static Stored Read(string? text, out Transform t)
    {
        t = Transform.Identity;
        if (string.IsNullOrWhiteSpace(text)) return Stored.Absent;
        var parts = text.Split(',');
        if (parts.Length != 12) return Stored.Invalid;
        var r = Transform.Identity;
        for (int i = 0; i < 12; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Stored.Invalid;
            r[i / 4, i % 4] = v;
        }
        if (!IsUsable(r)) return Stored.Invalid;
        t = r;
        return Stored.Valid;
    }

    static void Write(ObjectAttributes a, string key, Transform? t)
    {
        if (t is not { } v || IsIdentity(v)) { a.DeleteUserString(key); return; }
        var s = new string[12];
        for (int i = 0; i < 12; i++) s[i] = v[i / 4, i % 4].ToString("R", CultureInfo.InvariantCulture);
        a.SetUserString(key, string.Join(",", s));
    }
}
