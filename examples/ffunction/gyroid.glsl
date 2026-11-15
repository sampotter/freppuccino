// Graded gyroid infill (meant to be clipped by a body through the tree). Cell size shrinks and walls thin
// with height: phase is the integral of the local frequency, so cells stay continuous while grading, and the
// value is divided by the local frequency to stay near-distance. A slow swirl about the vertical axis keeps it
// from reading as a lattice.
param float cellBot = 15.0;  // cell size at the bottom
param float cellTop = 6.5;   // cell size at the top
param float thBot = 1.3;     // wall half thickness at the bottom
param float thTop = 0.4;     // ... at the top
param float swirl = 0.025;   // rad / unit about the vertical axis
param float halfH = 20.0;

float sdf(vec3 p) {
    float t = clamp((p.y + halfH) / (2.0 * halfH), 0.0, 1.0);
    float w0 = 6.2831853 / cellBot, w1 = 6.2831853 / cellTop;
    float w = mix(w0, w1, t);
    // vertical phase: integral of w(y) dy
    float y0 = p.y + halfH;
    float phy = w0 * y0 + (w1 - w0) * y0 * y0 / (4.0 * halfH);
    float a = swirl * length(p.xz);
    float c = cos(a), s = sin(a);
    vec2 xz = mat2(c, s, -s, c) * p.xz;
    vec3 q = vec3(xz.x * w, phy, xz.y * w);
    float g = dot(sin(q), cos(q.yzx));
    return abs(g) / w - mix(thBot, thTop, t);
}
