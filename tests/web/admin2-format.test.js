// Форматирование панели 2.0.
//
// Проверяется не «функция что-то вернула», а те решения, ради которых
// модуль вообще появился: русская запятая вместо точки, склонение по
// числу и прочерк вместо «Invalid Date» и «NaN».

const test = require('node:test');
const assert = require('node:assert');

const F = require('../../server/admin_ui/v2/format.js');

const NB = '\u00a0';

test('дробная часть отделяется запятой, а не точкой', () => {
  assert.strictEqual(F.dec(8.3), '8,3');
  assert.strictEqual(F.dec(92.44, 1), '92,4');
  assert.strictEqual(F.dec(10, 2), '10,00');
});

test('нечисло даёт прочерк, а не NaN на экране', () => {
  assert.strictEqual(F.dec(NaN), '—');
  assert.strictEqual(F.dec(Infinity), '—');
  assert.strictEqual(F.bytes(undefined), '—');
  assert.strictEqual(F.bytes(-1), '—');
  assert.strictEqual(F.eta(NaN), '—');
});

test('размер: единица через неразрывный пробел, доля до сотни единиц', () => {
  assert.strictEqual(F.bytes(0), `0${NB}Б`);
  assert.strictEqual(F.bytes(512), `512${NB}Б`);
  assert.strictEqual(F.bytes(1024), `1${NB}КБ`);
  // 1,5 ГБ — с долей, потому что меньше десяти
  assert.strictEqual(F.bytes(1.5 * 1024 ** 3), `1,5${NB}ГБ`);
  // 214 ГБ — без доли: десятая доля от сотен ничего не сообщает
  assert.strictEqual(F.bytes(214 * 1024 ** 3), `214${NB}ГБ`);
  // 10,5 МБ обязано пережить округление: на этой же функции печатается скорость
  assert.strictEqual(F.bytes(10.5 * 1024 ** 2), `10,5${NB}МБ`);
});

test('целое число не получает дописанный ноль', () => {
  assert.strictEqual(F.bytes(1024), `1${NB}КБ`);
  assert.strictEqual(F.bytes(5 * 1024 ** 4), `5${NB}ТБ`);
  assert.strictEqual(F.bytes(2 * 1024 ** 3), `2${NB}ГБ`);
});

test('размер не срывается за пределы известных единиц', () => {
  assert.strictEqual(F.bytes(5 * 1024 ** 4), `5${NB}ТБ`);
  assert.ok(F.bytes(9000 * 1024 ** 4).endsWith(`${NB}ТБ`));
});

test('склонение по числу покрывает 1, 2–4, 11–14 и круглые', () => {
  const p = (n) => F.plural(n, 'файл', 'файла', 'файлов');
  assert.strictEqual(p(1), 'файл');
  assert.strictEqual(p(21), 'файл');
  assert.strictEqual(p(2), 'файла');
  assert.strictEqual(p(23), 'файла');
  assert.strictEqual(p(5), 'файлов');
  assert.strictEqual(p(11), 'файлов'); // не «файл», хотя оканчивается на 1
  assert.strictEqual(p(12), 'файлов');
  assert.strictEqual(p(14), 'файлов');
  assert.strictEqual(p(100), 'файлов');
  assert.strictEqual(p(0), 'файлов');
});

test('склонение не ломается на отрицательных и дробных', () => {
  assert.strictEqual(F.plural(-1, 'день', 'дня', 'дней'), 'день');
  assert.strictEqual(F.plural(-3, 'день', 'дня', 'дней'), 'дня');
  assert.strictEqual(F.plural(2.7, 'день', 'дня', 'дней'), 'дня');
});

test('число со словом склеивается неразрывным пробелом', () => {
  assert.strictEqual(F.count(3, 'игра', 'игры', 'игр'), `3${NB}игры`);
});

test('доля в процентах: запятая и неразрывный пробел перед знаком', () => {
  assert.strictEqual(F.percent(201, 1950), `10,3${NB}%`);
  assert.strictEqual(F.percent(1, 3, 2), `33,33${NB}%`);
});

test('деление на ноль в доле не выдаёт Infinity', () => {
  assert.strictEqual(F.percent(5, 0), '—');
});

test('дата: пусто и мусор дают прочерк, а не Invalid Date', () => {
  assert.strictEqual(F.date(''), '—');
  assert.strictEqual(F.date(null), '—');
  assert.strictEqual(F.date('вчера'), '—');
  assert.strictEqual(F.dateTime('вчера'), '—');
});

test('дата выводится с ведущими нулями', () => {
  assert.strictEqual(F.date(new Date(2026, 8, 4)), '04.09.2026');
  assert.strictEqual(F.dateTime(new Date(2026, 8, 4, 3, 7)), '04.09.2026 03:07');
});

test('оставшееся время повторяет формат лаунчера', () => {
  assert.strictEqual(F.eta(43), `43${NB}с`);
  assert.strictEqual(F.eta(90), `2${NB}мин`); // округление вверх
  assert.strictEqual(F.eta(3725), `1${NB}ч 02${NB}мин`);
  assert.strictEqual(F.eta(90000), `1${NB}день 1${NB}ч`);
  assert.strictEqual(F.eta(172800), `2${NB}дня`);
});

test('отрицательное оставшееся время не показывается минусом', () => {
  assert.strictEqual(F.eta(-5), `0${NB}с`);
});

test('скорость — это размер со знаменателем', () => {
  assert.strictEqual(F.speed(10.5 * 1024 ** 2), `10,5${NB}МБ/с`);
});

/* ---------- Зона ---------- */

test('время подписано зоной, в которой показано', () => {
  // Сервер хранит UTC, показывается местное; без подписи одно читается
  // как другое, и назначенные работы уезжают на три часа
  const s = F.dateTimeZoned('2026-09-04T00:12:00Z');
  assert.match(s, /^04\.09\.2026 \d{2}:\d{2} UTC/);
});

test('подпись зоны верна и там, куда человек уехал', () => {
  // Поэтому смещение браузера, а не жёстко вписанное «МСК»
  const z = F.zone(new Date('2026-09-04T00:12:00Z'));
  assert.match(z, /^UTC([+−]\d{1,2}(:\d{2})?)?$/, z);
});

test('нулевое смещение называется UTC, а не «UTC+0»', () => {
  const utc = { getTimezoneOffset: () => 0 };
  assert.strictEqual(F.zone(utc), 'UTC');
});

test('получасовые зоны не округляются до часа', () => {
  // Индия — UTC+5:30, и «UTC+5» там просто неверно
  assert.strictEqual(F.zone({ getTimezoneOffset: () => -330 }), 'UTC+5:30');
  assert.strictEqual(F.zone({ getTimezoneOffset: () => 210 }), 'UTC−3:30');
});

test('пустое и непонятное время остаётся прочерком, а не «Invalid Date»', () => {
  assert.strictEqual(F.dateTimeZoned(''), '—');
  assert.strictEqual(F.dateTimeZoned('не дата'), '—');
  assert.strictEqual(F.dateTimeZoned(null), '—');
});
