// Время обращений в инбоксе — проверка в настоящем DOM (jsdom).
//
// ПОЧЕМУ НЕ РЯДОМ С ОСТАЛЬНЫМИ ТЕСТАМИ ФОРМАТИРОВАНИЯ. admin-logic.test.js
// вырезает функции регэкспом и прогоняет через new Function: для чистой
// логики этого достаточно, но c8 такой код к admin.js не привязывает и
// показывает на нём ноль (см. шапку admin-dom.test.js). Формат времени —
// как раз тот случай, когда проверить хочется не только арифметику зоны, но
// и то, что отрисованный список показывает именно её, поэтому здесь
// admin.js исполняется целиком, из настоящего admin.html.
//
// Загрузчик страницы повторяет loadAdminPage() из admin-dom.test.js: тот
// файл ничего не экспортирует, а тащить его целиком ради одного сценария
// дороже, чем пятнадцать строк здесь.
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { TextDecoder, TextEncoder } = require('node:util');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');
const HTML_PATH = path.join(ADMIN_DIR, 'admin.html');

// Порядок повторяет <script> в admin.html.
const SCRIPT_ORDER = [
  'admin-time.js',
  'ui-throttle.js',
  'upload-bench.js',
  'speed-chart.js',
  'line-chart.js',
  'chunk-upload.js',
  'rate-estimator.js',
  'ui-status.js',
  'upload-card.js',
  'game-gallery.js',
  'game-list.js',
  'admin.js',
];

function loadAdminPage(t) {
  let html = fs.readFileSync(HTML_PATH, 'utf8');
  html = html.replace(/<script src="https:\/\/cdn\.jsdelivr\.net[^<]*<\/script>\s*/, '');

  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost/admin/' });
  const { window } = dom;
  window.TextDecoder = TextDecoder;
  window.TextEncoder = TextEncoder;
  // Ни один сценарий здесь не ходит в сеть, но admin.js на верхнем уровне
  // сам дёргает fetch (стартовая загрузка разделов). Ответ, который никогда
  // не приходит, — единственный вариант без гонки: любой resolve добирается
  // до обработчика уже после window.close() в t.after(), и node --test
  // ругается на активность после теста.
  window.fetch = () => new Promise(() => {});
  window.confirm = () => true;

  const ctx = dom.getInternalVMContext();
  for (const file of SCRIPT_ORDER) {
    const abs = path.join(ADMIN_DIR, file);
    vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });
  }

  // admin.js держит setInterval на верхнем уровне: без закрытия окна
  // `node --test` не завершится после последнего теста.
  t.after(() => dom.window.close());

  return { window, ctx, document: window.document };
}

// __fbItems — top-level let в admin.js, то есть лексическая область
// контекста, а не свойство window: достать её можно только кодом,
// исполненным в том же контексте.
function renderInbox(ctx, items) {
  vm.runInContext('__fbItems = ' + JSON.stringify(items) + '; fbRenderList();', ctx);
}

test('список обращений показывает время по Москве, а не UTC из файла', (t) => {
  const { ctx, document } = loadAdminPage(t);
  renderInbox(ctx, [{
    id: 'a1',
    name: 'Вася',
    contact: 'vasya@example.com',
    comment: 'не запускается',
    createdAt: '2026-08-17T18:35:29Z',
    status: 'new',
  }]);

  const text = document.getElementById('fb_list').textContent;
  assert.match(text, /2026-08-17 21:35:29 МСК/);
  // Ровно то, что показывалось раньше: UTC с вырезанными T и Z.
  assert.ok(!text.includes('2026-08-17 18:35:29'), 'UTC-время не должно оставаться в списке');
});

test('в списке обращений переход через полночь двигает и дату', (t) => {
  const { ctx, document } = loadAdminPage(t);
  renderInbox(ctx, [{ id: 'a2', name: 'Петя', comment: 'ночью', createdAt: '2026-08-17T22:10:00Z' }]);

  assert.match(document.getElementById('fb_list').textContent, /2026-08-18 01:10:00 МСК/);
});

test('обращение без даты не рисует Invalid Date', (t) => {
  const { ctx, document } = loadAdminPage(t);
  renderInbox(ctx, [{ id: 'a3', name: 'Аноним', comment: 'без даты' }]);

  const text = document.getElementById('fb_list').textContent;
  assert.ok(!text.includes('Invalid Date'), text);
});
