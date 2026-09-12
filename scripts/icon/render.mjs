// Растеризация значка и упаковка в PNG/ICO/SVG. Без внешних зависимостей:
// каждая фигура — скруглённый прямоугольник, круг или эллипс с заливкой
// (сплошной, линейной или радиальной), и цвет каждой подвыборки пикселя
// считается аналитически. Края сглажены честно, а не фильтром поверх растра.
//
// Растр и SVG рисуют один и тот же список фигур из geometry.mjs, поэтому
// картинка во вкладке и в exe одна — с точностью до сглаживания браузера.
import zlib from 'node:zlib';
import { geometry } from './geometry.mjs';

const SUB = 8; // подвыборка на пиксель по каждой оси

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));

/* ---------- фигуры ---------- */

function bbox(S) {
  if (S.kind === 'rrect') return { x: S.x, y: S.y, w: S.w, h: S.h };
  if (S.kind === 'circle') return { x: S.cx - S.r, y: S.cy - S.r, w: 2 * S.r, h: 2 * S.r };
  return { x: S.cx - S.rx, y: S.cy - S.ry, w: 2 * S.rx, h: 2 * S.ry };
}

function inside(x, y, S) {
  if (S.kind === 'rrect') {
    if (x < S.x || y < S.y || x > S.x + S.w || y > S.y + S.h) return false;
    if (!S.r) return true;
    const cx = Math.min(Math.max(x, S.x + S.r), S.x + S.w - S.r);
    const cy = Math.min(Math.max(y, S.y + S.r), S.y + S.h - S.r);
    return (x - cx) ** 2 + (y - cy) ** 2 <= S.r * S.r;
  }
  if (S.kind === 'circle') return (x - S.cx) ** 2 + (y - S.cy) ** 2 <= S.r * S.r;
  const dx = (x - S.cx) / S.rx;
  const dy = (y - S.cy) / S.ry;
  return dx * dx + dy * dy <= 1;
}

/* ---------- заливки ---------- */

// Заливки подготавливаются один раз на фигуру: цвета остановок в числах и
// рамка фигуры, в долях которой заданы координаты градиента.
function prepare(S) {
  const F = S.fill;
  const B = bbox(S);
  if (F.type === 'solid') {
    const c = rgb(F.color);
    return () => [c[0], c[1], c[2], F.alpha];
  }
  const stops = F.stops.map(([t, color, alpha]) => [t, ...rgb(color), alpha]);
  const at = (t) => {
    t = Math.min(1, Math.max(0, t));
    let i = 1;
    while (i < stops.length - 1 && stops[i][0] < t) i++;
    const a = stops[i - 1];
    const b = stops[i];
    const u = b[0] === a[0] ? 0 : (t - a[0]) / (b[0] - a[0]);
    return [a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u, a[3] + (b[3] - a[3]) * u, a[4] + (b[4] - a[4]) * u];
  };
  if (F.type === 'linear') {
    // Как linearGradient в objectBoundingBox: проекция точки на отрезок x1y1→x2y2.
    const dx = F.x2 - F.x1;
    const dy = F.y2 - F.y1;
    const dd = dx * dx + dy * dy;
    return (x, y) => {
      const u = (x - B.x) / B.w - F.x1;
      const v = (y - B.y) / B.h - F.y1;
      return at((u * dx + v * dy) / dd);
    };
  }
  // radialGradient: расстояние до центра в долях радиуса.
  return (x, y) => {
    const u = (x - B.x) / B.w - F.cx;
    const v = (y - B.y) / B.h - F.cy;
    return at(Math.sqrt(u * u + v * v) / F.r);
  };
}

/* ---------- растр ---------- */

const cache = new Map();

export function raster(size, opts = {}) {
  const key = `${size}${opts.avatar ? ':avatar' : ''}`;
  if (cache.has(key)) return cache.get(key);
  const g = geometry(size, opts);
  const layers = g.shapes.map((S) => ({ S, B: bbox(S), color: prepare(S) }));
  const buf = new Uint8Array(size * size * 4);
  const n = SUB * SUB;
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      // Фигуры, не касающиеся пикселя, отсеиваются по рамке один раз на пиксель.
      const hit = layers.filter(({ B }) => px + 1 > B.x && px < B.x + B.w && py + 1 > B.y && py < B.y + B.h);
      if (!hit.length) continue;
      let r = 0;
      let gr = 0;
      let b = 0;
      let a = 0;
      for (let sy = 0; sy < SUB; sy++) {
        const y = py + (sy + 0.5) / SUB;
        for (let sx = 0; sx < SUB; sx++) {
          const x = px + (sx + 0.5) / SUB;
          // Обычное наложение «поверх» слой за слоем, premultiplied.
          let pr = 0;
          let pg = 0;
          let pb = 0;
          let pa = 0;
          for (const L of hit) {
            if (!inside(x, y, L.S)) continue;
            const [cr, cg, cb, ca] = L.color(x, y);
            pr = cr * ca + pr * (1 - ca);
            pg = cg * ca + pg * (1 - ca);
            pb = cb * ca + pb * (1 - ca);
            pa = ca + pa * (1 - ca);
          }
          r += pr;
          gr += pg;
          b += pb;
          a += pa;
        }
      }
      if (a <= 0) continue;
      const i = (py * size + px) * 4;
      buf[i] = Math.round(r / a);
      buf[i + 1] = Math.round(gr / a);
      buf[i + 2] = Math.round(b / a);
      buf[i + 3] = Math.round((a / n) * 255);
    }
  }
  cache.set(key, buf);
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

export function png(size, opts = {}) {
  const px = raster(size, opts);
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0; // фильтр None: deflate и так сжимает градиенты
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

const num = (v) => String(Math.round(v * 1000) / 1000);

function svgFill(id, F) {
  if (F.type === 'solid') return { attr: `fill="${F.color}"${F.alpha < 1 ? ` fill-opacity="${num(F.alpha)}"` : ''}`, def: '' };
  const stops = F.stops
    .map(([t, c, a]) => `<stop offset="${num(t)}" stop-color="${c}"${a < 1 ? ` stop-opacity="${num(a)}"` : ''}/>`)
    .join('');
  const def =
    F.type === 'linear'
      ? `<linearGradient id="${id}" x1="${num(F.x1)}" y1="${num(F.y1)}" x2="${num(F.x2)}" y2="${num(F.y2)}">${stops}</linearGradient>`
      : `<radialGradient id="${id}" cx="${num(F.cx)}" cy="${num(F.cy)}" r="${num(F.r)}">${stops}</radialGradient>`;
  return { attr: `fill="url(#${id})"`, def };
}

function svgShape(S, attr) {
  if (S.kind === 'rrect') {
    return `<rect x="${num(S.x)}" y="${num(S.y)}" width="${num(S.w)}" height="${num(S.h)}"${S.r ? ` rx="${num(S.r)}"` : ''} ${attr}/>`;
  }
  if (S.kind === 'circle') return `<circle cx="${num(S.cx)}" cy="${num(S.cy)}" r="${num(S.r)}" ${attr}/>`;
  return `<ellipse cx="${num(S.cx)}" cy="${num(S.cy)}" rx="${num(S.rx)}" ry="${num(S.ry)}" ${attr}/>`;
}

export function svg(size = 32, { title = null } = {}) {
  const g = geometry(size);
  const defs = [];
  const body = [];
  for (const S of g.shapes) {
    const { attr, def } = svgFill(`chillhub-${S.name}`, S.fill);
    if (def) defs.push(def);
    body.push(svgShape(S, attr));
  }
  // Плоская версия обязана лечь на пиксели и в браузере — без сглаживания краёв.
  const crisp = g.flat ? ' shape-rendering="crispEdges"' : '';
  return `<?xml version="1.0" encoding="UTF-8"?>
<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" xmlns="http://www.w3.org/2000/svg"${title ? ' role="img"' : ''}${crisp}>
${title ? `  <title>${title}</title>\n` : ''}  <defs>${defs.join('')}</defs>
  ${body.join('\n  ')}
</svg>
`;
}
