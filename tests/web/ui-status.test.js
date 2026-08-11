// Тесты server/admin_ui/ui-status.js — заметной строки статуса ошибки
// загрузки. Появился после реального случая на проде: ошибка на шаге
// распаковки уходила только в notify() (маленький <pre> внизу страницы), а
// строка статуса под прогрессом молча оставалась на "Старт обработки: ..."
// навсегда. См. комментарий в шапке файла.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { setStatusError, clearStatusError } = require(path.join('..', '..', 'server', 'admin_ui', 'ui-status.js'));

// fakeEl имитирует ровно то, что используют setStatusError/clearStatusError:
// textContent и classList.add/remove, бэкшенный обычным Set — этого
// достаточно для проверки без настоящего DOM.
function fakeEl() {
  const classes = new Set();
  return {
    textContent: '',
    classList: {
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
      has: (c) => classes.has(c),
    },
  };
}

test('setStatusError выставляет текст и подсвечивает text-danger', () => {
  const el = fakeEl();
  setStatusError(el, 'Ошибка обработки: zip: not a valid zip file');
  assert.strictEqual(el.textContent, 'Ошибка обработки: zip: not a valid zip file');
  assert.ok(el.classList.has('text-danger'));
});

test('setStatusError на null/undefined элементе не падает', () => {
  assert.doesNotThrow(() => setStatusError(null, 'x'));
  assert.doesNotThrow(() => setStatusError(undefined, 'x'));
});

test('clearStatusError снимает подсветку и может выставить новый текст', () => {
  const el = fakeEl();
  setStatusError(el, 'что-то сломалось');
  assert.ok(el.classList.has('text-danger'));
  clearStatusError(el, 'Подготовка к загрузке...');
  assert.strictEqual(el.textContent, 'Подготовка к загрузке...');
  assert.ok(!el.classList.has('text-danger'), 'подсветка ошибки должна сняться перед новой попыткой');
});

test('clearStatusError без resetText не трогает текст, только подсветку', () => {
  const el = fakeEl();
  el.textContent = 'какой-то текст';
  setStatusError(el, 'ошибка');
  clearStatusError(el);
  assert.strictEqual(el.textContent, 'ошибка', 'текст не должен измениться без явного resetText');
  assert.ok(!el.classList.has('text-danger'));
});

test('clearStatusError на null/undefined элементе не падает', () => {
  assert.doesNotThrow(() => clearStatusError(null, 'x'));
  assert.doesNotThrow(() => clearStatusError(undefined));
});
