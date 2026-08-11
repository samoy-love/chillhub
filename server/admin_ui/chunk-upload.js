// Ядро byte-level прогресса чанковой загрузки — вынесено из admin.js по той
// же причине, что upload-bench.js/ui-throttle.js/speed-chart.js/line-chart.js:
// только обычный require()-имый CommonJS-модуль даёт c8 построчное покрытие,
// код, вытащенный из admin.js регэкспом и исполненный через new Function,
// V8 с исходным файлом не связывает (см. шапку upload-bench.js).
//
// ПОЧЕМУ ЭТОТ МОДУЛЬ ВООБЩЕ ПОЯВИЛСЯ: чанк заливался через fetch(), а у
// Fetch API нет события прогресса отправки тела запроса — uploadedBytes
// увеличивался только целиком, одним скачком, когда весь чанк (десятки-сотни
// МБ) долетал до сервера. На медленном канале, где чанк идёт минуту и
// больше, это выглядело как «загрузка не начинается», а мгновенная скорость,
// посчитанная по этим редким скачкам, была почти случайным числом.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // putChunkXHR PUTs один чанк и репортит прогресс по мере отправки через
  // onProgress(loadedBytes), а не один раз в конце. deps.XHR позволяет
  // тестам подставить поддельный конструктор вместо настоящего
  // XMLHttpRequest — в браузере он не передаётся.
  function putChunkXHR(url, blob, onProgress, deps) {
    const XHRCtor = (deps && deps.XHR) || XMLHttpRequest;
    return new Promise((resolve) => {
      const xhr = new XHRCtor();
      xhr.open('PUT', url);
      xhr.upload.onprogress = (e) => { if (e.lengthComputable && onProgress) onProgress(e.loaded); };
      xhr.onreadystatechange = () => {
        if (xhr.readyState === 4) {
          let json = null;
          try { json = JSON.parse(xhr.responseText || 'null'); } catch (_) { /* not JSON */ }
          resolve({ ok: xhr.status >= 200 && xhr.status < 300, status: xhr.status, json });
        }
      };
      xhr.onerror = () => resolve({ ok: false, status: 0, json: null });
      xhr.send(blob);
    });
  }

  // pendingBytes суммирует байты чанков, которые уже стримятся, но ещё не
  // подтверждены сервером (Map: индекс чанка -> байт загружено). Прогресс,
  // показанный пользователю, — это uploadedBytes (целиком подтверждённые
  // чанки) плюс pendingBytes(inFlight), а не только uploadedBytes.
  function pendingBytes(inFlight) {
    let sum = 0;
    for (const v of inFlight.values()) sum += v;
    return sum;
  }

  // uploadChunkWithRetries заливает один чанк с повторными попытками — это
  // тело, которое раньше было продублировано втроём в admin.js (первый
  // проход, ретрай сорвавшихся чанков, дозаливка недостающих перед complete).
  // 409 — чанк уже лежит на сервере (гонка с ретраем или устаревший ответ
  // /status) и считается успехом, а не ошибкой.
  async function uploadChunkWithRetries(uploadId, index, blob, opts) {
    const o = opts || {};
    const maxAttempts = o.maxAttempts || 5;
    const retryDelayMs = o.retryDelayMs || 400;
    const put = o.put || putChunkXHR;
    let attempts = 0;
    while (attempts < maxAttempts) {
      attempts++;
      try {
        const r = await put(o.url, blob, o.onProgress, o.deps);
        if (r.ok) return { ok: true, attempts, exists: false, writeMs: Number((r.json && r.json.writeMs) || 0) | 0 };
        if (r.status === 409) return { ok: true, attempts, exists: true, writeMs: 0 };
        if (o.onAttemptFailed) o.onAttemptFailed({ index, attempts, status: r.status });
        await new Promise((res) => setTimeout(res, retryDelayMs * attempts));
      } catch (e) {
        if (o.onAttemptFailed) o.onAttemptFailed({ index, attempts, error: e });
        await new Promise((res) => setTimeout(res, retryDelayMs * attempts));
      }
    }
    return { ok: false, attempts, exists: false, writeMs: 0 };
  }

  // runWorkerPool гоняет worker(index) над indexes с ограниченной
  // параллельностью. concurrencyRef() читается перед каждым запуском
  // очередного воркера, так что предел можно менять на лету (пользователь
  // двигает слайдер параллельности прямо во время заливки). onActiveChange
  // получает текущее число активных воркеров после каждого изменения.
  function runWorkerPool(indexes, concurrencyRef, worker, onActiveChange) {
    return new Promise((resolve) => {
      let ptr = 0;
      let active = 0;
      const failed = [];
      if (indexes.length === 0) { resolve(failed); return; }
      function next() {
        while (active < concurrencyRef() && ptr < indexes.length) {
          const idx = indexes[ptr++];
          active++;
          if (onActiveChange) onActiveChange(active);
          Promise.resolve(worker(idx)).then((ok) => {
            active--;
            if (onActiveChange) onActiveChange(active);
            if (!ok) failed.push(idx);
            if (ptr >= indexes.length && active === 0) resolve(failed);
            else next();
          });
        }
      }
      next();
    });
  }

  return { putChunkXHR, pendingBytes, uploadChunkWithRetries, runWorkerPool };
});
