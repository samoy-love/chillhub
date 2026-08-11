// Тесты общего шаблона карточки заливки (server/admin_ui/upload-card.js).
//
// Смысл файла — в том, что разметка одна на две вкладки. Значит и проверять
// надо ровно это: что обе карточки получают полный набор идентификаторов, что
// исторические имена полей (ver/up_ver, btnUpload/man_upload) не переехали на
// общий префикс, и что подсказка о пути различается — это единственное, чем
// карточки имеют право отличаться.
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const { JSDOM } = require('jsdom');

const { uploadCardHtml, mountUploadCards, CHUNK_OPTIONS, CHUNK_DEFAULT } =
  require('../../server/admin_ui/upload-card.js');

// Суффиксы, которые admin.js ищет как `${prefix}_${suffix}` (см. runChunkedUpload).
const PREFIXED = [
  'drop', 'prog_wrap', 'pb', 'prog_stats', 'prog_pct', 'prog_bytes', 'prog_speed',
  'prog_median', 'prog_peak', 'prog_eta', 'prog_text', 'speed_wrap', 'speed',
  'chunk_size', 'conc', 'conc_val', 'active_wrap', 'active_now', 'active_cap',
  'cleanup', 'fit',
];

function idsOf(html) {
  const dom = new JSDOM('<div>' + html + '</div>');
  return new Set(
    [...dom.window.document.querySelectorAll('[id]')].map((el) => el.id)
  );
}

test('обе карточки получают полный набор prefix-идентификаторов', () => {
  for (const prefix of ['up', 'man']) {
    const ids = idsOf(uploadCardHtml(prefix));
    for (const suffix of PREFIXED) {
      assert.ok(ids.has(prefix + '_' + suffix), prefix + '_' + suffix + ' отсутствует');
    }
  }
});

test('исторические имена полей сохранены и не переехали на общий префикс', () => {
  // На эти идентификаторы завязаны admin.js и admin-dom.test.js; переименование
  // здесь тихо оторвало бы кнопку «Загрузить» от обработчика.
  const up = idsOf(uploadCardHtml('up'));
  assert.ok(up.has('up_ver') && up.has('up_zip') && up.has('up_latest') && up.has('btnUpload'));
  const man = idsOf(uploadCardHtml('man'));
  assert.ok(man.has('ver') && man.has('man_zip') && man.has('man_latest') && man.has('man_upload'));
});

test('подсказка о каталоге различается — это единственное отличие карточек', () => {
  assert.match(uploadCardHtml('up'), /content\/launcher\//);
  assert.match(uploadCardHtml('man'), /content\/&lt;gameId&gt;\//);
});

test('список размеров чанка отдаётся целиком и с выбранным значением по умолчанию', () => {
  const html = uploadCardHtml('man');
  const dom = new JSDOM('<div>' + html + '</div>');
  const opts = [...dom.window.document.querySelectorAll('#man_chunk_size option')];
  assert.strictEqual(opts.length, CHUNK_OPTIONS.length);
  const selected = opts.filter((o) => o.hasAttribute('selected'));
  assert.strictEqual(selected.length, 1);
  assert.strictEqual(Number(selected[0].value), CHUNK_DEFAULT);
});

test('неизвестный префикс даёт пустую строку, а не полукарточку', () => {
  assert.strictEqual(uploadCardHtml('nope'), '');
  assert.strictEqual(uploadCardHtml(''), '');
});

test('поле версии валидируется прямо в разметке', () => {
  // pattern ловит «1.39» ещё до отправки — до этого опечатка уезжала на сервер
  // вместе со всем архивом.
  const dom = new JSDOM('<div>' + uploadCardHtml('up') + '</div>');
  const input = dom.window.document.getElementById('up_ver');
  assert.strictEqual(input.getAttribute('pattern'), '\\d+\\.\\d+\\.\\d+');
});

test('счётчик активных потоков спрятан, пока заливка не идёт', () => {
  // «активно 0/0» в простое — шум; его показывает runChunkedUpload.
  const dom = new JSDOM('<div>' + uploadCardHtml('man') + '</div>');
  assert.match(dom.window.document.getElementById('man_active_wrap').getAttribute('style'), /display:\s*none/);
});

test('mountUploadCards заполняет плейсхолдеры и считает заполненные', () => {
  const dom = new JSDOM('<body><div data-upload-card="up"></div><div data-upload-card="man"></div></body>');
  const n = mountUploadCards(dom.window.document);
  assert.strictEqual(n, 2);
  assert.ok(dom.window.document.getElementById('btnUpload'));
  assert.ok(dom.window.document.getElementById('man_upload'));
});

test('mountUploadCards не трогает плейсхолдер с неизвестным префиксом', () => {
  const dom = new JSDOM('<body><div data-upload-card="wat"></div></body>');
  assert.strictEqual(mountUploadCards(dom.window.document), 0);
  assert.strictEqual(dom.window.document.querySelector('[data-upload-card]').innerHTML, '');
});
