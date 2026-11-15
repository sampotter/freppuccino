// Scherk / Karcher saddle tower: generalises Scherk's sinh(x) sinh(z) = sin(y) (= wings 2) to
// sinh(r)^n sin(n theta) / 2 = sin(y), giving 2n wings around stacked n-fold saddle tunnels (an approximation of
// Karcher's towers, not exactly minimal). The frame twists at an accelerating rate, tapers and leans as it rises. Field = f / |grad f| (numeric gradient) -> near-exact
// distance, thickened into a shell, sliced by an exact cylinder and planes, fused into a chamfered plinth.
param float period = 27.0;   // vertical period (one saddle stack)
param float wings = 3.0;     // n: 2n wings (2 = classic Scherk)
param float twist = 0.022;   // twist rate at the base (rad / unit)
param float twistAcc = 0.0006; // twist acceleration (rad / unit^2)
param float taper = 0.35;    // radius loss over the height
param float lean = 0.0025;   // axis lean (quadratic)
param float R0 = 16.0;       // envelope radius at the base
param float halfH = 30.0;
param float th = 0.9;        // shell half thickness
param float blend = 3.0;
param float plinthR = 21.0;
param float plinthH = 4.0;
param float cham = 1.2;

const float CAP = 0.45;      // cap |d| in periods/2pi (singular points have grad 0)

vec2 frameXZ(vec3 p) {
    float y = p.y + halfH;
    float a = twist * y + twistAcc * y * y;
    float c = cos(a), s = sin(a);
    vec2 xz = p.xz - vec2(lean * y * y, 0.0);
    return mat2(c, s, -s, c) * xz;
}
float scherk(vec3 p) {
    float w = 6.2831853 / period;
    vec2 q = frameXZ(p) * w;
    float r = length(q), a = atan(q.y, q.x);
    return 0.5 * pow(sinh(r), wings) * sin(wings * a) - sin(p.y * w);
}
float envR(float y) { float t = clamp((y + halfH) / (2.0 * halfH), 0.0, 1.0); return R0 * (1.0 - taper * t * t * (1.5 - 0.5 * t)); }

float sdf(vec3 p) {
    float w = 6.2831853 / period;
    float h = 0.01;
    float f = scherk(p);
    vec3 g = vec3(scherk(p + vec3(h, 0.0, 0.0)) - scherk(p - vec3(h, 0.0, 0.0)),
                  scherk(p + vec3(0.0, h, 0.0)) - scherk(p - vec3(0.0, h, 0.0)),
                  scherk(p + vec3(0.0, 0.0, h)) - scherk(p - vec3(0.0, 0.0, h))) / (2.0 * h);
    float d = clamp(f / max(length(g), 1e-6), -CAP / w, CAP / w);
    d = abs(d) - th;

    // exact envelope: leaning tapered cylinder, flat top, all sharp
    float y = p.y + halfH;
    float rc = length(p.xz - vec2(lean * y * y, 0.0)) - envR(p.y);
    d = max(d, rc * 0.95);
    d = max(d, p.y - halfH);

    // chamfered plinth
    vec2 pq = vec2(length(p.xz) - plinthR, abs(p.y + halfH + plinthH * 0.5) - plinthH * 0.5);
    float plinth = max(max(pq.x, pq.y), (pq.x + pq.y + cham) * 0.7071);
    d = smin(d, plinth, blend);
    d = max(d, -halfH - plinthH - p.y);
    return d;
}
