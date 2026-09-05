// Подбор параметров загрузки и уход за кэшем архивов.
//
// САМО ПРАВИЛО ПОДБОРА — не здесь: `upload-tuning.js` версии 1.0 считает
// размер куска и число потоков от размера файла и протокола, и это уже
// покрыто тестами. Здесь то, что делают с результатами прогонов: какой
// из них считать лучшим и стоит ли вообще трогать кэш.
//
// ПОЧЕМУ БЫСТРЕЙШИЙ ПРОГОН — НЕ ВСЕГДА ЛУЧШИЙ. Больше потоков не значит
// быстрее: на восьми канал начинает терять куски и переспрашивать их
// заново. Прогон с высокой скоростью и повторами хуже чуть более
// медленного без повторов — повторы на настоящем файле в разы длиннее,
// чем на пробе.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Tuning = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /** Штраф за повтор: каждый срезает десятую часть достигнутой скорости. */
  const RETRY_PENALTY = 0.1;

  const num = (v) => {
    const n = Number(v);
    return Number.isFinite(n) ? n : 0;
  };

  /**
   * Оценка прогона: скорость со скидкой за повторы.
   *
   * Скидка не косметическая: повтор куска на пробе стоит доли секунды, а
   * на полуторагигабайтном файле — минуты, и прогон, выигравший процент
   * скорости ценой трёх повторов, на деле проиграет.
   */
  function score(run) {
    const mbps = num(run && run.mbps);
    const retries = Math.max(0, num(run && run.retries));
    return mbps * Math.max(0, 1 - retries * RETRY_PENALTY);
  }

  /** Лучший прогон. Пустой список — нечего выбирать. */
  function best(runs) {
    const list = (runs || []).filter((r) => r && num(r.mbps) > 0);
    if (!list.length) return null;
    return list.reduce((a, b) => (score(b) > score(a) ? b : a));
  }

  /** Помечает лучший прогон в списке, не переставляя его. */
  function mark(runs) {
    const top = best(runs);
    return (runs || []).map((r) => Object.assign({}, r, { best: Boolean(top) && r === top }));
  }

  /**
   * Почему выбран именно он.
   *
   * Без объяснения подбор выглядит гаданием, и его результату не верят.
   */
  function why(runs) {
    const top = best(runs);
    if (!top) return 'Прогонов не было — подбирать не из чего.';

    const fastest = (runs || []).reduce((a, b) => (num(b.mbps) > num(a.mbps) ? b : a));
    if (fastest !== top) {
      return (
        'Быстрее всех шёл ' + fastest.chunk + ' на ' + fastest.streams + ' потоках, но с повторами (' +
        fastest.retries + '). Повтор на настоящем файле длиннее, чем на пробе, поэтому выбран ' +
        top.chunk + ' на ' + top.streams + ' потоках.'
      );
    }
    return 'Лучший прогон: ' + top.chunk + ' на ' + top.streams + ' потоках, без повторов.';
  }

  /** Что применить к загрузке. */
  function apply(run) {
    if (!run) return null;
    return { chunk: run.chunk, streams: num(run.streams) || 1 };
  }

  /* ---------- Память о прогонах ---------- */

  /* ГДЕ ХРАНИТЬ ПРОГОНЫ. На сервере им не место, и это не лень: прогон
     меряет канал между этим компьютером и сервером. С другой машины он
     ничего не значит, а показанный как общий — сбивает с толку. Поэтому
     он лежит в браузере того, кто мерил. */
  const KEY = 'ch2:bench';

  /** Запоминает прогон. Закрытое хранилище — не повод ронять подбор. */
  function remember(storage, runs) {
    if (!storage) return false;
    try {
      storage.setItem(KEY, JSON.stringify({ at: Date.now(), runs: runs || [] }));
      return true;
    } catch {
      return false;
    }
  }

  /** Читает прошлый прогон. Мусор в хранилище — это его отсутствие. */
  function recall(storage) {
    if (!storage) return [];
    try {
      const d = JSON.parse(storage.getItem(KEY) || 'null');
      return d && Array.isArray(d.runs) ? d.runs : [];
    } catch {
      return [];
    }
  }

  /* ---------- Кэш архивов ---------- */

  /** Доля диска, ниже которой чистить кэш пора, а не «можно». */
  const LOW_SPACE = 0.1;

  /**
   * Что делать с кэшем.
   *
   * Кэш экономит время пересборки: те же архивы Thunderstore не качаются
   * повторно. Чистить его по расписанию — значит платить за это временем
   * каждой следующей сборки. Повод один: кончается место.
   */
  function cacheAdvice(cache, disk) {
    const c = cache || {};
    const d = disk || {};
    const free = num(d.free);
    const total = num(d.total);
    const share = total > 0 ? free / total : 1;

    if (share < LOW_SPACE) {
      return {
        level: 'now',
        message: 'Места почти нет. Кэш занимает столько же, сколько несколько сборок, — его можно убрать первым.',
      };
    }
    if (num(c.bytes) > 0 && total > 0 && num(c.bytes) / total > 0.25) {
      return {
        level: 'soon',
        message: 'Кэш вырос до четверти диска. Убирать не срочно, но стоит посмотреть, что в нём лежит.',
      };
    }
    return {
      level: 'no',
      message: 'Чистить незачем: кэш экономит время пересборки, а место пока есть.',
    };
  }

  return { RETRY_PENALTY, LOW_SPACE, KEY, score, best, mark, why, apply, remember, recall, cacheAdvice };
});
