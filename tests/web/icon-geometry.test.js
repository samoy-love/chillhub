// Значок собирается из scripts/icon/geometry.mjs. Ломается он молча: шар
// съехал за круглую маску — на 256 не заметит никто, а на аватарке его
// обрежет; цвет плашки подобрали к кнопке — и в шапке лаунчера значок стал
// «ещё одной кнопкой». Здесь заперты свойства, ради которых знак такой.
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
const named = (g, name) => g.shapes.find((s) => s.name === name);

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

const at = (px, size, x, y) => {
  const i = (y * size + x) * 4;
  return [px[i], px[i + 1], px[i + 2], px[i + 3]];
};
const same = (p, hex) => {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return p[0] === r && p[1] === g && p[2] === b && p[3] === 255;
};

test('16 px — плоская версия: все края на сетке, плашка сплошная', () => {
  // На 16 px градиенты и эллипсы дают серую кашу, поэтому там три сплошных
  // цвета и только прямоугольники на целых пикселях.
  const g = geometry(16);
  assert.ok(g.flat);
  for (const S of g.shapes) {
    assert.equal(S.kind, 'rrect', `16: фигура ${S.name} не прямоугольник`);
    assert.equal(S.fill.type, 'solid', `16: фигура ${S.name} с градиентом`);
    for (const k of ['x', 'y', 'w', 'h', 'r']) assert.ok(whole(S[k]), `16: ${S.name} ${k}=${S[k]}`);
  }
  for (const size of ICO_SIZES.filter((s) => s > 16)) assert.ok(!geometry(size).flat, `${size}: плоская версия не для этого размера`);
});

test('16 px — растр даёт чистые цвета деталей', () => {
  // Серый пиксель внутри шара значит, что край съехал с сетки и сглаживание
  // размыло деталь.
  const px = raster(16);
  const { ball, stem, base } = geometry(16).blocks;
  const check = (list, color, name) => {
    for (const b of list) {
      for (const [x, y] of [[b.x, b.y], [b.x + b.w - 1, b.y + b.h - 1]]) {
        assert.ok(same(at(px, 16, x, y), color), `16: угол ${name} ${x},${y} не ${color}`);
      }
    }
  };
  check(ball, COLORS.flatBall, 'шара');
  check(stem, COLORS.flatStem, 'ножки');
  check(base, COLORS.flatBase, 'основания');
  assert.ok(same(at(px, 16, 2, 9), COLORS.flatPlate), '16: плашка рядом с ножкой не сплошная');
  assert.equal(at(px, 16, 0, 0)[3], 0, '16: угол за скруглением не прозрачный');
});

test('16 px — между деталями есть просвет, а ножка не шире шара', () => {
  const { ball, stem, base } = geometry(16).blocks;
  const bottomOf = (list) => Math.max(...list.map((b) => b.y + b.h));
  const topOf = (list) => Math.min(...list.map((b) => b.y));
  const widthOf = (list) => Math.max(...list.map((b) => b.x + b.w)) - Math.min(...list.map((b) => b.x));
  assert.ok(topOf(stem) >= bottomOf(ball), 'ножка врезается в шар');
  assert.ok(topOf(base) >= bottomOf(stem), 'основание врезается в ножку');
  assert.ok(widthOf(stem) < widthOf(ball) && widthOf(ball) < widthOf(base), 'ножка уже шара, шар уже основания');
  // Стик по центру плашки: поля сверху и снизу равны.
  assert.equal(topOf(ball), 16 - bottomOf(base), 'стик не по центру по вертикали');
});

test('плашка на сетке пикселей, знак — по центру и внутри круглой маски', () => {
  // Круглая аватарка (Discord, Steam) режет по вписанному кругу: шар и
  // основание обязаны целиком лежать внутри него на любом размере.
  for (const size of ICO_SIZES.filter((s) => s > 16)) {
    const g = geometry(size);
    for (const k of ['x', 'y', 'w', 'h', 'r']) assert.ok(whole(g.plate[k]), `${size}: плашка ${k}=${g.plate[k]}`);
    const c = size / 2;
    const R = g.plate.w / 2;
    const ball = named(g, 'ball');
    const base = named(g, 'base');
    const stem = named(g, 'stem');
    assert.ok(Math.abs(ball.cx - c) < 1e-9 && Math.abs(stem.x + stem.w / 2 - c) < 1e-9, `${size}: шар и ножка не по оси`);
    assert.ok(Math.abs(base.x + base.w / 2 - c) < 1e-9, `${size}: основание не по оси`);
    assert.ok(Math.hypot(ball.cx - c, ball.cy - c) + ball.r <= R, `${size}: шар вылезает за круг`);
    for (const [x, y] of [[base.x, base.y + base.h], [base.x + base.w, base.y + base.h]]) {
      assert.ok(Math.hypot(x - c, y - c) <= R, `${size}: угол основания ${x},${y} вылезает за круг`);
    }
  }
});

test('пропорции стика одинаковы на всех размерах', () => {
  for (const size of ICO_SIZES.filter((s) => s > 16)) {
    const g = geometry(size);
    const share = (v) => v / size;
    const ball = named(g, 'ball');
    const base = named(g, 'base');
    const stem = named(g, 'stem');
    assert.ok(share(ball.r * 2) >= 0.35 && share(ball.r * 2) <= 0.4, `${size}: шар ${(share(ball.r * 2) * 100).toFixed(0)} %`);
    assert.ok(share(base.w) >= 0.53 && share(base.w) <= 0.6, `${size}: основание ${(share(base.w) * 100).toFixed(0)} %`);
    assert.ok(share(stem.w) >= 0.11 && share(stem.w) <= 0.14, `${size}: ножка ${(share(stem.w) * 100).toFixed(1)} %`);
  }
});

test('знак и плашка различимы на любом фоне', () => {
  // Кантом значок не держится — только цветом, поэтому пороги здесь несущие.
  const plate = hexMix(COLORS.plateTop, COLORS.plateBottom, 0.5);
  assert.ok(contrast(COLORS.ball, plate) >= 4.5, 'шар на плашке');
  assert.ok(contrast(COLORS.steel, plate) >= 3, 'ножка на плашке');
  assert.ok(contrast(COLORS.baseBottom, plate) >= 3, 'основание на плашке');
  // На тёмных панелях плашка растворяется, силуэт держит сам предмет.
  for (const [name, c] of [['шар', COLORS.ball], ['ножка', COLORS.steel], ['основание', COLORS.baseBottom]]) {
    assert.ok(contrast(c, '#1b1b24') >= 3, `${name} на тёмной панели`);
  }
  // На светлой вкладке силуэт держит плашка.
  assert.ok(contrast(COLORS.plateTop, '#f3f3f3') >= 3, 'плашка на светлой вкладке');
  // Плоская версия — те же требования.
  for (const [name, c] of [['шар', COLORS.flatBall], ['ножка', COLORS.flatStem], ['основание', COLORS.flatBase]]) {
    assert.ok(contrast(c, COLORS.flatPlate) >= 3, `16: ${name} на плашке`);
  }
});

test('ни один цвет знака не совпадает с акцентом кнопок лаунчера', () => {
  // В шапке значок стоит рядом с кнопкой Steam цвета #7c5cff: знак того же
  // цвета читался как ещё один элемент управления.
  const accent = '#7c5cff';
  for (const [name, c] of Object.entries(COLORS)) {
    assert.ok(contrast(c, accent) >= 1.25, `${name} = ${c} неотличим от акцента кнопок`);
  }
});

test('растр: шар сверху, плашка под ним, угол прозрачный', () => {
  for (const size of ICO_SIZES.filter((s) => s > 16)) {
    const px = raster(size);
    const g = geometry(size);
    const ball = named(g, 'ball');
    const center = at(px, size, Math.round(ball.cx), Math.round(ball.cy));
    assert.ok(center[0] > 200 && center[0] > center[2] + 60, `${size}: центр шара не тёплый (${center})`);
    assert.equal(center[3], 255, `${size}: шар просвечивает`);
    // У левого края на середине высоты — плашка без скругления и без деталей.
    const edge = at(px, size, g.plate.x + 1, Math.floor(size / 2));
    assert.equal(edge[3], 255, `${size}: плашка у края не сплошная`);
    assert.ok(edge[2] > edge[0] && edge[2] > edge[1], `${size}: плашка у края не индиго (${edge})`);
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

test('svg повторяет тот же список фигур с теми же заливками', () => {
  const s = svg(32, { title: 'Chill Hub' });
  assert.match(s, /<title>Chill Hub<\/title>/);
  for (const S of geometry(32).shapes) {
    if (S.fill.type === 'solid') continue;
    assert.ok(s.includes(`id="chillhub-${S.name}"`), `svg: нет градиента ${S.name}`);
    assert.ok(s.includes(`fill="url(#chillhub-${S.name})"`), `svg: фигура ${S.name} без своей заливки`);
  }
  assert.ok(s.includes(`stop-color="${COLORS.ball}"`) && s.includes(`stop-color="${COLORS.plateTop}"`), 'svg: цвета не из палитры');
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
