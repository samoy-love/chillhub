// Проверяет, что upload-bench.js и ui-throttle.js реально работают в том
// режиме, для которого их UMD-обёртка и написана: как обычный <script> в
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
