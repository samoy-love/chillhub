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

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui', 'v2');

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
      { gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe', order: 0 },
      { gameId: 'peak', title: 'PEAK', exeRelativePath: 'PEAK.exe', order: 1 },
    ],
  },
  'mods/list': {
    items: [{ gameId: 'repo', title: 'R.E.P.O.', built: '1.9.9', active: '1.9.8', mods: 17, size: 251000000 }],
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
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/v2/' });
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
    } else if (raw) {
      body = raw;
    }
    calls.push({ method, url: u, body });

    const key = u.replace('/admin/api/', '').split('?')[0];
    const hit = table[key];
    if (typeof hit === 'function') return hit({ method, url: u, body });
    if (hit !== undefined) return { ok: true, status: 200, text: async () => JSON.stringify(hit) };
    return { ok: true, status: 200, text: async () => '{}' };
  };

  for (const src of [...window.document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src'))) {
    const file = path.resolve(V2, src);
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

  const sheet = await open(window, '#games', 'new-game');
  const save = sheet.querySelector('[data-flow="save"]');
  assert.ok(save.disabled, 'предложено сохранить неизменённый порядок');
  assert.match(text(sheet.querySelector('footer')), /сохранять нечего/);
});

test('перестановка называет последствие и уходит на сервер пересчитанной', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#games', 'new-game');
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

test('таблица прогонов помечает лучший и объясняет, почему он', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const sheet = await open(window, '#transfer', 'bench');
  const body = text(sheet.querySelector('.sheet-body'));
  assert.match(body, /выбрано/);
  assert.match(body, /Лучший прогон|Быстрее всех/);
  assert.ok(sheet.querySelector('tr.best'), 'лучший прогон не помечен');
});

/* ---------- Сессия ---------- */

test('анонима уводят на вход, а не показывают ему разделы', async (t) => {
  // Иначе 401 на первом же нажатии выглядит поломкой панели, а не отказом
  const { window, left } = await boot({
    'auth/me': () => ({ ok: false, status: 401, text: async () => '{}' }),
    'auth/refresh': () => ({ ok: false, status: 401, text: async () => '{}' }),
  });
  t.after(() => window.close());

  const gone = await until(() => left.length > 0);
  assert.ok(gone, 'аноним остался в панели');
  assert.deepStrictEqual(left, ['/admin/']);
});

test('истёкшую сессию обновляют молча, не показывая вход', async (t) => {
  let asked = 0;
  const { window, calls, left } = await boot({
    'auth/me': () => {
      asked++;
      return asked === 1
        ? { ok: false, status: 401, text: async () => '{}' }
        : { ok: true, status: 200, text: async () => JSON.stringify({ user: 'admin' }) };
    },
  });
  t.after(() => window.close());

  await until(() => window.document.querySelector('h1'));
  assert.ok(calls.some((c) => c.url.includes('auth/refresh')), 'сессию не пробовали обновить');
  assert.deepStrictEqual(left, [], 'увели на вход с живой сессией');
});

test('упавшая сеть не выкидывает из панели, а показывает снимок', async (t) => {
  const { window, left } = await boot({
    'auth/me': () => {
      throw new Error('сети нет');
    },
  });
  t.after(() => window.close());

  await until(() => window.document.querySelector('h1'));
  assert.deepStrictEqual(left, [], 'из-за упавшей сети увели на вход');
  assert.ok(window.document.querySelector('h1'), 'панель не показала ничего');
});

test('выход уводит на вход, даже если сервер на него не ответил', async (t) => {
  // Держать человека в панели, из которой он попросил выйти, хуже
  const { window, calls, left } = await boot({
    'auth/logout': () => {
      throw new Error('сети нет');
    },
  });
  t.after(() => window.close());

  await until(() => window.document.querySelector('[data-logout]'));
  window.document.querySelector('[data-logout]').click();

  const gone = await until(() => left.length > 0);
  assert.ok(gone, 'после выхода остались в панели');
  assert.ok(calls.some((c) => c.url.includes('auth/logout')), 'выход не дошёл до сервера');
});
