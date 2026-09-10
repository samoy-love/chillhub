// Значок собирается из scripts/icon/geometry.mjs. Ломается он молча: сместился
// на полпикселя — на 256 не заметит никто, а на 16 буква C расплывётся в пятно.
// Здесь заперты свойства, ради которых геометрия и задана таблицей.
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');

// Генератор значка написан модулями ES, а тесты здесь — CommonJS.
// Подгружаем его один раз перед прогоном.
let geometry, caps, ICO_SIZES, COLORS, ico, raster, svg;
test.before(async () => {
  ({ geometry, caps, ICO_SIZES, COLORS } = await import('../../scripts/icon/geometry.mjs'));
  ({ ico, raster, svg } = await import('../../scripts/icon/render.mjs'));
});

const whole = (v) => Number.isInteger(v);

// Контраст по WCAG — тот же счёт, что в тестах темы лаунчера.
function lum(hex) {
  const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}
const contrast = (a, b) => {
  const x = lum(a);
  const y = lum(b);
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
};
const hexMix = (a, b, t) =>
  '#' + [1, 3, 5].map((i) => {
    const va = parseInt(a.slice(i, i + 2), 16);
    const vb = parseInt(b.slice(i, i + 2), 16);
    return Math.round(va + (vb - va) * t).toString(16).padStart(2, '0');
  }).join('');

test('края кольца, точки и плашки лежат на сетке пикселей', () => {
  // Крайние точки кольца слева, сверху и снизу и края точки по горизонтали —
  // именно по ним глаз ловит резкость на 16 px.
  for (const size of ICO_SIZES) {
    const { plate, arc, dot } = geometry(size);
    for (const k of ['x', 'y', 'w', 'h', 'r']) assert.ok(whole(plate[k]), `${size}: плашка ${k}=${plate[k]}`);
    assert.ok(whole(arc.cx - arc.ro) && whole(arc.cy - arc.ro) && whole(arc.cy + arc.ro), `${size}: край кольца`);
    assert.ok(whole(arc.cx - arc.ri), `${size}: внутренний край кольца`);
    assert.ok(whole(dot.cx - dot.r) && whole(dot.cx + dot.r), `${size}: края точки`);
  }
});

test('знак стоит по центру плашки', () => {
  // Точка лежит на средней окружности кольца, поэтому знак вместе с ней
  // занимает ровно круг радиуса ro — и этот круг обязан быть по центру.
  for (const size of ICO_SIZES) {
    const { plate, arc, dot } = geometry(size);
    const left = arc.cx - arc.ro - plate.x;
    const right = plate.x + plate.w - (dot.cx + dot.r);
    const top = arc.cy - arc.ro - plate.y;
    const bottom = plate.y + plate.h - (arc.cy + arc.ro);
    assert.equal(left, right, `${size}: поля по бокам ${left} и ${right}`);
    assert.equal(top, bottom, `${size}: поля сверху и снизу ${top} и ${bottom}`);
  }
});

test('пропорции знака одинаковы на всех размерах', () => {
  // Таблица задаёт размеры пикселями, и знак легко «поплывёт» — на одном
  // размере жирная C, на соседнем тощая. Держим вилку, а не точное число.
  for (const size of ICO_SIZES) {
    const { arc } = geometry(size);
    const ro = arc.ro / size;
    const sw = arc.sw / size;
    assert.ok(ro >= 0.27 && ro <= 0.32, `${size}: кольцо ${(ro * 100).toFixed(1)} % холста`);
    assert.ok(sw >= 0.11 && sw <= 0.13, `${size}: толщина ${(sw * 100).toFixed(1)} % холста`);
  }
});

test('буква не закрывается и не прилипает к краю плашки', () => {
  for (const size of ICO_SIZES) {
    const { plate, arc } = geometry(size);
    // Внутренний просвет C: схлопнется — буква станет кругляшом.
    assert.ok(arc.ri >= 3, `${size}: просвет внутри C всего ${arc.ri}`);
    assert.ok(arc.ri >= arc.sw, `${size}: просвет ${arc.ri} уже толщины ${arc.sw}`);
    // Поле вокруг знака: без него знак упирается в скругление плашки.
    const margin = arc.cx - arc.ro - plate.x;
    assert.ok(margin >= Math.max(2, size * 0.1), `${size}: поле ${margin}`);
  }
});

test('точка не сливается с концами буквы', () => {
  // Слипнутся — и C читается с хвостом, а не с точкой. Просвет не меньше
  // полутора пикселей на мелких размерах и растёт вместе с холстом.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    for (const c of caps(g)) {
      const gap = Math.hypot(c.x - g.dot.cx, c.y - g.dot.cy) - g.arc.sw / 2 - g.dot.r;
      assert.ok(gap >= Math.max(1.5, size * 0.05) - 1e-9, `${size}: просвет ${gap.toFixed(2)}`);
    }
    // И разрыв не распахнут: C остаётся C, а не скобкой.
    assert.ok(g.arc.phi < Math.PI / 3, `${size}: разрыв ${((g.arc.phi * 360) / Math.PI).toFixed(0)}°`);
  }
});

test('знак и плашка различимы на любом фоне', () => {
  // Белый знак на обоих концах градиента и плашка на тёмной панели задач и
  // на светлой вкладке браузера. Кантом значок больше не держится — только
  // цветом, поэтому пороги здесь несущие.
  const middle = hexMix(COLORS.top, COLORS.bottom, 0.5);
  assert.ok(contrast(COLORS.mark, COLORS.bottom) >= 4.5, 'знак на нижнем краю градиента');
  assert.ok(contrast(COLORS.mark, COLORS.top) >= 3, 'знак на верхнем краю градиента');
  assert.ok(contrast(middle, '#202020') >= 3, 'плашка на тёмной панели задач');
  assert.ok(contrast(middle, '#f3f3f3') >= 3, 'плашка на светлой вкладке');
});

test('растр несёт плашку, букву и точку', () => {
  const at = (px, size, x, y) => {
    const i = (y * size + x) * 4;
    return [px[i], px[i + 1], px[i + 2], px[i + 3]];
  };
  const white = ([r, g, b, a]) => r === 255 && g === 255 && b === 255 && a === 255;
  // Точка в два пикселя на 16 px не бывает чисто белой: круг покрывает каждый
  // из четырёх её пикселей на 78 %. Поэтому для неё — «светлая», а не «белая».
  const light = ([r, g, b, a]) => r >= 200 && g >= 200 && b >= 200 && a === 255;
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const px = raster(size);
    const { cx, cy, ri, sw } = g.arc;
    // Середина левой дуги, центр точки — белые; центр буквы — плашка.
    assert.ok(white(at(px, size, Math.floor(cx - ri - sw / 2), Math.floor(cy))), `${size}: буква`);
    assert.ok(light(at(px, size, Math.floor(g.dot.cx), Math.floor(g.dot.cy))), `${size}: точка`);
    const hole = at(px, size, Math.floor(cx), Math.floor(cy));
    assert.ok(!white(hole) && hole[3] === 255, `${size}: внутри буквы должна быть плашка`);
    // Угол холста за скруглением — прозрачный.
    assert.equal(at(px, size, 0, 0)[3], 0, `${size}: угол не прозрачный`);
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
