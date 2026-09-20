// Сборка модпака: запуск и чтение потока.
//
// ПОЧЕМУ ПОТОК, А НЕ ПОЛОСКА. Сборка тянет до 1,8 ГБ полутора сотнями
// запросов и идёт до двадцати минут. Полоска прогресса на таком отрезке
// не отличает «качается большой файл» от «зависло», и человек либо ждёт
// впустую, либо перезапускает работающую сборку. Поэтому сервер шлёт
// строки NDJSON по мере работы, а панель их показывает.
//
// Само чтение потока — в `ndjson.js` версии 1.0, вместе с разбором
// неполных строк на границе кусков. Здесь порядок и решения.
//
// ОДИН СЛУЧАЙ ЧЕЛОВЕК МОЖЕТ РАЗРЕШИТЬ САМ. Если пакет пропал с
// Thunderstore, сборка останавливается — но её можно повторить без него.
// Это единственная ошибка, на которую есть осмысленный ответ, и
// единственная, о которой стоит спрашивать; остальные требуют разбора.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Build = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /** Пропавший с Thunderstore пакет — единственная восстановимая ошибка. */
  const MISSING = /больше нет на Thunderstore/;

  const isMissing = (message) => MISSING.test(String(message || ''));

  /**
   * Приводит событие потока к одному виду.
   *
   * Сервер шлёт события разной формы, и в панели 1.0 каждое место
   * доставало из них поля по-своему. Здесь одно место.
   */
  function normalize(ev) {
    const e = ev || {};
    const kind = String(e.type || e.kind || e.k || 'info');
    return {
      kind: kind,
      message: String(e.message || e.m || e.msg || ''),
      at: e.t || e.at || '',
      failed: kind === 'error',
    };
  }

  /**
   * Итог сборки по накопленным событиям.
   *
   * Отсутствие событий — отдельный исход, а не успех: так выглядит ответ,
   * который прокси задержал целиком вместо того, чтобы отдавать по мере
   * поступления. Считать это удачей нельзя, но и ошибкой сборки — тоже.
   */
  function outcome(events, seen) {
    const list = events || [];
    const failure = list.find((e) => e.failed);

    if (!seen) {
      return {
        ok: false,
        kind: 'buffered',
        message: 'Сервер не прислал ни одного события — похоже, ответ где-то задержали целиком.',
      };
    }
    if (failure && isMissing(failure.message)) {
      return { ok: false, kind: 'missing', message: failure.message, recoverable: true };
    }
    if (failure) {
      return { ok: false, kind: 'error', message: failure.message || 'сборка не удалась' };
    }
    return {
      ok: true,
      kind: 'done',
      message: 'Модпак собран. Чтобы игроки его получили, отдайте новую версию.',
    };
  }

  /**
   * Во что превратить тело неудачного ответа.
   *
   * Прокси и сам сервер на ошибке отдают страницу, а не разбор: вывести
   * её как есть значит вывалить человеку кусок HTML вместо причины.
   * Годится только короткий текст или поле `error` из разбора — иначе
   * остаётся код ответа, который хотя бы честен.
   */
  function errorText(body, status) {
    const raw = String(body || '').trim();
    const code = 'код ' + status;
    if (!raw) return code;

    try {
      const parsed = JSON.parse(raw);
      const msg = parsed && (parsed.error || parsed.message);
      if (msg) return String(msg).slice(0, 300);
    } catch {
      // не разбор — значит, страница или простой текст
    }

    if (/[<>]/.test(raw) || raw.length > 300) return code;
    return raw;
  }

  /**
   * Тело запроса.
   *
   * У пересборки состав не спрашивают: его читают из записи рядом с
   * манифестом. Пакет и пространство имён в теле пересборки не только
   * лишние — они бы и врали: у сборки, приехавшей профилем r2modman,
   * имени пакета на Thunderstore нет вовсе.
   */
  function requestBody(opts) {
    const o = opts || {};
    const body = o.rebuild
      ? { gameId: o.gameId, version: o.version }
      : { gameId: o.gameId, namespace: o.namespace, name: o.name };
    /* Адрес страницы пакета сервер разбирает сам. Он есть там, где
       проверка обновлений молчит — у свежего модпака её попросту нет, —
       и потому едет вместе с именем, а не вместо него. */
    if (!o.rebuild && o.packageUrl) body.packageUrl = o.packageUrl;
    if (!o.rebuild && o.version) body.version = o.version;
    if (o.allowMissing) body.allowMissing = '1';
    return body;
  }

  /** Куда уходит запрос: сборка нового состава и пересборка прежнего — разные вещи. */
  function endpoint(opts) {
    return opts && opts.rebuild ? '/admin/api/mods/rebuild' : '/admin/api/mods/build';
  }

  /**
   * ФОРМА, А НЕ РАЗБОР. Сервер читает и сборку, и пересборку через
   * `r.FormValue` — так же, как все остальные записи админки. Тело,
   * отправленное разбором, он просто не видит: `gameId` приходит пустым,
   * и обе кнопки отвечают «invalid gameId», не начав работу.
   */
  function encodeBody(body) {
    const p = new URLSearchParams();
    for (const k of Object.keys(body || {})) {
      if (body[k] !== undefined && body[k] !== null && body[k] !== '') p.set(k, String(body[k]));
    }
    return p.toString();
  }

  /**
   * Ведёт сборку.
   *
   * deps: { fetch, ndjson, on, confirm }
   * `confirm` спрашивают ровно один раз и только про пропавшие пакеты.
   */
  async function run(opts, deps) {
    const d = deps || {};
    const doFetch = d.fetch;
    const ndjson = d.ndjson;
    const on = d.on || function () {};

    const events = [];
    const emit = (ev) => {
      const n = normalize(ev);
      events.push(n);
      on(n);
    };

    let res;
    try {
      res = await doFetch(endpoint(opts), {
        method: 'POST',
        headers: {
          accept: 'application/x-ndjson',
          'cache-control': 'no-store',
          'content-type': 'application/x-www-form-urlencoded',
        },
        body: encodeBody(requestBody(opts)),
      });
    } catch {
      return { ok: false, kind: 'error', message: 'сервер не отвечает', events: events };
    }

    if (!res.ok) {
      let text = '';
      try {
        text = await res.text();
      } catch {
        text = '';
      }
      return { ok: false, kind: 'error', message: errorText(text, res.status), events: events };
    }

    const seen = await ndjson.readNdjsonStream(res, emit);
    const result = outcome(events, seen);

    /* Пропавшие пакеты: спрашиваем один раз и повторяем уже без них.
       Повтор не задаёт вопрос второй раз — иначе отказ на середине
       превратился бы в бесконечный диалог. */
    if (result.kind === 'missing' && !opts.allowMissing && d.confirm) {
      const agreed = await d.confirm({
        title: 'Собрать модпак без пропавших пакетов?',
        body: result.message,
        ok: 'Собрать без них',
        cancel: 'Отмена',
      });
      if (agreed) {
        return run(Object.assign({}, opts, { allowMissing: true }), deps);
      }
      return Object.assign({}, result, { cancelled: true, events: events });
    }

    return Object.assign({}, result, { events: events });
  }

  return {
    MISSING: MISSING,
    isMissing: isMissing,
    errorText: errorText,
    normalize: normalize,
    outcome: outcome,
    requestBody: requestBody,
    endpoint: endpoint,
    encodeBody: encodeBody,
    run: run,
  };
});
