// Загрузка ZIP кусками с докачкой после обрыва.
//
// ЧТО ЗДЕСЬ И ЧЕГО ЗДЕСЬ НЕТ. Здесь порядок шагов: спросить сервер, что у
// него уже есть, долить недостающее, попросить собрать, дочитать поток
// разбора. Самих кусков — повторов, пула, побайтового прогресса — здесь
// нет: они уже написаны и покрыты тестами в `chunk-upload.js` версии 1.0,
// и переписывать их заново значит выбросить отлаженный код.
//
// ПОЧЕМУ ЭТО ОТДЕЛЬНО ОТ ПАНЕЛИ. В версии 1.0 порядок жил внутри
// `runChunkedUpload`, склеенный с двумя десятками `getElementById`.
// Проверить его было невозможно: чтобы прогнать докачку, приходилось
// поднимать всю страницу. Здесь порядок не знает про DOM и сообщает о
// себе колбэками.
//
// ГЛАВНОЕ СВОЙСТВО — ДОКАЧКА. Загрузка на 1,8 ГБ рвётся, и повторять её с
// нуля неприемлемо. Поэтому первым делом спрашивается `status`: сервер
// говорит, какие куски у него уже есть, и заливаются только недостающие.
// Ответ 409 на кусок — это «он уже лежит», а не ошибка: так выглядит
// гонка повтора с ответом, устаревшим на полсекунды.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Upload = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const DEFAULT_CHUNK = 8 * 1024 * 1024;

  /* ---------- Версия сборки ---------- */

  /**
   * Что не так с номером версии.
   *
   * Правило то же, что у сервера (`IsSafeVersion`): номер становится
   * именем файла и частью адреса манифеста. Сервер откажет теми же
   * словами, но уже после того, как человек выберет файл на полтора
   * гигабайта, — а здесь это видно до.
   */
  const VERSION_RE = /^[A-Za-z0-9._-]+$/;
  function versionProblem(v) {
    const s = String(v === undefined || v === null ? '' : v).trim();
    if (!s) return 'Без номера версии сборку некуда положить';
    if (s === '.' || s === '..') return 'Такой номер означает папку, а не версию';
    if (!VERSION_RE.test(s)) return 'В номере только латиница, цифры, точка, дефис и подчёркивание';
    if (!/^\d+\.\d+\.\d+$/.test(s)) return 'Номер обычно из трёх чисел через точку — например, 1.6.47';
    return '';
  }

  /**
   * Следующий номер по порядку.
   *
   * Предлагается, а не навязывается: девять раз из десяти выпуск —
   * очередной патч, и набирать номер руками значит однажды ошибиться в
   * нём. Порт `bumpSemverPatch` из панели 1.0.
   */
  function nextVersion(current) {
    const v = String(current === undefined || current === null ? '' : current).trim();
    const three = /^(\d+)\.(\d+)\.(\d+)$/.exec(v);
    if (three) return three[1] + '.' + three[2] + '.' + (Number(three[3]) + 1);
    const two = /^(\d+)\.(\d+)$/.exec(v);
    if (two) return two[1] + '.' + two[2] + '.1';
    return '1.0.1';
  }

  /** Индексы кусков, которых у сервера нет. */
  function missingChunks(totalChunks, received) {
    const have = new Set((received || []).map((x) => Number(x) | 0));
    const out = [];
    for (let i = 0; i < totalChunks; i++) {
      if (!have.has(i)) out.push(i);
    }
    return out;
  }

  /** Сколько кусков нужно на файл. Пустой файл — это ноль кусков, а не один. */
  function chunkCount(totalSize, chunkSize) {
    const size = Number(chunkSize) > 0 ? Number(chunkSize) : DEFAULT_CHUNK;
    return Math.ceil(Math.max(0, Number(totalSize) || 0) / size);
  }

  /** Границы куска по его номеру. */
  function chunkRange(index, chunkSize, totalSize) {
    const start = index * chunkSize;
    return { start: start, end: Math.min(start + chunkSize, totalSize) };
  }

  /**
   * Доля выполненного.
   *
   * Считается по подтверждённым кускам ПЛЮС по байтам тех, что сейчас
   * летят: без второго слагаемого полоса стоит на месте всё время
   * восьмимегабайтного куска и дёргается скачком — выглядит как зависание.
   */
  function progress(confirmedBytes, inFlightBytes, totalBytes) {
    if (!totalBytes) return 0;
    const done = Math.min(totalBytes, confirmedBytes + inFlightBytes);
    return done / totalBytes;
  }

  /**
   * Ведёт загрузку от начала до конца.
   *
   * deps:
   *   api      — слой обращений (init/status/complete/cleanup/abort)
   *   chunks   — модуль chunk-upload.js версии 1.0
   *   slice    — (file, start, end) => кусок; в браузере это file.slice
   *   on       — сообщения о ходе: { phase, progress, message, uploaded }
   *   signal   — отмена
   */
  async function run(file, meta, deps) {
    const d = deps || {};
    const api = d.api;
    const chunks = d.chunks;
    const slice = d.slice || ((f, a, b) => f.slice(a, b));
    const on = d.on || function () {};
    const concurrency = d.concurrency || (() => 4);

    const say = (phase, extra) => on(Object.assign({ phase: phase }, extra || {}));

    say('init');
    const init = await api.uploadInit({
      kind: meta.kind,
      gameId: meta.gameId,
      version: meta.version,
      zipName: file.name,
      totalSize: file.size,
      chunkSize: meta.chunkSize || DEFAULT_CHUNK,
    });

    const uploadId = init && init.uploadId;
    if (!uploadId) throw new Error('сервер не выдал номер загрузки');

    const chunkSize = Number(init.chunkSize) > 0 ? Number(init.chunkSize) : meta.chunkSize || DEFAULT_CHUNK;
    const total = Number(init.totalChunks) > 0 ? Number(init.totalChunks) : chunkCount(file.size, chunkSize);

    /* Докачка: спрашиваем, что уже лежит. Ошибку здесь глотаем намеренно —
       не ответивший `status` означает «начнём сначала», а не «всё пропало». */
    let received = [];
    try {
      const st = await api.uploadStatus(uploadId);
      if (st && Array.isArray(st.received)) received = st.received;
    } catch {
      received = [];
    }

    let todo = missingChunks(total, received);
    const confirmed = new Set(received.map((x) => Number(x) | 0));

    say('upload', {
      uploadId: uploadId,
      total: total,
      resumed: received.length,
      progress: progress(confirmed.size * chunkSize, 0, file.size),
    });

    const inFlight = new Map();
    const report = () => {
      say('upload', {
        uploadId: uploadId,
        total: total,
        done: confirmed.size,
        progress: progress(confirmed.size * chunkSize, chunks.pendingBytes(inFlight), file.size),
      });
    };

    const worker = async (index) => {
      if (d.signal && d.signal.aborted) return false;
      const r = chunkRange(index, chunkSize, file.size);
      const blob = slice(file, r.start, r.end);
      inFlight.set(index, 0);

      const res = await chunks.uploadChunkWithRetries(uploadId, index, blob, {
        url: '/admin/api/upload/chunk?uploadId=' + encodeURIComponent(uploadId) + '&index=' + index,
        onProgress: (loaded) => {
          inFlight.set(index, loaded);
          report();
        },
        deps: d.xhrDeps,
        put: d.put,
        maxAttempts: d.maxAttempts,
        retryDelayMs: d.retryDelayMs,
      });

      inFlight.delete(index);
      if (res.ok) confirmed.add(index);
      report();
      return res.ok;
    };

    let failed = await chunks.runWorkerPool(todo, concurrency, worker);

    /* Второй проход по сорвавшимся. Он не «на всякий случай»: сервер мог
       ответить 500 на один кусок из трёхсот, и требовать из-за этого
       перезаливать всё — то же самое, что не иметь докачки. */
    if (failed.length) {
      say('retry', { count: failed.length });
      failed = await chunks.runWorkerPool(failed, concurrency, worker);
    }

    if (failed.length) {
      throw new Error('не удалось залить ' + failed.length + ' из ' + total);
    }

    say('complete');
    await api.uploadComplete({ uploadId: uploadId });

    return { uploadId: uploadId, total: total, chunkSize: chunkSize, resumed: received.length };
  }

  /**
   * Что показывать по событию разбора архива.
   *
   * Разбор — отдельный шаг после сборки файла, и идёт он минутами:
   * сервер распаковывает архив, считает sha256 каждого файла и пишет
   * манифест. Без своей строки этот шаг выглядит как зависание ровно в
   * тот момент, когда всё уже почти готово.
   */
  function processMessage(ev, format) {
    const e = ev || {};
    const f = format || (typeof window !== 'undefined' && window.CH2Format);
    const bytes = (n) => (f ? f.bytes(n) : String(n));
    if (e.type === 'start') return { text: 'Начали разбор архива', done: false };
    if (e.type === 'unzip') return { text: 'Распаковка: ' + (e.path || ''), done: false };
    if (e.type === 'composeStart') return { text: 'Готовим манифест: ' + (e.totalFiles || 0) + ' файлов', done: false };
    if (e.type === 'file') {
      return { text: 'Манифест: ' + (e.idx || 0) + ' файлов, ' + bytes(e.bytesDone || 0), done: false };
    }
    if (e.type === 'done') return { text: 'Манифест записан', done: true };
    if (e.type === 'error') return { text: 'Ошибка разбора: ' + (e.message || 'сбой'), done: false, failed: true };
    return null;
  }

  /**
   * Разбор архива на сервере.
   *
   * Метод обязательно POST: обработчик распаковывает архив, публикует
   * версию и удаляет ZIP, а CSRF-проверка на сервере действует только
   * для изменяющих методов — GET оставил бы это без защиты.
   *
   * Поток, не давший ни одной строки, — не успех. Так выглядит прокси,
   * сложивший ответ в буфер и оборвавший его: выдать это за «готово»
   * значит объявить версию загруженной, не зная этого.
   */
  async function process(uploadId, deps) {
    const d = deps || {};
    const on = d.on || function () {};
    let res;
    try {
      res = await d.fetch('/admin/api/upload/process?uploadId=' + encodeURIComponent(uploadId), {
        method: 'POST',
        headers: { accept: 'application/x-ndjson', 'cache-control': 'no-store' },
      });
    } catch {
      return { ok: false, message: 'сервер не отвечает' };
    }
    if (!res.ok) return { ok: false, message: 'код ' + res.status };

    let failed = '';
    let done = false;
    let lines = 0;
    await d.ndjson.readNdjsonStream(res, (ev) => {
      lines++;
      const m = processMessage(ev, d.format);
      if (!m) return;
      if (m.failed) failed = m.text;
      if (m.done) done = true;
      on(m);
    });

    if (failed) return { ok: false, message: failed };
    if (!lines) return { ok: false, message: 'сервер оборвал разбор молча — проверьте список версий' };
    if (!done) return { ok: false, message: 'разбор оборвался, не дописав манифест' };
    return { ok: true };
  }

  /** Отмена: сервер должен убрать за собой недособранную загрузку. */
  async function abort(api, uploadId) {
    try {
      await api.uploadAbort(uploadId);
      return true;
    } catch {
      return false;
    }
  }

  return {
    DEFAULT_CHUNK: DEFAULT_CHUNK,
    VERSION_RE: VERSION_RE,
    versionProblem: versionProblem,
    nextVersion: nextVersion,
    missingChunks: missingChunks,
    chunkCount: chunkCount,
    chunkRange: chunkRange,
    progress: progress,
    run: run,
    processMessage: processMessage,
    process: process,
    abort: abort,
  };
});
