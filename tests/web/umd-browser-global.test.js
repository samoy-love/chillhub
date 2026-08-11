// Проверяет, что upload-bench.js, ui-throttle.js и speed-chart.js реально
// работают в том режиме, для которого их UMD-обёртка и написана: как обычный
// <script> в
// браузере, без CommonJS. require() в остальных tests/web/*.test.js всегда
// идёт по ветке `module.exports` и никогда не исполняет `Object.assign(root,
// factory())` — этот тест исполняет исходники в vm-контексте без `module`,
// имитируя ровно то окружение, в котором эти файлы реально работают в
// admin.html: window есть, module — нет.
//
// filename в vm.runInContext — не для галочки: без него это ничем не лучше
// new Function() из admin-logic.test.js (см. комментарий в шапке
// upload-bench.js), а с ним V8 связывает исполнение с настоящим файлом.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function loadAsBrowserScript(relPath) {
  const abs = path.join(__dirname, '..', '..', relPath);
  const src = fs.readFileSync(abs, 'utf8');
  const sandbox = { window: {} };
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: abs });
  return sandbox.window;
}

test('upload-bench.js в браузерном режиме кладёт функции в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/upload-bench.js');
  assert.strictEqual(typeof w.parseBenchList, 'function');
  assert.strictEqual(typeof w.benchCombos, 'function');
  assert.strictEqual(typeof w.pickClosestChunkOption, 'function');
  assert.strictEqual(typeof w.benchUploadOnce, 'function');
  // И это не просто ссылки — функция действительно рабочая в этом окружении.
  // Обычный deepStrictEqual тут не годится: массив создан в другом vm-контексте,
  // то есть с чужим Array из другого реалма, и сравнение по прототипу падает
  // даже при равных значениях — сравниваем поэлементно через Array.from.
  assert.deepStrictEqual(Array.from(w.parseBenchList('4,8', 1)), [4, 8]);
});

test('ui-throttle.js в браузерном режиме кладёт makeUiThrottler в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/ui-throttle.js');
  assert.strictEqual(typeof w.makeUiThrottler, 'function');
  let runs = 0;
  const { schedule } = w.makeUiThrottler(1000, () => { runs++; }, {
    setTimeout: (fn) => fn(),
    now: () => 0,
  });
  schedule();
  assert.strictEqual(runs, 1);
});

test('speed-chart.js в браузерном режиме кладёт функции графика в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/speed-chart.js');
  assert.strictEqual(typeof w.mapPointsToPixels, 'function');
  assert.strictEqual(typeof w.drawSpeedChart, 'function');
  assert.deepStrictEqual(
    Array.from(w.mapPointsToPixels([{ t: 0, bps: 1 }], { width: 10, height: 10, padding: 0, horizonMs: 1000, now: 0 })).length,
    1
  );
});

test('line-chart.js в браузерном режиме кладёт функции графика в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/line-chart.js');
  assert.strictEqual(typeof w.mapSeriesToPixels, 'function');
  assert.strictEqual(typeof w.drawMultiLineChart, 'function');
  const px = w.mapSeriesToPixels([{ values: [1, 2] }], { width: 10, height: 10, padding: { left: 0, right: 0, top: 0, bottom: 0 } });
  assert.strictEqual(Array.from(px).length, 1);
  assert.strictEqual(Array.from(px[0]).length, 2);
});

test('chunk-upload.js в браузерном режиме кладёт putChunkXHR/pendingBytes в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/chunk-upload.js');
  assert.strictEqual(typeof w.putChunkXHR, 'function');
  assert.strictEqual(typeof w.pendingBytes, 'function');
  assert.strictEqual(w.pendingBytes(new Map([[0, 5], [1, 7]])), 12);
});

test('rate-estimator.js в браузерном режиме кладёт функции окна скорости в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/rate-estimator.js');
  assert.strictEqual(typeof w.pushByteSample, 'function');
  assert.strictEqual(typeof w.windowedRate, 'function');
  let samples = w.pushByteSample([], { t: 0, bytes: 0 }, 1000);
  samples = w.pushByteSample(samples, { t: 1000, bytes: 1000 }, 1000);
  assert.strictEqual(w.windowedRate(samples), 1000);
});
