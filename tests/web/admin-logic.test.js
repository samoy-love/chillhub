// Тесты чистой логики админки: версии, разбор дат и форматирование.
//
// Функции вытаскиваются из admin.js так же, как в admin-sanitize.test.js: скрипт
// браузерный, целиком в node не загружается, а эти функции чистые.
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const src = fs.readFileSync(
  path.join(__dirname, '..', '..', 'server', 'admin_ui', 'admin.js'), 'utf8');

function extract(name) {
  const re = new RegExp('function\\s+' + name + '\\s*\\([\\s\\S]*?\\n\\}', 'm');
  const m = src.match(re);
  if (!m) throw new Error('не найдена функция ' + name);
  return m[0];
}

function load(...names) {
  // Функции ссылаются друг на друга (normalizeHumanDate зовёт toRfc3339),
  // поэтому объявляем их в одной области.
  const body = names.map(extract).join('\n');
  return new Function(body + '\nreturn {' + names.join(',') + '};')();
}

const { bumpSemverPatch } = load('bumpSemverPatch');
const { formatBytes } = load('formatBytes');
const { normalizeHumanDate, toRfc3339 } = load('toRfc3339', 'normalizeHumanDate');

test('bumpSemverPatch поднимает патч, а не что-то другое', () => {
  assert.strictEqual(bumpSemverPatch('1.2.2'), '1.2.3');
  assert.strictEqual(bumpSemverPatch('0.0.0'), '0.0.1');
  // Перенос через девятку: 9 -> 10, а не 1.2.91 и не 1.3.0
  assert.strictEqual(bumpSemverPatch('1.2.9'), '1.2.10');
  assert.strictEqual(bumpSemverPatch('1.9.99'), '1.9.100');
});

test('bumpSemverPatch терпит пробелы и неполную версию', () => {
  assert.strictEqual(bumpSemverPatch('  1.2.3  '), '1.2.4');
  // Только major.minor — достраивается до патча
  assert.strictEqual(bumpSemverPatch('1.2'), '1.2.1');
});

test('bumpSemverPatch на мусоре даёт валидную версию, а не мусор', () => {
  // Важно: результат подставляется в поле версии и уходит в манифест.
  // Пустая или битая строка там означала бы неустановимую сборку.
  for (const bad of ['', '   ', 'abc', '1', 'v1.2.3', '1.2.3.4', null, undefined]) {
    const out = bumpSemverPatch(bad);
    assert.match(out, /^\d+\.\d+\.\d+$/, 'мусор ' + JSON.stringify(bad) + ' дал ' + JSON.stringify(out));
  }
});

test('formatBytes читаем и не выдаёт NaN', () => {
  for (const n of [0, 1, 1023, 1024, 1024 * 1024, 1024 ** 3, 1024 ** 4, -1, null, undefined, NaN]) {
    const s = String(formatBytes(n));
    assert.ok(!/NaN|Infinity|undefined/.test(s), 'formatBytes(' + n + ') = ' + s);
  }
});

test('разбор даты понимает оба формата фильтра', () => {
  // Поля фильтра в инбоксе принимают ISO и русский формат, с временем и без.
  for (const s of ['2026-01-15', '15.01.2026', '2026-01-15 10:30', '15.01.2026 10:30']) {
    const out = normalizeHumanDate(s, false);
    assert.match(out, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/, s + ' -> ' + out);
  }
});

test('признак endOfDay сдвигает время к концу суток', () => {
  const start = normalizeHumanDate('2026-01-15', false);
  const end = normalizeHumanDate('2026-01-15', true);
  assert.notStrictEqual(start, end, 'начало и конец суток обязаны различаться');
  // Конец суток должен быть ПОЗЖЕ начала — иначе фильтр «с … по …» не найдёт ничего.
  assert.ok(new Date(end) > new Date(start), start + ' .. ' + end);
});

test('пустая и неразбираемая дата дают пустую строку, а не Invalid Date', () => {
  // 99.99.9999 и 31.02 проходят регэксп, но не календарь: раньше Date молча
  // переполнялся и фильтр уезжал на другую дату (вплоть до пятизначного года,
  // который сервер в RFC3339 не разбирает).
  for (const bad of ['', '   ', 'вчера', '99.99.9999', '31.02.2026', '2026-02-31', '2026-13-01', null, undefined]) {
    const out = normalizeHumanDate(bad, false);
    assert.ok(out === '' || /^\d{4}-/.test(out),
      JSON.stringify(bad) + ' дало ' + JSON.stringify(out));
    assert.ok(!/Invalid/.test(String(out)));
  }
});

test('toRfc3339 всегда выдаёт UTC с суффиксом Z', () => {
  const out = toRfc3339(new Date(Date.UTC(2026, 0, 15, 10, 30, 45)));
  assert.strictEqual(out, '2026-01-15T10:30:45Z');
});

test('toRfc3339 дополняет однозначные числа нулём', () => {
  const out = toRfc3339(new Date(Date.UTC(2026, 0, 5, 4, 3, 2)));
  assert.strictEqual(out, '2026-01-05T04:03:02Z');
});
