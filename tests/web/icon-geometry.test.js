// Значок собирается из scripts/icon/geometry.mjs. Ломается он молча: сместился
// на пиксель — на 256 не заметит никто, а на 16 знак поедет. Здесь заперты
// свойства, ради которых геометрия и задана таблицей.
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');

// Генератор значка написан модулями ES, а тесты здесь — CommonJS.
// Подгружаем его один раз перед прогоном.
let geometry, ICO_SIZES, COLORS, ico, raster, svg;
test.before(async () => {
  ({ geometry, ICO_SIZES, COLORS } = await import('../../scripts/icon/geometry.mjs'));
  ({ ico, raster, svg } = await import('../../scripts/icon/render.mjs'));
});

const whole = (v) => Number.isInteger(v);

test('на каждом размере все координаты целые', () => {
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    for (const r of [g.plate, g.inner, ...g.bars]) {
      for (const k of ['x', 'y', 'w', 'h', 'r']) {
        assert.ok(whole(r[k]), `${size}: ${k}=${r[k]} не целое`);
      }
    }
  }
});

test('знак сидит в рамке с равными полями со всех сторон', () => {
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const last = g.bars[g.bars.length - 1];
    const left = g.bars[0].x - g.inner.x;
    const right = g.inner.x + g.inner.w - (g.bars[0].x + g.bars[0].w);
    const top = g.bars[0].y - g.inner.y;
    const bottom = g.inner.y + g.inner.h - (last.y + last.h);
    assert.equal(left, right, `${size}: поля по бокам ${left} и ${right}`);
    assert.equal(top, bottom, `${size}: поля сверху и снизу ${top} и ${bottom}`);
  }
});

test('поле вокруг знака держится около десятой доли холста', () => {
  // За счёт этого поля знак сидит в рамке, а не упирается в неё. Оно уже
  // дважды уезжало молча: сперва просело на 24 и 32 px, потом — когда подбор
  // стал гнаться за толщиной полос — расползлось от 3 % до 8 %.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const share = (g.bars[0].x - g.inner.x) / size;
    assert.ok(share >= 0.085 && share <= 0.14, `${size}: поле ${(share * 100).toFixed(1)} % вне вилки`);
  }
});

test('просветы внутри знака уже полей вокруг него', () => {
  // Ровно это и делает три полосы одной группой, а не тремя отдельными
  // предметами: своё от чужого отделяет расстояние. Сравняются — знак
  // рассыплется на три несвязанных прямоугольника.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const gap = g.bars[1].y - (g.bars[0].y + g.bars[0].h);
    const margin = g.bars[0].y - g.inner.y;
    assert.ok(gap <= margin, `${size}: просвет ${gap} не меньше поля ${margin} — группа рассыпается`);
  }
});

test('полосы не тощие и не распирающие', () => {
  // Ошибиться можно в обе стороны, и обе уже случались. Тонкие полосы делают
  // значок тусклым; толстые — упираются в обводку и распирают рамку. Держим
  // вилку, а не порог.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const last = g.bars[g.bars.length - 1];
    const bar = g.bars[0].h / size;
    const block = (last.y + last.h - g.bars[0].y) / size;
    assert.ok(bar >= 0.12 && bar <= 0.26, `${size}: полоса ${(bar * 100).toFixed(0)} % холста вне вилки`);
    assert.ok(block >= 0.48 && block <= 0.7, `${size}: знак ${(block * 100).toFixed(0)} % холста вне вилки`);
  }
});

test('полосы не слипаются друг с другом', () => {
  // Просвет между полосами — не меньше четверти их толщины. Иначе лесенка
  // читается одним пятном, особенно на мелком размере.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const gap = g.bars[1].y - (g.bars[0].y + g.bars[0].h);
    assert.ok(gap >= 1, `${size}: полосы сомкнулись`);
    assert.ok(gap / g.bars[0].h >= 0.22, `${size}: просвет ${gap} при полосе ${g.bars[0].h} — тесно`);
  }
});

test('между знаком и обводкой всегда остаётся плашка', () => {
  // Знак и обводка контрастируют друг с другом всего на 2,85 — стыкаться им
  // нельзя, иначе край знака сливается с краем значка.
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    assert.ok(g.clear >= 1, `${size}: просвет ${g.clear}`);
    for (const b of g.bars) {
      assert.ok(b.x >= g.inner.x + 1, `${size}: полоса прилипла к обводке слева`);
      assert.ok(b.x + b.w <= g.inner.x + g.inner.w - 1, `${size}: и справа`);
    }
  }
});

test('обводка относительно толще на мелких размерах', () => {
  // Обводка несущая: на тёмном фоне силуэт значка даёт только она. Поэтому
  // при уменьшении её доля растёт — до 16 px, где упирается в пиксельный пол
  // и тоньше уже некуда.
  const sizes = ICO_SIZES.filter((s) => s > 16);
  const share = sizes.map((s) => geometry(s).ring / s);
  assert.equal(geometry(16).ring, 1, '16: обводка обязана быть ровно в пиксель');
  for (const [i, s] of sizes.entries()) {
    assert.ok(geometry(s).ring >= 1, `${s}: обводка исчезла`);
    if (i > 0) assert.ok(share[i] <= share[i - 1] + 1e-9, `${s}: обводка потолстела относительно`);
  }
});

test('лесенка полос сужается сверху вниз', () => {
  for (const size of ICO_SIZES) {
    const w = geometry(size).bars.map((b) => b.w);
    for (let i = 1; i < w.length; i++) {
      assert.ok(w[i] < w[i - 1], `${size}: ширины ${w.join('/')} не убывают`);
    }
  }
});

test('на 16 px полос две, дальше три', () => {
  assert.equal(geometry(16).bars.length, 2);
  for (const s of ICO_SIZES.filter((s) => s > 16)) {
    assert.equal(geometry(s).bars.length, 3, `${s}`);
  }
});

test('растр несёт все три плоскости значка', () => {
  const hex = (px, i) =>
    '#' + [0, 1, 2].map((k) => px[i + k].toString(16).padStart(2, '0')).join('');
  for (const size of ICO_SIZES) {
    const g = geometry(size);
    const px = raster(size);
    const at = (x, y) => hex(px, (y * size + x) * 4);
    const mid = Math.floor(size / 2);
    assert.equal(at(g.plate.x, mid), COLORS.ring, `${size}: обводка`);
    assert.equal(at(g.inner.x, mid), COLORS.plate, `${size}: плашка`);
    const b = g.bars[0];
    // Проба берётся из середины полосы: углы у неё скруглены и сглажены.
    assert.equal(at(b.x + (b.w >> 1), b.y + (b.h >> 1)), COLORS.mark, `${size}: знак`);
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
