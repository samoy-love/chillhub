// Тесты чистых функций админки: экранирование и разбор URL.
//
// ЗАЧЕМ ОТДЕЛЬНЫЙ КАТАЛОГ. admin.js — браузерный скрипт, он не модуль и опирается на
// document/window, поэтому целиком в node не загружается. Функции ниже чистые, и
// вытащить их из исходника достаточно, чтобы покрыть самое опасное: именно на этих
// двух держится защита админки от XSS.
//
// Каталог вынесен из server/admin_ui, чтобы не попадать под браузерные правила ESLint.
//
// Запуск: node --test tests/web/

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const SRC = path.join(__dirname, '..', '..', 'server', 'admin_ui', 'admin.js');
const src = fs.readFileSync(SRC, 'utf8');

/** Достаёт объявление функции из исходника по имени. */
function extract(name) {
  const re = new RegExp('function\\s+' + name + '\\s*\\([\\s\\S]*?\\n\\}', 'm');
  const m = src.match(re);
  if (!m) throw new Error('не найдена функция ' + name + ' в admin.js');
  return m[0];
}

// eslint-disable-next-line no-eval
const sanitizeUrl = eval('(' + extract('sanitizeUrl').replace(/^function\s+sanitizeUrl/, 'function') + ')');
// eslint-disable-next-line no-eval
const escapeHtml = eval('(' + extract('escapeHtml').replace(/^function\s+escapeHtml/, 'function') + ')');

test('sanitizeUrl отвергает javascript: во всех видах', () => {
  const bad = [
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    '  javascript:alert(1)',
    'javascript\t:alert(1)',
    // Обход управляющими символами: браузер вырезает их ДО разбора схемы,
    // поэтому "java<TAB>script:" для него — javascript:. Регулярка схемы такую
    // строку не признавала и пропускала её как относительную ссылку.
    'java\tscript:alert(1)',
    'java\nscript:alert(1)',
    'java\rscript:alert(1)',
    'java\u0000script:alert(1)',
    'vbscript:msgbox(1)',
    'data:text/html;base64,PHNjcmlwdD4=',
    'file:///C:/Windows/System32',
  ];
  for (const v of bad) {
    assert.strictEqual(sanitizeUrl(v, false), '', 'должно быть отвергнуто: ' + JSON.stringify(v));
  }
});

test('sanitizeUrl пропускает безопасные ссылки', () => {
  const good = [
    'https://example.com/a?b=1',
    'http://example.com',
    'mailto:user@example.com',
    '/assets/images/a.png',
    './rel/path.png',
    '#anchor',
    '',
  ];
  for (const v of good) {
    assert.strictEqual(sanitizeUrl(v, false), v.replace(/[\u0000-\u001F\u007F]/g, '').trim(),
      'должно быть пропущено: ' + JSON.stringify(v));
  }
});

test('sanitizeUrl разрешает data: только для картинок и только в src', () => {
  assert.strictEqual(sanitizeUrl('data:image/png;base64,AAAA', true), 'data:image/png;base64,AAAA');
  assert.strictEqual(sanitizeUrl('data:image/webp;base64,AAAA', true), 'data:image/webp;base64,AAAA');
  // Не картинка — нельзя даже там, где data: разрешён.
  assert.strictEqual(sanitizeUrl('data:text/html;base64,AAAA', true), '');
  assert.strictEqual(sanitizeUrl('data:application/javascript,alert(1)', true), '');
  // В href (allowDataImage=false) data: не разрешён вовсе.
  assert.strictEqual(sanitizeUrl('data:image/png;base64,AAAA', false), '');
});

test('escapeHtml закрывает все пять опасных символов', () => {
  assert.strictEqual(escapeHtml('<script>'), '&lt;script&gt;');
  assert.strictEqual(escapeHtml('a & b'), 'a &amp; b');
  // Кавычки обязательны: без них значение вырывается из атрибута value="..."
  assert.strictEqual(escapeHtml('x" onerror="alert(1)'), 'x&quot; onerror=&quot;alert(1)');
  assert.strictEqual(escapeHtml("x' onerror='alert(1)"), 'x&#39; onerror=&#39;alert(1)');
});

test('escapeHtml экранирует амперсанд первым, иначе выходит двойное экранирование', () => {
  // Если бы & обрабатывался последним, "&lt;" превратился бы в "&amp;lt;".
  assert.strictEqual(escapeHtml('<'), '&lt;');
  assert.strictEqual(escapeHtml('&lt;'), '&amp;lt;');
});

test('escapeHtml переживает пустое и отсутствующее значение', () => {
  assert.strictEqual(escapeHtml(''), '');
  assert.strictEqual(escapeHtml(null), '');
  assert.strictEqual(escapeHtml(undefined), '');
});

test('разрыв атрибута через имя файла больше не проходит', () => {
  // Реальный сценарий: имя вставленного из буфера файла попадало в value="..."
  const evil = 'x" ><script src="https://cdn.jsdelivr.net/gh/a/b@main/p.js"></script><input v="';
  const attr = 'value="' + escapeHtml(evil) + '"';
  assert.ok(!/<script/i.test(attr), 'тег script не должен появиться в атрибуте');
  assert.ok(!attr.includes('" >'), 'атрибут не должен разрываться');
});
