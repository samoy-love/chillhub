// Полные сценарии вкладки «Моды» в jsdom: список версий, активация, дифф,
// README, сборка с потоком событий, кеш архивов и подтягивание метаданных.
//
// Отдельным файлом от mods-panel-dom.test.js: там два узких регрессионных
// сценария на конкретные найденные ошибки, здесь — обычная работа панели.
// Смешивать их значило бы утопить регрессии среди рутины.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');

function mountPanel(fetchImpl) {
  const html = fs.readFileSync(path.join(ADMIN_DIR, 'admin.html'), 'utf8');
  const start = html.indexOf('<section id="secMods"');
  const section = html.slice(start, html.indexOf('</section>', start) + '</section>'.length);

  const dom = new JSDOM('<!doctype html><body>' + section + '</body>', { runScripts: 'outside-only' });
  const { window } = dom;
  window.fetch = fetchImpl;
  window.notifyLevel = () => {};
  window.formatBytes = (n) => String(n) + ' B';
  window.confirm = () => true;
  // jsdom не даёт странице TextEncoder/TextDecoder, а браузер даёт. Без них
  // потоковая ветка readNdjsonStream падает на первом же чанке, и любой тест
  // на живой поток событий проверял бы обработку исключения вместо прогресса.
  window.TextEncoder = TextEncoder;
  window.TextDecoder = TextDecoder;

  const ctx = dom.getInternalVMContext();
  // Абсолютный путь обязателен: c8 привязывает покрытие к исходнику только по
  // нему, с относительным именем исполненный код остаётся в отчёте нулём.
  for (const file of ['ndjson.js', 'mods-panel.js']) {
    const abs = path.join(ADMIN_DIR, file);
    vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });
  }

  const panel = window.createModsPanel({ root: '#md_root' });
  assert.ok(panel, 'панель не создалась');
  return { window, panel, document: window.document };
}

function jsonResponse(body) {
  return Promise.resolve({
    ok: true,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  });
}

const GAMES = {
  items: [{
    gameId: 'lethal-company',
    title: 'Lethal Company',
    mods: { enabled: true, community: 'lethal-company', steamAppId: '1966720', loader: 'bepinex' },
  }],
};

// VERSIONS — типичный ответ /admin/mods/list: активная версия, вторая
// с пропущенными модами и доступное обновление у первой.
const VERSIONS = {
  active: 'Team-Pack-1.0.0',
  items: [
    {
      version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true,
      packages: 3, files: 7, bytes: 1024, missing: 0, createdAt: '2026-08-27T10:00:00',
    },
    {
      version: 'Old-Pack-0.9.0', displayName: 'Old', active: false,
      packages: 2, files: 4, bytes: 512, missing: 1, createdAt: '2026-08-20T10:00:00',
    },
  ],
  updates: [{ version: 'Team-Pack-1.0.0', namespace: 'Team', name: 'Pack', latest: '1.1.0', deprecated: false }],
};

// mount поднимает панель с готовым списком версий; extra перехватывает
// отдельные адреса, остальное отвечает заглушками.
async function mount(extra) {
  const calls = [];
  const ctx = mountPanel(function (url, opts) {
    const u = String(url);
    calls.push({ url: u, opts: opts || {} });
    const hit = extra && extra(u, opts || {});
    if (hit) return hit;
    if (u.startsWith('/admin/games')) return jsonResponse(GAMES);
    if (u.startsWith('/admin/mods/list')) return jsonResponse(VERSIONS);
    if (u.startsWith('/admin/mods/catalog')) return jsonResponse({ count: 0, results: [] });
    if (u.startsWith('/admin/mods/cache')) return jsonResponse({ files: 151, bytes: 2048, ttlDays: 30 });
    return jsonResponse({});
  });
  ctx.calls = calls;
  ctx.panel.reload();
  await settle();
  return ctx;
}

async function settle() {
  for (let i = 0; i < 30; i++) await Promise.resolve();
}

// putButton кладёт в сетку каталога одну кнопку и нажимает её — так же, как
// это делает настоящая карточка пакета.
async function clickCatalogButton(document, attr, value) {
  const grid = document.querySelector('[data-md="catalog"]');
  grid.innerHTML = '<button type="button" ' + attr + '="' + value + '">x</button>';
  grid.querySelector('button').click();
  await settle();
}

test('список версий показывает активную, обновление и пропущенные моды', async () => {
  const { document } = await mount();
  const html = document.querySelector('[data-md="versions"]').innerHTML;

  assert.match(html, /активен/);
  assert.match(html, /доступна 1\.1\.0/);
  assert.match(html, /пропущено 1/);
  // Активную версию нельзя ни удалить, ни активировать повторно.
  assert.doesNotMatch(html, /data-md-delete="Team-Pack-1\.0\.0"/);
  assert.match(html, /data-md-activate="Old-Pack-0\.9\.0"/);
});

test('активация и удаление версии уходят на сервер', async () => {
  const { document, calls } = await mount();

  document.querySelector('[data-md-activate]').click();
  await settle();
  const activate = calls.find((c) => c.url.startsWith('/admin/mods/activate'));
  assert.ok(activate, 'запрос активации не ушёл');
  assert.strictEqual(activate.opts.method, 'POST');
  assert.match(String(activate.opts.body), /version=Old-Pack-0\.9\.0/);

  document.querySelector('[data-md-delete]').click();
  await settle();
  assert.ok(calls.some((c) => c.url.startsWith('/admin/mods/deleteVersion')), 'запрос удаления не ушёл');
});

test('дифф состава показывается по кнопке', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/mods/diff')) return null;
    return jsonResponse({
      items: [
        { package: 'Author-CoolMod', from: '1.0.0', to: '1.1.0', change: 'updated' },
        { package: 'Other-Extra', to: '2.0.0', change: 'added' },
      ],
    });
  });

  document.querySelector('[data-md-diff]').click();
  await settle();

  const diff = document.querySelector('[data-md="diff"]');
  assert.ok(!diff.classList.contains('hidden'), 'блок диффа остался скрытым');
  assert.match(diff.innerHTML, /Author-CoolMod/);
  assert.match(diff.innerHTML, /1\.0\.0 → 1\.1\.0/);
});

test('README показывается текстом, а не разметкой', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/mods/readme')) return null;
    return jsonResponse({ markdown: '# Pack\n\n<img src=x onerror=alert(1)>', version: '1.0.0' });
  });

  await clickCatalogButton(document, 'data-mc-readme', 'Team/Pack');

  const box = document.querySelector('[data-md="readme"]');
  assert.ok(!box.classList.contains('hidden'), 'README не показан');
  const body = document.querySelector('[data-md="readmeBody"]');
  // README чужого автора вставляется ТЕКСТОМ: он полон сырого HTML с чужого
  // домена, и innerHTML здесь был бы приглашением к XSS в админке.
  assert.match(body.textContent, /onerror=alert\(1\)/);
  assert.strictEqual(body.querySelector('img'), null);

  document.querySelector('[data-md-readme-close]').click();
  assert.ok(box.classList.contains('hidden'), 'README не закрылся');
});

test('слаг подставляется значением, а не подсказкой', async () => {
  // Серый placeholder читался как заполненное поле, и «Подтянуть» отвечал
  // «Укажите слаг игры на Thunderstore» на то, что оператор уже видел.
  const { document } = await mount();

  const slug = document.querySelector('[data-md="slug"]');
  assert.strictEqual(slug.value, 'lethal-company');
  assert.strictEqual(slug.getAttribute('placeholder'), null);
});

test('у игры без модов экран объясняет, что даст подключение', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/games')) return null;
    return jsonResponse({ items: [{ gameId: 'peak', title: 'PEAK' }] });
  });

  const meta = document.querySelector('[data-md="meta"]').textContent;
  assert.match(meta, /PEAK/);
  assert.match(meta, /Подтянуть из Thunderstore/);
  // Слаг всё равно готов к нажатию: он берётся из идентификатора игры.
  assert.strictEqual(document.querySelector('[data-md="slug"]').value, 'peak');
});

test('ссылка на каталог сайта работает и до подключения модов', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/games')) return null;
    return jsonResponse({ items: [{ gameId: 'peak', title: 'PEAK' }] });
  });

  const browse = document.querySelector('[data-md="browse"]');
  assert.match(browse.getAttribute('href'), /thunderstore\.io\/c\/peak\//);
  assert.ok(!browse.classList.contains('disabled'), 'ссылка выключена, хотя слаг известен');
});

test('сборка ведёт прогресс по потоку событий', async () => {
  const events = [
    { type: 'start', message: 'разбор состава' },
    { type: 'resolved', total: 3, message: 'пакетов: 3' },
    { type: 'package', step: 1, total: 3, message: 'Team-Pack-1.0.0' },
    { type: 'package', step: 3, total: 3, message: 'BepInEx-BepInExPack-5.4.2305' },
    { type: 'done', message: 'собрано: 7 файлов' },
  ];
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    return Promise.resolve({
      ok: true,
      text: () => Promise.resolve(events.map((e) => JSON.stringify(e)).join('\n') + '\n'),
    });
  });

  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  // Полоса доходит до конца: сборка идёт минутами, и молчащий экран
  // неотличим от зависшей.
  assert.strictEqual(document.querySelector('[data-md="progress"]').style.width, '100%');
});

test('фаза разбора состава показывает счётчик, а не молчит', async () => {
  // Разбор дерева и опрос размеров архивов — первые две минуты сборки, и до
  // них не приходит ни одного события package. Именно эта тишина приехала как
  // «админка зависла на этапе разбор состава модпака».
  const events = [
    { type: 'start', message: 'разбор состава модпака' },
    { type: 'resolving', step: 1, message: 'BepInEx-BepInExPack-5.4.2305' },
    { type: 'resolving', step: 2, message: 'Team-Mod-1.0.0' },
    { type: 'sizing', step: 2, total: 2, message: 'Team-Mod-1.0.0' },
  ];
  const seen = [];
  let win = null;
  const mounted = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    return Promise.resolve({
      ok: true,
      body: {
        getReader() {
          let i = 0;
          // Кодировщик берём из окна jsdom, а не из Node: TextDecoder внутри
          // страницы не принимает Uint8Array из чужого realm и роняет чтение
          // потока — тест проверял бы не то, что нужно.
          const enc = new win.TextEncoder();
          return {
            read() {
              if (i >= events.length) return Promise.resolve({ done: true });
              const line = JSON.stringify(events[i++]) + '\n';
              // Снимаем строку состояния перед каждым событием: важно, что она
              // меняется по ходу, а не только в конце.
              seen.push(win.document.querySelector('[data-md="status"]').textContent);
              return Promise.resolve({ done: false, value: enc.encode(line) });
            },
          };
        },
      },
    });
  });
  win = mounted.window;
  const { document } = mounted;

  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  const status = document.querySelector('[data-md="status"]').textContent;
  assert.match(status, /Оценка размера 2\/2/);
  assert.ok(seen.some((t) => /Разбор состава: найдено модов 1/.test(t))
    || /Разбор состава: найдено модов/.test(seen.join('|')),
  'счётчик найденных модов не появлялся: ' + JSON.stringify(seen));
});

test('параллельное скачивание показывает обе величины и повторы', async () => {
  // Скачано и установлено — РАЗНЫЕ числа, пока работают шесть потоков.
  // Одна строка на оба показателя прыгала бы назад на каждом событии.
  const events = [
    { type: 'resolved', total: 4, message: 'пакетов: 4' },
    { type: 'downloading', step: 3, total: 4, bytes: 700, parallel: 2, message: 'A-Mod-1.0.0' },
    { type: 'retry', step: 1, total: 5, message: 'B-Mod-1.0.0 — попытка 1 из 5: обрыв' },
    { type: 'package', step: 1, total: 4, message: 'A-Mod-1.0.0' },
  ];
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    return Promise.resolve({
      ok: true,
      text: () => Promise.resolve(events.map((e) => JSON.stringify(e)).join('\n') + '\n'),
    });
  });

  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  const detail = document.querySelector('[data-md="detail"]').textContent;
  assert.match(detail, /скачано 3 из 4/);
  assert.match(detail, /установлено 4/, 'после успешной сборки установлены все: ' + detail);
  const retries = document.querySelector('[data-md="retries"]').textContent;
  assert.match(retries, /B-Mod-1\.0\.0 — попытка 1 из 5/);
  assert.ok(!document.querySelector('[data-md="retriesBox"]').classList.contains('hidden'),
    'блок повторов остался скрытым');
});

test('карточка сборки прячется, пока сборки нет, и сворачивается после неё', async () => {
  // Полная синяя полоса «скачано 22 из 22» посреди экрана — состояние из
  // прошлого: занимает место, ничем не убирается и врёт, что что-то идёт.
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    return Promise.resolve({ ok: true, text: () => Promise.resolve('{"type":"done"}\n') });
  });

  const card = document.querySelector('[data-md="buildCard"]');
  assert.ok(card.classList.contains('hidden'), 'карточка сборки видна до всякой сборки');

  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  assert.ok(!card.classList.contains('hidden'), 'карточка не показалась при сборке');
  assert.ok(
    document.querySelector('[data-md="progressBox"]').classList.contains('hidden'),
    'полоса прогресса осталась висеть после завершения');
});

test('сборка без единого события сообщает о буферизации', async () => {
  const { document, calls } = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    return Promise.resolve({ ok: true, text: () => Promise.resolve('') });
  });

  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  // Пустой ответ — отдельная неисправность (обычно прокси буферизует), и
  // молча показать «готово» здесь было бы худшим ответом.
  assert.ok(calls.some((c) => c.url.startsWith('/admin/api/mods/build')));
  assert.notStrictEqual(document.querySelector('[data-md="progress"]').style.width, '100%');
});

test('сборка с пропавшими модами переспрашивает и повторяет с согласием', async () => {
  let attempts = 0;
  const { document, window } = await mount(function (url, opts) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    attempts++;
    if (String(opts.body).includes('allowMissing=1')) {
      return Promise.resolve({ ok: true, text: () => Promise.resolve('{"type":"done"}\n') });
    }
    return Promise.resolve({
      ok: true,
      text: () => Promise.resolve(JSON.stringify({
        type: 'error',
        message: 'mods: 2 пакетов больше нет на Thunderstore: A-B-1.0.0',
      }) + '\n'),
    });
  });

  window.confirm = () => true;
  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  // Согласие оператора — единственное, что позволяет собрать неполный пак;
  // без повтора он остался бы с ошибкой и без способа её разрешить.
  assert.strictEqual(attempts, 2, 'повтор с согласием не случился');
});

test('сборка с пропавшими модами не повторяется без согласия', async () => {
  let attempts = 0;
  const { document, window } = await mount(function (url) {
    if (!url.startsWith('/admin/api/mods/build')) return null;
    attempts++;
    return Promise.resolve({
      ok: true,
      text: () => Promise.resolve(JSON.stringify({
        type: 'error',
        message: '3 пакетов больше нет на Thunderstore',
      }) + '\n'),
    });
  });

  window.confirm = () => false;
  await clickCatalogButton(document, 'data-mc-build', 'Team/Pack');

  assert.strictEqual(attempts, 1, 'отказ оператора не остановил сборку');
});

test('кеш архивов показывается и чистится', async () => {
  const { document, calls, window } = await mount();

  const cacheLine = document.querySelector('[data-md="cache"]').textContent;
  assert.match(cacheLine, /151 файлов/);
  assert.match(cacheLine, /30 дней/);

  document.querySelector('[data-md-cache-sweep]').click();
  await settle();
  assert.ok(
    calls.some((c) => c.url.startsWith('/admin/mods/cache') && c.opts.method === 'POST'),
    'подметание кеша не ушло на сервер');

  window.confirm = () => true;
  document.querySelector('[data-md-cache-clear]').click();
  await settle();
  assert.ok(
    calls.some((c) => String(c.opts.body || '').includes('all=1')),
    'полная очистка кеша не ушла на сервер');
});

test('метаданные игры подтягиваются из Thunderstore', async () => {
  const { document, calls } = await mount(function (url) {
    if (!url.startsWith('/admin/games/ecosystem')) return null;
    return jsonResponse({
      status: 'ok',
      mods: {
        enabled: true, community: 'lethal-company', loader: 'bepinex',
        steamAppId: '1966720', steamFolder: 'Lethal Company',
        sectionUuid: '018bb887-fa52-7236-0344-e714696ee5d5',
      },
      browseUrl: 'https://thunderstore.io/c/lethal-company/?ordering=most-downloaded',
    });
  });

  document.querySelector('[data-md="slug"]').value = 'lethal-company';
  document.querySelector('[data-md-pull]').click();
  await settle();

  assert.ok(calls.some((c) => c.url.startsWith('/admin/games/ecosystem')), 'запрос за метаданными не ушёл');
  const meta = document.querySelector('[data-md="meta"]').innerHTML;
  assert.match(meta, /1966720/);
  assert.match(meta, /Lethal Company/);
  // Ссылка на каталог сайта обязана нести UUID раздела: со слагом фильтр
  // молча не применяется, и оператор ищет пак среди тысяч пакетов.
  assert.match(document.querySelector('[data-md="browse"]').href, /section=018bb887/);
});

test('пустой слаг не отправляет запрос за метаданными', async () => {
  const { document, calls } = await mount();
  const before = calls.length;

  document.querySelector('[data-md="slug"]').value = '   ';
  document.querySelector('[data-md-pull]').click();
  await settle();

  assert.ok(
    !calls.slice(before).some((c) => c.url.startsWith('/admin/games/ecosystem')),
    'запрос ушёл с пустым слагом');
});

test('смена сортировки перезапрашивает каталог с первой страницы', async () => {
  const { document, window, calls } = await mount();

  document.querySelector('[data-md-next]').click();
  await settle();

  const ordering = document.querySelector('[data-md="ordering"]');
  ordering.value = 'newest';
  ordering.dispatchEvent(new window.Event('change', { bubbles: true }));
  await settle();

  const last = calls.filter((c) => c.url.startsWith('/admin/mods/catalog')).pop();
  assert.match(last.url, /ordering=newest/);
  // Страница обязана вернуться к первой: иначе новая сортировка показывается
  // со второй страницы и выглядит как «ничего не нашлось».
  assert.match(last.url, /page=1/);
});

test('перелистывание каталога не уходит в отрицательные страницы', async () => {
  const { document, calls } = await mount();

  document.querySelector('[data-md-prev]').click();
  await settle();

  const catalogCalls = calls.filter((c) => c.url.startsWith('/admin/mods/catalog'));
  for (const c of catalogCalls) {
    assert.doesNotMatch(c.url, /page=0|page=-/, 'запрос ушёл с невозможной страницей: ' + c.url);
  }
});

test('ошибка сервера в каталоге показывается на месте списка', async () => {
  const { document } = await mount(function (url) {
    if (!url.startsWith('/admin/mods/catalog')) return null;
    return Promise.resolve({ ok: false, text: () => Promise.resolve('каталог Thunderstore недоступен') });
  });

  assert.match(document.querySelector('[data-md="catalog"]').innerHTML, /недоступен/);
});
