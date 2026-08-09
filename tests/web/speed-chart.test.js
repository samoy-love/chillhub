// Тесты server/admin_ui/speed-chart.js — самодостаточного (без uPlot/CDN)
// графика скорости загрузки. Появился взамен uPlot-варианта, который молча
// не рисовал ничего, если window.uPlot не определён (CDN недоступен/заблокирован
// у части пользователей) — см. комментарий в шапке speed-chart.js.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { mapPointsToPixels, drawSpeedChart, formatAge } = require(path.join('..', '..', 'server', 'admin_ui', 'speed-chart.js'));

test('mapPointsToPixels принимает раздельные отступы по сторонам', () => {
  const now = 0;
  const points = [{ t: 0, bps: 5 }];
  const px = mapPointsToPixels(points, { width: 100, height: 100, padding: { left: 50, right: 0, top: 0, bottom: 0 }, horizonMs: 1000, now, maxBps: 10 });
  // Точка "сейчас" должна быть у правого края (width), независимо от левого отступа.
  assert.ok(px[0].x > 90, 'левый отступ не должен сдвигать точку "сейчас": x=' + px[0].x);
});

test('formatAge форматирует секунды и минуты', () => {
  assert.strictEqual(formatAge(5000), '5с');
  assert.strictEqual(formatAge(59000), '59с');
  assert.strictEqual(formatAge(60000), '1м');
  assert.strictEqual(formatAge(90000), '1м 30с');
  assert.strictEqual(formatAge(120000), '2м');
});

test('mapPointsToPixels отбрасывает точки старше horizonMs', () => {
  const now = 100000;
  const points = [
    { t: now - 200000, bps: 999 }, // старше горизонта — не должно попасть
    { t: now - 1000, bps: 10 },
    { t: now, bps: 20 },
  ];
  const px = mapPointsToPixels(points, { width: 200, height: 100, padding: 0, horizonMs: 120000, now });
  assert.strictEqual(px.length, 2);
});

test('mapPointsToPixels: самая свежая точка справа, самая старая слева', () => {
  const now = 60000;
  const points = [
    { t: now - 60000, bps: 5 }, // ровно на границе горизонта
    { t: now, bps: 5 },
  ];
  const px = mapPointsToPixels(points, { width: 200, height: 100, padding: 0, horizonMs: 60000, now });
  assert.strictEqual(px.length, 2);
  assert.ok(px[0].x < px[1].x, 'старая точка должна быть левее свежей: ' + JSON.stringify(px));
});

test('mapPointsToPixels: у точки с максимальной скоростью y наверху (минимальный y)', () => {
  const now = 1000;
  const points = [
    { t: now, bps: 10 },
    { t: now, bps: 100 },
  ];
  const px = mapPointsToPixels(points, { width: 200, height: 100, padding: 0, horizonMs: 120000, now });
  const low = px.find(p => p.bps === 10);
  const high = px.find(p => p.bps === 100);
  assert.ok(high.y < low.y, 'более быстрая точка должна рисоваться выше: ' + JSON.stringify(px));
});

test('mapPointsToPixels: пустой список точек не падает и возвращает пустой массив', () => {
  assert.deepStrictEqual(mapPointsToPixels([], { width: 200, height: 100, padding: 4, horizonMs: 1000, now: 0 }), []);
});

test('mapPointsToPixels: maxBps задаёт масштаб явно, даже если точки его не достигают', () => {
  const now = 0;
  const points = [{ t: 0, bps: 10 }];
  const px = mapPointsToPixels(points, { width: 100, height: 100, padding: 0, horizonMs: 1000, now, maxBps: 1000 });
  // При масштабе 0..1000 точка со скоростью 10 должна быть почти у нижнего края (y ~ height).
  assert.ok(px[0].y > 90, 'точка должна быть почти внизу при большом maxBps: y=' + px[0].y);
});

// fakeCanvas2D имитирует ровно те методы CanvasRenderingContext2D, которые
// использует drawSpeedChart — этого достаточно, чтобы убедиться, что функция
// вообще не падает на реальном наборе вызовов канвы и рисует линию только
// когда точек хватает.
function fakeCanvas(width, height) {
  const calls = [];
  const ctx = {
    setTransform: (...a) => calls.push(['setTransform', ...a]),
    clearRect: (...a) => calls.push(['clearRect', ...a]),
    fillRect: (...a) => calls.push(['fillRect', ...a]),
    beginPath: () => calls.push(['beginPath']),
    moveTo: (...a) => calls.push(['moveTo', ...a]),
    lineTo: (...a) => calls.push(['lineTo', ...a]),
    stroke: () => calls.push(['stroke']),
    fillText: (...a) => calls.push(['fillText', ...a]),
    set fillStyle(v) { calls.push(['fillStyle', v]); },
    set strokeStyle(v) { calls.push(['strokeStyle', v]); },
    set lineWidth(v) { calls.push(['lineWidth', v]); },
    set font(v) { calls.push(['font', v]); },
    set textAlign(v) { calls.push(['textAlign', v]); },
    set textBaseline(v) { calls.push(['textBaseline', v]); },
  };
  return {
    calls,
    clientWidth: width,
    clientHeight: height,
    width: 0,
    height: 0,
    getContext: () => ctx,
  };
}

// Три горизонтали сетки сами рисуются через moveTo/lineTo независимо от
// данных, так что "нет lineTo вообще" — не тот сигнал; считаем именно путь
// данных отдельным moveTo (один на всю ломаную), которых без точек быть не должно.
function countMoveTo(canvas) { return canvas.calls.filter(c => c[0] === 'moveTo').length; }

test('drawSpeedChart без точек рисует только сетку, без линии данных', () => {
  const canvas = fakeCanvas(300, 120);
  drawSpeedChart(canvas, [], { now: 1000 });
  assert.strictEqual(countMoveTo(canvas), 3, 'без точек должна остаться только сетка (3 moveTo)');
});

test('drawSpeedChart рисует линию данных при двух и более точках', () => {
  const canvas = fakeCanvas(300, 120);
  const points = [{ t: 900, bps: 10 }, { t: 1000, bps: 20 }];
  drawSpeedChart(canvas, points, { now: 1000, horizonMs: 120000 });
  assert.strictEqual(countMoveTo(canvas), 4, 'сетка (3) + начало ломаной данных (1)');
});

test('drawSpeedChart подгоняет пиксельный размер канвы под devicePixelRatio', () => {
  const canvas = fakeCanvas(200, 100);
  drawSpeedChart(canvas, [], { now: 0 });
  assert.strictEqual(canvas.width, 200);
  assert.strictEqual(canvas.height, 100);
});

test('drawSpeedChart подписывает пик, если передан formatSpeed', () => {
  const canvas = fakeCanvas(200, 100);
  drawSpeedChart(canvas, [{ t: 0, bps: 5_000_000 }], { now: 0, formatSpeed: (v) => v + ' B/s' });
  assert.ok(canvas.calls.some(c => c[0] === 'fillText' && String(c[1]).includes('B/s')));
});

test('drawSpeedChart подписывает три деления оси скорости слева', () => {
  const canvas = fakeCanvas(200, 100);
  drawSpeedChart(canvas, [{ t: 0, bps: 100 }], { now: 0, formatSpeed: (v) => Math.round(v) + 'bps' });
  // x=2 — фиксированная колонка подписей оси Y (см. fillText(..., 2, y) в реализации).
  const yLabels = canvas.calls.filter(c => c[0] === 'fillText' && c[2] === 2);
  assert.strictEqual(yLabels.length, 3, 'ожидались подписи для верха/середины/низа оси Y: ' + JSON.stringify(yLabels));
  assert.ok(yLabels.some(c => c[1] === '100bps'), 'верхняя подпись — максимум');
  // Ноль — литерал "0", а не formatSpeed(0): у formatSpeed из admin.js есть
  // отдельная семантика "скорость ещё не измерена" -> '', которая на оси
  // читалась бы как пустая подпись, а не как честный ноль.
  assert.ok(yLabels.some(c => c[1] === '0'), 'нижняя подпись — явный ноль, не formatSpeed(0)');
});

test('drawSpeedChart подписывает три деления оси времени снизу', () => {
  const canvas = fakeCanvas(200, 100);
  drawSpeedChart(canvas, [{ t: 900000, bps: 10 }], { now: 900000, horizonMs: 60000 });
  const xLabels = canvas.calls.filter(c => c[0] === 'fillText' && (String(c[1]).includes('назад') || c[1] === 'сейчас'));
  assert.strictEqual(xLabels.length, 3, 'ожидались подписи горизонта/середины/сейчас: ' + JSON.stringify(xLabels));
  assert.ok(xLabels.some(c => c[1] === 'сейчас'));
  assert.ok(xLabels.some(c => c[1] === '1м назад'));
});
