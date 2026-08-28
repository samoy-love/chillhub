// Что показать из приложенного к обращению журнала — и что сказать про остальное.
//
// Вынесено из admin.js обычным CommonJS-модулем по той же причине, что ndjson.js
// и ui-status.js: только его c8 связывает с исходником построчно, а код внутри
// admin.js, вытащенный регэкспом и исполненный через new Function, в отчёт о
// покрытии не попадает вовсе.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  // Сколько текста журнала показывать прямо на странице.
  //
  // Бандл диагностики — до мегабайта. Вставленный в <pre> целиком, он на каждое
  // открытие обращения заставляет браузер экранировать и разложить миллион
  // символов, и панель заметно подвисает — ровно на том обращении, которое
  // оператор открыл потому, что у пользователя что-то сломалось.
  //
  // Показывается ХВОСТ, а не начало: авария всегда в конце журнала, а начало —
  // это загрузка лаунчера, одинаковая у всех.
  const INLINE_TAIL_BYTES = 64 * 1024;

  // formatSize — короткая подпись объёма. Своя, а не formatBytes из admin.js:
  // модуль обязан работать и в тестах, где admin.js не загружен.
  function formatSize(n) {
    const v = Number(n) || 0;
    if (v >= 1024 * 1024) return (v / 1024 / 1024).toFixed(1) + ' МБ';
    if (v >= 1024) return Math.round(v / 1024) + ' КБ';
    return v + ' Б';
  }

  // feedbackLogsView решает, что отрисовать в блоке журнала.
  //
  // `has` отвечает на вопрос «есть ли что показывать», и отвечает по САМОМУ
  // тексту, а не по флагу attachLogs: пользователь мог попросить приложить
  // журнал, а тот не собрался — и блок с кнопкой «скачать» вёл бы в пустоту.
  // Обратный случай тоже реальный: старым обращениям журналы обрезает
  // уплотнение ящика, а флаг у них остаётся.
  function feedbackLogsView(item, tailBytes) {
    const text = item && typeof item.logs === 'string' ? item.logs : '';
    const size = text.length || Number(item && item.logBytes) || 0;
    if (!text) {
      return { has: false, text: '', note: '', truncated: false, size: size };
    }

    const limit = Number(tailBytes) > 0 ? Number(tailBytes) : INLINE_TAIL_BYTES;
    if (text.length <= limit) {
      return { has: true, text: text, note: formatSize(size), truncated: false, size: size };
    }

    return {
      has: true,
      text: text.slice(text.length - limit),
      note: formatSize(size) + ' · показан конец, ' + formatSize(limit) + ' — целиком в файле',
      truncated: true,
      size: size,
    };
  }

  return { feedbackLogsView, formatSize, INLINE_TAIL_BYTES };
});
