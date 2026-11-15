// Mock-up reference frame (no Rhino needed): raymarches a scene with the viewport shader's own shading,
// color * mix(0.78, 1, n.z*0.5+0.5), z up, IQ smin with colour blended by the smin weight; no shadows or AO.
// Good for "what good looks like" previews and framing tests. Usage: node mock-frame.mjs <hook|peak> <out.png>
import { writeFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';

const which = process.argv[2], out = process.argv[3];
const W = 1600, H = 900, SS = 2;
const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255);
const mix = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

let dist, color, cam, bgTop, bgBot;
if (which === 'hook') {
  const espresso = hex('#3b2620'), caramel = hex('#e0954a'), k = 0.55;
  const ab = (x, y, z) => [Math.hypot(x + 0.62, y, z) - 0.72, Math.hypot(x - 0.58, y - 0.1, z - 0.12) - 0.6];
  dist = (x, y, z) => { const [a, b] = ab(x, y, z); const h = Math.max(k - Math.abs(a - b), 0) / k; return Math.min(a, b) - h * h * k * 0.25; };
  color = (x, y, z) => { const [a, b] = ab(x, y, z); return mix(caramel, espresso, clamp(0.5 + 0.5 * (b - a) / k, 0, 1)); };
  cam = { pos: [-1.6, -4.6, 1.5], target: [-0.75, 0, -0.05], fov: 30 };
  bgTop = hex('#fff8ef'); bgBot = hex('#ead8c3');
} else {
  const col = hex('#e0954a'), cell = 0.72, om = 2 * Math.PI / cell, t = 0.075;
  const level = om * 1.528 * t / 2, inv = 1 / (om * Math.sqrt(3));
  dist = (x, y, z) => {
    const s = Math.hypot(x, y, z) - 1.0;
    const X = om * x, Y = om * y, Z = om * z;
    const g = Math.sin(X) * Math.cos(Y) + Math.sin(Y) * Math.cos(Z) + Math.sin(Z) * Math.cos(X);
    return Math.max(s, (Math.abs(g) - level) * inv);
  };
  color = () => col;
  cam = { pos: [-2.9, -4.3, 1.9], target: [-1.05, 0, -0.05], fov: 36 };
  bgTop = hex('#2a1e19'); bgBot = hex('#160f0d');
}

const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const norm = a => { const l = Math.hypot(a[0], a[1], a[2]); return [a[0] / l, a[1] / l, a[2] / l]; };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const fw = norm(sub(cam.target, cam.pos)), rt = norm(cross(fw, [0, 0, 1])), up = cross(rt, fw);
const th = Math.tan(cam.fov * Math.PI / 360);

// Bounding sphere (radius 2 around origin) to skip empty rays quickly.
const raw = Buffer.alloc((W * 3 + 1) * H);
for (let j = 0; j < H; j++) {
  const bg = mix(bgTop, bgBot, j / (H - 1));
  raw[j * (W * 3 + 1)] = 0;
  for (let i = 0; i < W; i++) {
    let r = 0, g = 0, b = 0;
    for (let sj = 0; sj < SS; sj++) for (let si = 0; si < SS; si++) {
      const u = ((i + (si + 0.5) / SS) / W * 2 - 1) * th * W / H;
      const v = (1 - (j + (sj + 0.5) / SS) / H * 2) * th;
      const d = norm([fw[0] + rt[0] * u + up[0] * v, fw[1] + rt[1] * u + up[1] * v, fw[2] + rt[2] * u + up[2] * v]);
      // ray vs bounding sphere r=1.8
      const oc = cam.pos, bq = oc[0] * d[0] + oc[1] * d[1] + oc[2] * d[2];
      const cq = oc[0] * oc[0] + oc[1] * oc[1] + oc[2] * oc[2] - 1.8 * 1.8, disc = bq * bq - cq;
      let c = bg;
      if (disc > 0) {
        let tt = -bq - Math.sqrt(disc); const tEnd = -bq + Math.sqrt(disc); let hit = false;
        for (let s = 0; s < 400 && tt < tEnd; s++) {
          const dd = dist(oc[0] + d[0] * tt, oc[1] + d[1] * tt, oc[2] + d[2] * tt);
          if (dd < 0.0004 * tt) { hit = true; break; }
          tt += dd * 0.95;
        }
        if (hit) {
          const px = oc[0] + d[0] * tt, py = oc[1] + d[1] * tt, pz = oc[2] + d[2] * tt, e = 0.0008;
          const n = norm([dist(px + e, py, pz) - dist(px - e, py, pz), dist(px, py + e, pz) - dist(px, py - e, pz), dist(px, py, pz + e) - dist(px, py, pz - e)]);
          const shade = 0.78 + 0.22 * (n[2] * 0.5 + 0.5), cc = color(px, py, pz);
          c = [cc[0] * shade, cc[1] * shade, cc[2] * shade];
        }
      }
      r += c[0]; g += c[1]; b += c[2];
    }
    const o = j * (W * 3 + 1) + 1 + i * 3, n = SS * SS;
    raw[o] = Math.round(r / n * 255); raw[o + 1] = Math.round(g / n * 255); raw[o + 2] = Math.round(b / n * 255);
  }
}

// Minimal PNG encoder.
const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = buf => { let c = 0xffffffff; for (const x of buf) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const cr = Buffer.alloc(4); cr.writeUInt32BE(crc(td)); return Buffer.concat([len, td, cr]); };
const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 2;
writeFileSync(out, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]));
console.log('wrote', out);
