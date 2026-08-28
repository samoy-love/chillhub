// Значки «здесь ждут действия» на вкладках «Лаунчер» и «Моды».
//
// Это единственное место, где панель сама сообщает о работе, которую никто не
// поручал: вышла сборка лаунчера и не активирована, вышло обновление модпака.
// Ложное срабатывание тут дороже отсутствия — значок, горящий без причины,
// перестают замечать за неделю.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { JSDOM } = require('jsdom');

const {
  describeLauncher, describeMods, applyBadge, refreshPendingBadges,
} = require('../../server/admin_ui/pending-badges.js');

test('лаунчер: значок горит, когда свежая сборка не активна', () => {
  const view = describeLauncher({ active: '1.6.3', newest: '1.6.5', pending: true });

  assert.strictEqual(view.show, true);
  assert.strictEqual(view.text, '1.6.5');
  assert.match(view.title, /1\.6\.5/);
  assert.match(view.title, /1\.6\.3/);
});

test('лаунчер: активная и есть самая свежая — значка нет', () => {
  assert.strictEqual(describeLauncher({ active: '1.6.5', newest: '1.6.5', pending: false }).show, false);
});

test('лаунчер: пустая история — не повод для значка', () => {
  // Лаунчер ещё ни разу не публиковали: решать пока нечего, а горящий значок
  // на пустом месте — это шум, который учит его игнорировать.
  assert.strictEqual(describeLauncher({ active: '', newest: '', pending: false }).show, false);
  assert.strictEqual(describeLauncher({ active: '', newest: '1.0.0', pending: true }).show, false);
  assert.strictEqual(describeLauncher(null).show, false);
});

test('моды: значок считает игры, а подсказка называет их', () => {
  const view = describeMods([
    { gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13' },
    { gameId: 'peak', title: '', latest: '1.4.0' },
  ]);

  assert.strictEqual(view.show, true);
  assert.strictEqual(view.text, '2');
  assert.match(view.title, /Lethal Company: 2\.2\.13/);
  // Без названия в подсказку идёт идентификатор — пустая строка не помогла бы.
  assert.match(view.title, /peak: 1\.4\.0/);
});

test('моды: обновлений нет — значка нет', () => {
  assert.strictEqual(describeMods([]).show, false);
  assert.strictEqual(describeMods(null).show, false);
});

test('пустое состояние прячет значок, а не показывает ноль', () => {
  const dom = new JSDOM('<span id="b" class="badge">7</span>');
  const el = dom.window.document.getElementById('b');

  applyBadge(el, { show: false });

  assert.strictEqual(el.style.display, 'none');
  assert.strictEqual(el.textContent, '');
  assert.strictEqual(el.getAttribute('title'), null);
});

test('значок раскладывается по вкладкам из ответа сервера', async () => {
  const dom = new JSDOM(''
    + '<span id="launcher_pending_badge" style="display:none"></span>'
    + '<span id="mods_pending_badge" style="display:none"></span>');
  const doc = dom.window.document;

  const data = await refreshPendingBadges(doc, () => Promise.resolve({
    ok: true,
    json: () => Promise.resolve({
      launcher: { active: '1.6.3', newest: '1.6.5', pending: true },
      mods: [{ gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13' }],
      pending: 2,
    }),
  }));

  assert.strictEqual(data.pending, 2);
  assert.strictEqual(doc.getElementById('launcher_pending_badge').textContent, '1.6.5');
  assert.strictEqual(doc.getElementById('mods_pending_badge').textContent, '1');
});

test('недоступная сводка не ломает панель и не зажигает значков', async () => {
  const dom = new JSDOM('<span id="launcher_pending_badge"></span><span id="mods_pending_badge"></span>');
  const doc = dom.window.document;

  assert.strictEqual(await refreshPendingBadges(doc, () => Promise.reject(new Error('нет сети'))), null);
  assert.strictEqual(await refreshPendingBadges(doc, () => Promise.resolve({ ok: false })), null);
  assert.strictEqual(doc.getElementById('launcher_pending_badge').textContent, '');
});
