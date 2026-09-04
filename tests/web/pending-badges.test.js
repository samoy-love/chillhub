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
    { gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13', behind: true },
    { gameId: 'peak', title: '', latest: '1.4.0', behind: true },
  ]);

  assert.strictEqual(view.show, true);
  assert.strictEqual(view.text, '2');
  assert.match(view.title, /Lethal Company: 2\.2\.13/);
  // Без названия в подсказку идёт идентификатор — пустая строка не помогла бы.
  assert.match(view.title, /peak: 1\.4\.0/);
});

// СВОДКА ТЕПЕРЬ ПРИСЫЛАЕТ СТРОКУ НА КАЖДУЮ ИГРУ С МОДАМИ, включая свежие.
// Считай значок по длине списка — он горел бы всегда и перестал бы что-либо
// значить; считать надо те строки, с которыми надо что-то делать.
test('моды: свежие игры значок не зажигают', () => {
  const view = describeMods([
    { gameId: 'lethal-company', title: 'Lethal Company', active: '2.2.12', latest: '2.2.12', behind: false },
    { gameId: 'peak', title: 'PEAK', active: '1.8.13', latest: '1.8.13', behind: false },
  ]);

  assert.strictEqual(view.show, false);
});

test('моды: среди свежих считается только отставшая', () => {
  const view = describeMods([
    { gameId: 'peak', title: 'PEAK', latest: '1.8.13', behind: false },
    { gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13', behind: true },
  ]);

  assert.strictEqual(view.text, '1');
  assert.match(view.title, /Lethal Company: 2\.2\.13/);
  assert.ok(!/PEAK/.test(view.title), 'свежая игра не должна попадать в подсказку');
});

// Устаревший пакет той же версии — тоже повод, но повод ДРУГОЙ: пересобирать
// нечего, надо решать, чем его заменить.
test('моды: устаревший пакет зажигает значок и говорит об этом словами', () => {
  const view = describeMods([
    { gameId: 'repo', title: 'REPO', active: '1.9.9', latest: '1.9.9', behind: false, deprecated: true },
  ]);

  assert.strictEqual(view.show, true);
  assert.strictEqual(view.text, '1');
  assert.match(view.title, /устаревш/i);
});

test('моды: строка с ошибкой проверки значка не зажигает', () => {
  // Состояние неизвестно — это не задача для человека, а повод посмотреть лог:
  // значок «здесь ждут действия» врал бы о том, что действие есть.
  const view = describeMods([
    { gameId: 'peak', title: 'PEAK', active: '1.8.13', error: 'Thunderstore не ответил' },
  ]);

  assert.strictEqual(view.show, false);
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
      mods: [{ gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13', behind: true }],
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

// КЛИК ПО ВКЛАДКЕ ДОЛЖЕН ВЕСТИ К ИГРЕ, А НЕ К СПИСКУ ИГР. Значок знал, что
// обновление есть, но не говорил, где: название игры было только в подсказке.
test('моды: значок несёт игру, с которой начинать', () => {
  const view = describeMods([
    { gameId: 'peak', title: 'PEAK', latest: '1.8.13', behind: false },
    { gameId: 'lethal-company', title: 'Lethal Company', latest: '2.2.13', behind: true },
  ]);

  assert.strictEqual(view.gameId, 'lethal-company');
});

test('игра со значка попадает в разметку и исчезает вместе с ним', () => {
  const dom = new JSDOM('<span id="b"></span>');
  const el = dom.window.document.getElementById('b');

  applyBadge(el, { show: true, text: '1', title: 'т', gameId: 'lethal-company' });
  assert.strictEqual(el.getAttribute('data-game-id'), 'lethal-company');

  applyBadge(el, { show: false });
  assert.strictEqual(el.getAttribute('data-game-id'), null);
});

// СВОДКУ СЕРВЕР ДЕРЖИТ ДЕСЯТЬ МИНУТ. После действия оператора это ровно то
// время, которое значок висел бы над уже сделанной работой.
test('после действия сводку просят пересчитать заново', async () => {
  const dom = new JSDOM('<span id="launcher_pending_badge"></span><span id="mods_pending_badge"></span>');
  const urls = [];
  const fetchImpl = (url) => {
    urls.push(url);
    return Promise.resolve({ ok: true, json: () => Promise.resolve({ launcher: {}, mods: [] }) });
  };

  await refreshPendingBadges(dom.window.document, fetchImpl);
  await refreshPendingBadges(dom.window.document, fetchImpl, { force: true });

  assert.deepStrictEqual(urls, ['/admin/summary', '/admin/summary?force=1']);
});
