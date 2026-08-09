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

  // benchUploadOnce измеряет реальную скорость заливки чанков для одной пары
  // (chunkSizeMB, conc) на пробном куске файла (не на всём файле — сетка
  // комбинаций иначе перезаливала бы весь архив на каждую ячейку). complete и
  // process ни разу не вызываются, поэтому версия и манифест не создаются;
  // черновая загрузка сразу отбрасывается через /admin/api/upload/abort,
  // а не ждёт часовую уборку /admin/api/upload/cleanup.
  //
  // deps позволяет тестам подменить fetch/now без обращения к сети и часам —
  // в браузере оба параметра не передаются и берутся из window.fetch/
  // performance.now.
  async function benchUploadOnce(file, chunkSizeMB, conc, probeBytes, deps) {
    const doFetch = (deps && deps.fetch) || fetch;
    const now = (deps && deps.now) || (() => performance.now());
    const desiredChunk = Math.max(1, Math.round(chunkSizeMB * 1024 * 1024));
    const totalSize = Math.max(desiredChunk, Math.min(probeBytes, file.size));
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
    let ptr = 0, active = 0, uploadedBytes = 0, failed = false;
    const t0 = now();
    await new Promise((resolve) => {
      function next() {
        if (failed) { if (active === 0) resolve(); return; }
        if (ptr >= idxs.length) { if (active === 0) resolve(); return; }
        const i = idxs[ptr++];
        active++;
        const start = i * chunkSize; const end = Math.min(start + chunkSize, totalSize);
        const blob = file.slice(start, end);
        doFetch('/admin/api/upload/chunk?uploadId=' + encodeURIComponent(uploadId) + '&index=' + i, { method: 'PUT', body: blob })
          .then(r => { if (r.ok) { uploadedBytes += (end - start); } else { failed = true; } })
          .catch(() => { failed = true; })
          .finally(() => { active--; if (!failed && ptr < idxs.length) next(); else if (active === 0) resolve(); });
      }
      for (let j = 0; j < Math.min(conc, idxs.length); j++) next();
    });
    const elapsedSec = Math.max(0.001, (now() - t0) / 1000);
    try { await doFetch('/admin/api/upload/abort?uploadId=' + encodeURIComponent(uploadId), { method: 'POST' }); } catch (_) { }
    if (failed || uploadedBytes <= 0) { return { ok: false, error: 'upload failed' }; }
    return { ok: true, chunkSize, concurrency: conc, bytes: uploadedBytes, seconds: elapsedSec, speed: uploadedBytes / elapsedSec };
  }

  return { parseBenchList, benchCombos, pickClosestChunkOption, benchUploadOnce };
});
