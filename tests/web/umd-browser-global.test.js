// Проверяет, что upload-bench.js, ui-throttle.js и остальные модули реально
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

test('chunk-upload.js в браузерном режиме кладёт putChunkXHR/pendingBytes в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/chunk-upload.js');
  assert.strictEqual(typeof w.putChunkXHR, 'function');
  assert.strictEqual(typeof w.pendingBytes, 'function');
  assert.strictEqual(w.pendingBytes(new Map([[0, 5], [1, 7]])), 12);
  assert.strictEqual(typeof w.uploadChunkWithRetries, 'function');
  assert.strictEqual(typeof w.runWorkerPool, 'function');
});

test('rate-estimator.js в браузерном режиме кладёт функции окна скорости в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/rate-estimator.js');
  assert.strictEqual(typeof w.makeRateEstimator, 'function');
  const est = w.makeRateEstimator(1000);
  est.push(0, 0);
  assert.strictEqual(est.push(1000, 1000), 1000);
});

test('upload-tuning.js в браузерном режиме кладёт автоподбор в window', () => {
  const w = loadAsBrowserScript('server/admin_ui/upload-tuning.js');
  assert.strictEqual(typeof w.pickUploadParams, 'function');
  assert.strictEqual(typeof w.connectionCap, 'function');
  assert.strictEqual(typeof w.rateWindowMs, 'function');
  // Функция действительно работает в этом окружении, а не просто присвоена:
  // admin.js зовёт её как глобальную ровно так же.
  const p = w.pickUploadParams(1.3 * 1024 * 1024 * 1024, { protocol: 'http/1.1' });
  assert.strictEqual(p.concurrency, 6);
});

/* ---------- Модули панели 2.0 ---------- */

/* Панель 2.0 подключает их обычными <script>, без сборщика: сломанная
   обёртка UMD означает, что модуль тихо не появится в window и раздел
   развалится уже в браузере — тесты, которые грузят их через require,
   этого не увидят. */
const V2_MODULES = [
  ['format.js', 'CH2Format', ['bytes', 'dec', 'percent']],
  ['api.js', 'CH2Api', ['makeApi', 'session', 'reason']],
  ['actions.js', 'CH2Actions', ['has', 'run']],
  ['store.js', 'CH2Store', ['createStore']],
  ['sections.js', 'CH2Sections', ['launcher', 'games', 'filterInbox']],
  ['upload.js', 'CH2Upload', ['run', 'process', 'abort']],
  ['build.js', 'CH2Build', ['run', 'outcome', 'errorText']],
  ['registry.js', 'CH2Registry', ['move', 'reorder', 'problems']],
  ['news.js', 'CH2News', ['address', 'payload', 'problems']],
  ['gallery.js', 'CH2Gallery', ['safePath', 'nameProblem']],
  ['tuning.js', 'CH2Tuning', ['best', 'why', 'remember']],
  ['views.js', 'CH2Views', ['sheet', 'maintForm', 'gameForm']],
  ['mods.js', 'CH2Mods', ['parsePackageUrl', 'planSpace']],
  ['manifest.js', 'CH2Manifest', ['diff', 'folders', 'between']],
];

for (const [file, global, fns] of V2_MODULES) {
  test(`${file} в браузерном режиме кладёт ${global} в window`, () => {
    const w = loadAsBrowserScript('server/admin_ui/' + file);
    assert.strictEqual(typeof w[global], 'object', `${global} не появился в window`);
    for (const fn of fns) {
      assert.strictEqual(typeof w[global][fn], 'function', `${global}.${fn} не функция`);
    }
  });
}

test('панель подключает ровно те модули, что лежат рядом', () => {
  // Забытый в index.html модуль — это раздел, падающий на первом нажатии;
  // лишний тег — запрос в никуда на каждой загрузке
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'server/admin_ui/index.html'), 'utf8');
  const linked = [...html.matchAll(/<script src="\/admin\/ui\/([^"]+)"/g)].map((m) => m[1]);
  // login.js — единственный скрипт панели, живущий отдельно: страницу
  // входа открывают БЕЗ сессии, и остальные модули ей недоступны
  const onDisk = fs
    .readdirSync(path.join(__dirname, '..', '..', 'server/admin_ui'))
    .filter((n) => n.endsWith('.js') && n !== 'login.js');

  for (const f of onDisk) {
    assert.ok(linked.includes(f), 'модуль лежит, но не подключён: ' + f);
  }
  for (const f of linked) {
    assert.ok(onDisk.includes(f), 'подключён несуществующий модуль: ' + f);
  }
});
