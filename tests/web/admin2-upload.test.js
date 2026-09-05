// Загрузка ZIP кусками в панели 2.0.
//
// Главное, что проверяется, — докачка. Загрузка на 1,8 ГБ рвётся, и
// повторять её с нуля неприемлемо: сервер говорит, какие куски у него
// есть, и заливаются только недостающие. Всё остальное здесь — про то,
// чтобы обрыв одного куска не отменял работу целиком.

const test = require('node:test');
const assert = require('node:assert');

const U = require('../../server/admin_ui/upload.js');
const chunks = require('../../server/admin_ui/chunk-upload.js');

/** Файл, у которого есть только размер, имя и нарезка. */
const fakeFile = (size, name) => ({
  name: name || 'ChillHub-1.0.0.zip',
  size: size,
  slice: (a, b) => ({ start: a, end: b, size: b - a }),
});

/** Панель без DOM: считает вызовы и отдаёт заданные ответы. */
function harness(opts) {
  const o = opts || {};
  const calls = { init: 0, status: 0, complete: 0, abort: 0 };
  const put = [];

  const api = {
    uploadInit: async (payload) => {
      calls.init++;
      calls.initPayload = payload;
      return Object.assign({ uploadId: 'u1', chunkSize: o.chunkSize || 1000 }, o.init || {});
    },
    uploadStatus: async () => {
      calls.status++;
      if (o.statusThrows) throw new Error('нет ответа');
      return { received: o.received || [] };
    },
    uploadComplete: async () => { calls.complete++; return { ok: true }; },
    uploadAbort: async () => { calls.abort++; return { ok: true }; },
  };

  const fail = new Set(o.failOnce || []);
  const putImpl = async (url, blob, onProgress) => {
    const index = Number(new URL('http://x' + url).searchParams.get('index'));
    put.push(index);
    if (onProgress) onProgress(blob.size);
    if (o.alwaysFail && o.alwaysFail.includes(index)) return { ok: false, status: 500, json: null };
    if (fail.has(index)) {
      fail.delete(index);
      return { ok: false, status: 500, json: null };
    }
    if (o.conflict && o.conflict.includes(index)) return { ok: false, status: 409, json: null };
    return { ok: true, status: 200, json: {} };
  };

  const events = [];
  return {
    calls, put, events,
    deps: {
      api, chunks,
      slice: (f, a, b) => f.slice(a, b),
      put: putImpl,
      retryDelayMs: 1,
      maxAttempts: o.maxAttempts || 2,
      concurrency: () => o.concurrency || 3,
      on: (e) => events.push(e),
    },
  };
}

/* ---------- Счёт кусков ---------- */

test('число кусков считается вверх, остаток тоже кусок', () => {
  assert.strictEqual(U.chunkCount(1000, 1000), 1);
  assert.strictEqual(U.chunkCount(1001, 1000), 2);
  assert.strictEqual(U.chunkCount(2500, 1000), 3);
});

test('пустой файл — это ноль кусков, а не один пустой', () => {
  assert.strictEqual(U.chunkCount(0, 1000), 0);
});

test('последний кусок обрезается по концу файла', () => {
  assert.deepStrictEqual(U.chunkRange(0, 1000, 2500), { start: 0, end: 1000 });
  assert.deepStrictEqual(U.chunkRange(2, 1000, 2500), { start: 2000, end: 2500 });
});

test('недостающие куски — это те, которых у сервера нет', () => {
  assert.deepStrictEqual(U.missingChunks(5, [0, 2]), [1, 3, 4]);
  assert.deepStrictEqual(U.missingChunks(3, []), [0, 1, 2]);
  assert.deepStrictEqual(U.missingChunks(3, [0, 1, 2]), []);
});

test('номера кусков от сервера могут прийти строками', () => {
  assert.deepStrictEqual(U.missingChunks(3, ['0', '2']), [1]);
});

/* ---------- Полоса ---------- */

test('полоса учитывает и подтверждённое, и летящее прямо сейчас', () => {
  // Без второго слагаемого полоса стоит всё время восьмимегабайтного
  // куска и потом дёргается скачком — выглядит как зависание
  assert.strictEqual(U.progress(0, 500, 1000), 0.5);
  assert.strictEqual(U.progress(1000, 0, 2000), 0.5);
});

test('полоса не переваливает за сто процентов', () => {
  assert.strictEqual(U.progress(900, 300, 1000), 1);
});

test('нулевой размер не делит на ноль', () => {
  assert.strictEqual(U.progress(0, 0, 0), 0);
});

/* ---------- Ход загрузки ---------- */

test('загрузка проходит init, куски и complete', async () => {
  const h = harness();
  const res = await U.run(fakeFile(2500), { kind: 'launcher', version: '1.0.0' }, h.deps);

  assert.strictEqual(h.calls.init, 1);
  assert.strictEqual(h.calls.complete, 1);
  assert.deepStrictEqual(h.put.slice().sort(), [0, 1, 2]);
  assert.strictEqual(res.total, 3);
});

test('в init уходит то, что просил сервер: имя, размер, версия', async () => {
  const h = harness();
  await U.run(fakeFile(2500, 'build.zip'), { kind: 'game', gameId: 'repo', version: '1.2.3' }, h.deps);
  assert.deepStrictEqual(h.calls.initPayload, {
    kind: 'game', gameId: 'repo', version: '1.2.3',
    zipName: 'build.zip', totalSize: 2500, chunkSize: U.DEFAULT_CHUNK,
  });
});

test('докачка не льёт то, что уже лежит на сервере', async () => {
  const h = harness({ received: [0, 1] });
  await U.run(fakeFile(2500), { kind: 'launcher', version: '1.0.0' }, h.deps);
  // Повторять 1,8 ГБ из-за обрыва на последнем куске неприемлемо
  assert.deepStrictEqual(h.put, [2]);
});

test('всё уже лежит — заливать нечего, но собрать надо', async () => {
  const h = harness({ received: [0, 1, 2] });
  await U.run(fakeFile(2500), { kind: 'launcher', version: '1.0.0' }, h.deps);
  assert.deepStrictEqual(h.put, []);
  assert.strictEqual(h.calls.complete, 1);
});

test('молчащий status не отменяет загрузку, а начинает её сначала', async () => {
  const h = harness({ statusThrows: true });
  await U.run(fakeFile(2000), { kind: 'launcher', version: '1.0.0' }, h.deps);
  assert.deepStrictEqual(h.put.slice().sort(), [0, 1]);
  assert.strictEqual(h.calls.complete, 1);
});

test('сорвавшийся кусок повторяется, а не роняет всю загрузку', async () => {
  const h = harness({ failOnce: [1] });
  await U.run(fakeFile(3000), { kind: 'launcher', version: '1.0.0' }, h.deps);
  // Единица уходила дважды: первый раз сорвалась
  assert.strictEqual(h.put.filter((i) => i === 1).length, 2);
  assert.strictEqual(h.calls.complete, 1);
});

test('ответ 409 на кусок — это «он уже лежит», а не ошибка', async () => {
  // Так выглядит гонка повтора с ответом, устаревшим на полсекунды
  const h = harness({ conflict: [0, 1] });
  await U.run(fakeFile(2000), { kind: 'launcher', version: '1.0.0' }, h.deps);
  assert.strictEqual(h.calls.complete, 1);
});

test('безнадёжный кусок останавливает загрузку с внятной причиной', async () => {
  const h = harness({ alwaysFail: [1] });
  await assert.rejects(
    U.run(fakeFile(3000), { kind: 'launcher', version: '1.0.0' }, h.deps),
    /не удалось залить 1 из 3/
  );
  // Собирать из недолитого нельзя
  assert.strictEqual(h.calls.complete, 0);
});

test('сервер без номера загрузки — это отказ, а не молчаливый успех', async () => {
  const h = harness({ init: { uploadId: '' } });
  await assert.rejects(U.run(fakeFile(1000), { kind: 'launcher' }, h.deps), /не выдал номер/);
});

test('размер куска диктует сервер, а не пожелание панели', async () => {
  const h = harness({ chunkSize: 500 });
  const res = await U.run(fakeFile(2000), { kind: 'launcher', chunkSize: 1000 }, h.deps);
  assert.strictEqual(res.chunkSize, 500);
  assert.strictEqual(res.total, 4);
});

test('о ходе загрузки сообщается по шагам, а не одним «готово»', async () => {
  const h = harness();
  await U.run(fakeFile(2000), { kind: 'launcher', version: '1.0.0' }, h.deps);
  const phases = [...new Set(h.events.map((e) => e.phase))];
  assert.deepStrictEqual(phases, ['init', 'upload', 'complete']);

  const last = h.events.filter((e) => e.phase === 'upload').pop();
  assert.strictEqual(last.progress, 1, 'полоса обязана дойти до конца');
});

test('докачка сообщает, сколько нашлось на сервере', async () => {
  const h = harness({ received: [0] });
  await U.run(fakeFile(2000), { kind: 'launcher', version: '1.0.0' }, h.deps);
  const first = h.events.find((e) => e.phase === 'upload');
  assert.strictEqual(first.resumed, 1);
});

test('отмена убирает за собой недособранную загрузку', async () => {
  const h = harness();
  const ok = await U.abort(h.deps.api, 'u1');
  assert.strictEqual(ok, true);
  assert.strictEqual(h.calls.abort, 1);
});

test('неудачная отмена не бросает наружу', async () => {
  const api = { uploadAbort: async () => { throw new Error('нет'); } };
  assert.strictEqual(await U.abort(api, 'u1'), false);
});

test('прерывание не даёт заливать оставшиеся куски', async () => {
  const h = harness();
  const controller = { aborted: true };
  h.deps.signal = controller;
  await assert.rejects(U.run(fakeFile(3000), { kind: 'launcher' }, h.deps), /не удалось залить/);
  assert.strictEqual(h.put.length, 0, 'после прерывания ни один кусок не уходит');
});

/* ---------- Разбор архива на сервере ---------- */

const ndjson = require('../../server/admin_ui/ndjson.js');
const format = require('../../server/admin_ui/format.js');

/** Ответ-поток из готовых строк NDJSON. */
function stream(lines) {
  const text = lines.map((l) => JSON.stringify(l)).join('\n') + '\n';
  return { ok: true, status: 200, text: async () => text };
}

test('каждый шаг разбора называет себя, а не молчит', () => {
  // Разбор идёт минутами, и без своей строки выглядит как зависание
  assert.match(U.processMessage({ type: 'start' }).text, /Начали разбор/);
  assert.match(U.processMessage({ type: 'unzip', path: 'ChillHub.exe' }).text, /ChillHub\.exe/);
  assert.match(U.processMessage({ type: 'composeStart', totalFiles: 478 }).text, /478/);
  assert.strictEqual(U.processMessage({ type: 'done' }).done, true);
});

test('счётчик файлов показывает и объём, по-русски', () => {
  const m = U.processMessage({ type: 'file', idx: 120, bytesDone: 11010048 }, format);
  assert.match(m.text, /120 файлов/);
  assert.match(m.text, /10,5\u00a0МБ/);
});

test('неизвестное событие не превращается в пустую строку на экране', () => {
  assert.strictEqual(U.processMessage({ type: 'whatever' }), null);
  assert.strictEqual(U.processMessage(null), null);
});

test('разбор доходит до манифеста и считается успешным', async () => {
  const seen = [];
  const res = await U.process('u1', {
    fetch: async () => stream([{ type: 'start' }, { type: 'file', idx: 1 }, { type: 'done' }]),
    ndjson,
    format,
    on: (m) => seen.push(m.text),
  });
  assert.strictEqual(res.ok, true);
  assert.strictEqual(seen.length, 3);
});

test('ошибка в потоке — это провал, даже если поток дочитался', async () => {
  const res = await U.process('u1', {
    fetch: async () => stream([{ type: 'start' }, { type: 'error', message: 'битый архив' }]),
    ndjson,
    format,
  });
  assert.strictEqual(res.ok, false);
  assert.match(res.message, /битый архив/);
});

test('молчащий поток не выдаётся за успех', async () => {
  // Так выглядит прокси, сложивший ответ в буфер и оборвавший его
  const res = await U.process('u1', { fetch: async () => ({ ok: true, text: async () => '' }), ndjson, format });
  assert.strictEqual(res.ok, false);
  assert.match(res.message, /проверьте список версий/);
});

test('поток без «манифест записан» — не успех', async () => {
  const res = await U.process('u1', {
    fetch: async () => stream([{ type: 'start' }, { type: 'file', idx: 4 }]),
    ndjson,
    format,
  });
  assert.strictEqual(res.ok, false);
  assert.match(res.message, /не дописав манифест/);
});

test('упавший запрос разбора не притворяется удачей', async () => {
  const dead = await U.process('u1', {
    fetch: async () => {
      throw new Error('нет сети');
    },
    ndjson,
    format,
  });
  assert.strictEqual(dead.ok, false);
  assert.match(dead.message, /сервер не отвечает/);

  const bad = await U.process('u1', { fetch: async () => ({ ok: false, status: 500 }), ndjson, format });
  assert.strictEqual(bad.ok, false);
  assert.match(bad.message, /500/);
});

/* ---------- Номер версии ---------- */

test('годный номер проходит', () => {
  assert.strictEqual(U.versionProblem('1.6.47'), '');
  assert.strictEqual(U.versionProblem('  1.6.47  '), '', 'пробелы по краям не должны мешать');
});

test('номер, который сервер не примет, назван до выбора файла', () => {
  // Иначе отказ приходит после того, как выбран архив на полтора гигабайта
  assert.match(U.versionProblem(''), /Без номера/);
  assert.match(U.versionProblem('..'), /означает папку/);
  assert.match(U.versionProblem('версия'), /только латиница/);
  assert.match(U.versionProblem('1.6'), /из трёх чисел/);
  assert.match(U.versionProblem('1.6.47-beta'), /из трёх чисел/);
});

test('следующий номер предлагается патчем', () => {
  // Девять выпусков из десяти — очередной патч, и набирать номер руками
  // значит однажды в нём ошибиться
  assert.strictEqual(U.nextVersion('1.6.46'), '1.6.47');
  assert.strictEqual(U.nextVersion('1.6.9'), '1.6.10');
});

test('неполный и непонятный номер не мешают предложить годный', () => {
  assert.strictEqual(U.nextVersion('1.6'), '1.6.1');
  assert.strictEqual(U.nextVersion('мусор'), '1.0.1');
  assert.strictEqual(U.nextVersion(''), '1.0.1');
  assert.strictEqual(U.nextVersion(null), '1.0.1');
});

test('предложенный номер сам по себе годится', () => {
  for (const v of ['1.6.46', '1.6', '', 'мусор']) {
    assert.strictEqual(U.versionProblem(U.nextVersion(v)), '', v);
  }
});

/* ---------- Чистка старых версий ---------- */

test('версии сравниваются числами, а не буквами', () => {
  // По алфавиту «1.0.10» меньше «1.0.9», и чистка снесла бы не то
  const sorted = ['1.0.10', '1.0.9', '1.0.2'].sort(U.compareVersions);
  assert.deepStrictEqual(sorted, ['1.0.2', '1.0.9', '1.0.10']);
});

test('под нож идёт всё старше активной, кроме двух перед ней', () => {
  // Откатиться на шаг-два должно оставаться возможным
  const all = ['1.0.0', '1.0.1', '1.0.2', '1.0.3', '1.0.4', '1.0.10'];
  assert.deepStrictEqual(U.prunable(all, '1.0.4'), ['1.0.0', '1.0.1']);
});

test('всё новее активной не трогается: это загруженное и не отданное', () => {
  const all = ['1.0.0', '1.0.1', '1.0.2', '1.0.3', '1.0.9'];
  assert.ok(!U.prunable(all, '1.0.3').includes('1.0.9'));
});

test('перед активной меньше двух — удалять нечего', () => {
  assert.deepStrictEqual(U.prunable(['1.0.0', '1.0.1'], '1.0.1'), []);
  assert.deepStrictEqual(U.prunable(['1.0.0', '1.0.1', '1.0.2'], '1.0.2'), []);
});

test('без активной версии не удаляется ничего: отсчитывать не от чего', () => {
  const all = ['1.0.0', '1.0.1', '1.0.2', '1.0.3'];
  assert.deepStrictEqual(U.prunable(all, ''), []);
  assert.deepStrictEqual(U.prunable(all, '9.9.9'), []);
  assert.deepStrictEqual(U.prunable(null, '1.0.0'), []);
});
