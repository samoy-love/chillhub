// Сборка модпака потоком в панели 2.0.
//
// Проверяется то, ради чего поток и заведён: сборка идёт до двадцати
// минут, и панель обязана отличать «идёт» от «зависло», «не удалось» от
// «можно повторить без пропавшего пакета» и — отдельно — «ответ задержали
// целиком» от успеха.

const test = require('node:test');
const assert = require('node:assert');

const B = require('../../server/admin_ui/v2/build.js');
const ndjson = require('../../server/admin_ui/ndjson.js');

/** Ответ, отдающий заданные строки NDJSON по кускам. */
function streamOf(lines, opts) {
  const o = opts || {};
  const text = lines.map((l) => JSON.stringify(l)).join('\n') + (lines.length ? '\n' : '');
  const encoder = new TextEncoder();
  const data = encoder.encode(text);

  let sent = false;
  return {
    ok: o.ok !== false,
    status: o.status || 200,
    text: async () => o.body || '',
    body: {
      getReader: () => ({
        read: async () => {
          if (sent || !data.length) return { done: true, value: undefined };
          sent = true;
          return { done: false, value: data };
        },
        releaseLock() {},
      }),
    },
  };
}

const deps = (res, extra) =>
  Object.assign({ fetch: async () => res, ndjson: ndjson }, extra || {});

/* ---------- Разбор событий ---------- */

test('событие приводится к одному виду, как бы сервер его ни назвал', () => {
  assert.deepStrictEqual(B.normalize({ type: 'info', message: 'раз' }).message, 'раз');
  assert.deepStrictEqual(B.normalize({ k: 'ok', m: 'два' }).message, 'два');
  assert.strictEqual(B.normalize({ k: 'ok' }).kind, 'ok');
  assert.strictEqual(B.normalize(null).kind, 'info');
});

test('признак ошибки выставляется по типу события', () => {
  assert.strictEqual(B.normalize({ type: 'error', message: 'x' }).failed, true);
  assert.strictEqual(B.normalize({ type: 'warn', message: 'x' }).failed, false);
});

/* ---------- Итог ---------- */

test('поток без ошибок — это успех и подсказка про активацию', () => {
  const r = B.outcome([B.normalize({ type: 'ok', message: 'собрано' })], true);
  assert.strictEqual(r.ok, true);
  // Собранное само к игрокам не поедет — это отдельное решение
  assert.match(r.message, /отдайте новую версию/);
});

test('ни одного события — это не успех, а задержанный ответ', () => {
  const r = B.outcome([], false);
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.kind, 'buffered');
  assert.match(r.message, /задержали целиком/);
});

test('ошибка доносит текст сервера', () => {
  const r = B.outcome([B.normalize({ type: 'error', message: 'диск полон' })], true);
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.kind, 'error');
  assert.strictEqual(r.message, 'диск полон');
});

test('пропавший пакет — отдельный, восстановимый исход', () => {
  const msg = 'Пакета Evaisa-LethalLib больше нет на Thunderstore';
  const r = B.outcome([B.normalize({ type: 'error', message: msg })], true);
  assert.strictEqual(r.kind, 'missing');
  assert.strictEqual(r.recoverable, true);
});

test('прочие ошибки восстановимыми не считаются', () => {
  const r = B.outcome([B.normalize({ type: 'error', message: 'нет места' })], true);
  assert.notStrictEqual(r.kind, 'missing');
  assert.strictEqual(r.recoverable, undefined);
});

/* ---------- Тело запроса ---------- */

test('в запрос уходит игра и пакет, версия — только если названа', () => {
  assert.deepStrictEqual(B.requestBody({ gameId: 'repo', namespace: 'ASTeam', name: 'MooModpack' }), {
    gameId: 'repo', namespace: 'ASTeam', name: 'MooModpack',
  });
  const withVersion = B.requestBody({ gameId: 'repo', namespace: 'A', name: 'B', version: '1.9.9' });
  assert.strictEqual(withVersion.version, '1.9.9');
});

test('согласие собрать без пропавших уходит на сервер отдельным признаком', () => {
  assert.strictEqual(B.requestBody({ allowMissing: true }).allowMissing, '1');
  assert.strictEqual(B.requestBody({}).allowMissing, undefined);
});

/* ---------- Ход сборки ---------- */

test('строки потока приходят по мере работы, а не одним куском в конце', async () => {
  const seen = [];
  const res = streamOf([
    { type: 'info', message: 'разбор модпака' },
    { type: 'get', message: 'MoreCompany — 1.2 МБ' },
    { type: 'ok', message: 'версия собрана' },
  ]);
  const r = await B.run({ gameId: 'repo' }, deps(res, { on: (e) => seen.push(e.message) }));

  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(seen, ['разбор модпака', 'MoreCompany — 1.2 МБ', 'версия собрана']);
});

test('ошибка в потоке останавливает сборку и доносится наружу', async () => {
  const res = streamOf([
    { type: 'info', message: 'начали' },
    { type: 'error', message: 'на диске кончилось место' },
  ]);
  const r = await B.run({ gameId: 'repo' }, deps(res));
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.message, 'на диске кончилось место');
  assert.strictEqual(r.events.length, 2, 'события до ошибки не теряются');
});

test('пустой поток отличается от успешной сборки', async () => {
  const r = await B.run({ gameId: 'repo' }, deps(streamOf([])));
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.kind, 'buffered');
});

test('отказ сервера до потока показывается его же текстом', async () => {
  const res = streamOf([], { ok: false, status: 409, body: 'сборка уже идёт' });
  const r = await B.run({ gameId: 'repo' }, deps(res));
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.message, 'сборка уже идёт');
});

test('молчащая сеть не выглядит ошибкой сборки', async () => {
  const r = await B.run({ gameId: 'repo' }, {
    fetch: async () => { throw new Error('нет сети'); },
    ndjson: ndjson,
  });
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.message, 'сервер не отвечает');
});

test('про пропавшие пакеты спрашивают и повторяют уже без них', async () => {
  const missing = 'Пакета X больше нет на Thunderstore';
  const bodies = [];
  let call = 0;

  const r = await B.run({ gameId: 'repo', namespace: 'A', name: 'B' }, {
    ndjson: ndjson,
    confirm: async () => true,
    fetch: async (url, init) => {
      bodies.push(JSON.parse(init.body));
      call++;
      return call === 1
        ? streamOf([{ type: 'error', message: missing }])
        : streamOf([{ type: 'ok', message: 'собрано без них' }]);
    },
  });

  assert.strictEqual(r.ok, true);
  assert.strictEqual(bodies.length, 2);
  assert.strictEqual(bodies[0].allowMissing, undefined);
  assert.strictEqual(bodies[1].allowMissing, '1', 'повтор обязан разрешить пропуск');
});

test('отказ пересобирать без пропавших не выдаётся за сбой', async () => {
  const missing = 'Пакета X больше нет на Thunderstore';
  const r = await B.run({ gameId: 'repo' }, {
    ndjson: ndjson,
    confirm: async () => false,
    fetch: async () => streamOf([{ type: 'error', message: missing }]),
  });
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.cancelled, true);
});

test('второй раз про пропавшие пакеты не спрашивают', async () => {
  // Иначе отказ на середине превратился бы в бесконечный диалог
  const missing = 'Пакета X больше нет на Thunderstore';
  let asked = 0;
  const r = await B.run({ gameId: 'repo' }, {
    ndjson: ndjson,
    confirm: async () => { asked++; return true; },
    fetch: async () => streamOf([{ type: 'error', message: missing }]),
  });
  assert.strictEqual(asked, 1);
  assert.strictEqual(r.ok, false);
});

test('без функции подтверждения пропавшие пакеты остаются ошибкой', async () => {
  const missing = 'Пакета X больше нет на Thunderstore';
  const r = await B.run({ gameId: 'repo' }, deps(streamOf([{ type: 'error', message: missing }])));
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.kind, 'missing');
});

/* ---------- Тело неудачного ответа ---------- */

test('страница ошибки от прокси не вываливается человеку куском HTML', () => {
  // Прокси на 502 отдаёт страницу, а не разбор
  assert.strictEqual(B.errorText('<!DOCTYPE html><html><body>Bad Gateway</body></html>', 502), 'код 502');
});

test('разобранная причина показывается словами сервера', () => {
  assert.strictEqual(B.errorText(JSON.stringify({ error: 'нет такого модпака' }), 400), 'нет такого модпака');
  assert.strictEqual(B.errorText(JSON.stringify({ message: 'очередь занята' }), 409), 'очередь занята');
});

test('короткий простой текст проходит как есть', () => {
  assert.strictEqual(B.errorText('очередь занята', 409), 'очередь занята');
});

test('пустое тело и простыня текста уступают место коду ответа', () => {
  // Код ответа скучен, но он хотя бы честен
  assert.strictEqual(B.errorText('', 500), 'код 500');
  assert.strictEqual(B.errorText('x'.repeat(400), 500), 'код 500');
});

test('длинную причину из разбора обрезаем, а не выбрасываем', () => {
  const long = B.errorText(JSON.stringify({ error: 'ы'.repeat(500) }), 400);
  assert.strictEqual(long.length, 300);
});
