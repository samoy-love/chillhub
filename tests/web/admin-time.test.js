// Время в админке одно на всю панель — московское.
//
// До этого каждая вкладка показывала отметки сервера по-своему: список сборок —
// в зоне браузера и без секунд, обслуживание — в зоне браузера длинной строкой,
// события метрик и список модпаков печатали UTC как есть, отрезав «T» и «Z».
// Отличить одно от другого на экране было нельзя: везде просто дата и время.
//
// Зона зашита в сам форматтер, поэтому проверки ниже дают один и тот же ответ и
// на машине с TZ=UTC, и на машине в другом поясе.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { formatMoscow, formatMoscowTime, formatMoscowClock, MOSCOW_TZ } =
  require(path.join(__dirname, '..', '..', 'server', 'admin_ui', 'admin-time.js'));

test('время сервера показывается по Москве и подписано', () => {
  // Москва — UTC+3 круглый год: перевода часов нет с 2014-го.
  assert.strictEqual(formatMoscow('2026-08-17T18:35:29Z'), '2026-08-17 21:35:29 МСК');
  // Зимой смещение то же — проверяем, что не приехал переход на зимнее время.
  assert.strictEqual(formatMoscow('2026-01-05T09:00:00Z'), '2026-01-05 12:00:00 МСК');
});

test('переход через полночь сдвигает и дату', () => {
  // Ровно тот случай, ради которого всё затевалось: «собран 2026-08-30 21:43»
  // по UTC — это уже 31-е число по Москве, и в списке версий сборка иначе
  // выглядит вчерашней.
  assert.strictEqual(formatMoscow('2026-08-30T21:43:27Z'), '2026-08-31 00:43:27 МСК');
});

test('секунды не отбрасываются', () => {
  // Список сборок раньше показывал только часы и минуты, и две сборки подряд
  // выглядели сделанными в одну и ту же минуту.
  assert.match(formatMoscow('2026-08-17T18:35:29Z'), /:29 МСК$/);
});

test('пустое остаётся пустым, а непонятное показывается как есть', () => {
  assert.strictEqual(formatMoscow(''), '');
  assert.strictEqual(formatMoscow(null), '');
  assert.strictEqual(formatMoscow(undefined), '');
  assert.strictEqual(formatMoscow('   '), '');
  // «Invalid Date» в таблице — потерянная информация вместо непонятной.
  assert.strictEqual(formatMoscow('когда-то'), 'когда-то');
});

test('короткие формы отрезают дату, а не зону', () => {
  assert.strictEqual(formatMoscowTime('2026-08-17T18:35:29Z'), '21:35:29 МСК');
  assert.strictEqual(formatMoscowTime(''), '');

  // В журнале страницы таких отметок двести подряд, и «МСК» в каждой строке —
  // шум: зона там та же, что и во всей панели.
  assert.strictEqual(formatMoscowClock('2026-08-17T18:35:29Z'), '21:35:29');
  assert.strictEqual(formatMoscowClock(''), '');
});

test('принимается и число миллисекунд, а не только строка', () => {
  // nowHms и метка черновика зовут форматтер от Date.now().
  const ms = Date.UTC(2026, 7, 17, 18, 35, 29);
  assert.strictEqual(formatMoscow(ms), '2026-08-17 21:35:29 МСК');
});

test('зона названа явно', () => {
  assert.strictEqual(MOSCOW_TZ, 'Europe/Moscow');
});
