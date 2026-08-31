// Порядок ответов галереи игры. game-gallery.test.js проверяет, что галерея
// рисует; здесь — что она рисует ТО, ЧТО ПРОСИЛИ ПОСЛЕДНИМ.
//
// Сценарий, ради которого файл написан: оператор выбирает игру A, её ответ
// задерживается, он выбирает B, ответ B приходит первым, а следом — ответ A и
// перерисовывает сетку. Кнопки на этих карточках отправляли имя файла игры A
// с идентификатором игры B, и сервер такой запрос выполняет: существование
// файла он не проверяет.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');

function jsonResponse(json) {
  return { ok: true, status: 200, json: async () => json, text: async () => JSON.stringify(json) };
}

async function settle() {
  for (let i = 0; i < 30; i++) await Promise.resolve();
}

// Разметка настоящая, а исполняется из всей админки один game-gallery.js:
// этот файл специально написан так, чтобы не зависеть от остальных скриптов
// (свои esc/toast с запасным путём), и поднимать ради него admin.js целиком —
// значит ловить в тесте чужие фоновые запросы.
function mountGallery(fetchImpl) {
  let html = fs.readFileSync(path.join(ADMIN_DIR, 'admin.html'), 'utf8');
  html = html.replace(/<script src="https:\/\/cdn\.jsdelivr\.net[^<]*<\/script>\s*/, '');

  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost/admin/' });
  const { window } = dom;
  window.fetch = fetchImpl;
  window.notifyLevel = () => {};
  window.confirm = () => true;

  const abs = path.join(ADMIN_DIR, 'game-gallery.js');
  const ctx = dom.getInternalVMContext();
  vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });

  return { dom, window, document: window.document };
}

function gridText(document) {
  return document.querySelector('[data-gg="grid"]').textContent;
}

test('опоздавший ответ по прошлой игре не перерисовывает сетку', async (t) => {
  let releaseSlow;
  const slow = new Promise((res) => { releaseSlow = res; });
  const calls = [];

  const { window, document, dom } = mountGallery(async function (url) {
    const u = String(url);
    calls.push(u);
    if (u.includes('gallery.json')) return jsonResponse({ cover: '', items: [] });
    if (u.includes('gameId=slow-game')) {
      await slow;
      return jsonResponse({ path: '', items: [{ name: 'from-slow.png', isDir: false, url: '/x/from-slow.png' }] });
    }
    return jsonResponse({ path: '', items: [{ name: 'from-fast.png', isDir: false, url: '/x/from-fast.png' }] });
  });
  t.after(() => dom.window.close());

  let current = 'slow-game';
  const gallery = window.createGameGallery({ root: '#gg_root', getGameId: () => current });

  const first = gallery.fetchAndRender();
  current = 'fast-game';
  await gallery.fetchAndRender();
  assert.match(gridText(document), /from-fast\.png/, 'выбранная игра нарисована');

  releaseSlow();
  await first;
  await settle();

  assert.match(gridText(document), /from-fast\.png/, 'сетку перерисовал устаревший ответ');
  assert.doesNotMatch(gridText(document), /from-slow\.png/, 'в сетке файлы игры, которая уже не выбрана');
});

test('«Сделать обложкой» после опоздавшего ответа не смешивает игру и файл', async (t) => {
  let releaseSlow;
  const slow = new Promise((res) => { releaseSlow = res; });
  const calls = [];

  const { window, document, dom } = mountGallery(async function (url, opts) {
    const u = String(url);
    calls.push({ url: u, body: String((opts && opts.body) || '') });
    if (u.includes('gallery.json')) return jsonResponse({ cover: '', items: [] });
    if (u.includes('gameId=slow-game')) {
      await slow;
      return jsonResponse({ path: '', items: [{ name: 'from-slow.png', isDir: false, url: '/x/from-slow.png' }] });
    }
    return jsonResponse({ path: '', items: [{ name: 'from-fast.png', isDir: false, url: '/x/from-fast.png' }] });
  });
  t.after(() => dom.window.close());

  let current = 'slow-game';
  const gallery = window.createGameGallery({ root: '#gg_root', getGameId: () => current });

  const first = gallery.fetchAndRender();
  current = 'fast-game';
  await gallery.fetchAndRender();
  releaseSlow();
  await first;
  await settle();

  document.querySelector('.gg-cover-btn').click();
  await settle();

  // Сервер (gamegallery.go) существование файла не проверяет: пара «игра B +
  // файл игры A» записывается в gallery.json как обложка, которой нет.
  const setCover = calls.find((c) => c.url.includes('/setCover'));
  assert.ok(setCover, 'запрос обложки не ушёл');
  assert.match(setCover.body, /gameId=fast-game/, 'обложка проставлена не выбранной игре');
  assert.match(setCover.body, /file=from-fast\.png/, 'в обложку уехал файл другой игры');
});

test('поиск ждёт паузы в наборе, а не шлёт запрос на каждую букву', async (t) => {
  const listCalls = [];
  const { window, document, dom } = mountGallery(async function (url) {
    const u = String(url);
    if (u.includes('gallery.json')) return jsonResponse({ cover: '', items: [] });
    listCalls.push(u);
    return jsonResponse({ path: '', items: [] });
  });
  t.after(() => dom.window.close());

  window.createGameGallery({ root: '#gg_root', gameId: 'lethal-company' });
  const search = document.querySelector('[data-gg="search"]');

  for (const q of ['b', 'bo', 'bos', 'boss']) {
    search.value = q;
    search.dispatchEvent(new window.Event('input'));
  }
  assert.strictEqual(listCalls.length, 0, 'запрос ушёл, не дождавшись конца слова');

  await new Promise((res) => setTimeout(res, 400));
  await settle();

  assert.strictEqual(listCalls.length, 1, 'на слово должен уйти один запрос, а не по одному на букву');
  assert.match(listCalls[0], /q=boss/, 'запрошено то, что набрано целиком');
});
