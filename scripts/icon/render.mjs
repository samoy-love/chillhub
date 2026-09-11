// Растеризация значка и упаковка в PNG/ICO. Без внешних зависимостей: каждая
// форма знака — плашка, кольцо, точка — задана аналитически, и покрытие пикселя
// считается подвыборкой. Так края сглажены честно, а не фильтром поверх растра.
import zlib from 'node:zlib';
import { geometry, COLORS } from './geometry.mjs';

const SUB = 8; // подвыборка на пиксель по каждой оси

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const TOP = rgb(COLORS.top);
const BOTTOM = rgb(COLORS.bottom);
const MARK = rgb(COLORS.mark);

function inRoundRect(x, y, R) {
  if (x < R.x || y < R.y || x > R.x + R.w || y > R.y + R.h) return false;
  const cx = Math.min(Math.max(x, R.x + R.r), R.x + R.w - R.r);
  const cy = Math.min(Math.max(y, R.y + R.r), R.y + R.h - R.r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= R.r * R.r;
}

// Точка на знаке: внутри одного из прямоугольников букв.
export function onMark(x, y, g) {
  return g.blocks.some((b) => x >= b.x && x < b.x + b.w && y >= b.y && y < b.y + b.h);
}

// Цвет плашки в точке: диагональный градиент, как linearGradient 0,0 → 1,1 в SVG.
export function plateColor(x, y, g) {
  const P = g.plate;
  const t = Math.min(1, Math.max(0, ((x - P.x) / P.w + (y - P.y) / P.h) / 2));
  return TOP.map((v, i) => v + (BOTTOM[i] - v) * t);
}

export function raster(size) {
  const g = geometry(size);
  const buf = new Uint8Array(size * size * 4);
  const n = SUB * SUB;
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let r = 0;
      let gr = 0;
      let b = 0;
      let hit = 0;
      for (let sy = 0; sy < SUB; sy++) {
        const y = py + (sy + 0.5) / SUB;
        for (let sx = 0; sx < SUB; sx++) {
          const x = px + (sx + 0.5) / SUB;
          if (!inRoundRect(x, y, g.plate)) continue;
          const c = onMark(x, y, g) ? MARK : plateColor(x, y, g);
          r += c[0];
          gr += c[1];
          b += c[2];
          hit++;
        }
      }
      if (!hit) continue;
      const i = (py * size + px) * 4;
      buf[i] = Math.round(r / hit);
      buf[i + 1] = Math.round(gr / hit);
      buf[i + 2] = Math.round(b / hit);
      buf[i + 3] = Math.round((hit / n) * 255);
    }
  }
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

export function svg(size = 32, { title = null } = {}) {
  const g = geometry(size);
  const P = g.plate;
  // Буквы — одним путём из прямоугольников: так в файле нет швов между
  // соседними блоками, которые браузер иначе сглаживает полупрозрачной линией.
  const d = g.blocks.map((b) => `M${b.x} ${b.y}h${b.w}v${b.h}h${-b.w}z`).join('');
  const body = [
    '<defs><linearGradient id="chillhub-plate" x1="0" y1="0" x2="1" y2="1">' +
      `<stop offset="0" stop-color="${COLORS.top}"/><stop offset="1" stop-color="${COLORS.bottom}"/>` +
      '</linearGradient></defs>',
    `<rect x="${P.x}" y="${P.y}" width="${P.w}" height="${P.h}" rx="${P.r}" fill="url(#chillhub-plate)"/>`,
    `<path d="${d}" fill="${COLORS.mark}"/>`,
  ];
  return `<?xml version="1.0" encoding="UTF-8"?>
<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" xmlns="http://www.w3.org/2000/svg"${title ? ' role="img"' : ''}>
${title ? `  <title>${title}</title>\n` : ''}  ${body.join('\n  ')}
</svg>
`;
}
