// Gooey tet mesh: hash-jittered seed per grid cell, struts along the 7 forward Kuhn edges (a Delaunay-like
// tetrahedral split), diagonals randomly pruned, all smooth-unioned so nodes swell into blobs.
// Toward the top: more struts survive pruning, struts thin, cells squash slightly. Trimmed flat by a chamfered prism; fused to a machined foot.
param float cell = 12.0;     // cell size at the base
param float grade = 0.45;    // extra cell density at the top (0 = uniform; squashes cells in y)
param float jitter = 0.2;    // seed jitter (fraction of a cell, < 0.5)
param float seed = 7.0;
param float r0 = 1.35;       // strut radius at the base
param float r1 = 0.6;        // strut radius at the top
param float blob = 0.8;      // node swelling (x strut radius)
param float kf = 1.8;        // blend radius (x strut radius)
param float pruneTop = 0.35; // fraction of diagonals dropped at the top
param float pruneBase = 0.85;// ... at the base
param float pruneAxis = 0.2; // fraction of axis struts dropped
param float halfX = 19.0;
param float halfY = 23.0;    // envelope half height
param float halfZ = 14.0;
param float cham = 5.0;      // envelope chamfer
param float footH = 3.0;
param float holeR = 2.2;

vec3 hash33(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}
vec3 site(vec3 c) { return c + 0.5 + jitter * (2.0 * hash33(c + seed * 17.13) - 1.0); }
float seg(vec3 p, vec3 a, vec3 b) {
    vec3 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h);
}

// lattice in "cell space" q (units of cells), returns distance in cells minus radius rq
// prune rate is a function of the strut's own height (q0..q1 = envelope bottom..top in cell units)
float lattice(vec3 q, float rq, float kq, float q0, float q1) {
    vec3 c = floor(q);
    float d = 1e5;
    for (int i = 0; i < 8; i++) {
        vec3 a = c - 1.0 + vec3(float(i % 2), float((i / 2) % 2), float(i / 4));
        vec3 sa = site(a);
        d = smin(d, length(q - sa) - rq * (1.0 + blob), kq);
        for (int j = 1; j < 8; j++) {
            vec3 o = vec3(float(j % 2), float((j / 2) % 2), float(j / 4));
            vec3 sb = site(a + o);
            float prune = mix(pruneBase, pruneTop, clamp((0.5 * (sa.y + sb.y) - q0) / (q1 - q0), 0.0, 1.0));
            float drop = (j == 1 || j == 2 || j == 4) ? pruneAxis : prune;
            if (hash33(a * 1.71 + o * 13.3 + seed).x < drop) continue;
            d = smin(d, seg(q, sa, sb) - rq, kq);
        }
    }
    // struts starting outside the 2x2x2 block are at least this far (conservative bound)
    // (seeds of cells c+1 are >= 1.5 - jitter - f away, seeds of c-1 that only link backward >= 0.5 - jitter + f;
    // continuous across cell faces, so no seams)
    vec3 f = q - c;
    vec3 m = min(1.5 - jitter - f, 0.5 - jitter + f);
    float far = min(min(m.x, m.y), m.z) - rq * (1.0 + blob);
    return min(d, far);
}

float sdf(vec3 p) {
    float t = clamp((p.y + halfY) / (2.0 * halfY), 0.0, 1.0);
    float rho = 1.0 + grade * t;                       // local density (cells per base cell)
    float H = halfY;
    float Fy = p.y + grade * (p.y + H) * (p.y + H) / (4.0 * H);
    vec3 q = vec3(p.x, Fy, p.z) / cell;
    float scale = cell / rho;                          // cell units -> model units (approx)
    float r = mix(r0, r1, t);
    float d = lattice(q, r / scale, kf * r / scale, -halfY / cell, (halfY + grade * halfY) / cell) * scale;

    // chamfered prism envelope, sharp trim
    vec3 a = abs(p);
    float env = max(max(a.x - halfX, a.z - halfZ), abs(p.y) - halfY);
    env = max(env, (a.x + a.z - (halfX + halfZ - cham)) * 0.7071);
    env = max(env, (a.x + p.y - (halfX + halfY - cham)) * 0.7071);
    env = max(env, (p.y + a.z - (halfY + halfZ - cham)) * 0.7071);
    d = max(d, env);

    // machined foot plate with exact holes; struts fuse into it
    float foot = sdBox(p - vec3(0.0, -halfY - footH * 0.5, 0.0), vec3(halfX + 3.0, footH * 0.5, halfZ + 3.0));
    d = smin(d, foot, 3.0);
    d = max(d, -halfY - footH - p.y);                     // flat underside
    d = max(d, max(a.x - halfX - 3.0, a.z - halfZ - 3.0));   // flat foot sides
    vec2 hp = vec2(a.x - (halfX - 1.0), a.z - (halfZ - 1.0));
    d = max(d, -(length(hp) - holeR));
    return d;
}
