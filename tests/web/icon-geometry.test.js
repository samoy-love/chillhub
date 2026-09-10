// Значок собирается из scripts/icon/geometry.mjs. Ломается он молча: сместился
// на пиксель — на 256 не заметит никто, а на 16 геймпад поедет. Здесь заперты
// свойства, ради которых геометрия и задана таблицей.
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');

// Генератор значка написан модулями ES, а тесты здесь — CommonJS.
// Подгружаем его один раз перед прогоном.
let geometry, ICO_SIZES, COLORS, framed, ico, raster, svg;
test.before(async () => {
  ({ geometry, ICO_SIZES, COLORS, framed } = await import('../../scripts/icon/geometry.mjs'));
  ({ ico, raster, svg } = await import('../../scripts/icon/render.mjs'));
});

const whole = (v) => Number.isInteger(v);

// Точка внутри скруглённого прямоугольника — тот же счёт, что у растеризатора.
function inside(x, y, R) {
  if (x < R.x || y < R.y || x > R.x + R.w || y > R.y + R.h) return false;
  const r = R.r || 0;
  if (r <= 0) return true;
  const cx = Math.min(Math.max(x, R.x + r), R.x + R.w - r);
  const cy = Math.min(Math.max(y, R.y + r), R.y + R.h - r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}

const overlap = (a, b, gap = 0) =>
  a.x < b.x + b.w + gap && b.x < a.x + a.w + gap && a.y < b.y + b.h + gap && b.y < a.y + a.h + gap;

test('на каждом размере все координаты целые', () => {
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    for (const r of [g.plate, g.inner, g.body, ...g.holes]) {
      for (const k of ['x', 'y', 'w', 'h', 'r']) {
        assert.ok(whole(r[k]), `${size}: ${k}=${r[k]} не целое`);
      }
    }
  }
});

test('корпус сидит по центру плашки с равными полями', () => {
  for (const size of ICO_SIZES) {
    const { inner, body } = geometry(size);
    const left = body.x - inner.x;
    const right = inner.x + inner.w - (body.x + body.w);
    const top = body.y - inner.y;
    const bottom = inner.y + inner.h - (body.y + body.h);
    assert.equal(left, right, `${size}: поля по бокам ${left} и ${right}`);
    assert.equal(top, bottom, `${size}: поля сверху и снизу ${top} и ${bottom}`);
  }
});

test('между корпусом и кантом всегда остаётся плашка', () => {
  // Кант и корпус одного цвета. Сомкнутся — геймпад прилипнет к краю значка
  // и перестанет быть отдельной формой.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    assert.ok(g.clear >= 1, `${size}: просвет ${g.clear}`);
    assert.ok(g.body.x >= g.inner.x + 1 && g.body.x + g.body.w <= g.inner.x + g.inner.w - 1, `${size}: по бокам`);
    assert.ok(g.body.y >= g.inner.y + 1 && g.body.y + g.body.h <= g.inner.y + g.inner.h - 1, `${size}: сверху или снизу`);
  }
});

test('корпус широкий и низкий — силуэт контроллера', () => {
  // Уже — и он читается таблеткой, выше — кнопкой. Держим вилку, а не порог.
  for (const size of ICO_SIZES) {
    const { body } = geometry(size);
    const w = body.w / size;
    const h = body.h / size;
    assert.ok(w >= 0.6 && w <= 0.78, `${size}: ширина ${(w * 100).toFixed(0)} % холста вне вилки`);
    assert.ok(h >= 0.3 && h <= 0.44, `${size}: высота ${(h * 100).toFixed(0)} % холста вне вилки`);
    assert.ok(body.w / body.h >= 1.6, `${size}: корпус ${body.w}×${body.h} слишком квадратный`);
    assert.equal(body.r, Math.floor(body.h / 2), `${size}: торцы корпуса не полукруглые`);
  }
});

test('крестовина и кнопки не прорезают край корпуса', () => {
  // Вырез, дошедший до края, выгрызает кусок силуэта: геймпад на 16 px
  // превращается в подкову. Вокруг каждого выреза — пиксель корпуса.
  for (const size of ICO_SIZES) {
    const { body, holes } = geometry(size);
    for (const h of holes) {
      assert.ok(framed(h, body), `${size}: вырез ${h.x},${h.y} ${h.w}×${h.h} у края корпуса`);
      // И независимо от framed: вырез целиком лежит внутри корпуса.
      assert.ok(inside(h.x + h.w / 2, h.y + h.h / 2, body), `${size}: вырез вне корпуса`);
    }
  }
});

test('крестовина — крест, а не квадрат', () => {
  // Две планки одной толщины крест-накрест, с общим центром. Толщина меньше
  // размаха: иначе крест заливается в квадрат и геймпад теряет лицо.
  for (const size of ICO_SIZES) {
    const [v, hz] = geometry(size).holes;
    assert.equal(v.w, hz.h, `${size}: планки разной толщины`);
    assert.equal(v.h, hz.w, `${size}: планки разного размаха`);
    assert.equal(v.x + v.w / 2, hz.x + hz.w / 2, `${size}: центры разъехались по горизонтали`);
    assert.equal(v.y + v.h / 2, hz.y + hz.h / 2, `${size}: центры разъехались по вертикали`);
    assert.ok(v.w < v.h, `${size}: толщина ${v.w} при размахе ${v.h} — квадрат`);
  }
});

test('кнопки не слипаются друг с другом и с крестовиной', () => {
  for (const size of ICO_SIZES) {
    const holes = geometry(size).holes;
    const cross = holes.slice(0, 2);
    const buttons = holes.slice(2);
    for (const b of buttons) {
      for (const c of cross) assert.ok(!overlap(b, c, 1), `${size}: кнопка прилипла к крестовине`);
    }
    if (buttons.length === 2) assert.ok(!overlap(buttons[0], buttons[1], 1), `${size}: кнопки слиплись`);
  }
});

test('на 16 px кнопка одна, дальше две', () => {
  // Две точки по пикселю на 16 сливаются в полоску; одна остаётся точкой.
  assert.equal(geometry(16).holes.length, 3);
  for (const s of ICO_SIZES.filter((s) => s > 16)) {
    assert.equal(geometry(s).holes.length, 4, `${s}`);
  }
});

test('кант относительно толще на мелких размерах', () => {
  // Кант несущий: на тёмном фоне силуэт значка даёт только он. Поэтому при
  // уменьшении его доля растёт — до 16 px, где упирается в пиксельный пол и
  // тоньше уже некуда.
  const sizes = ICO_SIZES.filter((s) => s > 16);
  const share = sizes.map((s) => geometry(s).ring / s);
  assert.equal(geometry(16).ring, 1, '16: кант обязан быть ровно в пиксель');
  for (const [i, s] of sizes.entries()) {
    assert.ok(geometry(s).ring >= 1, `${s}: кант исчез`);
    if (i > 0) assert.ok(share[i] <= share[i - 1] + 1e-9, `${s}: кант потолстел относительно`);
  }
});

test('растр несёт кант, плашку, корпус и вырезы', () => {
  const hex = (px, i) =>
    '#' + [0, 1, 2].map((k) => px[i + k].toString(16).padStart(2, '0')).join('');
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const px = raster(size);
    const at = (x, y) => hex(px, (y * size + x) * 4);
    const mid = Math.floor(size / 2);
    assert.equal(at(g.plate.x, mid), COLORS.ring, `${size}: кант`);
    assert.equal(at(g.inner.x, mid), COLORS.plate, `${size}: плашка`);
    // Корпус пробуется у верхнего края посередине: там прямой край, а не
    // скруглённый торец, и ни одного выреза.
    const [v] = g.holes;
    assert.equal(at(g.body.x + (g.body.w >> 1), g.body.y), COLORS.mark, `${size}: корпус`);
    assert.equal(at(v.x, v.y + (v.h >> 1)), COLORS.plate, `${size}: крестовина`);
  }
});

test('ico содержит все размеры и каждая запись указывает внутрь файла', () => {
  const buf = ico(ICO_SIZES);
  assert.equal(buf.readUInt16LE(0), 0);
  assert.equal(buf.readUInt16LE(2), 1);
  assert.equal(buf.readUInt16LE(4), ICO_SIZES.length);
  for (const [i, size] of ICO_SIZES.entries()) {
    const e = 6 + i * 16;
    assert.equal(buf[e] || 256, size);
    const len = buf.readUInt32LE(e + 8);
    const off = buf.readUInt32LE(e + 12);
    assert.ok(len > 0 && off + len <= buf.length, `${size}: запись выходит за файл`);
  }
});

test('значок в репозитории совпадает с тем, что даёт генератор', () => {
  // Файлы не правятся руками: правится geometry.mjs и запускается build.mjs.
  // Значок один на весь проект — лаунчер, установщик, админка, сайт, — и
  // разойтись хоть в одном месте он не должен.
  const full = ico(ICO_SIZES);
  const web = ico([16, 24, 32, 48]);
  const mark = svg(32, { title: 'Chill Hub' });
  const cases = [
    ['launcher/ChillHub/Assets/app.ico', full],
    ['scripts/app.ico', full],
    ['server/admin_ui/app.ico', full],
    ['landing/favicon.ico', web],
    ['landing/assets/icons/app.ico', web],
    ['server/admin_ui/favicon.svg', Buffer.from(mark, 'utf8')],
    ['landing/favicon.svg', Buffer.from(mark, 'utf8')],
    ['landing/assets/icons/logo.svg', Buffer.from(mark, 'utf8')],
  ];
  for (const [p, expected] of cases) {
    const file = readFileSync(resolve(__dirname, '../..', p));
    assert.ok(expected.equals(file), `${p} разошёлся с генератором`);
  }
});
