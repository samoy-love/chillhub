// Блок журнала в карточке обращения: что показать, что сказать про остальное.
//
// Журнал — до мегабайта текста, и это единственная часть обращения, ради
// которой его открывают: «у меня не запускается» без журнала не разбирается
// вовсе. Поэтому здесь проверяется не оформление, а три решения: есть ли что
// показывать, сколько показывать и правду ли говорит подпись про объём.
'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { feedbackLogsView, formatSize, INLINE_TAIL_BYTES } =
  require('../../server/admin_ui/feedback-logs.js');

test('без журнала блок не показывается', () => {
  assert.strictEqual(feedbackLogsView({ attachLogs: false, logs: '' }).has, false);
  assert.strictEqual(feedbackLogsView({}).has, false);
  assert.strictEqual(feedbackLogsView(null).has, false);
});

test('флаг attachLogs без текста ничего не показывает', () => {
  // Пользователь попросил приложить журнал, а тот не собрался — кнопка
  // «Скачать» вела бы в пустоту. Обратный случай тоже реальный: старым
  // обращениям журналы обрезает уплотнение ящика, а флаг у них остаётся.
  const view = feedbackLogsView({ attachLogs: true, logs: '', logBytes: 0 });
  assert.strictEqual(view.has, false);
});

test('короткий журнал показывается целиком и без оговорок', () => {
  const view = feedbackLogsView({ logs: 'строка одна\nстрока два' });

  assert.strictEqual(view.has, true);
  assert.strictEqual(view.truncated, false);
  assert.match(view.text, /строка два/);
  assert.match(view.note, /Б$/);
});

test('длинный журнал обрезается с КОНЦА, а не с начала', () => {
  // Авария всегда в конце журнала; начало — загрузка лаунчера, одинаковая у всех.
  const logs = 'НАЧАЛО' + 'x'.repeat(200) + 'АВАРИЯ';
  const view = feedbackLogsView({ logs }, 64);

  assert.strictEqual(view.truncated, true);
  assert.strictEqual(view.text.length, 64);
  assert.match(view.text, /АВАРИЯ$/);
  assert.doesNotMatch(view.text, /НАЧАЛО/);
});

test('подпись обрезанного журнала называет полный объём, а не показанный', () => {
  const view = feedbackLogsView({ logs: 'y'.repeat(3 * 1024 * 1024) }, 1024);

  assert.strictEqual(view.size, 3 * 1024 * 1024);
  assert.match(view.note, /3\.0 МБ/);
  assert.match(view.note, /показан конец/);
});

test('объём берётся из logBytes, когда самого текста в ответе нет', () => {
  // Так отвечает список обращений: тексты журналов в нём не приезжают вовсе.
  const view = feedbackLogsView({ attachLogs: true, logBytes: 1048576 });

  assert.strictEqual(view.has, false);
  assert.strictEqual(view.size, 1048576);
});

test('formatSize переводит байты в человеческие единицы', () => {
  assert.strictEqual(formatSize(0), '0 Б');
  assert.strictEqual(formatSize(512), '512 Б');
  assert.strictEqual(formatSize(2048), '2 КБ');
  assert.strictEqual(formatSize(1572864), '1.5 МБ');
  assert.strictEqual(formatSize(undefined), '0 Б');
});

test('порог показа задан и разумен', () => {
  // Значение видно тесту намеренно: если однажды кто-то поставит сюда мегабайт,
  // панель снова начнёт подвисать на открытии обращения, и это должно быть
  // осознанным решением, а не опечаткой.
  assert.ok(INLINE_TAIL_BYTES > 0 && INLINE_TAIL_BYTES <= 256 * 1024);
});
