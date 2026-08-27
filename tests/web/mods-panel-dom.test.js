// Вкладка «Моды» в настоящем DOM (jsdom): панель целиком, а не отдельные
// чистые функции из mods-panel.test.js.
//
// Проверяется здесь ровно то, что чистой функцией не проверить — работа со
// <select> и со строкой состояния. Обе ошибки, ради которых тест написан, были
// именно такими: перерисовка списка игр сбрасывала выбор на первый пункт, а
// блок finally затирал итог разбора состава в том же тике, в котором тот
// появлялся.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');

// mountPanel поднимает разметку вкладки «Моды» из настоящего admin.html и
// создаёт над ней панель. Разметка берётся из файла, а не пишется в тесте:
// иначе тест проверял бы свою копию, а не то, что видит оператор.
function mountPanel(fetchImpl) {
  const html = fs.readFileSync(path.join(ADMIN_DIR, 'admin.html'), 'utf8');
  const section = html.slice(
    html.indexOf('<section id="secMods"'),
    html.indexOf('</section>', html.indexOf('<section id="secMods"')) + '</section>'.length);
  assert.ok(section.includes('data-md="game"'), 'в admin.html не нашлась разметка вкладки «Моды»');

  const dom = new JSDOM('<!doctype html><body>' + section + '</body>', { runScripts: 'outside-only' });
  const { window } = dom;
  window.fetch = fetchImpl;
  // notifyLevel/formatBytes живут в admin.js; панель зовёт их через мягкие
  // обёртки, но в браузере они есть — подставляем заглушки.
  window.notifyLevel = () => {};
  window.formatBytes = (n) => String(n) + ' B';
  window.confirm = () => true;

  const ctx = dom.getInternalVMContext();
  vm.runInContext(fs.readFileSync(path.join(ADMIN_DIR, 'ndjson.js'), 'utf8'), ctx, { filename: 'ndjson.js' });
  vm.runInContext(fs.readFileSync(path.join(ADMIN_DIR, 'mods-panel.js'), 'utf8'), ctx, { filename: 'mods-panel.js' });

  const panel = window.createModsPanel({ root: '#md_root' });
  assert.ok(panel, 'панель не создалась');
  return { window, panel, document: window.document };
}

// jsonResponse — минимальный ответ fetch, которого хватает панели.
function jsonResponse(body) {
  return Promise.resolve({
    ok: true,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  });
}

const GAMES = {
  items: [
    {
      gameId: 'lethal-company',
      title: 'Lethal Company',
      mods: { enabled: true, community: 'lethal-company', steamAppId: '1966720', loader: 'bepinex' },
    },
    {
      gameId: 'how-to-fish',
      title: 'How to Fish',
      mods: { enabled: true, community: 'how-to-fish', steamAppId: '4001890', loader: 'bepinex' },
    },
  ],
};

// routes отвечает на все запросы панели и запоминает, какие gameId она слала.
function routes(seenGameIds) {
  return function (url) {
    const u = String(url);
    const gid = (u.match(/gameId=([^&]*)/) || [])[1];
    if (gid) seenGameIds.push(decodeURIComponent(gid));

    if (u.startsWith('/admin/games')) return jsonResponse(GAMES);
    if (u.startsWith('/admin/mods/list')) return jsonResponse({ items: [], active: '', updates: [] });
    if (u.startsWith('/admin/mods/catalog')) return jsonResponse({ count: 0, results: [] });
    if (u.startsWith('/admin/mods/cache')) return jsonResponse({ files: 0, bytes: 0, ttlDays: 30 });
    return jsonResponse({});
  };
}

// settle прокручивает микрозадачи: панель работает на промисах без колбэков.
async function settle() {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

test('выбор игры в списке не сбрасывается на первую', async () => {
  const seen = [];
  const { window, panel, document } = mountPanel(routes(seen));

  panel.reload();
  await settle();

  const select = document.querySelector('[data-md="game"]');
  assert.strictEqual(select.options.length, 2);
  assert.strictEqual(select.value, 'lethal-company');

  // Оператор выбирает вторую игру.
  seen.length = 0;
  select.value = 'how-to-fish';
  select.dispatchEvent(new window.Event('change', { bubbles: true }));
  await settle();

  // Раньше здесь срабатывал повторный запрос списка игр, перерисовывавший
  // <select>: браузер выбирал первый <option>, и панель молча возвращалась к
  // lethal-company — вместе с запросами и кнопкой «Собрать».
  assert.strictEqual(select.value, 'how-to-fish', 'выбор в списке сбросился');
  assert.ok(seen.length > 0, 'панель не перезапросила данные выбранной игры');
  for (const gid of seen) {
    assert.strictEqual(gid, 'how-to-fish', 'запрос ушёл не за ту игру: ' + gid);
  }
});

test('итог разбора состава остаётся на экране', async () => {
  const plan = {
    version: 'ASTeam-LethalReloaded-2.2.12',
    packages: 151,
    missing: [],
    totalBytes: 1932735283,
    cachedBytes: 0,
    spaceOk: false,
    spaceNote: 'нужно около 3.6 ГБ, доступно 2.1 ГБ',
  };
  const base = routes([]);
  const { panel, document } = mountPanel(function (url, opts) {
    if (String(url).startsWith('/admin/mods/resolve')) return jsonResponse(plan);
    return base(url, opts);
  });

  panel.reload();
  await settle();

  // Зовём разбор так же, как это делает кнопка «Состав» на карточке каталога.
  const grid = document.querySelector('[data-md="catalog"]');
  grid.innerHTML = '<button type="button" data-mc-resolve="ASTeam/LethalReloaded">Состав</button>';
  grid.querySelector('button').click();
  await settle();

  const status = document.querySelector('[data-md="status"]').textContent;
  // Раньше блок finally звал setBusy(false) без текста и стирал эту строку —
  // предупреждение о нехватке места исчезало в том же тике.
  assert.match(status, /Пакетов: 151/);
  assert.match(status, /МАЛО МЕСТА/);
});

test('кнопки разблокируются после разбора состава', async () => {
  const base = routes([]);
  const { panel, document } = mountPanel(function (url, opts) {
    if (String(url).startsWith('/admin/mods/resolve')) {
      return jsonResponse({ packages: 1, missing: [], totalBytes: 10, spaceOk: true });
    }
    return base(url, opts);
  });

  panel.reload();
  await settle();

  const grid = document.querySelector('[data-md="catalog"]');
  grid.innerHTML = '<button type="button" data-mc-resolve="A/B">Состав</button>';
  grid.querySelector('button').click();
  await settle();

  const busy = Array.from(document.querySelectorAll('button[data-md-busy]'));
  assert.ok(busy.length > 0, 'в разметке нет кнопок с data-md-busy');
  for (const b of busy) {
    assert.strictEqual(b.disabled, false, 'кнопка осталась заблокированной после разбора');
  }
});
