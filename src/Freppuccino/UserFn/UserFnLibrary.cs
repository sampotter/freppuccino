namespace Freppuccino.UserFn;

public static class UserFnLibrary
{
    /// GLSL library user code may call (smin, smax, dot2, sdBox, ...). Single source for the fragment shader too:
    /// splice it once, before any emitted FFunction. C# ports: Runtime/Builtins (keep in sync).
    public const string Glsl = """
        // IQ quadratic smooth min/max; k = blend distance, 0 = hard.
        float smin(float a, float b, float k)
        {
            if (k <= 0.0) return min(a, b);
            float h = max(k - abs(a - b), 0.0) / k;
            return min(a, b) - h * h * k * 0.25;
        }
        float smax(float a, float b, float k) { return -smin(-a, -b, k); }
        float dot2(vec2 v) { return dot(v, v); }
        // IQ, https://iquilezles.org/articles/distfunctions/ (all in local space, Y-up)
        float sdBox(vec3 p, vec3 b)
        {
            vec3 q = abs(p) - b;
            return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
        }
        float sdTorus(vec3 p, vec2 t)
        {
            vec2 q = vec2(length(p.xz) - t.x, p.y);
            return length(q) - t.y;
        }
        float sdCappedCylinder(vec3 p, float h, float r)
        {
            vec2 d = abs(vec2(length(p.xz), p.y)) - vec2(r, h);
            return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
        }
        float sdCappedCone(vec3 p, float h, float r1, float r2)
        {
            vec2 q = vec2(length(p.xz), p.y);
            vec2 k1 = vec2(r2, h);
            vec2 k2 = vec2(r2 - r1, 2.0 * h);
            vec2 ca = vec2(q.x - min(q.x, (q.y < 0.0) ? r1 : r2), abs(q.y) - h);
            vec2 cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot2(k2), 0.0, 1.0);
            float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
            return s * sqrt(min(dot2(ca), dot2(cb)));
        }
        // Not IQ's sdEllipsoid: its value at the centre depends on the direction of approach, which breaks
        // smooth CSG and meshing. This has the exact surface and changes by at most 1 per unit distance,
        // but it underestimates distance. Keep in sync with Meshing/FrepField.cs.
        float sdEllipsoid(vec3 p, vec3 r) // unit sphere scaled by r, times the smallest semi-axis
        {
            return (length(p / r) - 1.0) * min(r.x, min(r.y, r.z));
        }
        // Unit square base centered at the origin, apex at (0, h, 0).
        // IQ's version measures distance to the four slanted faces only; the base enters just through the sign.
        // Below the base that overestimates (the ray steps through the floor and stops inside), and just above it
        // the interior distance ignores the floor (normals there point at the side faces). Both patched at the end.
        float sdPyramid(vec3 p, float h)
        {
            float m2 = h * h + 0.25;
            p.xz = abs(p.xz);
            p.xz = (p.z > p.x) ? p.zx : p.xz;
            p.xz -= 0.5;
            vec3 q = vec3(p.z, h * p.y - 0.5 * p.x, h * p.x + 0.5 * p.y);
            float s = max(-q.x, 0.0);
            float t = clamp((q.y - 0.5 * p.z) / (m2 + 0.25), 0.0, 1.0);
            float a = m2 * (q.x + s) * (q.x + s) + q.y * q.y;
            float b = m2 * (q.x + 0.5 * t) * (q.x + 0.5 * t) + (q.y - m2 * t) * (q.y - m2 * t);
            float d2 = min(q.y, -q.x * m2 - q.y * 0.5) > 0.0 ? 0.0 : min(a, b);
            float d = sqrt((d2 + q.z * q.z) / m2) * sign(max(q.z, -p.y));
            // Below the base every point of the solid sits above the base square, so the base is nearest (exact).
            // p.xz here is already abs()'d and shifted by -0.5, so max(p.xz, 0) is the overhang past the square.
            if (p.y < 0.0) return length(vec3(max(p.x, 0.0), p.y, max(p.z, 0.0)));
            return max(d, -p.y); // inside: floor may be closer than the side faces; outside (d > 0): unchanged
        }
        float sdPyramidScaled(vec3 p, float h, float s) { return sdPyramid(p / s, h / s) * s; } // base side s
        """;
}
