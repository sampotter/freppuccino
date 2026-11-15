// Sectioned nautilus: a log-spiral tube (r = a e^(b theta)) whose whorls embrace, hollowed per whorl so the
// earlier whorls show inside the later ones, with curved septa dividing the chambers. Growth ribs ripple the
// wall. A sharp section plane exposes the interior; it sits on an exact chamfered plinth with a locating pin.
param float a = 1.2;         // spiral scale
param float W = 3.0;         // whorl expansion per revolution
param float c = 0.56;        // tube radius / spiral radius (> 0.5: whorls overlap)
param float thetaMax = 15.2; // aperture angle (rad)
param float th = 0.45;       // wall half thickness
param float septa = 13.0;    // septa per revolution
param float septTh = 0.3;
param float bend = 0.9;      // septum curvature
param float ribs = 34.0;     // growth ribs per revolution
param float ribA = 0.012;    // rib amplitude (x tube radius)
param float plinthY = -20.0;

const float TAU = 6.2831853;

float sdf(vec3 p) {
    float b = log(W) / TAU;
    vec2 xy = p.xy - vec2(4.0, 1.0);
    float rho = length(xy);
    float phi = atan(xy.y, xy.x);
    float d = 1e5;
    for (int k = 0; k < 4; k++) {
        float theta = phi + TAU * float(k);
        if (theta > thetaMax + 1.0) continue;
        float r = a * exp(b * theta);
        float tr = c * r * (1.0 + ribA * sin(ribs * theta));
        vec2 e = vec2(rho - r, p.z * 1.12);
        float tube = (length(e) - tr) / 1.12;
        float wall = abs(tube) - th;
        // septa: thin curved discs across the tube, behind the living chamber
        float s = theta + bend * dot(e, e) / (r * r);
        float per = TAU / septa;
        float sp = abs((fract(s / per + 0.5) - 0.5) * per) * rho - septTh;
        sp = max(sp, tube);
        sp = max(sp, theta - (thetaMax - 1.4));
        wall = min(wall, sp);
        // aperture: tube ends at thetaMax (sharp)
        wall = max(wall, (theta - thetaMax) * r * 0.6);
        d = min(d, wall);
    }
    d = max(d, p.z);                              // section plane (sharp)
    // exact chamfered plinth + pin
    vec3 q = p - vec3(2.0, plinthY, -6.0);
    float pl = sdBox(q, vec3(16.0, 3.0, 10.0));
    pl = max(pl, (abs(q.x) + q.y - 16.0 - 3.0 + 1.5) * 0.7071);
    pl = max(pl, (abs(q.z) + q.y - 10.0 - 3.0 + 1.5) * 0.7071);
    float pin = sdCappedCylinder(q - vec3(0.0, 6.0, 0.0), 4.0, 1.6);
    return min(d, min(pl, pin));
}
