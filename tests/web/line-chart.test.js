// Тесты server/admin_ui/line-chart.js — многосерийного графика без uPlot/CDN,
// которым теперь рисуется «Динамика по дням» в разделе «Метрики». См.
// комментарий в шапке файла: это второе (и последнее) место, где раньше
// грузился uPlot с unpkg.com — после инцидента с графиком скорости загрузки
// (см. speed-chart.js) внешнюю зависимость решили не оставлять и здесь.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { mapSeriesToPixels, drawMultiLineChart } = require(path.join('..', '..', 'server', 'admin_ui', 'line-chart.js'));

test('mapSeriesToPixels раскладывает несколько рядов по общей оси X', () => {
  const series = [
    { values: [0, 10] },
    { values: [5, 5] },
  ];
  const px = mapSeriesToPixels(series, { width: 100, height: 100, padding: { left: 0, right: 0, top: 0, bottom: 0 } });
  assert.strictEqual(px.length, 2);
  assert.strictEqual(px[0].length, 2);
  // Первая точка первого ряда (t=0) — самое начало оси X.
  assert.strictEqual(px[0][0].x, 0);
  // Последняя точка (t=1, единственный второй индекс) — конец оси X.
  assert.strictEqual(px[0][1].x, 100);
});

test('mapSeriesToPixels: более высокое значение — меньший y (выше на графике)', () => {
  const series = [{ values: [10, 100] }];
  const px = mapSeriesToPixels(series, { width: 100, height: 100, padding: { left: 0, right: 0, top: 0, bottom: 0 } });
  assert.ok(px[0][1].y < px[0][0].y, JSON.stringify(px));
});

test('mapSeriesToPixels: maxY можно задать явно', () => {
  const series = [{ values: [10] }];
  const px = mapSeriesToPixels(series, { width: 100, height: 100, padding: { left: 0, right: 0, top: 0, bottom: 0 }, maxY: 1000 });
  assert.ok(px[0][0].y > 90, 'при большом maxY точка должна быть почти внизу: y=' + px[0][0].y);
});

test('mapSeriesToPixels: пустой массив рядов не падает', () => {
  assert.deepStrictEqual(mapSeriesToPixels([], { width: 100, height: 100, padding: { left: 0, right: 0, top: 0, bottom: 0 } }), []);
});

// Тот же поддельный 2D-контекст, что и в speed-chart.test.js — этого набора
// методов достаточно для drawMultiLineChart.
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
  return { calls, clientWidth: width, clientHeight: height, width: 0, height: 0, getContext: () => ctx };
}

test('drawMultiLineChart рисует по одной линии на серию', () => {
  const canvas = fakeCanvas(300, 150);
  const xs = [0, 1, 2];
  const series = [
    { label: 'A', color: '#0d6efd', values: [1, 2, 3] },
    { label: 'B', color: '#198754', values: [3, 2, 1] },
  ];
  drawMultiLineChart(canvas, xs, series, {});
  const strokeColors = canvas.calls.filter(c => c[0] === 'strokeStyle').map(c => c[1]);
  assert.ok(strokeColors.includes('#0d6efd'));
  assert.ok(strokeColors.includes('#198754'));
});

test('drawMultiLineChart не рисует линию для ряда с одной точкой', () => {
  const canvas = fakeCanvas(300, 150);
  drawMultiLineChart(canvas, [0], [{ label: 'A', color: '#0d6efd', values: [5] }], {});
  const strokeColors = canvas.calls.filter(c => c[0] === 'strokeStyle').map(c => c[1]);
  assert.ok(!strokeColors.includes('#0d6efd'), 'одной точки недостаточно для линии');
});

test('drawMultiLineChart подписывает деления оси Y, включая явный ноль', () => {
  const canvas = fakeCanvas(300, 150);
  drawMultiLineChart(canvas, [0, 1], [{ values: [0, 100] }], { formatY: (v) => Math.round(v) + 'u' });
  const yLabels = canvas.calls.filter(c => c[0] === 'fillText' && c[2] !== 150);
  assert.ok(yLabels.some(c => c[1] === '100u'));
  assert.ok(yLabels.some(c => c[1] === '0'), 'ноль должен быть явным, не formatY(0)');
});

test('drawMultiLineChart подписывает деления оси X через xLabelFor', () => {
  const canvas = fakeCanvas(300, 150);
  const xLabelFor = (i) => 'day' + i;
  drawMultiLineChart(canvas, [0, 1, 2], [{ values: [1, 2, 3] }], { xLabelFor });
  const xLabels = canvas.calls.filter(c => c[0] === 'fillText' && String(c[1]).startsWith('day'));
  assert.strictEqual(xLabels.length, 3);
  assert.ok(xLabels.some(c => c[1] === 'day0'));
  assert.ok(xLabels.some(c => c[1] === 'day2'));
});

test('drawMultiLineChart заполняет legendHost цветными подписями серий', () => {
  const canvas = fakeCanvas(300, 150);
  const legendHost = { innerHTML: '' };
  drawMultiLineChart(canvas, [0, 1], [
    { label: 'Запуски', color: '#0d6efd', values: [1, 2] },
    { label: 'Ошибки', color: '#dc3545', values: [0, 1] },
  ], { legendHost });
  assert.ok(legendHost.innerHTML.includes('Запуски'));
  assert.ok(legendHost.innerHTML.includes('Ошибки'));
  assert.ok(legendHost.innerHTML.includes('#0d6efd'));
});

test('drawMultiLineChart без legendHost не падает', () => {
  const canvas = fakeCanvas(300, 150);
  assert.doesNotThrow(() => drawMultiLineChart(canvas, [0, 1], [{ label: 'A', values: [1, 2] }], {}));
});
