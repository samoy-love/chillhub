// Тесты server/admin_ui/rate-estimator.js — скользящего окна для скорости
// загрузки. Появился после реального случая на проде: на экране скорость
// прыгала между нулём и сотнями МБ/с несколько раз в секунду. Считалась она
// как "байты с прошлого тика / 200мс", а байты приходят из
// xhr.upload.onprogress, то есть отражают опустошение буфера сокета ОС, а не
// сеть: буфер полон — прогресса нет, разгрёбся — за один тик прилетает
// несколько мегабайт. См. комментарий в шапке файла.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { makeRateEstimator } = require(path.join('..', '..', 'server', 'admin_ui', 'rate-estimator.js'));

test('скорость неизвестна (0, не NaN/Infinity), пока в окне меньше двух точек', () => {
  const est = makeRateEstimator(4000);
  assert.strictEqual(est.rate(), 0);
  assert.strictEqual(est.push(0, 100), 0);
});

test('скорость — байты/сек между самой старой и самой новой точкой окна', () => {
  const est = makeRateEstimator(4000);
  est.push(0, 0);
  assert.strictEqual(est.push(2000, 20_000_000), 10_000_000);
});

test('промежуточные точки не влияют — важны только края окна', () => {
  const est = makeRateEstimator(4000);
  est.push(0, 0);
  est.push(500, 4_500_000); // рывок в середине окна: 9 МБ/с на этом отрезке
  est.push(700, 4_500_000); // и тут же полка
  assert.strictEqual(est.push(1000, 5_000_000), 5_000_000);
});

test('нулевая разница времени между краями окна даёт 0, а не Infinity', () => {
  const est = makeRateEstimator(4000);
  est.push(100, 0);
  assert.strictEqual(est.push(100, 5000), 0);
});

test('точки старше окна выбрасываются', () => {
  const est = makeRateEstimator(4000);
  est.push(0, 0);
  est.push(1000, 1_000_000);
  est.push(3000, 3_000_000);
  // t=6000 выталкивает t=0 и t=1000 (обе старше 4000мс от неё), остаётся
  // пара t=3000/t=6000: 3 МБ за 3 секунды.
  assert.strictEqual(est.push(6000, 6_000_000), 1_000_000);
});

test('после огромного разрыва во времени остаётся одна точка и скорость обнуляется', () => {
  const est = makeRateEstimator(100);
  est.push(0, 0);
  est.push(1000, 1000);
  // Буфер не опустошается полностью — но одной точки мало для скорости.
  assert.strictEqual(est.push(100_000, 999_999), 0);
});

test('рывок буфера сокета размазывается по окну, а не выдаётся за скорость канала', () => {
  const est = makeRateEstimator(5000);
  // 5 секунд буфер стоит: onprogress не двигается вовсе.
  let rate = 0;
  for (let t = 0; t <= 5000; t += 200) rate = est.push(t, 10_000_000);
  assert.strictEqual(rate, 0, 'стоячий счётчик — нулевая скорость');
  // Буфер разгрёбся: 20 МБ «прилетело» за один тик в 200мс.
  rate = est.push(5200, 30_000_000);
  const naiveInstantRate = 20_000_000 / 0.2; // 100 МБ/с — то, что показывал старый расчёт
  assert.ok(rate < naiveInstantRate / 10, 'окно должно сгладить рывок: ' + rate + ' vs ' + naiveInstantRate);
  assert.ok(rate > 0);
});

test('откат счётчика не уводит скорость в минус', () => {
  // Так ведёт себя чанковая заливка: при ретрае недоотправленные байты чанка
  // выкидываются из inFlight, и displayed уменьшается.
  const est = makeRateEstimator(5000);
  est.push(0, 0);
  est.push(1000, 10_000_000);
  est.push(2000, 20_000_000);
  // Чанк сорвался: 8 МБ учтённого прогресса откатились назад.
  const afterDrop = est.push(2200, 12_000_000);
  assert.ok(afterDrop > 0, 'скорость не должна уходить в минус: ' + afterDrop);
  assert.strictEqual(afterDrop, 20_000_000 / 2.2, 'откат — полка, а не движение назад');
  // Пока байты перезаливаются, окно видит полку и скорость честно падает,
  // но остаётся положительной, а не NaN.
  const duringRetry = est.push(3200, 15_000_000);
  assert.ok(duringRetry > 0);
  assert.ok(duringRetry < afterDrop, 'простой на перезаливке виден как падение скорости');
  // Как только счётчик перевалил прежний максимум, скорость снова растёт.
  assert.ok(est.push(4200, 30_000_000) > duringRetry);
});

test('монотонный рост даёт ровно ту скорость, с которой шла заливка', () => {
  const est = makeRateEstimator(5000);
  const bps = 12_500_000; // 100 Мбит/с
  let rate = 0;
  for (let t = 0; t <= 20000; t += 200) rate = est.push(t, (bps * t) / 1000);
  assert.strictEqual(Math.round(rate), bps);
});

test('spanMs показывает, сколько времени покрывает окно', () => {
  const est = makeRateEstimator(4000);
  // Меньше двух точек — окна нет, и доверять нечему.
  assert.strictEqual(est.spanMs(), 0);
  est.push(1000, 0);
  assert.strictEqual(est.spanMs(), 0);
  est.push(1200, 5_000_000);
  assert.strictEqual(est.spanMs(), 200);
  est.push(4000, 9_000_000);
  assert.strictEqual(est.spanMs(), 3000);
});

test('spanMs не считает точки, выпавшие из окна', () => {
  // Окно 1 с: старые точки выбрасываются push-ем, и span обязан это отражать —
  // иначе вызывающий код решит, что накопил длинную историю.
  const est = makeRateEstimator(1000);
  est.push(0, 0);
  est.push(500, 1000);
  est.push(5000, 2000);
  assert.ok(est.spanMs() <= 1000, 'span = ' + est.spanMs());
});

test('короткое окно выдаёт скорость, которой на канале не было', () => {
  // Ровно тот случай, ради которого появился spanMs: два подтверждения чанка
  // с разницей в 10 мс дают сотни МБ/с. Сама rate() честно считает, что ей
  // дали, — отсеивать такие замеры обязан вызывающий, по span.
  const est = makeRateEstimator(30000);
  est.push(0, 0);
  const bogus = est.push(10, 8 * 1024 * 1024);
  assert.ok(bogus > 500 * 1024 * 1024, 'ожидался всплеск, получено ' + bogus);
  assert.strictEqual(est.spanMs(), 10);
});
