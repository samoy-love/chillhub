// Подписи колонок у ячеек: на телефоне строка таблицы рисуется карточкой,
// и значение без подписи там читается как число без смысла.

const test = require('node:test');
const assert = require('node:assert');
const { JSDOM } = require('jsdom');

const { labelTable, labelTables, watchTables } = require('../../server/admin_ui/table-labels.js');

const html = (body) => new JSDOM('<!doctype html><body>' + body + '</body>').window;

test('ячейка получает подпись своей колонки', () => {
  const w = html(
    '<table><thead><tr><th>Версия</th><th class="num">Размер</th><th></th></tr></thead>' +
      '<tbody><tr><td>1.6.25</td><td>121 МБ</td><td class="act"><button>Удалить</button></td></tr></tbody></table>'
  );
  const t = w.document.querySelector('table');
  assert.strictEqual(labelTable(t), 2);
  const cells = [...t.querySelectorAll('td')];
  assert.strictEqual(cells[0].dataset.label, 'Версия');
  assert.strictEqual(cells[1].dataset.label, 'Размер');
  // Колонке кнопок подпись «Действия» ни к чему — её и нет в заголовке
  assert.ok(!cells[2].hasAttribute('data-label'));
  assert.ok(t.classList.contains('labelled'));
});

test('объединённая ячейка не сдвигает подписи остальных', () => {
  const w = html(
    '<table><thead><tr><th colspan="2">Файл</th><th>Размер</th></tr></thead>' +
      '<tbody><tr><td>+</td><td>a.dll</td><td>4 КБ</td></tr><tr><td colspan="2">итого</td><td>9 КБ</td></tr></tbody></table>'
  );
  labelTables(w.document.body);
  const rows = [...w.document.querySelectorAll('tbody tr')];
  assert.strictEqual(rows[0].cells[2].dataset.label, 'Размер');
  assert.strictEqual(rows[1].cells[1].dataset.label, 'Размер');
});

test('таблица без заголовка остаётся как есть', () => {
  const w = html('<table><tbody><tr><td>x</td></tr></tbody></table>');
  assert.strictEqual(labelTable(w.document.querySelector('table')), 0);
  assert.ok(!w.document.querySelector('td').hasAttribute('data-label'));
});

test('таблица, вставленная перерисовкой, подписывается сама', async () => {
  const w = html('<main></main>');
  const obs = watchTables(w.document.body, w.MutationObserver);
  w.document.querySelector('main').innerHTML =
    '<section><table><thead><tr><th>Игра</th></tr></thead><tbody><tr><td>PEAK</td></tr></tbody></table></section>';
  await new Promise((r) => setTimeout(r, 0));
  assert.strictEqual(w.document.querySelector('td').dataset.label, 'Игра');

  // Строка, дописанная в уже подписанную таблицу, тоже получает подпись
  const tr = w.document.createElement('tr');
  tr.innerHTML = '<td>R.E.P.O.</td>';
  w.document.querySelector('tbody').append(tr);
  await new Promise((r) => setTimeout(r, 0));
  assert.strictEqual(tr.cells[0].dataset.label, 'Игра');
  obs.disconnect();
});
