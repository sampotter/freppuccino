namespace Freppuccino.Meshing;

/// Displacement textures for the Displace node: CPU port of the GLSL in Shaders/FrepShaderGen.cs (pcg3d, gnoise, fbm,
/// voronoiEdge, knurl, opDisplace). Keep the two in sync. Integer hashing (PCG3D) so CPU and GPU pick the same gradients and
/// cell points; only float vs double rounding differs.
///
/// Every pattern is a height h(p) in [0, 1] with a known gradient bound, so opDisplace can stay Lipschitz 1:
///   value = (d − a · w · h) / (1 + |a| · (max|∇h| + max|∇w|)),  w = mask weight in [0, 1].
/// Rows (one Displace node, 4 vec4, packed by Csg.FrepDisplaceMath.Rows):
///   r0 = (pattern, amplitude a, 1 / scale, 1 / L)
///   r1 = (1 / fade (0 = no mask), knurl teeth per radian K, knurl inner radius rho0, 1 / knurl ramp width)
///   r2 = (knurl axis start, -)
///   r3 = (knurl axis unit direction, -)
public static class FrepNoise
{
    // ── Lipschitz constants (per unit of 1 / scale), measured numerically with margin; see DisplaceTests. ──

    /// Perlin: h = clamp(0.5 + PerlinGain · fbm, 0, 1). |fbm| ≤ ≈ 0.76 (sampled), so the gain spreads it over ≈ [0, 1].
    public const double PerlinGain = 0.65;
    /// max |∇fbm| in lattice units: 3.52 over 3M samples (one octave alone: 3.31). The worst case of three octaves adding
    /// up (3 · 3.31 / 1.75 = 5.7) never happens in practice and would slow the raymarch, so 4.0: ~15% over the measured peak.
    public const double FbmGradBound = 4.0;
    public const double PerlinLip = PerlinGain * FbmGradBound;

    /// Voronoi: h = min((F2 − F1) / VoronoiGroove, 1). F1, F2 are distances (Lipschitz 1 each), so |∇(F2 − F1)| ≤ 2.
    public const double VoronoiGroove = 0.24;
    public const double VoronoiLip = 2.0 / VoronoiGroove;
    /// Feature points sit in [Jitter0, 1 − Jitter0] of their cell, so a 3×3×3 search finds F1 and F2 (nearly always).
    public const double Jitter0 = 0.05;

    // ── hashing ──

    /// Jarzynski & Olano, "Hash Functions for GPU Rendering" (2020). Same uint arithmetic as GLSL (wraps mod 2^32).
    public static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z)
    {
        unchecked
        {
            x = x * 1664525u + 1013904223u;
            y = y * 1664525u + 1013904223u;
            z = z * 1664525u + 1013904223u;
            x += y * z; y += z * x; z += x * y;
            x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
            x += y * z; y += z * x; z += x * y;
            return (x, y, z);
        }
    }

    static (uint, uint, uint) Hash(double cx, double cy, double cz) =>
        Pcg3d(unchecked((uint)(int)cx), unchecked((uint)(int)cy), unchecked((uint)(int)cz));

    // Perlin's 12 cube-edge gradients.
    static readonly V3[] Grad =
    [
        new(1, 1, 0), new(-1, 1, 0), new(1, -1, 0), new(-1, -1, 0),
        new(1, 0, 1), new(-1, 0, 1), new(1, 0, -1), new(-1, 0, -1),
        new(0, 1, 1), new(0, -1, 1), new(0, 1, -1), new(0, -1, -1),
    ];

    // ── patterns ──

    /// Gradient noise with quintic fade (Perlin 2002 style), roughly in [−1, 1].
    public static double GNoise(V3 x)
    {
        double ix = Math.Floor(x.X), iy = Math.Floor(x.Y), iz = Math.Floor(x.Z);
        double fx = x.X - ix, fy = x.Y - iy, fz = x.Z - iz;
        double ux = Fade(fx), uy = Fade(fy), uz = Fade(fz);
        double G(int ox, int oy, int oz)
        {
            var (h, _, _) = Hash(ix + ox, iy + oy, iz + oz);
            var g = Grad[h % 12u];
            return g.X * (fx - ox) + g.Y * (fy - oy) + g.Z * (fz - oz);
        }
        return Mix(Mix(Mix(G(0, 0, 0), G(1, 0, 0), ux), Mix(G(0, 1, 0), G(1, 1, 0), ux), uy),
                   Mix(Mix(G(0, 0, 1), G(1, 0, 1), ux), Mix(G(0, 1, 1), G(1, 1, 1), ux), uy), uz);
    }

    /// 3 octaves, gain ½, lacunarity 2, offsets so octaves don't line up at the origin. Weights sum to 1.75, normalized.
    public static double Fbm(V3 x) =>
        (GNoise(x) + 0.5 * GNoise(x * 2 + new V3(17.13, 5.71, 11.37)) + 0.25 * GNoise(x * 4 + new V3(3.93, 23.17, 7.51))) / 1.75;

    public static double Perlin(V3 x) => Math.Clamp(0.5 + PerlinGain * Fbm(x), 0, 1);

    /// F2 − F1 (cell units) over the 3×3×3 neighbourhood.
    public static double VoronoiEdge(V3 x)
    {
        double ix = Math.Floor(x.X), iy = Math.Floor(x.Y), iz = Math.Floor(x.Z);
        double fx = x.X - ix, fy = x.Y - iy, fz = x.Z - iz;
        double f1 = 8, f2 = 8;
        for (int k = -1; k <= 1; k++)
            for (int j = -1; j <= 1; j++)
                for (int l = -1; l <= 1; l++)
                {
                    var (hx, hy, hz) = Hash(ix + l, iy + j, iz + k);
                    double px = l + Jitter0 + (1 - 2 * Jitter0) * Unit(hx) - fx;
                    double py = j + Jitter0 + (1 - 2 * Jitter0) * Unit(hy) - fy;
                    double pz = k + Jitter0 + (1 - 2 * Jitter0) * Unit(hz) - fz;
                    double d = Math.Sqrt(px * px + py * py + pz * pz);
                    if (d < f1) { f2 = f1; f1 = d; }
                    else if (d < f2) f2 = d;
                }
        return f2 - f1;
    }

    public static double Voronoi(V3 x) => Math.Min(VoronoiEdge(x) / VoronoiGroove, 1);

    /// Diamond knurl around the axis (a, unit d): two helical tooth families u, v = θ·K ± z/s, pyramids = min of two triangle
    /// waves. K = whole teeth / 2π, so θ wrapping at ±π doesn't leave a seam. Faded in between rho0 and rho0 + 1/invRamp so the
    /// angle's gradient (K / ρ) stays bounded; 0 inside rho0.
    public static double Knurl(V3 p, V3 a, V3 d, double invScale, double k, double rho0, double invRamp)
    {
        var v = p - a;
        double z = V3.Dot(v, d);
        var r = v - d * z;
        double rho = r.Length;
        if (rho <= rho0) return 0;
        var (e1, e2) = Basis(d);
        double th = Math.Atan2(V3.Dot(r, e2), V3.Dot(r, e1));
        double u = th * k + z * invScale, w = th * k - z * invScale;
        return Math.Min(Tri(u), Tri(w)) * Math.Min((rho - rho0) * invRamp, 1);
    }

    /// e1 ⟂ d: world +Z projected off d (world +X if d is near vertical); e2 = d × e1. Same as the GLSL knurlBasis.
    public static (V3 E1, V3 E2) Basis(V3 d)
    {
        var r = Math.Abs(d.Z) > 0.99 ? new V3(1, 0, 0) : new V3(0, 0, 1);
        var e1 = (r - d * V3.Dot(r, d)).Normalized();
        return (e1, V3.Cross(d, e1));
    }

    /// Pattern height in [0, 1] at world point p.
    public static double Height(V4[] w, V3 p)
    {
        double invScale = w[0].Z;
        return (int)Math.Round(w[0].X) switch
        {
            1 => Voronoi(p * invScale),
            2 => Knurl(p, w[2].Xyz, w[3].Xyz, invScale, w[1].Y, w[1].Z, w[1].W),
            _ => Perlin(p * invScale),
        };
    }

    /// The Displace node's value from its child's value d and mask value m (+∞ / any value when there's no mask: 1 / fade = 0).
    public static double Displace(V4[] w, V3 p, double d, double m)
    {
        double a = w[0].Y;
        if (a == 0) return d;
        double weight = Math.Clamp(1 - Math.Max(m, 0) * w[1].X, 0, 1);
        return weight <= 0 ? d * w[0].W : (d - a * weight * Height(w, p)) * w[0].W;
    }

    // ── helpers (GLSL equivalents) ──

    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    static double Mix(double a, double b, double t) => a + (b - a) * t;
    /// Top 24 bits of a hash as [0, 1): exact in float32 too.
    static double Unit(uint h) => (h >> 8) * (1.0 / 16777216.0);
    /// 1 at integers, 0 at half-integers, slope ±2.
    static double Tri(double x) => 2 * Math.Abs(x - Math.Floor(x) - 0.5);
}
