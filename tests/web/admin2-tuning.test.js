// Подбор параметров загрузки и уход за кэшем в панели 2.0.
//
// Главное правило здесь: быстрейший прогон — не всегда лучший. Больше
// потоков не значит быстрее, на восьми канал начинает терять куски и
// переспрашивать их заново. Повтор на пробе стоит доли секунды, а на
// полуторагигабайтном файле — минуты.

const test = require('node:test');
const assert = require('node:assert');

const T = require('../../server/admin_ui/tuning.js');
const tuning = require('../../server/admin_ui/upload-tuning.js');

const run = (chunk, streams, mbps, retries) => ({ chunk, streams, mbps, retries: retries || 0 });

/* ---------- Выбор лучшего ---------- */

test('без повторов побеждает самый быстрый', () => {
  const top = T.best([run('4 МиБ', 4, 61.7), run('8 МиБ', 4, 92.4), run('2 МиБ', 2, 40)]);
  assert.strictEqual(top.chunk, '8 МиБ');
});

test('повторы отнимают у прогона его преимущество', () => {
  // 79,3 с тремя повторами хуже 74 без них: повтор на настоящем файле длиннее
  const top = T.best([run('2 МиБ', 8, 79.3, 3), run('8 МиБ', 4, 74)]);
  assert.strictEqual(top.streams, 4);
});

test('малое число повторов преимущества не отменяет', () => {
  const top = T.best([run('8 МиБ', 4, 92.4, 1), run('4 МиБ', 4, 70)]);
  assert.strictEqual(top.chunk, '8 МиБ');
});

test('прогон с нулевой скоростью в выбор не попадает', () => {
  const top = T.best([run('8 МиБ', 4, 0), run('4 МиБ', 2, 12)]);
  assert.strictEqual(top.chunk, '4 МиБ');
});

test('пустой список прогонов — нечего выбирать', () => {
  assert.strictEqual(T.best([]), null);
  assert.strictEqual(T.best(null), null);
  assert.strictEqual(T.best([run('8 МиБ', 4, 0)]), null);
});

test('оценка падает вместе с числом повторов', () => {
  assert.ok(T.score(run('x', 4, 100)) > T.score(run('x', 4, 100, 1)));
  assert.ok(T.score(run('x', 4, 100, 1)) > T.score(run('x', 4, 100, 5)));
});

test('прогон с запредельными повторами не уходит в минус', () => {
  assert.strictEqual(T.score(run('x', 4, 100, 50)), 0);
});

test('мусор в числах не ломает оценку', () => {
  assert.strictEqual(T.score({ mbps: 'быстро' }), 0);
  assert.strictEqual(T.score(null), 0);
});

/* ---------- Пометка и объяснение ---------- */

test('лучший помечается ровно один', () => {
  const marked = T.mark([run('4 МиБ', 4, 61.7), run('8 МиБ', 4, 92.4), run('2 МиБ', 2, 40)]);
  assert.strictEqual(marked.filter((r) => r.best).length, 1);
  assert.strictEqual(marked.find((r) => r.best).chunk, '8 МиБ');
});

test('пометка не переставляет прогоны местами', () => {
  const marked = T.mark([run('4 МиБ', 4, 61.7), run('8 МиБ', 4, 92.4)]);
  assert.deepStrictEqual(marked.map((r) => r.chunk), ['4 МиБ', '8 МиБ']);
});

test('объяснение называет причину, когда победил не самый быстрый', () => {
  // Без объяснения подбор выглядит гаданием, и его результату не верят
  const text = T.why([run('2 МиБ', 8, 79.3, 3), run('8 МиБ', 4, 74)]);
  assert.match(text, /Быстрее всех/);
  assert.match(text, /повторами \(3\)/);
  assert.match(text, /8 МиБ на 4 потоках/);
});

test('когда быстрейший и лучший совпали, объяснение короткое', () => {
  const text = T.why([run('8 МиБ', 4, 92.4), run('4 МиБ', 4, 61.7)]);
  assert.match(text, /без повторов/);
  assert.ok(!/Быстрее всех/.test(text));
});

test('без прогонов объяснение честно говорит, что выбирать не из чего', () => {
  assert.match(T.why([]), /Прогонов не было/);
});

test('применение берёт из прогона кусок и потоки', () => {
  assert.deepStrictEqual(T.apply(run('8 МиБ', 4, 92.4)), { chunk: '8 МиБ', streams: 4 });
  assert.strictEqual(T.apply(null), null);
});

/* ---------- Кэш ---------- */

test('пока места хватает, кэш чистить незачем', () => {
  // Кэш экономит время пересборки: те же архивы не качаются повторно
  const a = T.cacheAdvice({ bytes: 8 * 1024 ** 3 }, { free: 300 * 1024 ** 3, total: 480 * 1024 ** 3 });
  assert.strictEqual(a.level, 'no');
  assert.match(a.message, /экономит время пересборки/);
});

test('кончающееся место делает чистку срочной', () => {
  const a = T.cacheAdvice({ bytes: 8 * 1024 ** 3 }, { free: 20 * 1024 ** 3, total: 480 * 1024 ** 3 });
  assert.strictEqual(a.level, 'now');
});

test('разросшийся кэш — повод посмотреть, но не срочный', () => {
  const a = T.cacheAdvice({ bytes: 200 * 1024 ** 3 }, { free: 200 * 1024 ** 3, total: 480 * 1024 ** 3 });
  assert.strictEqual(a.level, 'soon');
  assert.match(a.message, /не срочно/);
});

test('без данных о диске совет не пугает почём зря', () => {
  assert.strictEqual(T.cacheAdvice({ bytes: 0 }, null).level, 'no');
  assert.strictEqual(T.cacheAdvice(null, null).level, 'no');
});

/* ---------- Стыковка с подбором версии 1.0 ---------- */

test('автоподбор от размера файла берётся из модуля 1.0', () => {
  const p = tuning.pickUploadParams(1.5 * 1024 ** 3, {});
  assert.ok(p.chunkSize >= tuning.MIN_CHUNK && p.chunkSize <= tuning.MAX_CHUNK);
  assert.ok(p.concurrency >= 1);
});

test('маленькому файлу не назначают потоков больше, чем кусков', () => {
  // Потоки нечем занять, а хвост они делают рваным
  const p = tuning.pickUploadParams(tuning.MIN_CHUNK, {});
  assert.ok(p.concurrency <= Math.max(1, p.chunks));
});

test('пустой файл не превращается в ноль потоков', () => {
  const p = tuning.pickUploadParams(0, {});
  assert.ok(p.concurrency >= 1);
  assert.ok(p.chunkSize > 0);
});

/* ---------- Память о прогонах ---------- */

/** Хранилище, какое бывает в браузере. */
function storage() {
  const map = new Map();
  return {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v)),
    removeItem: (k) => map.delete(k),
  };
}

test('прогон запоминается и читается обратно', () => {
  // Он меряет канал ЭТОГО компьютера, поэтому и лежит в этом браузере
  const s = storage();
  const runs = [{ chunk: '8 МиБ', streams: 4, mbps: 92.4, retries: 0 }];
  assert.strictEqual(T.remember(s, runs), true);
  assert.deepStrictEqual(T.recall(s), runs);
});

test('без прошлого прогона возвращается пустой список, а не выдумка', () => {
  assert.deepStrictEqual(T.recall(storage()), []);
  assert.deepStrictEqual(T.recall(null), []);
});

test('мусор в хранилище — это его отсутствие', () => {
  const s = storage();
  s.setItem(T.KEY, 'не json');
  assert.deepStrictEqual(T.recall(s), []);
  s.setItem(T.KEY, JSON.stringify({ runs: 'не список' }));
  assert.deepStrictEqual(T.recall(s), []);
});

test('закрытое хранилище не роняет подбор', () => {
  const dead = {
    getItem: () => {
      throw new Error('заблокировано');
    },
    setItem: () => {
      throw new Error('заблокировано');
    },
  };
  assert.strictEqual(T.remember(dead, []), false);
  assert.deepStrictEqual(T.recall(dead), []);
  assert.strictEqual(T.remember(null, []), false);
});
