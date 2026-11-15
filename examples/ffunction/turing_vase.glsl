// Vase with reaction-diffusion-like labyrinth relief: a sum of plane waves of one wavelength in random
// directions on the (angle, height) wall, sharpened into ridges. Wave angular frequencies are integers, so there
// is no seam. Leaning, asymmetric smooth wall; exact turned collar with a chamfered rim and a stepped foot.
param float H = 30.0;        // half height
param float rMid = 13.0;     // nominal radius
param float wave = 5.0;      // ridge wavelength
param float amp = 0.75;      // ridge height
param float gain = 2.0;      // ridge sharpness
param float th = 1.5;        // wall thickness (inside the ridges)
param float seed = 4.0;
param float collarH = 5.0;
param float footH = 4.0;
param float cham = 0.8;

const int N = 9;

float hash1(float n) {   // sin-free hash: same on GPU (float) and CPU (double)
    n = fract(n * 0.1031 + seed * 0.0713);
    n *= n + 33.33;
    n *= n + n;
    return fract(n);
}

vec2 axis(float y) { return vec2(4.5 * sin(0.05 * y + 0.4) - 1.8, 2.5 * sin(0.08 * y - 0.3)); }
float radius(float y) {
    float t = clamp((y + H) / (2.0 * H), 0.0, 1.0);
    return rMid * (0.55 + 0.68 * sin(3.1416 * pow(t, 0.8)) - 0.1 * t + 0.05 * sin(9.0 * t));
}

float sdf(vec3 p) {
    float y = p.y;
    vec2 xz = p.xz - axis(y);
    float rho = length(xz);
    float ang = atan(xz.y, xz.x);
    float r = radius(y);
    float k = 6.2831853 / wave;
    // labyrinth field
    float f = 0.0;
    for (int i = 0; i < N; i++) {
        float a = 6.2831853 * hash1(float(i) + 0.5);
        float m = floor(k * rMid * cos(a) + 0.5);             // integer angular frequency: seamless
        float n = k * sin(a);
        f += cos(m * ang + n * y + 6.2831853 * hash1(float(i) + 17.0));
    }
    f /= sqrt(float(N));
    float fade = smoothstep(H - collarH - 1.0, H - collarH - 5.0, y) * smoothstep(-H + footH + 1.0, -H + footH + 5.0, y);
    float ridge = amp * fade * clamp(0.5 + gain * f, 0.0, 1.0);

    float slope = 1.3;                                        // |dr/dy| + lean bound -> normalisation
    float dOut = (rho - r - ridge) / slope;
    float dIn = (rho - (r - th)) / slope;
    float d = max(dOut, -dIn);

    // exact turned collar + chamfered rim (axis at the rim)
    vec2 ca = axis(H - collarH * 0.5);
    float rc = radius(H - collarH * 0.5);
    float rq = length(p.xz - ca);
    float collar = max(max(rq - rc - 0.6, rc - th - rq), abs(y - (H - collarH * 0.5)) - collarH * 0.5);
    collar = max(collar, (rq - (rc + 0.6) + y - H + cham) * 0.7071);   // outer chamfer
    d = min(d, collar);
    // stepped foot: solid exact disc
    vec2 fa = axis(-H);
    float rf = radius(-H) + 2.5;
    vec2 fq = vec2(length(p.xz - fa) - rf, abs(y + H - footH * 0.5) - footH * 0.5);
    float foot = max(max(fq.x, fq.y), (fq.x + y + H - footH + cham) * 0.7071);   // chamfered top edge
    d = min(d, foot);
    d = max(d, abs(y) - H);
    return d;
}
