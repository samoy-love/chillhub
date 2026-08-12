// Тесты ядра инструмента подбора параметров загрузки (server/admin_ui/upload-bench.js).
//
// В отличие от admin-logic.test.js и admin-sanitize.test.js, этот модуль не
// вытаскивается регэкспом из admin.js: он самостоятельный CommonJS-файл и
// require()-ится как есть, поэтому c8 видит его построчно, а не как строку,
// исполненную через new Function (см. комментарий в шапке upload-bench.js).
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const {
  parseBenchList,
  benchCombos,
  pickClosestChunkOption,
  benchUploadOnce,
  benchProbeBytes,
  benchPlan,
  benchProgress,
} = require(path.join('..', '..', 'server', 'admin_ui', 'upload-bench.js'));

test('parseBenchList разбирает список и масштабирует', () => {
  assert.deepStrictEqual(parseBenchList('4,8,16', 1), [4, 8, 16]);
  assert.deepStrictEqual(parseBenchList('1, 2 ,3', 1024), [1024, 2048, 3072]);
});

test('parseBenchList отбрасывает мусор молча', () => {
  assert.deepStrictEqual(parseBenchList('4,,abc,-1,0,8', 1), [4, 8]);
  assert.deepStrictEqual(parseBenchList('', 1), []);
  assert.deepStrictEqual(parseBenchList(null, 1), []);
  assert.deepStrictEqual(parseBenchList(undefined, 1), []);
});

test('benchCombos — декартово произведение чанков и параллельности', () => {
  const combos = benchCombos([4, 8], [2, 6]);
  assert.deepStrictEqual(combos, [
    { cs: 4, c: 2 }, { cs: 4, c: 6 },
    { cs: 8, c: 2 }, { cs: 8, c: 6 },
  ]);
});

test('benchCombos на пустом списке даёт пустой список, а не исключение', () => {
  assert.deepStrictEqual(benchCombos([], [2, 6]), []);
  assert.deepStrictEqual(benchCombos([4, 8], []), []);
});

test('pickClosestChunkOption находит ближайшее доступное значение', () => {
  const options = [65536, 1048576, 8388608, 33554432];
  assert.strictEqual(pickClosestChunkOption(options, 8000000), 8388608);
  assert.strictEqual(pickClosestChunkOption(options, 100), 65536);
  assert.strictEqual(pickClosestChunkOption(options, 1e9), 33554432);
  // Значение ровно между двумя опциями — берётся первое из встреченных по
  // порядку (та же логика, что раньше жила инлайном в applyBenchBest).
  assert.strictEqual(pickClosestChunkOption([100, 200], 150), 100);
});

test('pickClosestChunkOption на пустом списке возвращает null', () => {
  assert.strictEqual(pickClosestChunkOption([], 123), null);
});

// fakeFile имитирует ровно ту часть File/Blob API, которую использует
// benchUploadOnce: .size и .slice(start, end) -> объект с .size.
function fakeFile(size) {
  return {
    size,
    slice(start, end) {
      return { size: Math.max(0, Math.min(end, size) - start) };
    },
  };
}

// fakeFetch маршрутизирует по URL так же, как это делают реальные ручки
// /admin/api/upload/{init,chunk,abort}, но без сети — только чтобы проверить,
// что benchUploadOnce шлёт нужные запросы и правильно считает результат.
function fakeFetch({ chunkSize, totalChunks, failChunkIndex, initOk = true, chunkWriteMs = 5 } = {}) {
  const calls = { init: 0, chunk: [], abort: 0 };
  return {
    calls,
    fetch: async (url, _opts) => {
      if (url.startsWith('/admin/api/upload/init')) {
        calls.init++;
        if (!initOk) { return { ok: false, status: 500 }; }
        return {
          ok: true,
          json: async () => ({ uploadId: 'deadbeef', chunkSize, totalChunks }),
        };
      }
      if (url.startsWith('/admin/api/upload/chunk')) {
        const idx = Number(new URL(url, 'http://x').searchParams.get('index'));
        calls.chunk.push(idx);
        if (failChunkIndex !== undefined && idx === failChunkIndex) {
          return { ok: false, status: 500 };
        }
        return { ok: true, json: async () => ({ writeMs: chunkWriteMs }) };
      }
      if (url.startsWith('/admin/api/upload/abort')) {
        calls.abort++;
        return { ok: true, json: async () => ({ status: 'ok' }) };
      }
      throw new Error('unexpected url ' + url);
    },
  };
}

test('benchUploadOnce загружает все чанки пробы и считает скорость', async () => {
  const chunkSize = 1024 * 1024;
  const totalChunks = 4;
  const { fetch, calls } = fakeFetch({ chunkSize, totalChunks });
  let clock = 0;
  const now = () => { clock += 100; return clock; }; // 100ms шаг на каждый вызов now()

  const r = await benchUploadOnce(fakeFile(chunkSize * totalChunks), 1, 2, chunkSize * totalChunks, { fetch, now });

  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.chunkSize, chunkSize);
  assert.strictEqual(r.concurrency, 2);
  assert.strictEqual(r.bytes, chunkSize * totalChunks);
  assert.ok(r.speed > 0, 'скорость должна быть положительной: ' + r.speed);
  assert.strictEqual(calls.init, 1);
  assert.strictEqual(calls.chunk.length, totalChunks);
  // Проба должна быть отброшена сразу же, а не оставлена до /upload/cleanup.
  assert.strictEqual(calls.abort, 1);
});

test('benchUploadOnce всё равно отбрасывает пробу, если чанк не залился', async () => {
  const chunkSize = 1024 * 1024;
  const totalChunks = 3;
  const { fetch, calls } = fakeFetch({ chunkSize, totalChunks, failChunkIndex: 1 });

  const r = await benchUploadOnce(fakeFile(chunkSize * totalChunks), 1, 2, chunkSize * totalChunks, { fetch });

  assert.strictEqual(r.ok, false);
  assert.ok(r.error, 'должна быть причина отказа');
  assert.strictEqual(calls.abort, 1, 'неудачная проба не должна оставаться на диске');
});

test('benchUploadOnce возвращает ok:false, если init отказал', async () => {
  const { fetch, calls } = fakeFetch({ initOk: false });
  const r = await benchUploadOnce(fakeFile(1024 * 1024), 1, 2, 1024 * 1024, { fetch });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /HTTP 500 init/);
  assert.strictEqual(calls.abort, 0, 'нечего отбрасывать — upload/init не выдал id');
});

test('benchUploadOnce переживает сетевую ошибку на init', async () => {
  const fetch = async () => { throw new Error('boom'); };
  const r = await benchUploadOnce(fakeFile(1024), 1, 1, 1024, { fetch });
  assert.strictEqual(r.ok, false);
  assert.match(r.error, /boom/);
});

test('benchUploadOnce ограничивает пробу probeBytes, не заливая весь файл', async () => {
  const chunkSize = 1024 * 1024;
  const probeBytes = chunkSize * 2;
  const totalChunks = 2; // сервер сам вернул бы totalChunks по totalSize=probeBytes
  const { fetch, calls } = fakeFetch({ chunkSize, totalChunks });

  const r = await benchUploadOnce(fakeFile(chunkSize * 100), 1, 4, probeBytes, { fetch });

  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.bytes, probeBytes, 'должна залиться только проба, а не весь 100-чанковый файл');
  assert.strictEqual(calls.chunk.length, totalChunks);
});

// ---- Сколько прогон стоит: объём и оценка времени ----
//
// Раньше единственным индикатором была строка «Тест 1/25», которая не менялась
// минутами: ни объёма, ни времени, ни скорости. Эти три функции — арифметика
// нового статуса, и ошибка в них врёт пользователю ровно там, где он и так не
// понимает, сколько ещё ждать.

test('benchProbeBytes: чанк крупнее пробы заливается целиком', () => {
  const MB = 1024 * 1024;
  // Проба 512 МБ, чанк 256 МБ — уедет 512 МБ.
  assert.strictEqual(benchProbeBytes(256, 512 * MB, 10000 * MB), 512 * MB);
  // Проба 100 МБ, чанк 256 МБ — меньше одного чанка залить нельзя, уедет 256 МБ.
  assert.strictEqual(benchProbeBytes(256, 100 * MB, 10000 * MB), 256 * MB);
  // Файл меньше пробы — ограничение по файлу.
  assert.strictEqual(benchProbeBytes(1, 512 * MB, 7 * MB), 7 * MB);
});

test('benchPlan считает сетку и суммарный объём прогона', () => {
  const MB = 1024 * 1024;
  const plan = benchPlan([16, 32], [4, 8], 512 * MB, 10000 * MB);
  assert.strictEqual(plan.combos.length, 4);
  // Все четыре ячейки заливают ровно пробу: чанки меньше неё.
  assert.strictEqual(plan.totalBytes, 4 * 512 * MB);
  assert.deepStrictEqual(plan.combos[0], { cs: 16, c: 4, bytes: 512 * MB });
});

test('benchPlan учитывает, что крупный чанк раздувает ячейку', () => {
  const MB = 1024 * 1024;
  // Чанк 256 МБ при пробе 64 МБ: ячейка зальёт 256 МБ, а не 64.
  const plan = benchPlan([16, 256], [1], 64 * MB, 10000 * MB);
  assert.strictEqual(plan.totalBytes, 64 * MB + 256 * MB);
});

test('benchPlan на пустых списках даёт пустой прогон, а не исключение', () => {
  const plan = benchPlan([], [4], 1024, 1024);
  assert.deepStrictEqual(plan, { combos: [], totalBytes: 0 });
});

test('benchProgress считает долю, среднюю скорость и остаток', () => {
  const p = benchProgress({ doneBytes: 250, totalBytes: 1000, elapsedSec: 10, liveSpeed: 0 });
  assert.strictEqual(p.pct, 25);
  assert.strictEqual(p.avgSpeed, 25);        // 250 байт за 10 с
  assert.strictEqual(p.etaSec, 30);          // осталось 750 байт при 25 Б/с
});

test('benchProgress предпочитает живую скорость средней', () => {
  // Канал просел вдвое прямо сейчас: остаток обязан вырасти, а не остаться
  // оптимистичным по средней за весь прогон.
  const p = benchProgress({ doneBytes: 500, totalBytes: 1000, elapsedSec: 10, liveSpeed: 25 });
  assert.strictEqual(p.etaSec, 20);
  assert.strictEqual(p.avgSpeed, 50);
});

test('benchProgress отдаёт null вместо выдуманного остатка', () => {
  // На старте скорости ещё нет: «осталось 0 с» врёт убедительнее прочерка.
  const p = benchProgress({ doneBytes: 0, totalBytes: 1000, elapsedSec: 0 });
  assert.strictEqual(p.etaSec, null);
  assert.strictEqual(p.pct, 0);
  assert.strictEqual(benchProgress({}).etaSec, null);
});

test('benchProgress не даёт доле уйти выше 100 и остатку в минус', () => {
  const p = benchProgress({ doneBytes: 1500, totalBytes: 1000, elapsedSec: 10 });
  assert.strictEqual(p.pct, 100);
  assert.strictEqual(p.etaSec, 0);
});

// ---- Прогресс и остановка внутри одной комбинации ----

test('benchUploadOnce репортит прогресс по мере подтверждения чанков', async () => {
  const chunkSize = 1024 * 1024;
  const totalChunks = 4;
  const { fetch } = fakeFetch({ chunkSize, totalChunks });
  const seen = [];
  const r = await benchUploadOnce(fakeFile(chunkSize * totalChunks), 1, 2, chunkSize * totalChunks, {
    fetch,
    onProgress: (p) => seen.push(p),
  });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(seen.length, totalChunks, 'по событию на каждый чанк');
  // Счётчик обязан только расти — на нём держится расчёт скорости.
  for (let i = 1; i < seen.length; i++) {
    assert.ok(seen[i].uploadedBytes > seen[i - 1].uploadedBytes, 'байты должны расти');
  }
  const last = seen[seen.length - 1];
  assert.strictEqual(last.uploadedBytes, chunkSize * totalChunks);
  assert.strictEqual(last.totalSize, chunkSize * totalChunks);
  assert.strictEqual(last.chunksDone, totalChunks);
  assert.strictEqual(last.totalChunks, totalChunks);
});

test('benchUploadOnce останавливается по signal и всё равно отбрасывает пробу', async () => {
  const chunkSize = 1024 * 1024;
  const totalChunks = 8;
  const { fetch, calls } = fakeFetch({ chunkSize, totalChunks });
  const signal = { aborted: false };
  // Останавливаем после первого же подтверждённого чанка.
  const r = await benchUploadOnce(fakeFile(chunkSize * totalChunks), 1, 1, chunkSize * totalChunks, {
    fetch, signal,
    onProgress: () => { signal.aborted = true; },
  });
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.aborted, true);
  assert.ok(calls.chunk.length < totalChunks, 'остаток сетки заливаться не должен');
  // Незавершённая проба — это гигабайты на диске: abort обязателен и здесь.
  assert.strictEqual(calls.abort, 1);
});

test('benchUploadOnce с уже поднятым signal не заливает ни одного чанка', async () => {
  const chunkSize = 1024;
  const { fetch, calls } = fakeFetch({ chunkSize, totalChunks: 4 });
  const r = await benchUploadOnce(fakeFile(chunkSize * 4), 1, 2, chunkSize * 4, {
    fetch, signal: { aborted: true },
  });
  assert.strictEqual(r.aborted, true);
  assert.strictEqual(calls.chunk.length, 0);
});
