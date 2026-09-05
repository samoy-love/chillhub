// Растеризация значка и упаковка в PNG/ICO. Без внешних зависимостей:
// значок состоит из скруглённых прямоугольников, их площадь считается точно.
import zlib from 'node:zlib';
import { geometry, COLORS } from './geometry.mjs';

const SUB = 8; // подвыборка на пиксель по каждой оси

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));

function inside(x, y, R) {
  if (x < R.x || y < R.y || x > R.x + R.w || y > R.y + R.h) return false;
  const r = R.r || 0;
  if (r <= 0) return true;
  const cx = Math.min(Math.max(x, R.x + r), R.x + R.w - r);
  const cy = Math.min(Math.max(y, R.y + r), R.y + R.h - r);
  const dx = x - cx;
  const dy = y - cy;
  return dx * dx + dy * dy <= r * r;
}

function coverage(px, py, R) {
  // Целые края попадают ровно на границу пикселя, подвыборка их не портит.
  let hit = 0;
  for (let sy = 0; sy < SUB; sy++) {
    const y = py + (sy + 0.5) / SUB;
    for (let sx = 0; sx < SUB; sx++) {
      if (inside(px + (sx + 0.5) / SUB, y, R)) hit++;
    }
  }
  return hit / (SUB * SUB);
}

function paint(buf, size, R, color) {
  const [cr, cg, cb] = rgb(color);
  const x0 = Math.max(0, Math.floor(R.x));
  const y0 = Math.max(0, Math.floor(R.y));
  const x1 = Math.min(size, Math.ceil(R.x + R.w));
  const y1 = Math.min(size, Math.ceil(R.y + R.h));
  for (let y = y0; y < y1; y++) {
    for (let x = x0; x < x1; x++) {
      const a = coverage(x, y, R);
      if (a <= 0) continue;
      const i = (y * size + x) * 4;
      const da = buf[i + 3] / 255;
      const oa = a + da * (1 - a);
      for (let c = 0; c < 3; c++) {
        const src = [cr, cg, cb][c];
        buf[i + c] = Math.round((src * a + buf[i + c] * da * (1 - a)) / oa);
      }
      buf[i + 3] = Math.round(oa * 255);
    }
  }
}

export function raster(size) {
  const g = geometry(size);
  const buf = new Uint8Array(size * size * 4);
  paint(buf, size, g.plate, COLORS.ring); // внешний контур — цвет обводки
  paint(buf, size, g.inner, COLORS.plate); // плашка вырезает из него кольцо
  for (const b of g.bars) paint(buf, size, b, COLORS.mark);
  return buf;
}

/* ---------- PNG ---------- */

const CRC = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(b) {
  let c = -1;
  for (let i = 0; i < b.length; i++) c = CRC[(c ^ b[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

export function png(size) {
  const px = raster(size);
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0; // фильтр None: картинка плоская, предсказание не помогает
    Buffer.from(px.buffer, y * size * 4, size * 4).copy(raw, y * (size * 4 + 1) + 1);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/* ---------- ICO ---------- */

function bmp32(size) {
  const px = raster(size);
  const head = Buffer.alloc(40);
  head.writeUInt32LE(40, 0);
  head.writeInt32LE(size, 4);
  head.writeInt32LE(size * 2, 8); // XOR и AND вместе
  head.writeUInt16LE(1, 12);
  head.writeUInt16LE(32, 14);
  head.writeUInt32LE(size * size * 4, 20);

  const xor = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const s = ((size - 1 - y) * size + x) * 4; // BMP снизу вверх
      const d = (y * size + x) * 4;
      xor[d] = px[s + 2];
      xor[d + 1] = px[s + 1];
      xor[d + 2] = px[s];
      xor[d + 3] = px[s + 3];
    }
  }
  // Маска нулевая: прозрачность несёт альфа-канал, но Windows требует её наличия.
  const stride = Math.ceil(size / 32) * 4;
  return Buffer.concat([head, xor, Buffer.alloc(stride * size)]);
}

export function ico(sizes) {
  const images = sizes.map((s) => (s >= 256 ? png(s) : bmp32(s)));
  const head = Buffer.alloc(6);
  head.writeUInt16LE(0, 0);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(sizes.length, 4);
  let offset = 6 + sizes.length * 16;
  const dir = sizes.map((s, i) => {
    const e = Buffer.alloc(16);
    e[0] = s >= 256 ? 0 : s;
    e[1] = s >= 256 ? 0 : s;
    e.writeUInt16LE(1, 4);
    e.writeUInt16LE(32, 6);
    e.writeUInt32LE(images[i].length, 8);
    e.writeUInt32LE(offset, 12);
    offset += images[i].length;
    return e;
  });
  return Buffer.concat([head, ...dir, ...images]);
}

/* ---------- SVG ---------- */

const rr = (R, fill) =>
  `<rect x="${R.x}" y="${R.y}" width="${R.w}" height="${R.h}"${R.r ? ` rx="${R.r}"` : ''} fill="${fill}"/>`;

export function svg(size = 32, { title = null } = {}) {
  const g = geometry(size);
  const body = [
    rr(g.plate, COLORS.ring),
    rr(g.inner, COLORS.plate),
    ...g.bars.map((b) => rr(b, COLORS.mark)),
  ];
  return `<?xml version="1.0" encoding="UTF-8"?>
<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" xmlns="http://www.w3.org/2000/svg"${title ? ' role="img"' : ''}>
${title ? `  <title>${title}</title>\n` : ''}  ${body.join('\n  ')}
</svg>
`;
}
