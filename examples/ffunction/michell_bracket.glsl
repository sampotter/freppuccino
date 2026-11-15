// Michell cantilever: struts follow the two orthogonal log-spiral families (principal stress lines
// of a point load carried to a circular support). Struts taper toward the tip and fuse with fillets;
// wall plate, hub and pin eye are machined flat with exact bolt / pin holes.
param float R0 = 7.0;        // Michell support circle radius
param float span = 1.45;      // fan half-angle (rad); tip at R0 * exp(span)
param float cells = 3.0;     // spiral struts per family across the fan
param float rRoot = 2.7;     // strut radius at the support
param float rTip = 1.0;      // strut radius at the tip
param float rNet = 0.6;      // interior strut radius factor
param float chordTop = 1.25; // tension chord radius factor
param float chordBot = 1.35; // compression chord radius factor (buckling)
param float blend = 2.6;     // fillet radius
param float droop = -0.22;   // fan rotation (rad): load hangs low
param float plateT = 5.0;    // wall plate thickness
param float width = 6.0;     // machined half width (z)
param float eyeR = 5.2;
param float pinR = 2.3;
param float boltR = 2.0;
param float cham = 0.8;      // hole chamfer

const float SQ2 = 1.41421356;

float wrapd(float a, float per) { return (fract(a / per + 0.5) - 0.5) * per; }
float tube(float d2, float z, float r) { return length(vec2(d2, z)) - r; }
mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }

// chamfered through-hole along z (radius r, faces at |z| = w)
float hole(vec2 xy, float z, float r, float w) {
    return (length(xy) - r - max(0.0, abs(z) - (w - cham))) * 0.7071;
}

float sdf(vec3 p) {
    vec2 c = vec2(-17.0, 6.0);                 // support centre
    vec2 q = rot(-droop) * (p.xy - c);         // fan frame: +x toward the load
    float rho = max(length(q), 1e-3);
    float phi = atan(q.y, q.x);
    float u = log(rho / R0);
    float per = span / cells;
    float t = clamp(u / span, 0.0, 1.0);
    float r = mix(rRoot, rTip, pow(t, 0.75));
    float s = rho / SQ2;                       // log-polar -> model units (conformal)

    // interior spiral nets, masked to the fan
    float na = tube(abs(wrapd(u + phi, per)) * s, p.z, r * rNet);
    float nb = tube(abs(wrapd(u - phi, per)) * s, p.z, r * rNet);
    float reg = max(u + abs(phi) - span, -u) * s;
    float net = smax(min(na, nb), reg, 0.6);

    // boundary chords (thicker), trimmed to the fan length
    float ct = tube(abs(u + phi - span) * s, p.z, r * chordTop);
    float cb = tube(abs(u - phi - span) * s, p.z, r * chordBot);
    float ch = max(min(ct, cb), max(u - span, -0.15 - u) * rho);
    float lat = smin(net, ch, blend);

    // hub disc on the support circle
    float hub = sdCappedCylinder(vec3(q.x, p.z, q.y), width, R0 + 0.5);
    // pin eye at the tip
    vec2 e = q - vec2(R0 * exp(span), 0.0);
    float eye = sdCappedCylinder(vec3(e.x, p.z, e.y), width - 1.5, eyeR);
    // wall plate
    float px = c.x - R0 - 1.0;
    float plate = sdBox(p - vec3(px - plateT * 0.5, 0.0, 0.0), vec3(plateT * 0.5, 24.0, width)) - 0.6;

    float d = smin(lat, hub, blend);
    d = smin(d, eye, blend * 0.8);
    d = smin(d, plate, blend * 1.3);

    // machining: flat back + side faces, exact holes
    d = max(d, (px - plateT) - p.x);
    d = max(d, abs(p.z) - width);
    d = max(d, -hole(e, p.z, pinR, width - 1.5));
    vec3 b = p - vec3(px - plateT * 0.5, 0.0, 0.0);
    float bolts = min(length(vec2(b.y - 17.0, b.z)), length(vec2(b.y + 17.0, b.z))) - boltR;
    float cbore = max(min(length(vec2(b.y - 17.0, b.z)), length(vec2(b.y + 17.0, b.z))) - boltR * 1.8, -(b.x - plateT * 0.5 + 1.5));
    d = max(d, -min(bolts, cbore));
    float bore = length(q) - R0 * 0.45;        // lightening bore through the hub
    d = max(d, -bore);
    return d;
}
