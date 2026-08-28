// Тесты server/admin_ui/upload-tuning.js — автоподбора параметров заливки.
// Появился после реального случая: на «16 потоков / 16 МБ» заливка замирала,
// график скорости шёл пилой, а в самом начале показывал больше 100 МБ/с на
// канале, где столько не бывает. Причина у всех трёх симптомов общая —
// параметры выставлялись руками и не были связаны ни с размером файла, ни с
// тем, сколько запросов браузер вообще может открыть одновременно.
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const {
  pickUploadParams, pickConcurrency, connectionCap, rateWindowMs, MIN_CHUNK, MAX_CHUNK,
} = require(path.join('..', '..', 'server', 'admin_ui', 'upload-tuning.js'));

const MB = 1024 * 1024;
const GB = 1024 * MB;
// Тот же список, что в <select> карточки загрузки (upload-card.js).
const OPTIONS = [65536, 131072, 262144, 524288, 1 * MB, 2 * MB, 4 * MB, 8 * MB,
  16 * MB, 32 * MB, 64 * MB, 128 * MB, 256 * MB, 512 * MB];

test('потолок параллельности — 6 на HTTP/1.1 и больше на HTTP/2', () => {
  assert.strictEqual(connectionCap('http/1.1'), 6);
  assert.strictEqual(connectionCap('h2'), 12);
  assert.strictEqual(connectionCap('h3'), 12);
});

test('неизвестный протокол берёт консервативный потолок HTTP/1.1', () => {
  // Пустая строка — это «Resource Timing ничего не сказал», и предполагать
  // при этом HTTP/2 нельзя: ошибка в эту сторону возвращает ровно тот сценарий
  // с очередью запросов, ради которого потолок и заведён.
  assert.strictEqual(connectionCap(''), 6);
  assert.strictEqual(connectionCap(undefined), 6);
});

test('параллельность растёт с размером файла, но не выше потолка соединений', () => {
  assert.strictEqual(pickConcurrency(10 * MB, 'http/1.1'), 2);
  assert.strictEqual(pickConcurrency(300 * MB, 'http/1.1'), 4);
  assert.strictEqual(pickConcurrency(5 * GB, 'http/1.1'), 6);
});

test('1.3 ГБ по HTTP/1.1 — это 16 МБ на 6 потоков', () => {
  // Тот самый файл из-за которого всё началось: 84 чанка, четырнадцать волн
  // по 6 потоков. Ни очереди запросов в браузере, ни рваного хвоста.
  const p = pickUploadParams(1.3 * GB, { protocol: 'http/1.1', chunkOptions: OPTIONS });
  assert.strictEqual(p.chunkSize, 16 * MB);
  assert.strictEqual(p.concurrency, 6);
  assert.strictEqual(p.chunks, Math.ceil(1.3 * GB / (16 * MB)));
});

test('подобранный размер чанка всегда из списка <select>', () => {
  for (const size of [30 * MB, 300 * MB, 1.3 * GB, 4 * GB, 30 * GB]) {
    const p = pickUploadParams(size, { protocol: 'http/1.1', chunkOptions: OPTIONS });
    assert.ok(OPTIONS.includes(p.chunkSize), 'размер ' + p.chunkSize + ' отсутствует в списке');
  }
});

test('автоподбор не уходит за границы разумного размера чанка', () => {
  // Ни 64 КБ на мелком файле (полмиллиона запросов на большой заливке —
  // ровно то, от чего защищается planChunks на сервере), ни 512 МБ на
  // огромном: сорванный чанк такого размера перезаливается вечно.
  const small = pickUploadParams(3 * MB, { chunkOptions: OPTIONS });
  const huge = pickUploadParams(30 * GB, { chunkOptions: OPTIONS });
  assert.strictEqual(small.chunkSize, MIN_CHUNK);
  assert.strictEqual(huge.chunkSize, MAX_CHUNK);
});

test('чанков всегда хотя бы вдвое больше, чем потоков', () => {
  // Иначе последняя волна рваная: часть потоков закончила, один дотягивает
  // свой кусок, канал в это время стоит.
  for (const size of [1 * MB, 3 * MB, 20 * MB, 100 * MB, 700 * MB, 2 * GB, 20 * GB]) {
    const p = pickUploadParams(size, { protocol: 'h2', chunkOptions: OPTIONS });
    assert.ok(p.chunks >= p.concurrency * 2 || p.concurrency === 1,
      'размер ' + size + ': ' + p.chunks + ' чанков на ' + p.concurrency + ' потоков');
  }
});

test('пустой размер не превращается в ноль потоков или ноль чанков', () => {
  const p = pickUploadParams(0, { chunkOptions: OPTIONS });
  assert.strictEqual(p.concurrency, 1);
  assert.strictEqual(p.chunks, 1);
  assert.ok(p.chunkSize > 0);
});

test('без списка вариантов возвращается точный расчёт, а не округление', () => {
  const p = pickUploadParams(1.3 * GB, { protocol: 'http/1.1' });
  assert.ok(p.chunkSize >= MIN_CHUNK && p.chunkSize <= MAX_CHUNK);
  assert.ok(!OPTIONS.includes(p.chunkSize), 'без chunkOptions округлять не к чему');
});

test('окно скорости по умолчанию — 5 секунд, пока о частоте чанков ничего не известно', () => {
  assert.strictEqual(rateWindowMs([]), 5000);
  assert.strictEqual(rateWindowMs(null), 5000);
});

test('частые чанки не сужают окно ниже пяти секунд', () => {
  // Шесть потоков по 16 МБ на быстром канале подтверждают чанк втрое чаще
  // раза в секунду. Четыре таких интервала — это 1.4с, а окно уже сглаживания
  // не даёт: минимум держится.
  assert.strictEqual(rateWindowMs([350, 360, 340, 370]), 5000);
});

test('редкие чанки расширяют окно до четырёх интервалов', () => {
  assert.strictEqual(rateWindowMs([3000, 3000, 3000]), 12000);
});

test('окно не растёт бесконечно — потолок 20 секунд', () => {
  assert.strictEqual(rateWindowMs([60000, 60000, 60000]), 20000);
});

test('единичный ретрай с длинной паузой не раздувает окно', () => {
  // Медиана, а не среднее: одна пауза в минуту среди секундных интервалов
  // иначе прибила бы окно к потолку до конца заливки.
  assert.strictEqual(rateWindowMs([800, 900, 60000, 850, 870]), 5000);
});

test('мусор в интервалах игнорируется, а не даёт NaN', () => {
  assert.strictEqual(rateWindowMs([NaN, -5, 0, Infinity]), 5000);
});

test('список без единого подходящего варианта не роняет подбор в null', () => {
  // Границы автоподбора и список <select> заданы в разных файлах, и если они
  // когда-нибудь разойдутся так, что пересечения не останется, лучше выдать
  // ближайшее из имеющегося, чем chunkSize=null и деление на него.
  const p = pickUploadParams(1.3 * GB, { chunkOptions: [65536, 512 * MB] });
  assert.ok(p.chunkSize === 65536 || p.chunkSize === 512 * MB);
  assert.ok(p.chunks > 0);
});
