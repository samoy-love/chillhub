// makeUiThrottler — общий планировщик редких UI-обновлений (полоса прогресса,
// проценты, график скорости) во время долгой загрузки.
//
// Раньше это делал requestAnimationFrame напрямую, и это ломалось ровно тогда,
// когда админ переключался на другую вкладку во время многогигабайтной
// заливки: rAF полностью перестаёт вызываться в свёрнутой/фоновой вкладке
// (Chrome и Firefox держат её на 0 fps), поэтому процент, скорость и
// uPlot-график замирали до возврата на вкладку — снаружи это выглядело как
// «график вообще не рисуется». setTimeout продолжает срабатывать (пусть и с
// троттлингом ОС/браузера), поэтому используется здесь вместо rAF.
//
// Вынесено в отдельный CommonJS-модуль по той же причине, что и
// upload-bench.js: только так на него можно написать тест, покрытие которого
// c8 свяжет именно с этим файлом, а не потеряет на границе new Function.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // makeUiThrottler(intervalMs, run, deps) возвращает { schedule() }.
  // schedule() можно звать сколь угодно часто (например, на каждый успешно
  // залитый чанк) — run() при этом вызывается не чаще раза в intervalMs, и
  // не более одного отложенного вызова стоит в очереди одновременно.
  //
  // deps.setTimeout/deps.now позволяют тестам подменить таймер и часы, не
  // трогая реальные global setTimeout/performance.now — в браузере оба
  // параметра не передаются.
  function makeUiThrottler(intervalMs, run, deps) {
    const setTimeoutFn = (deps && deps.setTimeout) || setTimeout;
    const now = (deps && deps.now) || (() => performance.now());
    // -Infinity, not 0: with a real clock this never mattered (performance.now()
    // is never that close to 0 by the time an upload starts), but 0 as the
    // initial "last run" timestamp meant the very first schedule() could lose
    // the race against intervalMs if now() itself started near 0 — exactly
    // what a deterministic fake clock in tests does.
    let lastRunTs = -Infinity;
    let scheduled = false;

    function schedule() {
      const nowTs = now();
      if (nowTs - lastRunTs < intervalMs) return;
      lastRunTs = nowTs;
      if (scheduled) return;
      scheduled = true;
      setTimeoutFn(() => { scheduled = false; run(); }, 0);
    }

    return { schedule };
  }

  return { makeUiThrottler };
});
