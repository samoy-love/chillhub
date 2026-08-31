// Необратимые действия админки и подтверждения перед ними.
//
// Общее у всех сценариев ниже: между тем, что панель ОБЕЩАЕТ оператору, и
// тем, что произойдёт на сервере, разницы быть не должно — ни в тексте
// диалога, ни в списке, посчитанном заранее, ни в порядке «сначала спросили,
// потом стёрли».
//
// Отдельным файлом от admin-dom.test.js: там проверяется, что кнопки вообще
// работают, здесь — что они не врут. Приём загрузки страницы тот же.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { TextDecoder, TextEncoder } = require('node:util');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');
const HTML_PATH = path.join(ADMIN_DIR, 'admin.html');

// Порядок повторяет <script> в admin.html — см. подробности в admin-dom.test.js.
const SCRIPT_ORDER = [
  'admin-time.js', 'ui-throttle.js', 'upload-bench.js', 'speed-chart.js', 'line-chart.js',
  'chunk-upload.js', 'rate-estimator.js', 'upload-tuning.js', 'ui-status.js', 'upload-card.js',
  'game-gallery.js', 'game-list.js', 'ndjson.js', 'mods-panel.js', 'registry-diff.js', 'admin.js',
];

function jsonResponse(json, status) {
  const st = status || 200;
  return { ok: st >= 200 && st < 300, status: st, statusText: 'OK', json: async () => json, text: async () => JSON.stringify(json) };
}

async function ticks(n) {
  for (let i = 0; i < (n || 6); i++) await new Promise((res) => setTimeout(res, 0));
}

function loadAdminPage(t, fetchImpl) {
  let html = fs.readFileSync(HTML_PATH, 'utf8');
  html = html.replace(/<script src="https:\/\/cdn\.jsdelivr\.net[^<]*<\/script>\s*/, '');

  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost/admin/' });
  const { window } = dom;
  window.TextDecoder = TextDecoder;
  window.TextEncoder = TextEncoder;
  window.fetch = fetchImpl || (async () => jsonResponse({ status: 'ok' }));
  window.confirm = () => true;

  const ctx = dom.getInternalVMContext();
  for (const file of SCRIPT_ORDER) {
    const abs = path.join(ADMIN_DIR, file);
    vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });
  }

  t.after(() => dom.window.close());
  return { dom, window, document: window.document };
}

// askConfirm подменяется целиком: тесту нужен не bootstrap, а то, ЧТО именно
// диалог обещает и когда его показали.
function captureConfirm(window, answer) {
  const seen = [];
  window.askConfirm = async (opts) => { seen.push(opts); return answer; };
  return seen;
}

function notes(window) {
  const seen = [];
  window.notifyLevel = (msg, level) => { seen.push({ msg: String(msg), level }); };
  return seen;
}

// ---- Кнопка «Новая» в редакторе новостей ----

test('«Новая»: отказ в диалоге оставляет набранный текст на месте', async (t) => {
  const { window, document } = loadAdminPage(t);
  captureConfirm(window, false);

  document.getElementById('ns_slug').value = 'patch-1-7';
  const ta = document.getElementById('ns_md');
  ta.value = '# Большой патч\n\nПисали час.';
  ta.dispatchEvent(new window.Event('input'));

  document.getElementById('ns_btnNew').dispatchEvent(new window.Event('click'));
  await ticks();

  assert.match(ta.value, /Писали час/, 'текст стёрли до того, как оператор ответил на вопрос');
  assert.strictEqual(document.getElementById('ns_slug').value, 'patch-1-7', 'слаг тоже не должен исчезать по «Отмене»');
});

test('«Новая»: согласие кладёт черновик под ключом со слагом и только потом чистит', async (t) => {
  const { window, document } = loadAdminPage(t);
  captureConfirm(window, true);

  document.getElementById('ns_scope').value = 'launcher';
  document.getElementById('ns_slug').value = 'patch-1-7';
  const ta = document.getElementById('ns_md');
  ta.value = '# Большой патч\n\nПисали час.';
  ta.dispatchEvent(new window.Event('input'));

  document.getElementById('ns_btnNew').dispatchEvent(new window.Event('click'));
  await ticks();

  // Ключ черновика строится по слагу. Пока слаг обнулялся до сохранения,
  // черновик уезжал в 'news_draft:launcher::' и «Восстановить» его не находил.
  const saved = window.localStorage.getItem('news_draft:launcher::patch-1-7');
  assert.ok(saved, 'черновик сохранён не под тем ключом, по которому его будут искать');
  assert.match(String(JSON.parse(saved).md), /Писали час/);

  assert.doesNotMatch(ta.value, /Писали час/, 'после согласия поле обязано очиститься');
  assert.strictEqual(document.getElementById('ns_slug').value, '');
});

// ---- Диалоги удаления версий ----

test('диалог удаления версии обещает откат на предыдущую — как и делает сервер', async (t) => {
  const { window, document } = loadAdminPage(t);
  const asked = captureConfirm(window, false);

  const root = document.createElement('div');
  root.innerHTML = '<button class="vr-delete" data-ver="1.6.25">Удалить</button>';
  document.body.appendChild(root);
  window.bindVersionActions(root, 'vr', 'launcher', async () => {});

  root.querySelector('.vr-delete').dispatchEvent(new window.Event('click', { bubbles: true }));
  await ticks();

  const bullets = (asked[0].bullets || []).join(' ');
  // recalcLatest переставляет latest.json на ближайшую предыдущую версию, а
  // не убирает его: диалог обещал обратное, и откат всех лаунчеров выглядел
  // как то, чего оператор не просил.
  assert.match(bullets, /ПРЕДЫДУЩАЯ|предыдущая/, 'про смену активной версии не сказано ничего');
  assert.match(bullets, /откат/i, 'не сказано, что лаунчеры уедут на другую сборку');
  assert.doesNotMatch(bullets, /обновляться станет не на что, пока вы не назначите/,
    'диалог обещает то, чего сервер не делает');
});

test('массовая чистка спрашивает сервер заново: в диалоге тот набор, который удалят', async (t) => {
  // Таблицу отрисовали при активной 1.0.5, а к моменту клика из другой вкладки
  // залили и активировали 1.0.6 — набор под нож сдвинулся на одну версию.
  const calls = [];
  const fetchStub = async (url, opts) => {
    const u = String(url);
    calls.push({ url: u, method: (opts && opts.method) || 'GET' });
    if (u.includes('/api/list?')) {
      return jsonResponse({
        latest: '1.0.6',
        items: ['1.0.1', '1.0.2', '1.0.3', '1.0.4', '1.0.5', '1.0.6'].map((v) => ({ version: v })),
      });
    }
    return jsonResponse({ deleted: ['1.0.1', '1.0.2', '1.0.3'], failed: [], active: '1.0.6' });
  };
  const { window, document } = loadAdminPage(t, fetchStub);
  const asked = captureConfirm(window, false);

  const root = document.createElement('div');
  root.innerHTML = window.versionsTableHtml(
    ['1.0.1', '1.0.2', '1.0.3', '1.0.4', '1.0.5'].map((v) => ({ version: v })), '1.0.5', 'vr',
  );
  document.body.appendChild(root);
  window.bindVersionActions(root, 'vr', 'launcher', async () => {});

  root.querySelector('.vr-prune').dispatchEvent(new window.Event('click', { bubbles: true }));
  await ticks();

  assert.strictEqual(asked.length, 1, 'диалог не показан');
  const body = String(asked[0].body);
  assert.match(body, /Останутся активная 1\.0\.6/, 'диалог называет активную версию по устаревшей таблице');
  assert.match(body, /1\.0\.1, 1\.0\.2, 1\.0\.3/, 'перечислено не то, что удалит сервер');
  // 1.0.3 в старой таблице была «одной из двух версий перед активной», то есть
  // целью отката. Именно она и стиралась молча.
  assert.match(body, /3 шт\./);
});

test('массовая чистка: удалённое сверх показанного не выдаётся за успех', async (t) => {
  const fetchStub = async (url) => {
    if (String(url).includes('/api/list?')) {
      return jsonResponse({ latest: '1.0.4', items: ['1.0.1', '1.0.2', '1.0.3', '1.0.4'].map((v) => ({ version: v })) });
    }
    // Сервер посчитал набор сам и снёс на одну версию больше показанного.
    return jsonResponse({ deleted: ['1.0.1', '1.0.2'], failed: [], active: '1.0.4' });
  };
  const { window, document } = loadAdminPage(t, fetchStub);
  captureConfirm(window, true);
  const said = notes(window);

  const root = document.createElement('div');
  root.innerHTML = '<button class="vr-prune"></button>';
  document.body.appendChild(root);
  window.bindVersionActions(root, 'vr', 'launcher', async () => {});

  root.querySelector('.vr-prune').dispatchEvent(new window.Event('click', { bubbles: true }));
  await ticks(10);

  const last = said[said.length - 1];
  assert.strictEqual(last.level, 'error', 'расхождение с диалогом — не успешная чистка');
  assert.match(last.msg, /1\.0\.2/, 'не названа версия, которую сервер удалил сверх списка');
});

// ---- Тосты ----

test('сохранение списка игр показывает своё сообщение, а не реестр целиком', async (t) => {
  const registry = { items: Array.from({ length: 30 }, (_, i) => ({ gameId: 'game-' + i, title: 'Игра ' + i })) };
  const { window } = loadAdminPage(t, async () => jsonResponse(registry));
  const said = notes(window);

  await window.mgmSave();
  // mgmSave заканчивается перечитыванием таблицы; без слива очереди оно
  // доедет уже после закрытия окна и уронит прогон целиком.
  await ticks(10);

  const success = said.filter((n) => n.level === 'success');
  assert.strictEqual(success.length, 1, 'об успехе должно быть сказано один раз');
  assert.doesNotMatch(success[0].msg, /[{}]/, 'в тост уехало тело ответа — весь реестр JSON');
  assert.match(success[0].msg, /сохран/i);
});

// ---- Вкладка «Моды» ----

test('переход по метке «моды» выбирает игру один раз, а не при каждом возврате', async (t) => {
  const { window } = loadAdminPage(t, async () => jsonResponse({ items: [] }));

  window.openModsForGame('lethal-company');
  await ticks();

  // Флаг прочитан — дальше выбор игры принадлежит оператору: он мог
  // переключиться в самой панели, и возврат на вкладку не должен это отменять.
  assert.strictEqual(window.__modsWantGame || '', '', 'флаг остался и подменит выбор при следующем открытии вкладки');
});
