// Vertical 2-colour gradient PNG (the brand viewport background), lightly dithered so H.264 doesn't band it.
// Usage: node bg.mjs <width> <height> <#top> <#bottom> <out.png>
//   Latte:    #fff8ef -> #ead8c3      Espresso: #2a1e19 -> #160f0d
import { writeFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';

const [W, H] = [Number(process.argv[2]), Number(process.argv[3])];
const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const top = hex(process.argv[4]), bot = hex(process.argv[5]), out = process.argv[6];
let seed = 12345;
const rnd = () => ((seed = (seed * 1103515245 + 12345) >>> 0) / 4294967296);
const raw = Buffer.alloc((W * 3 + 1) * H);
for (let y = 0; y < H; y++) {
  const t = y / (H - 1), row = y * (W * 3 + 1);
  for (let x = 0; x < W; x++) {
    const d = rnd() - 0.5; // +-0.5 LSB, same for all channels so it stays neutral
    for (let c = 0; c < 3; c++) raw[row + 1 + x * 3 + c] = Math.max(0, Math.min(255, Math.round(top[c] + (bot[c] - top[c]) * t + d)));
  }
}
const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = buf => { let c = 0xffffffff; for (const x of buf) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const cr = Buffer.alloc(4); cr.writeUInt32BE(crc(td)); return Buffer.concat([len, td, cr]); };
const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 2;
writeFileSync(out, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]));
console.log('wrote', out);
