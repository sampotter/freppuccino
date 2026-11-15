// Catenoid neck morphing into a helicoid (the two are locally isometric members of one associate family).
// Both fields are normalised to near-true distance, capped, then blended by height; the shell lands on a
// bolted flange at the foot and wraps an exact hex-bored hub at the top. Outer and top cuts are sharp.
param float neck = 8.0;      // catenoid waist radius c
param float neckY = -14.0;    // waist height
param float pitch = 0.13;    // helicoid twist (rad / unit)
param float morph0 = -17.0;  // morph starts (pure catenoid below)
param float morph1 = 6.0;    // morph ends (pure helicoid above)
param float th = 0.85;       // shell half thickness
param float blend = 2.2;
param float halfH = 28.0;
param float Rbot = 30.0;     // outer cut radius at the foot
param float Rtop = 12.0;     // ... at the top
param float hubR = 3.4;
param float hexF = 1.6;      // hex bore half across-flats
param float flangeT = 3.0;
param float flangeIn = 19.0; // flange inner radius
param float boltR = 1.6;

const float CAP = 5.0;

float catenoid(vec3 p) {
    float s = (p.y - neckY) / neck;
    return (length(p.xz) - neck * cosh(s)) / cosh(s);
}
float helicoid(vec3 p) {
    float rho = length(p.xz);
    float a = atan(p.z, p.x) - pitch * p.y;
    // sin(a) (not a wrapped angle) keeps the signed field continuous, so it can be blended with the catenoid
    return rho * sin(a) / sqrt(1.0 + pitch * pitch * rho * rho);
}
float hex2(vec2 p, float r) {   // exact hexagon, half across-flats r
    vec3 k = vec3(-0.8660254, 0.5, 0.57735);
    p = abs(p);
    p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
    p -= vec2(clamp(p.x, -k.z * r, k.z * r), r);
    return length(p) * sign(p.y);
}

float sdf(vec3 p) {
    float t = smoothstep(morph0, morph1, p.y);
    float dc = clamp(catenoid(p), -CAP, CAP);
    float dh = clamp(helicoid(p), -CAP, CAP);
    float d = abs(mix(dc, dh, t)) - th;

    // sharp outer cut: a cone frustum around the axis
    float u = clamp((p.y + halfH) / (2.0 * halfH), 0.0, 1.0);
    d = max(d, length(p.xz) - mix(Rbot, Rtop, u));

    // hub with exact hex bore, from the morph zone to the top
    float hub = sdCappedCylinder(p - vec3(0.0, 0.5 * (halfH + 2.0), 0.0), 0.5 * (halfH - 2.0), hubR);
    d = smin(d, hub, blend);

    // bolted foot flange
    float yb = p.y + halfH;
    vec2 fq = vec2(abs(length(p.xz) - 0.5 * (Rbot + flangeIn)) - 0.5 * (Rbot - flangeIn), abs(yb - flangeT * 0.5) - flangeT * 0.5);
    float flange = min(max(fq.x, fq.y), 0.0) + length(max(fq, 0.0));
    d = smin(d, flange, blend);

    d = max(d, abs(p.y) - halfH);                       // flat top and underside
    d = max(d, -hex2(p.xz, hexF));
    // five bolts, irregular pitch
    float bolts = 1e5;
    for (int i = 0; i < 5; i++) {
        float a = 6.2831853 * (float(i) + 0.13 * sin(float(i) * 2.7)) / 5.0;
        vec2 c = (Rbot - 3.5) * vec2(cos(a), sin(a));
        bolts = min(bolts, length(p.xz - c) - boltR);
    }
    d = max(d, -max(bolts, p.y + halfH - flangeT - 1.0));
    return d;
}
