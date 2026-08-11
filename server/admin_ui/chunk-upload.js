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

  return { putChunkXHR, pendingBytes };
});
