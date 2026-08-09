// Тесты makeUiThrottler (server/admin_ui/ui-throttle.js) — планировщика,
// который заменил requestAnimationFrame в прогрессе загрузки именно потому,
// что rAF замирает в свёрнутой вкладке. Модуль require()-ится как обычный
// CommonJS-файл, поэтому c8 видит его построчно (см. комментарий в шапке
// upload-bench.js — та же причина, что и там).
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { makeUiThrottler } = require(path.join('..', '..', 'server', 'admin_ui', 'ui-throttle.js'));

// fakeTimer records scheduled callbacks instead of actually waiting, and lets
// the test fire them on demand — this is what proves the throttler uses a
// real timer (which keeps running in a backgrounded tab) and not rAF (which
// doesn't exist here at all, unlike jsdom-based setups).
function fakeTimer() {
  const pending = [];
  const setTimeoutFn = (fn) => { pending.push(fn); return pending.length; };
  return {
    setTimeoutFn,
    pendingCount: () => pending.length,
    flushOne: () => { const fn = pending.shift(); if (fn) fn(); },
    flushAll: () => { while (pending.length) { pending.shift()(); } },
  };
}

test('schedule() запускает run не чаще intervalMs', () => {
  let now = 0;
  let runs = 0;
  const timer = fakeTimer();
  const { schedule } = makeUiThrottler(500, () => { runs++; }, { setTimeout: timer.setTimeoutFn, now: () => now });

  schedule(); // now=0: первый вызов проходит порог сразу (lastRunTs изначально 0)
  timer.flushAll();
  assert.strictEqual(runs, 1);

  now = 100; // ещё внутри интервала
  schedule();
  assert.strictEqual(timer.pendingCount(), 0, 'внутри intervalMs таймер вообще не должен ставиться');
  assert.strictEqual(runs, 1);

  now = 600; // интервал прошёл
  schedule();
  timer.flushAll();
  assert.strictEqual(runs, 2);
});

test('несколько schedule() подряд коалесцируются в один запланированный запуск', () => {
  let now = 1000; // старт не с нуля, чтобы первый schedule() тоже уважал интервал
  let runs = 0;
  const timer = fakeTimer();
  const { schedule } = makeUiThrottler(500, () => { runs++; }, { setTimeout: timer.setTimeoutFn, now: () => now });

  schedule();
  timer.flushAll(); // первый run состоялся, lastRunTs=1000

  now = 1600;
  schedule(); schedule(); schedule(); // три вызова за один и тот же "тик"
  assert.strictEqual(timer.pendingCount(), 1, 'должен стоять ровно один отложенный run, а не три');
  timer.flushAll();
  assert.strictEqual(runs, 2);
});

test('после срабатывания таймера следующий schedule() снова может запланировать run', () => {
  let now = 0;
  let runs = 0;
  const timer = fakeTimer();
  const { schedule } = makeUiThrottler(200, () => { runs++; }, { setTimeout: timer.setTimeoutFn, now: () => now });

  schedule(); timer.flushAll();
  now = 300; schedule(); timer.flushAll();
  now = 600; schedule(); timer.flushAll();

  assert.strictEqual(runs, 3);
});

test('без deps использует настоящие setTimeout/performance.now и всё равно вызывает run', async () => {
  let runs = 0;
  const { schedule } = makeUiThrottler(10, () => { runs++; });
  schedule();
  await new Promise((resolve) => setTimeout(resolve, 50));
  assert.strictEqual(runs, 1);
});
