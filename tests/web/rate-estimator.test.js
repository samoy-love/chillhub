// Тесты server/admin_ui/rate-estimator.js — скользящего окна для скорости
// загрузки. Появился после реального случая на проде: при параллельности 32
// чанки завершались волнами (десятки в одну секунду — сервер это тоже видит,
// см. writeMs в логе), а старый расчёт "байты с прошлого тика / 200мс"
// показывал всплески в сотни МБ/с ровно на каждой такой волне. См. комментарий
// в шапке файла.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { pushByteSample, windowedRate } = require(path.join('..', '..', 'server', 'admin_ui', 'rate-estimator.js'));

test('windowedRate на пустом и однолементном буфере — 0, не NaN/Infinity', () => {
  assert.strictEqual(windowedRate([]), 0);
  assert.strictEqual(windowedRate([{ t: 0, bytes: 100 }]), 0);
});

test('windowedRate считает байты/сек между самой старой и самой новой точкой', () => {
  const samples = [{ t: 0, bytes: 0 }, { t: 2000, bytes: 20_000_000 }];
  assert.strictEqual(windowedRate(samples), 10_000_000);
});

test('windowedRate игнорирует промежуточные точки — важны только края окна', () => {
  const samples = [
    { t: 0, bytes: 0 },
    { t: 500, bytes: 999_999_999 }, // выброс посередине не должен влиять на результат
    { t: 1000, bytes: 5_000_000 },
  ];
  assert.strictEqual(windowedRate(samples), 5_000_000);
});

test('windowedRate возвращает 0 при нулевой или отрицательной разнице времени', () => {
  assert.strictEqual(windowedRate([{ t: 100, bytes: 0 }, { t: 100, bytes: 5000 }]), 0);
});

test('pushByteSample выкидывает точки старше окна от последней добавленной', () => {
  let samples = [];
  samples = pushByteSample(samples, { t: 0, bytes: 0 }, 4000);
  samples = pushByteSample(samples, { t: 1000, bytes: 1000 }, 4000);
  samples = pushByteSample(samples, { t: 3000, bytes: 3000 }, 4000);
  // Пятая точка на t=6000 должна вытолкнуть t=0 (6000-0=6000 > 4000), но
  // оставить t=1000 (6000-1000=5000... тоже > 4000 -> тоже вылетает),
  // а вот t=3000 (6000-3000=3000 <= 4000) остаётся.
  samples = pushByteSample(samples, { t: 6000, bytes: 6000 }, 4000);
  assert.deepStrictEqual(samples.map(s => s.t), [3000, 6000]);
});

test('pushByteSample никогда не опустошает буфер полностью (хотя бы одна точка остаётся)', () => {
  let samples = [];
  samples = pushByteSample(samples, { t: 0, bytes: 0 }, 100);
  samples = pushByteSample(samples, { t: 100_000, bytes: 999 }, 100); // огромный разрыв
  assert.strictEqual(samples.length, 1, 'должна остаться хотя бы последняя точка');
  assert.strictEqual(samples[0].t, 100_000);
});

test('сценарий с прода: волна из 32 чанков подтверждается в один тик — скорость размазывается по окну', () => {
  const chunkBytes = 16 * 1024 * 1024; // 16 МБ
  let samples = [];
  const windowMs = 4000;
  // Устойчивая заливка первые 18 секунд: понемногу растущий счётчик "в полёте".
  for (let t = 0; t <= 18000; t += 200) {
    samples = pushByteSample(samples, { t, bytes: Math.round((t / 18000) * 32 * chunkBytes) }, windowMs);
  }
  // Волна: все 32 чанка подтверждаются в один тик на t=18200 — тот самый скачок.
  samples = pushByteSample(samples, { t: 18200, bytes: 32 * chunkBytes }, windowMs);
  const rate = windowedRate(samples);
  // Мгновенный расчёт (старая логика) на этом тике дал бы (32*16МБ)/0.2с ≈ 2.6 ГБ/с.
  const naiveInstantRate = (32 * chunkBytes) / 0.2;
  assert.ok(rate < naiveInstantRate / 10, 'окно должно радикально сгладить всплеск: ' + rate + ' vs ' + naiveInstantRate);
  // При этом окно не должно занижать скорость до нуля — заливка реально шла.
  assert.ok(rate > 0);
});
