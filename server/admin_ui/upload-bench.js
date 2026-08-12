// Тестируемое ядро инструмента подбора параметров загрузки (карточка «Тест
// параметров загрузки» в разделе «Игры»). Вынесено из admin.js в отдельный
// файл, а не объявлено внутри него, ровно из-за того, как устроено покрытие
// тестами в этом репозитории: tests/web/*.test.js вытаскивают функции из
// admin.js регэкспом и исполняют их через `new Function(...)`, а такой код
// V8 не связывает с исходным файлом — строки admin.js остаются «непокрытыми»
// независимо от того, что тест на самом деле их проверил. Обычный
// CommonJS-модуль, наоборот, `require()`-ится как есть, и c8 видит его
// построчно. Файл подключается в admin.html отдельным <script> ДО admin.js и
// экспортирует те же имена в window — admin.js обращается к ним как к
// глобальным функциям, как и к остальным функциям в этом каталоге.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // parseBenchList разбирает поле "4,8,16" в список положительных чисел,
  // умноженных на scale (мегабайты -> байты и т.п.). Мусор (пустые элементы,
  // не-числа, отрицательные) молча отбрасывается: пустой список — это то, что
  // вызывающий код обязан заметить сам ("укажите хотя бы одно значение"), а не
  // повод падать здесь.
  function parseBenchList(input, scale) {
    return String(input || '').split(',').map(s => s.trim()).filter(Boolean)
      .map(s => Number(s) * scale).filter(n => Number.isFinite(n) && n > 0);
  }

  // benchCombos — декартово произведение размеров чанка и параллельности:
  // именно эта сетка комбинаций прогоняется тестом параметров.
  function benchCombos(chunkSizesMB, concs) {
    const out = [];
    for (const cs of chunkSizesMB) {
      for (const c of concs) { out.push({ cs, c }); }
    }
    return out;
  }

  // pickClosestChunkOption находит среди доступных значений <select> ближайшее
  // к измеренному оптимальному размеру чанка в байтах. Список — обычные числа
  // (значения option.value), а не DOM-узлы, поэтому функция не трогает
  // document и проверяется без браузерного окружения.
  function pickClosestChunkOption(optionValues, targetBytes) {
    let best = null, bestDiff = Infinity;
    for (const v of optionValues) {
      const diff = Math.abs(v - targetBytes);
      if (diff < bestDiff) { bestDiff = diff; best = v; }
    }
    return best;
  }

  // benchProbeBytes — сколько байт реально заливает одна комбинация. Это не
  // просто «проба»: чанк меньше пробы не делают, поэтому при чанке крупнее
  // probeBytes ячейка заливает целый чанк. Формула одна и та же здесь и в
  // benchUploadOnce — иначе обещанный объём разошёлся бы с фактическим.
  function benchProbeBytes(chunkSizeMB, probeBytes, fileSize) {
    const desiredChunk = Math.max(1, Math.round(chunkSizeMB * 1024 * 1024));
    return Math.max(desiredChunk, Math.min(probeBytes, fileSize));
  }

  // benchPlan считает, во что обойдётся прогон, ДО его запуска: сколько
  // комбинаций и сколько гигабайт уедет на сервер. 25 ячеек по 512 МБ — это
  // 12,5 ГБ и часы времени, и узнавать об этом по факту, глядя на замерший
  // «Тест 1/25», — худший способ.
  function benchPlan(chunkSizesMB, concs, probeBytes, fileSize) {
    const combos = [];
    let totalBytes = 0;
    for (const cs of chunkSizesMB) {
      for (const c of concs) {
        const bytes = benchProbeBytes(cs, probeBytes, fileSize);
        combos.push({ cs, c, bytes });
        totalBytes += bytes;
      }
    }
    return { combos, totalBytes };
  }

  // benchProgress — арифметика строки состояния: доля выполненного, средняя
  // скорость за прогон и оценка остатка. ETA считается по живой скорости,
  // когда она известна, и по средней в остальных случаях: средняя за час
  // прогона перестаёт замечать, что канал просел прямо сейчас.
  //
  // Неизвестное — это null, а не ноль и не Infinity: «осталось 0 с» на старте
  // врёт убедительнее, чем прочерк.
  function benchProgress(state) {
    const s = state || {};
    const done = Math.max(0, Number(s.doneBytes || 0));
    const total = Math.max(0, Number(s.totalBytes || 0));
    const elapsed = Math.max(0, Number(s.elapsedSec || 0));
    const live = Number(s.liveSpeed || 0);
    const pct = total > 0 ? Math.min(100, (done * 100) / total) : 0;
    const avgSpeed = elapsed > 0 && done > 0 ? done / elapsed : 0;
    const speed = live > 0 ? live : avgSpeed;
    const left = Math.max(0, total - done);
    const etaSec = speed > 0 && total > 0 ? left / speed : null;
    return { pct, avgSpeed, etaSec };
  }

  // benchUploadOnce измеряет реальную скорость заливки чанков для одной пары
  // (chunkSizeMB, conc) на пробном куске файла (не на всём файле — сетка
  // комбинаций иначе перезаливала бы весь архив на каждую ячейку). complete и
  // process ни разу не вызываются, поэтому версия и манифест не создаются;
  // черновая загрузка сразу отбрасывается через /admin/api/upload/abort,
  // а не ждёт часовую уборку /admin/api/upload/cleanup.
  //
  // deps позволяет тестам подменить fetch/now без обращения к сети и часам —
  // в браузере оба параметра не передаются и берутся из window.fetch/
  // performance.now. Там же:
  //   onProgress({uploadedBytes, totalSize, chunksDone, totalChunks})
  //     вызывается по мере подтверждения чанков. Гранулярность — чанк, а не
  //     байт: тело fetch-запроса событий отправки не даёт (см. шапку
  //     chunk-upload.js), поэтому на пробе из двух чанков по 256 МБ шаг
  //     двигается дважды. Прошедшее время и средняя скорость от этого не
  //     зависят и тикают ровно — их считает вызывающий.
  //   signal — объект с полем aborted: прогон на два часа обязан
  //     останавливаться, не дожидаясь конца сетки.
  async function benchUploadOnce(file, chunkSizeMB, conc, probeBytes, deps) {
    const doFetch = (deps && deps.fetch) || fetch;
    const now = (deps && deps.now) || (() => performance.now());
    const onProgress = (deps && deps.onProgress) || null;
    const signal = (deps && deps.signal) || null;
    const stopped = () => !!(signal && signal.aborted);
    const desiredChunk = Math.max(1, Math.round(chunkSizeMB * 1024 * 1024));
    const totalSize = benchProbeBytes(chunkSizeMB, probeBytes, file.size);
    const probeName = 'bench-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8);
    let initRes;
    try {
      initRes = await doFetch('/admin/api/upload/init', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          kind: 'game', gameId: 'bench', version: probeName, zipName: 'bench.bin', totalSize, chunkSize: desiredChunk
        })
      });
    } catch (e) { return { ok: false, error: String(e) }; }
    if (!initRes.ok) { return { ok: false, error: 'HTTP ' + initRes.status + ' init' }; }
    const init = await initRes.json();
    const uploadId = init.uploadId;
    const chunkSize = init.chunkSize || desiredChunk;
    const totalChunks = init.totalChunks || Math.ceil(totalSize / chunkSize);
    const idxs = []; for (let i = 0; i < totalChunks; i++) idxs.push(i);
    let ptr = 0, active = 0, uploadedBytes = 0, chunksDone = 0, failed = false;
    const t0 = now();
    await new Promise((resolve) => {
      function next() {
        if (failed || stopped()) { if (active === 0) resolve(); return; }
        if (ptr >= idxs.length) { if (active === 0) resolve(); return; }
        const i = idxs[ptr++];
        active++;
        const start = i * chunkSize; const end = Math.min(start + chunkSize, totalSize);
        const blob = file.slice(start, end);
        doFetch('/admin/api/upload/chunk?uploadId=' + encodeURIComponent(uploadId) + '&index=' + i, { method: 'PUT', body: blob })
          .then(r => {
            if (r.ok) {
              uploadedBytes += (end - start);
              chunksDone++;
              if (onProgress) onProgress({ uploadedBytes, totalSize, chunksDone, totalChunks });
            } else { failed = true; }
          })
          .catch(() => { failed = true; })
          .finally(() => { active--; if (!failed && !stopped() && ptr < idxs.length) next(); else if (active === 0) resolve(); });
      }
      for (let j = 0; j < Math.min(conc, idxs.length); j++) next();
    });
    const elapsedSec = Math.max(0.001, (now() - t0) / 1000);
    // Пробу отбрасываем в любом случае, включая остановку на полпути: иначе
    // прерванный прогон оставит на диске гигабайты мусора.
    try { await doFetch('/admin/api/upload/abort?uploadId=' + encodeURIComponent(uploadId), { method: 'POST' }); } catch (_) { }
    if (stopped()) { return { ok: false, aborted: true, error: 'остановлено' }; }
    if (failed || uploadedBytes <= 0) { return { ok: false, error: 'upload failed' }; }
    return {
      ok: true, chunkSize, concurrency: conc, bytes: uploadedBytes,
      seconds: elapsedSec, speed: uploadedBytes / elapsedSec, totalSize, totalChunks,
    };
  }

  return {
    parseBenchList, benchCombos, pickClosestChunkOption, benchUploadOnce,
    benchProbeBytes, benchPlan, benchProgress,
  };
});
