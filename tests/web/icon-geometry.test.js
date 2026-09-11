// Значок собирается из scripts/icon/geometry.mjs. Ломается он молча: край буквы
// съехал на полпикселя — на 256 не заметит никто, а на 16 буквы размоются в
// серое пятно. Здесь заперты свойства, ради которых геометрия и собрана из блоков.
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');

// Генератор значка написан модулями ES, а тесты здесь — CommonJS.
// Подгружаем его один раз перед прогоном.
let geometry, ICO_SIZES, COLORS, ico, raster, svg, adminIconVersion;
test.before(async () => {
  ({ geometry, ICO_SIZES, COLORS } = await import('../../scripts/icon/geometry.mjs'));
  ({ ico, raster, svg } = await import('../../scripts/icon/render.mjs'));
  ({ adminIconVersion } = await import('../../scripts/icon/build.mjs'));
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

test('все края букв и плашки лежат на сетке пикселей', () => {
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    for (const k of ['x', 'y', 'w', 'h', 'r']) assert.ok(whole(g.plate[k]), `${size}: плашка ${k}=${g.plate[k]}`);
    for (const b of g.blocks) {
      for (const k of ['x', 'y', 'w', 'h']) assert.ok(whole(b[k]), `${size}: блок ${k}=${b[k]}`);
      assert.ok(b.w >= 1 && b.h >= 1, `${size}: пустой блок`);
    }
  }
});

test('буквы стоят по центру плашки — с точностью до пикселя', () => {
  // Полупиксель размыл бы края, поэтому нечётный остаток уходит вправо и вниз.
  // Больше пикселя разницы — уже перекос, который видно.
  for (const size of ICO_SIZES) {
    const { box } = geometry(size);
    const left = box.x;
    const right = size - (box.x + box.w);
    const top = box.y;
    const bottom = size - (box.y + box.h);
    assert.ok(right - left >= 0 && right - left <= 1, `${size}: поля по бокам ${left} и ${right}`);
    assert.ok(bottom - top >= 0 && bottom - top <= 1, `${size}: поля сверху и снизу ${top} и ${bottom}`);
  }
});

test('у C есть просвет, у H — щель между стойками, между буквами — промежуток', () => {
  // Схлопнутся — и вместо CH на 16 px выходят два сплошных прямоугольника.
  for (const size of ICO_SIZES) {
    const { t, letterC, letterH } = geometry(size);
    const [top, back] = letterC;
    const [left, right] = letterH;
    assert.ok(top.w - back.w >= 2, `${size}: просвет C всего ${top.w - back.w}`);
    assert.ok(right.x - (left.x + left.w) >= 1, `${size}: стойки H слиплись`);
    assert.ok(left.x - (top.x + top.w) >= 1, `${size}: C и H слиплись`);
    assert.ok(back.h - 2 * t >= t, `${size}: внутренний проём C ниже толщины штриха`);
  }
});

test('пропорции знака одинаковы на всех размерах', () => {
  // Каждая величина округляется отдельно, и знак легко «поплывёт»: на одном
  // размере жирные буквы, на соседнем тощие. Держим вилку, а не точное число.
  for (const size of ICO_SIZES) {
    const { t, box } = geometry(size);
    const share = (v) => v / size;
    assert.ok(share(box.w) >= 0.55 && share(box.w) <= 0.7, `${size}: ширина ${(share(box.w) * 100).toFixed(0)} %`);
    assert.ok(share(box.h) >= 0.4 && share(box.h) <= 0.5, `${size}: высота ${(share(box.h) * 100).toFixed(0)} %`);
    if (size >= 24) assert.ok(share(t) >= 0.1 && share(t) <= 0.13, `${size}: штрих ${(share(t) * 100).toFixed(1)} %`);
  }
});

test('перекладина H — посередине высоты', () => {
  for (const size of ICO_SIZES) {
    const { box, letterH } = geometry(size);
    const bar = letterH[2];
    const above = bar.y - box.y;
    const below = box.y + box.h - (bar.y + bar.h);
    assert.ok(Math.abs(above - below) <= 1, `${size}: над перекладиной ${above}, под ней ${below}`);
  }
});

test('знак и плашка различимы на любом фоне', () => {
  // Кантом значок не держится — только цветом, поэтому пороги здесь несущие.
  const middle = hexMix(COLORS.top, COLORS.bottom, 0.5);
  assert.ok(contrast(COLORS.mark, COLORS.bottom) >= 4.5, 'знак на нижнем краю градиента');
  assert.ok(contrast(COLORS.mark, COLORS.top) >= 3, 'знак на верхнем краю градиента');
  assert.ok(contrast(middle, '#202020') >= 3, 'плашка на тёмной панели задач');
  assert.ok(contrast(middle, '#f3f3f3') >= 3, 'плашка на светлой вкладке');
});

test('растр несёт плашку и чисто белые буквы', () => {
  // Буквы на сетке пикселей обязаны получаться ЧИСТО белыми: серый пиксель
  // внутри штриха значит, что край съехал с сетки и сглаживание размыло букву.
  const at = (px, size, x, y) => {
    const i = (y * size + x) * 4;
    return [px[i], px[i + 1], px[i + 2], px[i + 3]];
  };
  const white = ([r, g, b, a]) => r === 255 && g === 255 && b === 255 && a === 255;
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const px = raster(size);
    for (const b of g.blocks) {
      for (const [x, y] of [[b.x, b.y], [b.x + b.w - 1, b.y + b.h - 1]]) {
        assert.ok(white(at(px, size, x, y)), `${size}: угол блока ${x},${y} не белый`);
      }
    }
    // Внутри C — плашка, угол холста за скруглением — прозрачный.
    const [top, back] = g.letterC;
    const inside = at(px, size, back.x + back.w, top.y + top.h);
    assert.ok(!white(inside) && inside[3] === 255, `${size}: внутри C должна быть плашка`);
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

test('админка ссылается на значок с версией текущего значка', () => {
  // Браузер держит значок вкладки в своём отдельном кэше дольше любых
  // заголовков: пока адрес тот же, он показывает старую картинку. Версия в
  // адресе — хеш самого значка, её проставляет build.mjs.
  const v = adminIconVersion();
  for (const page of ['server/admin_ui/index.html', 'server/admin_ui/login.html']) {
    const html = readFileSync(resolve(__dirname, '../..', page), 'utf8');
    const refs = [...html.matchAll(/\/admin\/ui\/(?:favicon\.svg|app\.ico)(\?v=[a-f0-9]+)?/g)];
    assert.ok(refs.length > 0, `${page}: нет ссылки на значок`);
    for (const m of refs) assert.equal(m[1], `?v=${v}`, `${page}: ${m[0]} — не текущая версия значка`);
  }
});
