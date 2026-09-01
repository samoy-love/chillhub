// Парность блокировки в панели модов: у каждого setBusy(true) обязан быть свой
// setBusy(false) на ЛЮБОМ выходе, а не только на удачном.
//
// Оба сценария здесь — про состояние кнопок, а не про запросы, поэтому они
// живут отдельно от mods-panel-flows.test.js (обычная работа панели) и
// mods-panel-dom.test.js (разметка карточек).
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');

const GAMES = {
  items: [{
    gameId: 'lethal-company',
    title: 'Lethal Company',
    mods: { enabled: true, community: 'lethal-company', steamAppId: '1966720', loader: 'bepinex' },
  }],
};

function jsonResponse(body) {
  return Promise.resolve({
    ok: true,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  });
}

async function settle() {
  for (let i = 0; i < 30; i++) await Promise.resolve();
}

// mount поднимает секцию «Моды» из настоящего admin.html; extra перехватывает
// нужные тесту адреса, остальное отвечает заглушками.
async function mount(extra) {
  const html = fs.readFileSync(path.join(ADMIN_DIR, 'admin.html'), 'utf8');
  const start = html.indexOf('<section id="secMods"');
  const section = html.slice(start, html.indexOf('</section>', start) + '</section>'.length);

  const dom = new JSDOM('<!doctype html><body>' + section + '</body>', { runScripts: 'outside-only' });
  const { window } = dom;
  window.notifyLevel = () => {};
  window.formatBytes = (n) => String(n) + ' B';
  window.confirm = () => true;
  window.TextEncoder = TextEncoder;
  window.TextDecoder = TextDecoder;
  window.fetch = function (url, opts) {
    const u = String(url);
    const hit = extra && extra(u, opts || {});
    if (hit) return hit;
    if (u.startsWith('/admin/games')) return jsonResponse(GAMES);
    if (u.startsWith('/admin/mods/list')) return jsonResponse({ active: '', items: [] });
    if (u.startsWith('/admin/mods/catalog')) return jsonResponse({ count: 0, results: [] });
    if (u.startsWith('/admin/mods/cache')) return jsonResponse({ files: 0, bytes: 0, ttlDays: 30 });
    return jsonResponse({});
  };

  const ctx = dom.getInternalVMContext();
  for (const file of ['ndjson.js', 'mods-panel.js']) {
    const abs = path.join(ADMIN_DIR, file);
    vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });
  }

  const panel = window.createModsPanel({ root: '#md_root' });
  assert.ok(panel, 'панель не создалась');
  panel.reload();
  await settle();
  return { window, document: window.document, panel };
}

// Кнопки карточки каталога рисует сам сервер каталога; здесь достаточно одной
// такой кнопки в сетке — панель ловит клик по атрибуту.
async function clickCatalogButton(document, attr, value) {
  const grid = document.querySelector('[data-md="catalog"]');
  grid.innerHTML = '<button type="button" ' + attr + '="' + value + '">x</button>';
  grid.querySelector('button').click();
  await settle();
}

function busyButtons(document) {
  return [...document.querySelectorAll('button[data-md-busy]')];
}

test('ошибка сервера при разборе состава не оставляет кнопки Thunderstore серыми', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/mods/resolve')) return null;
    // Thunderstore недоступен — обычный день, а не исключительная ситуация.
    return Promise.resolve({ ok: false, text: () => Promise.resolve('502 от Thunderstore') });
  });

  const buttons = busyButtons(document);
  assert.ok(buttons.length >= 2, 'в разметке должны быть кнопки, которые блокирует setBusy');

  await clickCatalogButton(document, 'data-mc-resolve', 'Team/Pack');

  const stuck = buttons.filter((b) => b.disabled);
  assert.deepStrictEqual(stuck, [], 'после отказа сервера панель осталась заблокированной — лечится только F5');
  assert.deepStrictEqual(buttons.filter((b) => b.hasAttribute('title')), [], 'подпись «идёт работа» тоже должна уйти');
});

test('повторная сборка без пропавших модов идёт с заблокированными кнопками и видимой полосой', async () => {
  let release;
  const secondBuild = new Promise((res) => { release = res; });
  let attempts = 0;

  const { document, window } = await mount(function (url, opts) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    attempts++;
    if (String(opts.body).includes('allowMissing=1')) {
      // Вторая сборка — это минуты скачивания: ответ придёт, только когда
      // тест её отпустит.
      return secondBuild.then(() => ({ ok: true, text: () => Promise.resolve('{"type":"done"}\n') }));
    }
    return Promise.resolve({
      ok: true,
      text: () => Promise.resolve(JSON.stringify({
        type: 'error',
        message: '2 пакетов больше нет на Thunderstore: A-B-1.0.0',
      }) + '\n'),
    });
  });

  window.confirm = () => true;
  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  assert.strictEqual(attempts, 2, 'повтор с согласием оператора не начался');

  const buttons = busyButtons(document);
  assert.deepStrictEqual(
    buttons.filter((b) => !b.disabled), [],
    'пока сборка идёт, кнопки обязаны быть заблокированы: иначе рядом запускают вторую такую же',
  );
  assert.ok(
    !document.querySelector('[data-md="progressBox"]').classList.contains('hidden'),
    'полоса прогресса спрятана, хотя сборка идёт — снаружи это «ничего не происходит»',
  );

  release();
  await settle();

  assert.deepStrictEqual(buttons.filter((b) => b.disabled), [], 'после сборки блокировка снимается');
  assert.ok(
    document.querySelector('[data-md="progressBox"]').classList.contains('hidden'),
    'законченная сборка сворачивает карточку прогресса',
  );
});
