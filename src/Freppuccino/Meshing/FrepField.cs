namespace Freppuccino.Meshing;

/// Minimal double-precision vector for the meshing core, which deliberately has no Rhino dependency
/// (so it can be tested outside Rhino).
public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(Dot(this, this));
    public V3 Normalized() { var l = Length; return l > 0 ? this * (1 / l) : this; }
}

/// One shader uniform row (vec4); warp params, see Csg.FrepWarpMath.Rows.
public readonly record struct V4(double X, double Y, double Z, double W)
{
    public V3 Xyz => new(X, Y, Z);
}

/// Same kinds and parameter packing as Scene.SdKind / SdPrimitive.P.
public enum FieldKind { Sphere, Box, Torus, Cylinder, Cone, Ellipsoid, Pyramid }

/// Twist/Bend/Repeat: domain warps of one operand (see <see cref="FrepField.AddWarp"/>). Shell/Gyroid: modifiers of one
/// operand's value (<see cref="FrepField.AddModifier"/>). Pipe: a leaf (<see cref="FrepField.AddPipe"/>).
public enum FieldOp { Union, Intersect, Difference, Twist, Bend, Repeat, Shell, Gyroid, Pipe }

/// A primitive (by index) or an earlier node (by index).
public readonly record struct FieldRef(bool IsNode, int Index)
{
    public static FieldRef Primitive(int index) => new(false, index);
    public static FieldRef Node(int index) => new(true, index);
}

/// CPU evaluator for the same field the viewport shader raymarches (see Shaders/FrepShaderGen.cs):
/// IQ primitive distance functions in their local frames, combined by CSG nodes with IQ's quadratic
/// smooth min/max, and a hard minimum over the roots. Keep the two in sync.
///
/// Every primitive here changes by at most 1 per unit distance, and the CSG operations preserve that,
/// so |field| <= distance to the surface: the mesher relies on it to skip empty space.
/// Twist/bend warps divide by their local stretch bound for the same reason (as the shader does). That is why
/// the ellipsoid and pyramid, here and in the shader, are not IQ's formulas as published (see PrimValue).
///
/// Build it with <see cref="AddPrimitive"/>, <see cref="AddNode"/> and <see cref="AddRoot"/>. With no
/// roots declared, all primitives are smooth-unioned in order with <see cref="Blend"/>.
public sealed class FrepField
{
    // X, Y, Z are the canonical-to-world columns (bounds). Ix, Iy, Iz are the rows of the inverse (the dots in PrimValue).
    readonly record struct Prim(FieldKind Kind, V3 O, V3 X, V3 Y, V3 Z, V3 P, double Lip, V3 Ix, V3 Iy, V3 Iz);
    // Rows: warp / modifier / pipe params (null for CSG). Code: CSG blend style code (style + 4 · steps, see Csg.FrepNodeMath).
    readonly record struct Node(FieldOp Op, double K, FieldRef[] Operands, V4[]? Rows = null, double Code = 0);

    static bool IsWarp(FieldOp op) => op is FieldOp.Twist or FieldOp.Bend or FieldOp.Repeat;
    static bool IsModifier(FieldOp op) => op is FieldOp.Shell or FieldOp.Gyroid;

    readonly List<Prim> prims = [];
    readonly List<Node> nodes = [];
    readonly List<FieldRef> roots = [];
    readonly object sealing = new();
    volatile bool isSealed;
    bool hasWarps;

    /// Blend distance of the implicit union used when no roots are declared; 0 = hard union.
    public double Blend { get; init; }

    public int Count => prims.Count;

    /// <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> are the columns of the canonical-to-world map
    /// (the frame axes when the primitive is rigid). <paramref name="lip"/> is σ_min of that map: the local distance is
    /// multiplied by it so the field stays Lipschitz 1 in world space.
    public int AddPrimitive(FieldKind kind, V3 origin, V3 x, V3 y, V3 p, V3? z = null, double lip = 1)
    {
        var zz = z ?? V3.Cross(x, y);
        if (!InverseRows(x, y, zz, out var ix, out var iy, out var iz))
        {
            ix = x; iy = y; iz = zz;
        }
        prims.Add(new Prim(kind, origin, x, y, zz, p, lip, ix, iy, iz));
        return prims.Count - 1;
    }

    static bool InverseRows(V3 x, V3 y, V3 z, out V3 r0, out V3 r1, out V3 r2)
    {
        double det = V3.Dot(x, V3.Cross(y, z));
        double volume = x.Length * y.Length * z.Length;
        if (!(Math.Abs(det) > 1e-12 * Math.Max(volume, 1e-30)))
        {
            r0 = r1 = r2 = default;
            return false;
        }
        double inv = 1.0 / det;
        r0 = V3.Cross(y, z) * inv;
        r1 = V3.Cross(z, x) * inv;
        r2 = V3.Cross(x, y) * inv;
        return true;
    }

    public int AddSphere(V3 center, double radius) =>
        AddPrimitive(FieldKind.Sphere, center, new V3(1, 0, 0), new V3(0, 1, 0), new V3(radius, 0, 0));

    public int AddBox(V3 center, V3 x, V3 y, V3 half) => AddPrimitive(FieldKind.Box, center, x, y, half);

    /// Difference is operands[0] minus the rest. Operands must already exist. At least one operand.
    /// <paramref name="code"/>: blend style code (0 = round; Csg.FrepNodeMath.BlendCode).
    public int AddNode(FieldOp op, double k, IEnumerable<FieldRef> operands, double code = 0)
    {
        nodes.Add(new Node(op, k, [.. operands], Code: code));
        return nodes.Count - 1;
    }

    /// Shell / Gyroid of <paramref name="child"/>; one row as packed by Csg.FrepNodeMath.ShellRows / GyroidRows.
    public int AddModifier(FieldOp op, V4[] rows, FieldRef child)
    {
        if (!IsModifier(op)) throw new ArgumentOutOfRangeException(nameof(op));
        if (rows.Length != 1) throw new ArgumentException($"{op} needs 1 row", nameof(rows));
        nodes.Add(new Node(op, 0, [child], rows));
        return nodes.Count - 1;
    }

    /// Pipe leaf; rows as packed by Csg.FrepNodeMath.PipeRowsOf (header + points).
    public int AddPipe(V4[] rows)
    {
        if (rows.Length < 2) throw new ArgumentException("Pipe needs a header and at least one point row", nameof(rows));
        nodes.Add(new Node(FieldOp.Pipe, 0, [], rows));
        return nodes.Count - 1;
    }

    /// Domain warp of <paramref name="child"/>; <paramref name="rows"/> as packed by Csg.FrepWarpMath.Rows (shader uniform layout).
    public int AddWarp(FieldOp op, V4[] rows, FieldRef child)
    {
        int want = op switch { FieldOp.Twist => 2, FieldOp.Bend => 3, FieldOp.Repeat => 5, _ => throw new ArgumentOutOfRangeException(nameof(op)) };
        if (rows.Length != want) throw new ArgumentException($"{op} needs {want} rows", nameof(rows));
        nodes.Add(new Node(op, 0, [child], rows));
        hasWarps = true;
        return nodes.Count - 1;
    }

    public void AddRoot(FieldRef root) => roots.Add(root);

    public bool HasRoots => roots.Count > 0;

    void Seal()
    {
        if (isSealed) return;
        lock (sealing)
        {
            if (isSealed) return;
            if (roots.Count == 0 && prims.Count > 0)
                roots.Add(FieldRef.Node(AddNode(FieldOp.Union, Blend, Enumerable.Range(0, prims.Count).Select(FieldRef.Primitive))));
            isSealed = true;
        }
    }

    // Per-thread memo: one slice of (prims + nodes) per point context; each warp opens a new context for its subtree.
    [ThreadStatic] static double[]? scratch;

    public double Value(V3 p)
    {
        Seal();
        if (!hasWarps) return FlatValue(p);
        int stride = prims.Count + nodes.Count, top = 0;
        var memo = scratch ?? new double[Math.Max(64, stride * 2)];
        int ctx = Push(ref memo, ref top, stride);
        double result = double.PositiveInfinity;
        foreach (var r in roots) result = Math.Min(result, Eval(r, p, ctx, ref memo, ref top, stride));
        scratch = memo;
        return result;
    }

    // No warps: one context, all operands evaluated in order (~30% faster than Eval).
    double FlatValue(V3 p)
    {
        int np = prims.Count, nn = nodes.Count;
        Span<double> v = np + nn <= 256 ? stackalloc double[np + nn] : new double[np + nn];
        for (int i = 0; i < np; i++) v[i] = PrimValue(prims[i], p);
        for (int i = 0; i < nn; i++)
        {
            var n = nodes[i];
            double d;
            if (n.Op == FieldOp.Pipe) d = Pipe(n.Rows!, p);
            else
            {
                d = v[Slot(n.Operands[0])];
                if (IsModifier(n.Op)) d = Modify(n, p, d);
                else for (int o = 1; o < n.Operands.Length; o++) d = Combine(n, d, v[Slot(n.Operands[o])]);
            }
            v[np + i] = d;
        }
        double result = double.PositiveInfinity;
        foreach (var r in roots) result = Math.Min(result, v[Slot(r)]);
        return result;
    }

    int Slot(FieldRef r) => r.IsNode ? prims.Count + r.Index : r.Index;

    static double Combine(in Node n, double d, double t) => n.Op switch
    {
        FieldOp.Union => Bmin(d, t, n.K, n.Code),
        FieldOp.Intersect => -Bmin(-d, -t, n.K, n.Code),
        _ => -Bmin(-d, t, n.K, n.Code),
    };

    /// Port of bmin in Shaders/FrepShaderGen.cs; keep in sync. code = style + 4 · steps: 0 round, 1 chamfer, 2 stairs.
    static double Bmin(double a, double b, double k, double code)
    {
        if (k <= 0) return Math.Min(a, b);
        int c = (int)Math.Round(code), style = c & 3;
        if (style == 0) return Smin(a, b, k);
        double m = Math.Min(a, b);
        if (style == 1) return Math.Min(m, (a + b - k) * 0.5);
        double s = k / Math.Max(c >> 2, 1), w = b - k;
        return Math.Min(m, 0.5 * (w + a + Math.Abs(Mod(w - a + s, 2 * s) - s)));
    }

    /// GLSL mod (floored), not C#'s %.
    static double Mod(double x, double y) => x - y * Math.Floor(x / y);

    /// Port of opShell / opGyroid in Shaders/FrepShaderGen.cs; keep in sync. Rows: see Csg.FrepNodeMath.
    static double Modify(in Node n, V3 p, double d)
    {
        var r = n.Rows![0];
        if (n.Op == FieldOp.Shell) return r.X <= 0 ? d : Math.Max(d, -d - r.X);
        if (r.X <= 0) return d;
        double x = p.X * r.X, y = p.Y * r.X, z = p.Z * r.X;
        double g = Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
        double v = Math.Max(d, (Math.Abs(g) - r.Y) * r.Z);
        return r.W > 0 ? Math.Min(v, Math.Max(d, -d - r.W)) : v;
    }

    /// Port of sdPipe in Shaders/FrepShaderGen.cs; keep in sync. w[0] = (Lipschitz bound, count, -, color); w[1 + i] = (point, radius).
    static double Pipe(V4[] w, V3 p)
    {
        int n = Math.Min((int)(w[0].Y + 0.5), w.Length - 1);
        var a = w[1];
        double d = (p - a.Xyz).Length - a.W;
        for (int i = 1; i < n; i++)
        {
            var e = w[1 + i];
            V3 pa = p - a.Xyz, ba = e.Xyz - a.Xyz;
            double h = Math.Clamp(V3.Dot(pa, ba) / Math.Max(V3.Dot(ba, ba), 1e-20), 0, 1);
            d = Math.Min(d, (pa - ba * h).Length - (a.W + (e.W - a.W) * h));
            a = e;
        }
        return d / w[0].X;
    }

    static int Push(ref double[] memo, ref int top, int stride)
    {
        int ctx = top;
        top += stride;
        if (memo.Length < top) Array.Resize(ref memo, Math.Max(top, memo.Length * 2));
        memo.AsSpan(ctx, stride).Fill(double.NaN);
        return ctx;
    }

    double Eval(FieldRef r, V3 p, int ctx, ref double[] memo, ref int top, int stride)
    {
        int slot = ctx + Slot(r);
        double d = memo[slot];
        if (!double.IsNaN(d)) return d;
        if (!r.IsNode) d = PrimValue(prims[r.Index], p);
        else
        {
            var n = nodes[r.Index];
            if (n.Op == FieldOp.Pipe) d = Pipe(n.Rows!, p);
            else if (IsModifier(n.Op)) d = Modify(n, p, Eval(n.Operands[0], p, ctx, ref memo, ref top, stride));
            else if (IsWarp(n.Op))
            {
                var q = Warp(n.Op, n.Rows!, p, out double lip);
                int saved = top;
                d = Eval(n.Operands[0], q, Push(ref memo, ref top, stride), ref memo, ref top, stride) / lip;
                top = saved;
            }
            else
            {
                d = Eval(n.Operands[0], p, ctx, ref memo, ref top, stride);
                for (int o = 1; o < n.Operands.Length; o++) d = Combine(n, d, Eval(n.Operands[o], p, ctx, ref memo, ref top, stride));
            }
        }
        return memo[slot] = d;
    }

    // Domain warps, ported from opTwist/opBend/opRepeat in Shaders/FrepShaderGen.cs; keep in sync. Returns the point to
    // evaluate the child at; lip = local Lipschitz bound (child value is divided by it). Row layouts: see the GLSL.
    static V3 Warp(FieldOp op, V4[] w, V3 p, out double lip)
    {
        lip = 1;
        switch (op)
        {
            case FieldOp.Twist:
            {
                V3 a = w[0].Xyz, d = w[1].Xyz;
                double rate = w[0].W, extent = w[1].W;
                var v = p - a;
                double h = V3.Dot(v, d);
                var r = v - d * h;
                double ang = -rate * h, c = Math.Cos(ang), s = Math.Sin(ang);
                double sh = Math.Abs(rate) * Math.Min(r.Length, extent);
                lip = TwistLip(sh);
                return a + d * h + r * c + V3.Cross(d, r) * s;
            }
            case FieldOp.Bend:
            {
                V3 a = w[0].Xyz, d = w[1].Xyz, n = w[2].Xyz;
                double L = w[0].W, beta = w[1].W, minR = w[2].W;
                var v = p - a;
                double h = V3.Dot(v, d);
                if (beta < 1e-5 || h <= 0) return p;
                double y = V3.Dot(v, n);
                var b = v - d * h - n * y;
                double R = L / beta;
                double cx = h, cy = R - y; // from the arc center
                double alpha = Math.Atan2(cx, cy), h0, y0;
                if (alpha <= beta)
                {
                    double rho = Len(cx, cy);
                    h0 = R * alpha;
                    y0 = R - rho;
                    lip = Math.Max(1, R / Math.Max(rho, minR));
                }
                else
                {
                    double qx = h - R * Math.Sin(beta), qy = y - R * (1 - Math.Cos(beta)); // from the arc end
                    h0 = L + qx * Math.Cos(beta) + qy * Math.Sin(beta);
                    y0 = -qx * Math.Sin(beta) + qy * Math.Cos(beta);
                }
                return a + d * h0 + n * y0 + b;
            }
            case FieldOp.Repeat:
            {
                V3 o = w[0].Xyz, x = w[1].Xyz, y = w[2].Xyz, z = V3.Cross(x, y);
                var dl = p - o;
                static double Cell(double local, double s, double lo, double hi) =>
                    Math.Clamp(Math.Round(local / Math.Max(s, 1e-9), MidpointRounding.AwayFromZero), lo, hi) * s;
                double ix = Cell(V3.Dot(dl, x), w[0].W, w[3].X, w[4].X);
                double iy = Cell(V3.Dot(dl, y), w[1].W, w[3].Y, w[4].Y);
                double iz = Cell(V3.Dot(dl, z), w[2].W, w[3].Z, w[4].Z);
                return p - x * ix - y * iy - z * iz;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    /// Upper bound of Warp's lip over the child's extent.
    static double MaxLip(FieldOp op, V4[] w) => op switch
    {
        FieldOp.Twist => TwistLip(Math.Abs(w[0].W) * w[1].W),
        FieldOp.Bend when w[1].W >= 1e-5 => Math.Max(1, w[0].W / w[1].W / Math.Max(w[2].W, 1e-12)),
        _ => 1,
    };

    static double TwistLip(double sh) => 0.5 * (sh + Math.Sqrt(sh * sh + 4));

    /// Gradient by central differences over <paramref name="step"/>. At a crease it mixes both sides, but
    /// only within <paramref name="step"/> of the crease.
    public V3 Gradient(V3 p, double step)
    {
        double i = 0.5 / step;
        return new V3(
            (Value(new V3(p.X + step, p.Y, p.Z)) - Value(new V3(p.X - step, p.Y, p.Z))) * i,
            (Value(new V3(p.X, p.Y + step, p.Z)) - Value(new V3(p.X, p.Y - step, p.Z))) * i,
            (Value(new V3(p.X, p.Y, p.Z + step)) - Value(new V3(p.X, p.Y, p.Z - step))) * i);
    }

    /// IQ quadratic smooth minimum; k = blend distance, 0 = hard.
    static double Smin(double a, double b, double k)
    {
        if (k <= 0) return Math.Min(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k * 0.25;
    }

    // IQ, https://iquilezles.org/articles/distfunctions/, ported from the GLSL in FrepShaderGen (local space, Y-up).
    static double PrimValue(in Prim s, V3 world)
    {
        var d = world - s.O;
        if (s.Kind == FieldKind.Sphere) return d.Length - s.P.X;
        double x = V3.Dot(d, s.Ix), y = V3.Dot(d, s.Iy), z = V3.Dot(d, s.Iz);
        double value = s.Kind switch
        {
            FieldKind.Box => Box(x, y, z, s.P),
            FieldKind.Torus => Len(Len(x, z) - s.P.X, y) - s.P.Y,
            FieldKind.Cylinder => Cylinder(x, y, z, s.P),
            FieldKind.Cone => Cone(x, y, z, s.P),
            FieldKind.Ellipsoid => Ellipsoid(x, y, z, s.P),
            FieldKind.Pyramid => Pyramid(x, y, z, s.P),
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        };
        // σ_min(A). Ellipsoid already folds its own scale in; its lip is 1.
        return value * s.Lip;
    }

    static double Box(double x, double y, double z, V3 b)
    {
        double qx = Math.Abs(x) - b.X, qy = Math.Abs(y) - b.Y, qz = Math.Abs(z) - b.Z;
        return Len(Math.Max(qx, 0), Math.Max(qy, 0), Math.Max(qz, 0)) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
    }

    static double Cylinder(double x, double y, double z, V3 p) // P.X = half height, P.Y = radius
    {
        double dx = Len(x, z) - p.Y, dy = Math.Abs(y) - p.X;
        return Math.Min(Math.Max(dx, dy), 0) + Len(Math.Max(dx, 0), Math.Max(dy, 0));
    }

    static double Cone(double x, double y, double z, V3 p) // P.X = half height, P.Y = radius at -h, P.Z = radius at +h
    {
        double h = p.X, r1 = p.Y, r2 = p.Z;
        double qx = Len(x, z), qy = y;
        double k2x = r2 - r1, k2y = 2 * h;
        double cax = qx - Math.Min(qx, qy < 0 ? r1 : r2), cay = Math.Abs(qy) - h;
        double t = Math.Clamp(((r2 - qx) * k2x + (h - qy) * k2y) / (k2x * k2x + k2y * k2y), 0, 1);
        double cbx = qx - r2 + k2x * t, cby = qy - h + k2y * t;
        double sign = cbx < 0 && cay < 0 ? -1 : 1;
        return sign * Math.Sqrt(Math.Min(cax * cax + cay * cay, cbx * cbx + cby * cby));
    }

    // A unit sphere scaled by r, times the smallest semi-axis: exact surface, and it changes by at most 1 per unit
    // distance. IQ's k0 (k0 - 1) / k1 is a better distance estimate, but its value at the centre depends on the
    // direction of approach; under a smooth CSG node that becomes a sign jump.
    static double Ellipsoid(double x, double y, double z, V3 r)
    {
        double k0 = Len(x / r.X, y / r.Y, z / r.Z);
        return (k0 - 1) * Math.Min(r.X, Math.Min(r.Y, r.Z));
    }

    // P.X = height, P.Y = base side; base centred on the origin, apex at +Y.
    // IQ's sdPyramid for a unit base, scaled by the side, with the shader's two corrections (see sdPyramid in
    // FrepShaderGen): IQ's measures distance to the slanted faces only, so below the base use the distance to
    // the base square, and inside let the base count.
    static double Pyramid(double x, double y, double z, V3 p)
    {
        double side = p.Y, h = p.X / side;
        double px = Math.Abs(x / side), py = y / side, pz = Math.Abs(z / side);
        double m2 = h * h + 0.25;
        if (pz > px) (px, pz) = (pz, px);
        px -= 0.5; pz -= 0.5;
        if (py < 0) return Len(Math.Max(px, 0), py, Math.Max(pz, 0)) * side;
        double qx = pz, qy = h * py - 0.5 * px, qz = h * px + 0.5 * py;
        double a0 = Math.Max(-qx, 0);
        double t = Math.Clamp((qy - 0.5 * pz) / (m2 + 0.25), 0, 1);
        double a = m2 * (qx + a0) * (qx + a0) + qy * qy;
        double b = m2 * (qx + 0.5 * t) * (qx + 0.5 * t) + (qy - m2 * t) * (qy - m2 * t);
        double d2 = Math.Min(qy, -qx * m2 - qy * 0.5) > 0 ? 0 : Math.Min(a, b);
        double signed = Math.Sqrt((d2 + qz * qz) / m2) * Math.Sign(Math.Max(qz, -py));
        return Math.Max(signed, -py) * side;
    }

    static double Len(double a, double b) => Math.Sqrt(a * a + b * b);
    static double Len(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);

    /// World-axis bounds containing the surface. Empty (Min > Max on some axis) if there is none.
    ///
    /// Computed as bounds of sublevel sets {field <= t}, from the roots at t = 0 down the CSG tree, so
    /// that cutters, intersected-away regions and primitives no root reaches do not inflate them:
    ///   union:      smin >= min - d, so {smin <= t} lies in the union of the operands' {<= t + d}, where
    ///               d = k/4 for two operands and k for more (the deficit below the plain minimum
    ///               compounds as x' = x + (k - x)²/4k, which approaches but never exceeds k);
    ///   intersect:  smax >= max, so it lies in the overlap of the operands' {<= t};
    ///   difference: it lies in the base's {<= t};
    ///   warp:       child(q) / lip <= t needs child(q) <= t · max lip; the child's box is then mapped forward
    ///               (see <see cref="WarpBox"/>);
    ///   shell / gyroid: max(child, …) and min of two such terms are >= child, so it lies in the child's {<= t};
    ///   pipe:       a leaf; field <= t means within radius + t · lip of the polyline.
    /// Chamfer / stairs unions sink by up to k/2 per join (bmin >= min − k/2), so their d is k/2 · (operands − 1).
    /// The offset is in field value, not distance: a primitive whose field underestimates distance
    /// (the ellipsoid) grows by more than t, which <see cref="PrimBounds"/> accounts for.
    public (V3 Min, V3 Max) Bounds()
    {
        Seal();
        // The level each primitive and node is asked for. Nodes are in creation order, operands first,
        // so one pass from the last node down reaches every operand after all of its users. A node used
        // at several levels takes the largest, which only widens the result, and each is visited once
        // however much of the graph is shared.
        double none = double.NegativeInfinity;
        var primLevel = new double[prims.Count];
        var nodeLevel = new double[nodes.Count];
        Array.Fill(primLevel, none);
        Array.Fill(nodeLevel, none);
        void Ask(FieldRef r, double t)
        {
            var level = r.IsNode ? nodeLevel : primLevel;
            level[r.Index] = Math.Max(level[r.Index], t);
        }
        foreach (var r in roots) Ask(r, 0);
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            double t = nodeLevel[i];
            if (t == none) continue;
            var n = nodes[i];
            if (n.Op == FieldOp.Pipe) continue;
            if (n.Op == FieldOp.Difference || IsModifier(n.Op)) { Ask(n.Operands[0], t); continue; }
            if (IsWarp(n.Op)) { Ask(n.Operands[0], t * MaxLip(n.Op, n.Rows!)); continue; }
            double d = 0;
            if (n.Op == FieldOp.Union && n.Operands.Length > 1 && n.K > 0)
                d = ((int)Math.Round(n.Code) & 3) == 0
                    ? n.K * (n.Operands.Length > 2 ? 1.0 : 0.25)
                    : 0.5 * n.K * (n.Operands.Length - 1);
            foreach (var o in n.Operands) Ask(o, t + d);
        }

        var nodeBox = new (V3 Min, V3 Max)[nodes.Count];
        (V3 Min, V3 Max) Box(FieldRef r) => r.IsNode ? nodeBox[r.Index] : PrimBounds(prims[r.Index], primLevel[r.Index]);
        for (int i = 0; i < nodes.Count; i++)
        {
            nodeBox[i] = Empty;
            if (nodeLevel[i] == none) continue;
            var n = nodes[i];
            if (n.Op == FieldOp.Pipe)
            {
                nodeBox[i] = PipeBounds(n.Rows!, nodeLevel[i]);
                continue;
            }
            var box = Box(n.Operands[0]);
            if (IsWarp(n.Op))
            {
                if (!IsEmpty(box)) box = WarpBox(n.Op, n.Rows!, box);
            }
            else if (n.Op == FieldOp.Union)
                for (int o = 1; o < n.Operands.Length; o++) box = Join(box, Box(n.Operands[o]));
            else if (n.Op == FieldOp.Intersect)
                for (int o = 1; o < n.Operands.Length; o++)
                {
                    var other = Box(n.Operands[o]);
                    box = (
                        new V3(Math.Max(box.Min.X, other.Min.X), Math.Max(box.Min.Y, other.Min.Y), Math.Max(box.Min.Z, other.Min.Z)),
                        new V3(Math.Min(box.Max.X, other.Max.X), Math.Min(box.Max.Y, other.Max.Y), Math.Min(box.Max.Z, other.Max.Z)));
                }
            nodeBox[i] = IsEmpty(box) ? Empty : box;
        }

        var all = Empty;
        foreach (var r in roots) all = Join(all, Box(r));
        return all;
    }

    static readonly (V3 Min, V3 Max) Empty = (
        new V3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new V3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    static bool IsEmpty((V3 Min, V3 Max) b) => !(b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z);

    static (V3 Min, V3 Max) Join((V3 Min, V3 Max) a, (V3 Min, V3 Max) b) => (
        new V3(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
        new V3(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)));

    /// World box holding the warp's image of a child box (child-space q → world p with Warp(p) = q).
    ///   twist:  rotation about the axis, keeps h and radius: finite cylinder around the axis;
    ///   bend:   h <= 0 is identity (child box); arc region keeps b and rho = R - y: annular sector;
    ///           tail is rigid: child box corners moved;
    ///   repeat: child box ⊕ box of the farthest cell offsets.
    public static (V3 Min, V3 Max) WarpBox(FieldOp op, V4[] w, (V3 Min, V3 Max) child)
    {
        Span<V3> corners = stackalloc V3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new V3((i & 1) == 0 ? child.Min.X : child.Max.X, (i & 2) == 0 ? child.Min.Y : child.Max.Y,
                (i & 4) == 0 ? child.Min.Z : child.Max.Z);
        switch (op)
        {
            case FieldOp.Twist:
            {
                V3 a = w[0].Xyz, d = w[1].Xyz;
                double h0 = double.PositiveInfinity, h1 = double.NegativeInfinity, r = 0;
                foreach (var c in corners)
                {
                    double h = V3.Dot(c - a, d);
                    h0 = Math.Min(h0, h);
                    h1 = Math.Max(h1, h);
                    r = Math.Max(r, (c - a - d * h).Length);
                }
                // Disc of radius r perpendicular to d spans r·sqrt(1 - d_i²) along axis i.
                var e = new V3(r * Math.Sqrt(Math.Max(0, 1 - d.X * d.X)), r * Math.Sqrt(Math.Max(0, 1 - d.Y * d.Y)),
                    r * Math.Sqrt(Math.Max(0, 1 - d.Z * d.Z)));
                return Join((a + d * h0 - e, a + d * h0 + e), (a + d * h1 - e, a + d * h1 + e));
            }
            case FieldOp.Bend:
            {
                V3 a = w[0].Xyz, d = w[1].Xyz, n = w[2].Xyz, bd = V3.Cross(d, n);
                double L = w[0].W, beta = w[1].W;
                if (beta < 1e-5) return child;
                double R = L / beta, sb = Math.Sin(beta), cb = Math.Cos(beta);
                // Child box in the (d, n, bd) frame at a; tail corners mapped by the inverse of Warp's rigid tail map.
                V3 lo = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity), hi = lo * -1;
                var box = child;
                foreach (var c in corners)
                {
                    var v = c - a;
                    double h = V3.Dot(v, d), y = V3.Dot(v, n), b = V3.Dot(v, bd);
                    lo = new V3(Math.Min(lo.X, h), Math.Min(lo.Y, y), Math.Min(lo.Z, b));
                    hi = new V3(Math.Max(hi.X, h), Math.Max(hi.Y, y), Math.Max(hi.Z, b));
                    double qh = h - L;
                    var p = a + d * (R * sb + qh * cb - y * sb) + n * (R * (1 - cb) + qh * sb + y * cb) + bd * b;
                    box = Join(box, (p, p));
                }
                // Arc: q = (R alpha, R - rho, b) ↦ (rho sin alpha, R - rho cos alpha, b); alpha <= pi (atan2 range).
                double r0 = Math.Max(0, R - hi.Y), r1 = R - lo.Y;
                double a0 = Math.Max(0, lo.X / R), a1 = Math.Min(Math.Min(beta, Math.PI), hi.X / R);
                if (r1 >= r0 && a1 >= a0)
                {
                    double sh0 = double.PositiveInfinity, sh1 = double.NegativeInfinity, sy0 = sh0, sy1 = sh1;
                    void Add(double rho, double al)
                    {
                        double h = rho * Math.Sin(al), y = R - rho * Math.Cos(al);
                        sh0 = Math.Min(sh0, h); sh1 = Math.Max(sh1, h);
                        sy0 = Math.Min(sy0, y); sy1 = Math.Max(sy1, y);
                    }
                    const double Q = Math.PI / 2;
                    foreach (var rho in (ReadOnlySpan<double>)[r0, r1])
                    {
                        Add(rho, a0);
                        Add(rho, a1);
                        for (double k = Math.Ceiling(a0 / Q) * Q; k <= a1; k += Q) Add(rho, k); // extremes at quarter turns
                    }
                    for (int i = 0; i < 8; i++)
                    {
                        var p = a + d * ((i & 1) == 0 ? sh0 : sh1) + n * ((i & 2) == 0 ? sy0 : sy1) + bd * ((i & 4) == 0 ? lo.Z : hi.Z);
                        box = Join(box, (p, p));
                    }
                }
                return box;
            }
            case FieldOp.Repeat:
            {
                V3 x = w[1].Xyz, y = w[2].Xyz, z = V3.Cross(x, y);
                var box = child;
                for (int i = 0; i < 8; i++)
                {
                    var off = x * (((i & 1) == 0 ? w[3].X : w[4].X) * w[0].W) + y * (((i & 2) == 0 ? w[3].Y : w[4].Y) * w[1].W) +
                              z * (((i & 4) == 0 ? w[3].Z : w[4].Z) * w[2].W);
                    box = Join(box, (child.Min + off, child.Max + off));
                }
                return box;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    /// World-axis bounds of {pipe field <= t}: the polyline's points ± (largest radius + t · Lipschitz bound).
    static (V3 Min, V3 Max) PipeBounds(V4[] w, double t)
    {
        int n = Math.Min((int)(w[0].Y + 0.5), w.Length - 1);
        var box = Empty;
        for (int i = 0; i < n; i++)
        {
            var e = w[1 + i];
            double g = Math.Max(e.W, 0) + Math.Max(t, 0) * w[0].X;
            var r = new V3(g, g, g);
            box = Join(box, (e.Xyz - r, e.Xyz + r));
        }
        return box;
    }

    /// World-axis bounds of {primitive field <= t}, t >= 0, from a box in the primitive's local frame.
    static (V3 Min, V3 Max) PrimBounds(in Prim s, double t)
    {
        // Field value is σ_min · local distance, so {field <= t} is a local offset of t / σ_min.
        double grow = s.Lip > 1e-12 ? t / s.Lip : t;
        V3 lo, hi;
        switch (s.Kind)
        {
            case FieldKind.Sphere: hi = new V3(s.P.X + grow, s.P.X + grow, s.P.X + grow); lo = hi * -1; break;
            case FieldKind.Box: hi = new V3(s.P.X + grow, s.P.Y + grow, s.P.Z + grow); lo = hi * -1; break;
            case FieldKind.Torus: hi = new V3(s.P.X + s.P.Y + grow, s.P.Y + grow, s.P.X + s.P.Y + grow); lo = hi * -1; break;
            case FieldKind.Cylinder: hi = new V3(s.P.Y + grow, s.P.X + grow, s.P.Y + grow); lo = hi * -1; break;
            case FieldKind.Cone:
            {
                double r = Math.Max(s.P.Y, s.P.Z) + grow;
                hi = new V3(r, s.P.X + grow, r); lo = hi * -1;
                break;
            }
            case FieldKind.Ellipsoid:
            {
                // (|p/r| - 1) min(r) <= t  <=>  |p/r| <= 1 + t / min(r): the semi-axes scale.
                double scale = 1 + grow / Math.Min(s.P.X, Math.Min(s.P.Y, s.P.Z));
                hi = s.P * scale; lo = hi * -1;
                break;
            }
            case FieldKind.Pyramid: // an exact distance; local y runs from the base at 0 to the apex at h
            {
                double b = s.P.Y * 0.5;
                lo = new V3(-b - grow, -grow, -b - grow); hi = new V3(b + grow, s.P.X + grow, b + grow);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(s));
        }
        var mid = (lo + hi) * 0.5;
        var e = (hi - lo) * 0.5;
        var c = s.O + s.X * mid.X + s.Y * mid.Y + s.Z * mid.Z;
        var w = new V3(
            Math.Abs(s.X.X) * e.X + Math.Abs(s.Y.X) * e.Y + Math.Abs(s.Z.X) * e.Z,
            Math.Abs(s.X.Y) * e.X + Math.Abs(s.Y.Y) * e.Y + Math.Abs(s.Z.Y) * e.Z,
            Math.Abs(s.X.Z) * e.X + Math.Abs(s.Y.Z) * e.Y + Math.Abs(s.Z.Z) * e.Z);
        return (c - w, c + w);
    }
}
