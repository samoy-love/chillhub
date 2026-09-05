// Длинные дела панели 2.0 целиком, в настоящем DOM.
//
// Приём тот же, что в admin2-dom.test.js: реальный index.html грузится в
// jsdom, все его <script> исполняются в браузерном порядке, сеть
// подменена полностью. Отличие — здесь проверяются не одиночные записи,
// а дела, которые идут минутами и умеют оборваться на середине.
//
// Смысл именно в обрывах. Что панель показывает на успешном пути, видно
// и глазами; что она показывает, когда связь легла на сороковом
// проценте, а пакет пропал с Thunderstore, — не видно никак, и в версии
// 1.0 это не показывалось вовсе.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui');

const FIXTURES = {
  summary: { launcher: { pending: true, newest: '1.6.25', active: '1.6.24' }, mods: [] },
  list: {
    items: [
      { version: '1.6.25', date: '04.09.2026', files: 478, size: 121400000, state: 'uploaded' },
      { version: '1.6.24', date: '31.08.2026', files: 476, size: 121100000, state: 'active' },
    ],
  },
  games: {
    items: [
      // Моды включены только у первой: про остальных `mods/list` и не спросят
      { gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe', order: 0, mods: { enabled: true } },
      { gameId: 'peak', title: 'PEAK', exeRelativePath: 'PEAK.exe', order: 1 },
    ],
  },
  /* Форма ответа настоящая: список версий плюс активная. Отдельного
     поля с собранной версией сервер не отдаёт — она первая в списке. */
  'mods/list': {
    gameId: 'repo',
    active: '1.9.8',
    items: [
      { version: '1.9.9', displayName: 'Moo Modpack', createdAt: '2026-09-01T10:00:00Z', packages: 17, bytes: 251000000 },
      { version: '1.9.8', displayName: 'Moo Modpack', packages: 16, bytes: 250000000 },
    ],
  },
  'news/list': { items: [{ id: 'release', slug: 'release', title: 'Заметка', published: false }] },
  'news/get': { markdown: '# Заметка\n\nТекст заметки', published: false, coverUrl: '' },
  'feedback/list': { items: [{ id: 'f1', type: 'bug', status: 'new', comment: 'обрывается' }] },
  'maintenance/get': { enabled: false, reason: '', blocks: {} },
  'metrics/summary': { days: [{ date: '04.09', launcherStarts: 10, updates: 4, errors: 1 }] },
  'metrics/errors': { items: [{ code: 'download_reset', n: 3, what: 'обрыв' }] },
  'system/free': { freeBytes: 214000000000, totalBytes: 480000000000 },
  'mods/cache': { files: 412, bytes: 8900000000 },
  'games/gallery': {
    cover: 'cover.png',
    items: [
      { name: 'cover.png', size: 240000 },
      { name: 'screens', dir: true },
      { name: 'guide.pdf', size: 90000 },
    ],
  },
};

/** Поднимает панель в jsdom. `routes` подменяет отдельные ответы. */
async function boot(routes) {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/' });
  const { window } = dom;

  const calls = [];
  const table = Object.assign({}, FIXTURES, routes || {});

  window.fetch = async (url, init) => {
    const u = String(url);
    const method = (init && init.method) || 'GET';
    let body = null;
    const raw = init && init.body;
    if (typeof raw === 'string') {
      try {
        body = JSON.parse(raw);
      } catch {
        // Запись уезжает формой: сервер читает её, а не JSON
        body = Object.fromEntries(new URLSearchParams(raw));
      }
    } else if (raw && typeof raw.entries === 'function') {
      /* multipart: так уходят ручки, которые на сервере начинаются с
         ParseMultipartForm. Разбираем в тот же простой объект, что и
         форму, — проверкам важны имена полей, а не способ упаковки. */
      body = Object.fromEntries(raw.entries());
    } else if (raw) {
      body = raw;
    }
    /* Транспорт перекладывает заголовки в Headers, добавляя CSRF, —
       читаем оба вида, иначе тип тела теряется на ровном месте. */
    const h = init && init.headers;
    const type = (h && (typeof h.get === 'function' ? h.get('content-type') : h['content-type'])) || '';
    calls.push({ method, url: u, body, type });

    /* Манифесты сборок раздаются публично, а не через админ-API: их
       адрес не начинается с префикса, и подменяются они отдельно. */
    if (!u.startsWith('/admin/api/')) {
      return table.__raw ? table.__raw(u) : { ok: false, status: 404, text: async () => '' };
    }

    const key = u.replace('/admin/api/', '').split('?')[0];
    const hit = table[key];
    if (typeof hit === 'function') return hit({ method, url: u, body });
    if (hit !== undefined) return { ok: true, status: 200, text: async () => JSON.stringify(hit) };
    return { ok: true, status: 200, text: async () => '{}' };
  };

  for (const src of [...window.document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src'))) {
    const file = path.join(V2, src.replace('/admin/ui/', ''));
    vm.runInContext(fs.readFileSync(file, 'utf8'), dom.getInternalVMContext(), { filename: file });
  }

  /* Настоящий переход jsdom не выполняет — вместо него считаем уходы на
     вход. Панель ходит туда одной функцией именно ради этого. */
  const left = [];
  if (window.CH2Api) window.CH2Api.goLogin = () => left.push(window.CH2Api.LOGIN);

  for (let i = 0; i < 40; i++) await new Promise((r) => setTimeout(r, 0));
  return { window, calls, dom, left };
}

/* Досматривает начатое до конца.

   Дело после ответа сервера ещё закрывает лист, перечитывает разделы и
   перерисовывает раздел. Оборвать окно раньше — значит получить падение
   в чужой асинхронной работе вместо результата теста. */
const settle = (n = 30) => until(() => false, n);

/** Ждёт условия, прокручивая очередь микрозадач. */
async function until(fn, tries = 200) {
  for (let i = 0; i < tries; i++) {
    const v = fn();
    if (v) return v;
    await new Promise((r) => setTimeout(r, 0));
  }
  return null;
}

/** Открывает раздел и жмёт кнопку дела. */
async function open(window, hash, act) {
  window.location.hash = hash;
  const btn = await until(() => window.document.querySelector(`[data-act="${act}"]`));
  assert.ok(btn, 'кнопки «' + act + '» нет в разделе ' + hash);
  btn.click();
  const sheet = await until(() => window.document.querySelector('.sheet'));
  assert.ok(sheet, 'лист «' + act + '» не открылся');
  return sheet;
}

const text = (el) => el.textContent.replace(/\s+/g, ' ').trim();

/* ---------- Оболочка ---------- */

test('лист закрывается по Escape, крестику и клику мимо', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());
  const doc = window.document;

  await open(window, '#launcher', 'upload');
  doc.querySelector('[data-sheet-close]').click();
  assert.strictEqual(doc.querySelector('.sheet'), null, 'крестик не закрыл лист');

  await open(window, '#launcher', 'upload');
  doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.strictEqual(doc.querySelector('.sheet'), null, 'Escape не закрыл лист');

  const back = (await open(window, '#launcher', 'upload')).closest('.sheet-back');
  back.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  assert.strictEqual(doc.querySelector('.sheet'), null, 'клик мимо не закрыл лист');
});

test('фокус уходит внутрь листа, а не остаётся на странице под ним', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());
  const sheet = await open(window, '#launcher', 'upload');
  assert.ok(sheet.contains(window.document.activeElement), 'фокус остался снаружи листа');
});

/* ---------- Загрузка ---------- */

test('загрузка доходит до конца и не выдаёт себя за выкатку игрокам', async (t) => {
  const chunks = [];
  const { window, calls } = await boot({
    'upload/init': { uploadId: 'u1', chunkSize: 4, totalChunks: 3 },
    'upload/status': { received: [] },
    'upload/process': () => ({
      ok: true,
      status: 200,
      text: async () => JSON.stringify({ type: 'start' }) + '\n' + JSON.stringify({ type: 'done' }) + '\n',
    }),
  });
  t.after(() => window.close());

  const sheet = await open(window, '#launcher', 'upload');
  window.XMLHttpRequest = undefined; // куски льём подменённым PUT, а не XHR

  // Файл подсовываем напрямую: диалог выбора в jsdom не открыть
  window.CH2Upload.run(
    { name: 'ChillHub-1.6.25.zip', size: 12, slice: () => 'кусок' },
    { kind: 'launcher', chunkSize: 4 },
    {
      api: window.CH2Api.makeApi(),
      chunks: {
        uploadChunkWithRetries: async (id, i) => {
          chunks.push(i);
          return { ok: true };
        },
        runWorkerPool: window.runWorkerPool,
        pendingBytes: window.pendingBytes,
      },
      slice: () => 'кусок',
      concurrency: () => 2,
    }
  );

  await until(() => calls.some((c) => c.url.includes('upload/complete')));
  assert.deepStrictEqual(chunks.sort(), [0, 1, 2], 'залиты не все куски');
  assert.ok(sheet, 'лист пропал посреди загрузки');

  const done = window.CH2Views.uploadStatus({ phase: 'done' });
  assert.match(done.text, /Игрокам версия пока не ушла/);
});

test('докачка спрашивает сервер и не льёт то, что уже лежит', async (t) => {
  const sent = [];
  const { window } = await boot({
    'upload/init': { uploadId: 'u1', chunkSize: 4, totalChunks: 3 },
    'upload/status': { received: [0, 1] },
  });
  t.after(() => window.close());

  await window.CH2Upload.run(
    { name: 'a.zip', size: 12 },
    { kind: 'launcher', chunkSize: 4 },
    {
      api: window.CH2Api.makeApi(),
      chunks: {
        uploadChunkWithRetries: async (id, i) => {
          sent.push(i);
          return { ok: true };
        },
        runWorkerPool: window.runWorkerPool,
        pendingBytes: window.pendingBytes,
      },
      slice: () => 'кусок',
      concurrency: () => 2,
    }
  );

  // Заливка на 1,8 ГБ рвётся, и повторять её с нуля неприемлемо
  assert.deepStrictEqual(sent, [2], 'докачка залила уже лежащие куски');
});

/* ---------- Сборка ---------- */

test('журнал сборки наполняется строками по мере их прихода', async (t) => {
  const lines = [
    { type: 'info', message: 'читаем список модов' },
    { type: 'info', message: 'качаем BepInEx' },
    { type: 'done', message: 'собрано 17 модов' },
  ];
  const { window } = await boot({
    'mods/build': () => ({ ok: true, status: 200, text: async () => lines.map((l) => JSON.stringify(l)).join('\n') + '\n' }),
  });
  t.after(() => window.close());

  const sheet = await open(window, '#packs', 'build');
  await until(() => sheet.querySelector('[data-build-outcome]'));

  const body = text(sheet.querySelector('.sheet-body'));
  assert.match(body, /читаем список модов/);
  assert.match(body, /собрано 17 модов/);
  assert.match(body, /отдайте новую версию/, 'сборка выдана за выкатку игрокам');
});

test('пропавший с Thunderstore пакет спрашивают один раз и собирают без него', async (t) => {
  let attempts = 0;
  const { window, calls } = await boot({
    'mods/build': ({ body }) => {
      attempts++;
      const missing = { type: 'error', message: 'пакета Mod.Foo больше нет на Thunderstore' };
      const okDone = { type: 'done', message: 'собрано без пропавших' };
      return {
        ok: true,
        status: 200,
        text: async () => JSON.stringify(body && body.allowMissing ? okDone : missing) + '\n',
      };
    },
  });
  t.after(() => window.close());

  const sheet = await open(window, '#packs', 'build');
  const modal = await until(() => window.document.querySelector('.modal'));
  assert.ok(modal, 'про пропавшие пакеты не спросили');
  assert.match(modal.textContent, /без пропавших пакетов/);

  modal.querySelector('[data-yes]').click();
  await until(() => sheet.querySelector('[data-build-outcome]'));

  assert.strictEqual(attempts, 2, 'повтор без пропавших не случился');
  // Второй вопрос превратил бы отказ на середине в бесконечный диалог
  assert.strictEqual(window.document.querySelector('.modal'), null, 'спросили второй раз');
  assert.ok(calls.some((c) => c.body && c.body.allowMissing === '1'));
});

test('страница ошибки от прокси не попадает на экран куском разметки', async (t) => {
  const { window } = await boot({
    'mods/build': () => ({ ok: false, status: 502, text: async () => '<!DOCTYPE html><html><body>Bad Gateway</body></html>' }),
  });
  t.after(() => window.close());

  const sheet = await open(window, '#packs', 'build');
  const out = await until(() => sheet.querySelector('[data-build-outcome]'));
  assert.ok(out, 'итог сборки не показан');
  assert.match(out.textContent, /код 502/);
  assert.ok(!/DOCTYPE|<body>/.test(out.textContent), 'на экран вывалилась страница ошибки');
});

/* ---------- Новость ---------- */

test('лист на экране всегда один', async (t) => {
  // Два нажатия «Написать» подряд клали на страницу два наложенных
  // редактора: человек правит верхний, нижний ждёт под ним со своим
  // черновиком и своими обработчиками, а Escape закрывает только верхний
  const { window } = await boot();
  t.after(() => window.close());

  await open(window, '#news', 'new-post');
  assert.strictEqual(window.document.querySelectorAll('[data-sheet]').length, 1);

  window.document.querySelector('[data-act="new-post"]').click();
  await settle();
  assert.strictEqual(window.document.querySelectorAll('[data-sheet]').length, 1, 'лист открылся поверх прежнего');
});

test('лист, открытый из листа, ложится поверх, а не вместо', async (t) => {
  // Выбор вложения открывается ИЗ редактора заметки и по выбору пишет
  // разметку обратно в его поле. Закрыв редактор, панель писала бы в
  // вырванный из страницы узел, и набранное пропадало бы молча
  const { window } = await boot();
  t.after(() => window.close());

  const editor = await open(window, '#news', 'new-post');
  await until(() => editor.querySelector('[name="markdown"]'));

  editor.querySelector('[data-flow="assets"]').click();
  await until(() => window.document.querySelectorAll('[data-sheet]').length === 2);

  assert.strictEqual(window.document.querySelectorAll('[data-sheet]').length, 2, 'редактор закрылся под выбором вложения');
  assert.ok(editor.isConnected, 'редактор вырван из страницы');
});

test('заметка сохраняется тем, что набрали, и черновик после этого убирается', async (t) => {
  const { window, calls, left } = await boot();
  t.after(() => window.close());
  void left;

  const sheet = await open(window, '#news', 'new-post');
  await until(() => sheet.querySelector('[name="markdown"]'));

  const set = (name, value) => {
    const el = sheet.querySelector(`[name="${name}"]`);
    el.value = value;
    el.dispatchEvent(new window.Event('input', { bubbles: true }));
  };
  set('markdown', '# Вышла 1.6.25\n\nПочинили обрыв скачивания больших файлов.');
  set('slug', 'release-1-6-25');

  // Черновик пишется на каждый ввод: заметку набирают минутами
  assert.ok(window.CH2News.readDraft(window.localStorage, { slug: 'release-1-6-25' }), 'черновик не сохранён');

  sheet.querySelector('[data-flow="save"]').click();
  await until(() => calls.some((c) => c.url.includes('news/save')));

  const saved = calls.find((c) => c.url.includes('news/save'));
  // Имена полей — контракт сервера: scope, slug, markdown; заголовка среди них нет
  assert.strictEqual(saved.body.scope, 'launcher');
  assert.strictEqual(saved.body.slug, 'release-1-6-25');
  assert.match(saved.body.markdown, /# Вышла 1\.6\.25/);
  assert.ok(!('title' in saved.body), 'уехало поле, которого сервер не знает');

  const gone = await until(() => window.CH2News.readDraft(window.localStorage, { slug: 'release-1-6-25' }) === null);
  assert.ok(gone, 'черновик остался после сохранения');
  await settle();
});

test('пустую новость не отправляют, а показывают, чего не хватает', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#news', 'new-post');
  await until(() => sheet.querySelector('[name="title"]'));
  sheet.querySelector('[data-flow="save"]').click();

  await new Promise((r) => setTimeout(r, 10));
  assert.ok(!calls.some((c) => c.url.includes('news/save')), 'пустая новость ушла на сервер');
  assert.ok(sheet.querySelector('.help--bad'), 'не сказано, чего не хватает');
});

test('оставшийся черновик предлагают вернуть, а не подставляют молча', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const where = { slug: 'release', gameId: '' };
  window.CH2News.saveDraft(window.localStorage, Object.assign({ markdown: '# Заметка\n\nНедописанное' }, where));

  const sheet = await open(window, '#news', 'edit-post');
  await until(() => sheet.querySelector('[data-draft-restore]'));
  assert.ok(sheet.querySelector('[data-draft-restore]'), 'про черновик не сказали');

  // Молча подставленный черновик затёр бы то, что уже на сервере
  assert.match(sheet.querySelector('[name="markdown"]').value, /Текст заметки/);

  sheet.querySelector('[data-draft-restore]').click();
  await until(() => /Недописанное/.test(sheet.querySelector('[name="markdown"]').value));
  assert.match(sheet.querySelector('[name="markdown"]').value, /Недописанное/);
  await settle();
});

/* ---------- Галерея ---------- */

test('галерея показывает содержимое папки и помечает обложку', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'gallery');
  await until(() => sheet.querySelector('[data-name="cover.png"]'));

  const body = text(sheet.querySelector('.sheet-body'));
  assert.match(body, /cover\.png/);
  assert.match(body, /обложка/);
  // Обложкой можно сделать только картинку, и только не текущую
  assert.strictEqual(sheet.querySelector('[data-cover="cover.png"]'), null);
  assert.strictEqual(sheet.querySelector('[data-cover="guide.pdf"]'), null);
});

test('удаление обложки предупреждает о том, что увидит игрок', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'gallery');
  await until(() => sheet.querySelector('[data-remove="cover.png"]'));
  sheet.querySelector('[data-remove="cover.png"]').click();

  const modal = await until(() => window.document.querySelector('.modal'));
  assert.ok(modal, 'удаление не спросило');
  assert.match(modal.textContent, /витрина останется с градиентом/);

  modal.querySelector('[data-no]').click();
  await new Promise((r) => setTimeout(r, 10));
  assert.ok(!calls.some((c) => c.url.includes('gallery/delete')), 'отказ всё равно удалил');
});

test('переименование в занятое имя не уходит на сервер', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'gallery');
  await until(() => sheet.querySelector('[data-rename="guide.pdf"]'));

  // Windows не различает регистр: «Cover.png» затёрло бы «cover.png» молча
  window.prompt = () => 'Cover.PNG';
  sheet.querySelector('[data-rename="guide.pdf"]').click();

  await new Promise((r) => setTimeout(r, 10));
  assert.ok(!calls.some((c) => c.url.includes('gallery/rename')), 'занятое имя ушло на сервер');
  assert.match(text(window.document.querySelector('.toast')), /уже занято/);
});

/* ---------- Порядок игр ---------- */

test('пока порядок не менялся, сохранять нечего', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'order');
  const save = sheet.querySelector('[data-flow="save"]');
  assert.ok(save.disabled, 'предложено сохранить неизменённый порядок');
  assert.match(text(sheet.querySelector('footer')), /сохранять нечего/);
});

test('перестановка называет последствие и уходит на сервер пересчитанной', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'order');
  sheet.querySelector('[data-down="repo"]').click();

  assert.match(text(sheet.querySelector('footer')), /Игроки увидят новый порядок сразу/);
  sheet.querySelector('[data-flow="save"]').click();
  await until(() => calls.some((c) => c.url.includes('games/save')));

  const saved = calls.find((c) => c.url.includes('games/save'));
  const ids = saved.body.items.map((g) => g.gameId);
  assert.deepStrictEqual(ids, ['peak', 'repo']);
  // Лаунчер помнит игру по её месту в списке — номера пересчитываются целиком
  assert.deepStrictEqual(saved.body.items.map((g) => g.order), [0, 1]);
  await settle();
});

/* ---------- Подбор параметров ---------- */

test('прогоны помнит браузер, который их и мерил', async (t) => {
  // Прогон меряет канал ЭТОГО компьютера: с другой машины его число не
  // значит ничего, а показанное как общее — сбивает с толку
  const { window } = await boot();
  t.after(() => window.close());

  window.CH2Tuning.remember(window.localStorage, [
    { chunk: '8 МиБ', streams: 4, mbps: 92.4, retries: 0 },
    { chunk: '2 МиБ', streams: 8, mbps: 79.3, retries: 3 },
  ]);

  const sheet = await open(window, '#transfer', 'bench');
  const body = text(sheet.querySelector('.sheet-body'));
  assert.match(body, /выбрано/);
  assert.match(body, /Лучший прогон: 8 МиБ на 4 потоках/);
  assert.ok(sheet.querySelector('tr.best'), 'лучший прогон не помечен');
});

test('без прошлого прогона таблица честно пуста', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#transfer', 'bench');
  assert.match(text(sheet.querySelector('.sheet-body')), /Прогонов ещё не было/);
});

/* ---------- Что изменится у игрока ---------- */

/** Манифест сборки, как его отдаёт раздача. */
const manifest = (files) => ({
  ok: true,
  status: 200,
  text: async () => JSON.stringify({ version: '1.0', files }),
});

test('разница считается из настоящих манифестов, а не из снимка', async (t) => {
  const { window } = await boot({
    __raw: (url) => {
      if (url.includes('1.6.24')) {
        return manifest([
          { path: 'ChillHub.exe', size: 100, blake3: 'старый' },
          { path: 'Old.dll', size: 50, blake3: 'x' },
        ]);
      }
      return manifest([
        { path: 'ChillHub.exe', size: 100, blake3: 'новый' },
        { path: 'New.dll', size: 70, blake3: 'n' },
      ]);
    },
  });
  t.after(() => window.close());

  window.location.hash = '#launcher';
  const counts = await until(() => {
    const el = window.document.querySelector('[data-diff-counts]');
    return el && el.textContent.trim() ? el : null;
  });
  assert.ok(counts, 'счётчики разницы не появились');

  const shown = text(counts);
  assert.match(shown, /\+1/, 'не посчитано добавленное');
  assert.match(shown, /~1/, 'не посчитано изменённое');
  assert.match(shown, /−1/, 'не посчитано пропавшее');

  const tree = text(window.document.querySelector('[data-diff]'));
  assert.match(tree, /ChillHub\.exe/);
  assert.match(tree, /New\.dll/);
  assert.match(tree, /Old\.dll/);
});

test('подчищенный старый манифест не выдаётся за «всё совпало»', async (t) => {
  // Иначе решение об активации принимают вслепую, думая, что видят всё
  const { window } = await boot({
    __raw: (url) => (url.includes('1.6.24') ? { ok: false, status: 404 } : manifest([])),
  });
  t.after(() => window.close());

  window.location.hash = '#launcher';
  const box = await until(() => {
    const el = window.document.querySelector('[data-diff]');
    return el && /Сравнить|совпад/.test(el.textContent) ? el : null;
  });
  assert.ok(box, 'ничего не сказано про разницу');
  assert.match(text(box), /Сравнить не с чем/);
  assert.match(text(box), /старые подчищаются/);
});

/* ---------- Технические работы ---------- */

test('работы уходят на сервер с причиной, окном и блоками', async (t) => {
  // Кнопка без них отдала бы игрокам заглушку без единого слова
  const { window, calls } = await boot();
  t.after(() => window.close());

  window.location.hash = '#maint';
  await until(() => window.document.querySelector('[name="reason"]'));

  const set = (name, value) => {
    const el = window.document.querySelector(`[data-maint] [name="${name}"]`);
    el.value = value;
  };
  set('reason', 'Переносим сборки, вернёмся к 21:00');
  set('endsAt', '2030-01-01T21:00');
  window.document.querySelector('[data-maint] [name="launch"]').checked = false;

  window.document.querySelector('[data-act="maint.on"]').click();
  const modal = await until(() => window.document.querySelector('.modal'));
  modal.querySelector('[data-yes]').click();

  await until(() => calls.some((c) => c.url.includes('maintenance/set')));
  const sent = calls.find((c) => c.url.includes('maintenance/set'));

  // Ручка разбирает именно JSON: форма для неё — «invalid json body»
  assert.match(sent.type || '', /json/);
  assert.strictEqual(sent.body.enabled, true);
  assert.match(sent.body.reason, /вернёмся к 21:00/);
  assert.strictEqual(sent.body.blocks.launch, false);
  assert.match(sent.body.endsAt, /^\d{4}-\d{2}-\d{2}T/);
  await settle();
});

test('работы, которые ничего не закрывают, на сервер не уходят', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  window.location.hash = '#maint';
  await until(() => window.document.querySelector('[name="reason"]'));
  for (const n of ['install', 'update', 'launch']) {
    window.document.querySelector(`[data-maint] [name="${n}"]`).checked = false;
  }
  window.document.querySelector('[data-act="maint.on"]').click();

  await new Promise((r) => setTimeout(r, 20));
  assert.ok(!calls.some((c) => c.url.includes('maintenance/set')), 'пустые работы ушли на сервер');
  assert.match(text(window.document.querySelector('.toast')), /ничего и не делают/);
});

test('окно с концом раньше начала не доходит до сервера', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  window.location.hash = '#maint';
  await until(() => window.document.querySelector('[name="reason"]'));
  window.document.querySelector('[data-maint] [name="startsAt"]').value = '2030-01-01T20:00';
  window.document.querySelector('[data-maint] [name="endsAt"]').value = '2030-01-01T10:00';
  window.document.querySelector('[data-act="maint.on"]').click();

  await new Promise((r) => setTimeout(r, 20));
  assert.ok(!calls.some((c) => c.url.includes('maintenance/set')));
  assert.match(text(window.document.querySelector('.toast')), /позже начала/);
});

/* ---------- Карточка игры ---------- */

test('игру можно править: поля открываются с тем, что в реестре', async (t) => {
  // В панели 2.0 реестр какое-то время был таблицей только на чтение
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#games';
  const btn = await until(() => window.document.querySelector('[data-act="edit-game"]'));
  btn.click();
  const sheet = await until(() => window.document.querySelector('.sheet'));

  assert.strictEqual(sheet.querySelector('[name="gameId"]').value, 'repo');
  assert.strictEqual(sheet.querySelector('[name="title"]').value, 'R.E.P.O.');
  assert.strictEqual(sheet.querySelector('[name="exeRelativePath"]').value, 'REPO.exe');
  // Идентификатор уже стал именем папки — править его нельзя
  assert.ok(sheet.querySelector('[name="gameId"]').hasAttribute('readonly'));
});

test('правка уезжает всем реестром, не теряя чужих полей', async (t) => {
  // Сервер принимает список целиком, а в строках есть поля, которых
  // таблица не показывает
  const { window, calls } = await boot({
    games: {
      items: [
        { gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe', order: 0, mods: { enabled: true }, secretField: 'не трогать' },
        { gameId: 'peak', title: 'PEAK', exeRelativePath: 'PEAK.exe', order: 1 },
      ],
    },
  });
  t.after(() => window.close());

  window.location.hash = '#games';
  (await until(() => window.document.querySelector('[data-act="edit-game"]'))).click();
  const sheet = await until(() => window.document.querySelector('.sheet'));

  sheet.querySelector('[name="title"]').value = 'R.E.P.O. (новое)';
  sheet.querySelector('[data-flow="save"]').click();
  await until(() => calls.some((c) => c.url.includes('games/save')));

  const saved = calls.find((c) => c.url.includes('games/save'));
  const rows = saved.body.items;
  assert.strictEqual(rows.length, 2, 'вторая игра пропала из реестра');
  assert.strictEqual(rows[0].title, 'R.E.P.O. (новое)');
  assert.strictEqual(rows[0].secretField, 'не трогать', 'стёрлось поле, которого не видно в таблице');
  await settle();
});

test('игра без исполняемого файла на сервер не уходит', async (t) => {
  // Запускать её было бы нечем, а ошибка вылезла бы у игрока
  const { window, calls } = await boot();
  t.after(() => window.close());

  window.location.hash = '#games';
  (await until(() => window.document.querySelector('[data-act="edit-game"]'))).click();
  const sheet = await until(() => window.document.querySelector('.sheet'));

  sheet.querySelector('[name="exeRelativePath"]').value = '';
  sheet.querySelector('[data-flow="save"]').click();

  await new Promise((r) => setTimeout(r, 20));
  assert.ok(!calls.some((c) => c.url.includes('games/save')), 'игра без exe ушла на сервер');
  assert.match(text(window.document.querySelector('.toast')), /исполняемый файл/);
});

test('новая игра проверяется по тем же правилам, но с открытым именем', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'new-game');
  assert.ok(!sheet.querySelector('[name="gameId"]').hasAttribute('readonly'));

  sheet.querySelector('[name="gameId"]').value = 'ПлохойID';
  sheet.querySelector('[name="title"]').value = 'Игра';
  sheet.querySelector('[name="exeRelativePath"]').value = 'game.exe';
  sheet.querySelector('[data-flow="save"]').click();

  await new Promise((r) => setTimeout(r, 20));
  assert.ok(!calls.some((c) => c.url.includes('games/save')));
  assert.match(text(window.document.querySelector('.toast')), /латиницу в нижнем регистре/);
});

/* ---------- Откуда данные ---------- */

test('раздел с упавшей ручкой честно говорит, что показывает снимок', async (t) => {
  // Всплывающего сообщения при запуске мало: оно живёт четыре секунды,
  // а раздел открывают через полчаса
  const { window } = await boot({
    'mods/list': () => ({ ok: false, status: 500, text: async () => 'сервер лёг' }),
  });
  t.after(() => window.close());

  window.location.hash = '#packs';
  // Дожидаемся именно этого раздела: обзор тоже честно помечен, и найти
  // его пометку вместо нужной ничего не докажет
  await until(() => window.document.querySelector('h1').textContent === 'Сборки модов');
  const note = await until(() => window.document.querySelector('[data-stale]'));

  assert.ok(note, 'раздел молчит про снимок');
  assert.match(text(note), /показан снимок/);
  assert.match(text(note), /сборки модов/);
  assert.match(text(note), /Записывать в этом состоянии нельзя/);
});

test('обзор помечается так же: он собран из тех же разделов', async (t) => {
  const { window } = await boot({
    'mods/list': () => ({ ok: false, status: 500, text: async () => 'сервер лёг' }),
  });
  t.after(() => window.close());

  window.location.hash = '#overview';
  const note = await until(() => window.document.querySelector('[data-stale]'));
  assert.ok(note, 'обзор молчит про снимок');
  assert.match(text(note), /сборки/);
});

test('живой раздел никакой пометки не показывает', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#games';
  await until(() => window.document.querySelector('h1'));
  for (let i = 0; i < 20; i++) await new Promise((r) => setTimeout(r, 0));
  assert.strictEqual(window.document.querySelector('[data-stale]'), null, 'пометка на живых данных');
});

test('пометка называет раздел словами навигации, а не ключом хранилища', async (t) => {
  // «metrics не ответил» человеку не говорит ничего
  const { window } = await boot({
    'metrics/summary': () => ({ ok: false, status: 500, text: async () => '' }),
  });
  t.after(() => window.close());

  window.location.hash = '#errors';
  const note = await until(() => window.document.querySelector('[data-stale]'));
  assert.ok(note);
  assert.match(text(note), /метрики/);
  assert.ok(!/metrics/.test(text(note)));
});

/* ---------- Скорость заливки ---------- */

test('скорость считается по байтам и не рисуется чаще четырёх раз в секунду', async (t) => {
  // Событий прогресса летят сотни в секунду — по одному на кусок каждого
  // потока. Перерисовка на каждое занимает кадр вместо загрузки
  const { window } = await boot();
  t.after(() => window.close());

  await open(window, '#launcher', 'upload');
  assert.strictEqual(typeof window.makeRateEstimator, 'function', 'оценщик скорости не подключён');
  assert.strictEqual(typeof window.makeUiThrottler, 'function', 'троттлер не подключён');

  // Оценщик тот же, что в 1.0: пока окно уже минимального, он молчит
  const rate = window.makeRateEstimator(4000, { minSpanMs: 1200 });
  rate.push(0, 0);
  rate.push(300, 30 * 1024 * 1024);
  assert.strictEqual(rate.rate(), 0, 'скорость показана по буферам, а не по каналу');

  rate.push(2000, 40 * 1024 * 1024);
  assert.ok(rate.rate() > 0, 'скорость не появилась и после набора окна');
});

/* ---------- Что и какой версией грузим ---------- */

test('загрузка спрашивает цель и версию до выбора файла', async (t) => {
  // Сервер без gameId и версии отвечает отказом. Узнавать это, выбрав
  // архив на полтора гигабайта, — потерять время дважды
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#launcher', 'upload');
  assert.ok(sheet.querySelector('[name="target"]'), 'не спрашивают, что грузим');
  assert.ok(sheet.querySelector('[name="version"]'), 'не спрашивают версию');

  // Номер предложен следующим по порядку от той, что у игроков
  assert.strictEqual(sheet.querySelector('[name="version"]').value, '1.6.25');
});

test('пока версия не годится, файл выбирать не предлагают', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#launcher', 'upload');
  const field = sheet.querySelector('[name="version"]');
  field.value = 'не версия';
  field.dispatchEvent(new window.Event('change', { bubbles: true }));

  await until(() => sheet.querySelector('[data-flow="pick"]').disabled);
  assert.ok(sheet.querySelector('[data-flow="pick"]').disabled, 'предложили выбрать файл под негодный номер');
  assert.match(text(sheet), /только латиница, цифры/);
});

test('загрузка доносит до сервера и игру, и версию', async (t) => {
  const { window, calls } = await boot({
    'upload/init': { uploadId: 'u1', chunkSize: 4, totalChunks: 1 },
    'upload/status': { received: [] },
  });
  t.after(() => window.close());

  const sheet = await open(window, '#launcher', 'upload');
  const target = sheet.querySelector('[name="target"]');
  target.value = 'repo';
  target.dispatchEvent(new window.Event('change', { bubbles: true }));

  await window.CH2Upload.run(
    { name: 'a.zip', size: 4 },
    { kind: 'game', gameId: 'repo', version: '1.0.1', chunkSize: 4 },
    {
      api: window.CH2Api.makeApi(),
      chunks: {
        uploadChunkWithRetries: async () => ({ ok: true }),
        runWorkerPool: window.runWorkerPool,
        pendingBytes: window.pendingBytes,
      },
      slice: () => 'кусок',
      concurrency: () => 1,
    }
  );

  const init = calls.find((c) => c.url.includes('upload/init'));
  assert.strictEqual(init.body.gameId, 'repo');
  assert.strictEqual(init.body.version, '1.0.1');
  await settle();
});

test('смена цели предлагает свой номер: версии игры и лаунчера не связаны', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#launcher', 'upload');
  const target = sheet.querySelector('[name="target"]');
  target.value = 'repo';
  target.dispatchEvent(new window.Event('change', { bubbles: true }));

  await until(() => sheet.querySelector('[name="version"]').value !== '1.6.25');
  assert.strictEqual(sheet.querySelector('[name="version"]').value, '1.0.1');
});

/* ---------- Опоздавшие ответы ---------- */

test('опоздавший ответ по прошлой папке не перерисовывает галерею', async (t) => {
  // Ответы возвращаются не в том порядке, в каком уходили, и опоздавший
  // затирал бы свежий: на экране прошлая папка, а путь говорит про новую
  let hold;
  const gate = new Promise((r) => (hold = r));
  let call = 0;

  const { window } = await boot({
    'games/gallery': async ({ url }) => {
      call++;
      const nested = url.includes('path=screens');
      if (!nested) await gate; // первый запрос отвечает последним
      return {
        ok: true,
        status: 200,
        text: async () =>
          JSON.stringify({ cover: '', items: [{ name: nested ? 'ИЗ ПАПКИ.png' : 'ИЗ КОРНЯ.png', size: 1 }] }),
      };
    },
  });
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'gallery');
  await until(() => call >= 1);

  // Уходим в подпапку, пока корень ещё отвечает
  sheet.querySelector('[data-sheet-body]').innerHTML =
    '<button type="button" data-go="screens">screens</button>';
  sheet.querySelector('[data-go]').click();
  await until(() => call >= 2);
  await until(() => text(sheet).includes('ИЗ ПАПКИ.png'));

  hold();
  await settle(40);

  assert.ok(text(sheet).includes('ИЗ ПАПКИ.png'), 'опоздавший ответ затёр свежий');
  assert.ok(!text(sheet).includes('ИЗ КОРНЯ.png'), 'на экране содержимое прошлой папки');
});

/* ---------- Пересборка версии ---------- */

test('пересборка активной версии предупреждает, что заменит её у игроков', async (t) => {
  // Сборка ляжет под тем же номером: у половины окажется старый набор,
  // у половины новый — и различить их будет нечем
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#packs';
  await until(() => window.document.querySelector('[data-act="versions"]'));
  window.document.querySelector('[data-act="versions"]').click();

  const sheet = await until(() => window.document.querySelector('.sheet'));
  await until(() => sheet.querySelector('[data-act="rebuild"]'));

  const active = [...sheet.querySelectorAll('[data-act="rebuild"]')].find(
    (b) => JSON.parse(b.dataset.args).active
  );
  assert.ok(active, 'у активной версии нет кнопки пересборки');
  active.click();

  const modal = await until(() => window.document.querySelector('.modal'));
  assert.ok(modal, 'пересборка активной не спросила');
  assert.match(text(modal), /заменит то, что игроки уже качают/);
  modal.querySelector('[data-no]').click();
  await settle();
});

test('игра без модпака не исчезает молча, а объясняет, чего ей не хватает', async (t) => {
  // Подключить моды было негде: раздел показывал только те игры, у
  // которых они уже есть
  const { window } = await boot({
    games: {
      items: [
        { gameId: 'repo', title: 'R.E.P.O.', mods: { enabled: true } },
        { gameId: 'peak', title: 'PEAK' },
      ],
    },
  });
  t.after(() => window.close());

  window.location.hash = '#packs';
  await until(() => window.document.body.textContent.includes('Без модпака'));
  assert.match(window.document.body.textContent, /Без модпака: PEAK/);
  assert.ok(window.document.querySelector('[data-act="ecosystem"]'), 'подключить моды негде');
});

test('кнопка внутри листа работает так же, как кнопка раздела', async (t) => {
  // Обработчики висели на разметке текущего раздела, и всё, что
  // появлялось позже — а появляется в листах, — было мёртвым: кнопка
  // есть, нажимается, не делает ничего
  const { window, calls } = await boot();
  t.after(() => window.close());

  window.location.hash = '#packs';
  await until(() => window.document.querySelector('[data-act="versions"]'));
  window.document.querySelector('[data-act="versions"]').click();

  const sheet = await until(() => window.document.querySelector('.sheet'));
  const activate = await until(() => sheet.querySelector('[data-act="mods.activate"]'));
  activate.click();

  const modal = await until(() => window.document.querySelector('.modal'));
  assert.ok(modal, 'кнопка листа ничего не сделала');
  modal.querySelector('[data-yes]').click();

  await until(() => calls.some((c) => c.url.includes('mods/activate')));
  assert.ok(calls.some((c) => c.url.includes('mods/activate')), 'действие из листа не дошло до сервера');
  await settle();
});

test('одно действие названо на экране одним словом', async (t) => {
  // Три кнопки сборки с тремя подписями — это три названия одного и
  // того же, и читатель ищет между ними разницу
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#packs';
  await until(() => window.document.querySelector('[data-act="build"]'));
  for (let i = 0; i < 20; i++) await new Promise((r) => setTimeout(r, 0));

  const labels = [...window.document.querySelectorAll('[data-act="build"]')].map((b) =>
    b.textContent.trim().split(' ')[0]
  );
  assert.ok(labels.length > 0, 'кнопки сборки нет вовсе');
  assert.deepStrictEqual([...new Set(labels)], ['Собрать'], 'подписи разошлись: ' + labels.join(', '));
});

test('акцент на экране один: подсвечено то, что делают сейчас', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#packs';
  await until(() => window.document.querySelector('[data-act="build"]'));
  for (let i = 0; i < 20; i++) await new Promise((r) => setTimeout(r, 0));

  const accented = [...window.document.querySelectorAll('[data-act="build"].btn--accent')];
  assert.ok(accented.length <= 1, 'акцентных кнопок сборки больше одной: ' + accented.length);
});
