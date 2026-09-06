// Панель 2.0 целиком, в настоящем DOM.
//
// Приём тот же, что в admin-dom.test.js для версии 1.0: реальный
// index.html грузится в jsdom, и все его <script> исполняются в том же
// порядке, что в браузере. Только так проверяется то, ради чего панель и
// переделывалась, — что кнопка доводит дело до сервера, спрашивает перед
// необратимым и не врёт об успехе.
//
// Сеть подменяется целиком: ни один запрос наружу не уходит, а каждый
// записывается, и тест сверяет метод, адрес и тело.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui');

/** Ответы, которыми притворяется сервер. Форма — как у настоящего API. */
function serverFixtures() {
  return {
    'summary': { launcher: { pending: true, newest: '1.6.25', active: '1.6.24' }, mods: [] },
    list: {
      /* Форма серверная: версии от старых к новым, активная — отдельным
         полем `latest`. Признака `state` в строке нет вовсе. */
      items: [
        { version: '1.6.24', createdAt: '2026-08-31T22:41:00Z', files: 476, bytes: 121100000 },
        { version: '1.6.25', createdAt: '2026-09-04T03:12:00Z', files: 478, bytes: 121400000 },
      ],
      latest: '1.6.24',
    },
    /* У игры включены моды: без этого панель про сборки не спрашивает
       вовсе — `mods/list` у игры без модпака отвечает 400. Форма поля
       взята из реестра (adminapi/mods/profile.go). */
    'games': { items: [{ gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe', mods: { enabled: true, steamAppId: '3241660' } }] },
    'mods/list': {
      items: [{ gameId: 'repo', title: 'R.E.P.O.', built: '1.9.9', active: '1.9.8', mods: 17, size: 251000000 }],
    },
    'news/list': { items: [{ id: 'n1', title: 'Заметка', published: false }] },
    'feedback/list': { items: [{ id: 'f1', type: 'bug', status: 'new', comment: 'обрывается' }] },
    'maintenance/get': {
      state: { enabled: false, blocks: { install: false, update: false, launch: false } },
      effective: { enabled: false, blocks: {}, serverTime: '2026-09-06T12:00:00Z' },
    },
    'metrics/summary': {
      from: '2026-08-07T00:00:00Z',
      to: '2026-09-06T00:00:00Z',
      totals: { launcherStarts: 10, updates: 4, updateOk: 3, updateFail: 1, errors: 1 },
      byDay: [{ date: '2026-09-04', launcherStarts: 10, updates: 4, errors: 1 }],
      topErrors: [{ key: 'download_reset', count: 3 }],
    },

    'system/free': { bytes: 214000000000, total: 480000000000 },
    'mods/cache': { files: 412, bytes: 8900000000 },
  };
}

/** Разбирает тело запроса, каким бы оно ни было: форма, JSON или файл. */
function readBody(init) {
  const raw = init && init.body;
  if (!raw) return null;
  if (typeof raw !== 'string') return raw;
  try {
    return JSON.parse(raw);
  } catch {
    return Object.fromEntries(new URLSearchParams(raw));
  }
}

/** Поднимает панель в jsdom и отдаёт окно вместе с журналом запросов. */
async function boot(overrides) {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/' });
  const { window } = dom;

  const calls = [];
  const fixtures = Object.assign(serverFixtures(), overrides || {});

  window.fetch = async (url, init) => {
    const u = String(url);
    const method = (init && init.method) || 'GET';
    // Запись уезжает формой, чтение — без тела; JSON остался у двух ручек
    const body = readBody(init);
    calls.push({ method, url: u, body });

    const key = u.replace('/admin/api/', '').split('?')[0];
    if (Object.prototype.hasOwnProperty.call(fixtures, key)) {
      const v = fixtures[key];
      if (v && v.__fail) {
        return { ok: false, status: v.__fail, text: async () => JSON.stringify({ error: v.error || 'сбой' }) };
      }
      return { ok: true, status: 200, text: async () => JSON.stringify(v) };
    }
    return { ok: true, status: 200, text: async () => '{}' };
  };

  // Скрипты страницы — в том же порядке, что в браузере
  const scripts = [...window.document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src'));
  for (const src of scripts) {
    // Страница лежит по /admin/, а её модули — по /admin/ui/, поэтому
    // адреса в разметке абсолютные. Стенд повторяет то же отображение,
    // что делает nginx: /admin/ui/<файл> → server/admin_ui/<файл>.
    const file = path.join(V2, src.replace('/admin/ui/', ''));
    const code = fs.readFileSync(file, 'utf8');
    vm.runInContext(code, dom.getInternalVMContext(), { filename: file });
  }

  // Дать загрузке разделов дойти до конца
  for (let i = 0; i < 40; i++) await new Promise((r) => setTimeout(r, 0));
  return { window, calls, dom };
}

/** Ждёт, пока в журнале появится запрос, подходящий под условие. */
async function until(fn, tries = 60) {
  for (let i = 0; i < tries; i++) {
    if (fn()) return true;
    await new Promise((r) => setTimeout(r, 0));
  }
  return false;
}

test('панель поднимается и читает все разделы с сервера', async () => {
  const { window, calls } = await boot();

  const gets = calls.filter((c) => c.method === 'GET').map((c) => c.url.replace('/admin/api/', ''));
  const asked = gets.map((u) => u.split('?')[0]);
  for (const path of ['list', 'games', 'mods/list', 'news/list', 'feedback/list', 'maintenance/get', 'system/free']) {
    assert.ok(asked.includes(path), 'не запрошен раздел ' + path);
  }
  // Ни одного запроса мимо своего префикса
  assert.ok(calls.every((c) => c.url.startsWith('/admin/api/')), 'запрос ушёл мимо админ-API');
  assert.ok(window.document.querySelector('h1'), 'заголовок раздела не отрисован');
});

test('каждый запрос несёт то, без чего сервер отвечает отказом', async () => {
  // РАНЬШЕ ЭТА ПРОВЕРКА СМОТРЕЛА НА АДРЕС ЦЕЛИКОМ И СЧИТАЛА «list»
  // запрошенным разделом. Ровно так она и пропустила боевую поломку:
  // панель слала `list`, `mods/list` и `news/list` без обязательных
  // параметров, сервер отвечал 400, а тест был зелёным, потому что
  // строка совпадала.
  //
  // Требования взяты у обработчиков: builds.ListVersions и mods.List
  // проверяют gameId, news.List — scope, metrics.ErrorEvents — code
  // (это разбор одного кода ошибки, а не список: сводка ошибок приходит
  // в metrics/summary, и запрашивать errors на старте незачем).
  const { calls } = await boot();
  const need = { list: 'gameId', 'mods/list': 'gameId', 'news/list': 'scope', 'metrics/errors': 'code' };

  for (const c of calls.filter((x) => x.method === 'GET')) {
    const [path, query] = c.url.replace('/admin/api/', '').split('?');
    const param = need[path];
    if (!param) continue;
    const has = new URLSearchParams(query || '').get(param);
    assert.ok(has, `${path} уходит без ${param} — сервер ответит 400`);
  }
});

test('снимок не ходит в сеть сам', async () => {
  // Снимок нужен на случай «раздел не загрузился». Ходил он при этом в те
  // же эндпоинты вторым слоем — девять лишних запросов на запуск, чей
  // результат не читал никто, и четыре ошибки в консоли из них
  const { window, calls } = await boot();
  const before = calls.length;

  await window.CHILLHUB_DATA.load();
  assert.strictEqual(calls.length, before, 'снимок сходил в сеть');
});

test('первый экран показывает решение, а не сводку цифр', async () => {
  const { window } = await boot();
  window.location.hash = '#overview';
  const text = window.document.body.textContent;
  assert.match(text, /Что решить/);
  // Загруженная версия новее активной обязана попасть в решения
  assert.match(text, /1\.6\.25/);
});

test('необратимое действие сначала спрашивает и называет объект', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));

  const btn = window.document.querySelector('[data-act="launcher.activate"]');
  assert.ok(btn, 'кнопки активации нет');
  btn.click();

  const shown = await until(() => window.document.querySelector('.modal'));
  assert.ok(shown, 'вопрос не показан');

  const modal = window.document.querySelector('.modal');
  assert.match(modal.textContent, /1\.6\.25/, 'вопрос не называет версию');
  assert.match(modal.textContent, /Отдать игрокам/);

  // Пока не ответили — на сервер ничего не ушло
  assert.strictEqual(calls.filter((c) => c.url.includes('activate')).length, 0);
});

test('отказ в диалоге не отправляет запрос', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));
  window.document.querySelector('[data-act="launcher.activate"]').click();
  await until(() => window.document.querySelector('.modal'));

  window.document.querySelector('.modal [data-no]').click();
  await until(() => !window.document.querySelector('.modal'));

  assert.strictEqual(calls.filter((c) => c.url.split('?')[0].includes('/activate')).length, 0);
  assert.ok(!window.document.querySelector('.modal'), 'окно не закрылось');
});

test('согласие доводит действие до сервера и перечитывает раздел', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));
  window.document.querySelector('[data-act="launcher.activate"]').click();
  await until(() => window.document.querySelector('.modal'));
  window.document.querySelector('.modal [data-yes]').click();

  // Параметры теперь висят на адресе — сервер читает их именно оттуда
  const sent = await until(() => calls.some((c) => c.method === 'POST' && c.url.split('?')[0].endsWith('/activate')));
  assert.ok(sent, 'запрос активации не ушёл');

  const at = (c) => c.url.split('?')[0];
  const req = calls.find((c) => at(c).endsWith('/activate'));
  // И в адресе, и телом формы — сервер читает то одним способом, то другим
  assert.match(req.url, /gameId=launcher&version=1\.6\.25/);
  assert.deepStrictEqual(req.body, { gameId: 'launcher', version: '1.6.25' });

  // После записи раздел обязан перечитаться, иначе экран останется врать
  const before = calls.filter((c) => at(c).endsWith('/list')).length;
  const reread = await until(() => calls.filter((c) => at(c).endsWith('/list')).length > before - 1);
  assert.ok(reread);
});

test('окно закрывается по Escape и это считается отказом', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));
  window.document.querySelector('[data-act="launcher.activate"]').click();
  await until(() => window.document.querySelector('.modal'));

  window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await until(() => !window.document.querySelector('.modal'));

  assert.ok(!window.document.querySelector('.modal'));
  assert.strictEqual(calls.filter((c) => c.url.endsWith('/activate')).length, 0);
});

test('фокус в окне стоит на отказе, а не на опасной кнопке', async () => {
  const { window } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));
  window.document.querySelector('[data-act="launcher.activate"]').click();
  await until(() => window.document.querySelector('.modal'));

  const focused = window.document.activeElement;
  assert.ok(focused && focused.hasAttribute('data-no'), 'пробел не должен запускать необратимое');
});

test('обратимое действие уходит без вопроса', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#games';
  await until(() => window.document.querySelector('[data-act="games.scan"]'));
  window.document.querySelector('[data-act="games.scan"]').click();

  const sent = await until(() => calls.some((c) => c.url.endsWith('games/scan')));
  assert.ok(sent, 'сканирование не ушло на сервер');
  assert.ok(!window.document.querySelector('.modal'), 'обратимое действие спрашивать не должно');
});

test('отказ сервера показывается текстом, а не молчанием', async () => {
  const { window } = await boot({ 'games/scan': { __fail: 409, error: 'каталог занят' } });
  window.location.hash = '#games';
  await until(() => window.document.querySelector('[data-act="games.scan"]'));
  window.document.querySelector('[data-act="games.scan"]').click();

  const shown = await until(() => /каталог занят/.test(window.document.body.textContent));
  assert.ok(shown, 'причина отказа не показана человеку');
});

test('молчащий раздел не оставляет панель пустой и говорит об этом', async () => {
  const { window } = await boot({ 'feedback/list': { __fail: 500 } });
  const text = window.document.body.textContent;
  // Остальные разделы обязаны прочитаться
  assert.match(text, /Chill Hub/);
  assert.match(text, /Не ответили разделы|inbox/);
});

test('все разделы открываются и рисуют заголовок', async () => {
  const { window } = await boot();
  const ids = ['overview', 'launcher', 'packs', 'games', 'news', 'inbox', 'maint', 'errors', 'transfer'];
  for (const id of ids) {
    window.location.hash = '#' + id;
    await until(() => window.document.querySelector('h1'));
    const h1 = window.document.querySelector('h1');
    assert.ok(h1 && h1.textContent.trim().length > 2, 'раздел ' + id + ' без заголовка');
  }
});

test('у каждой кнопки панели есть обработчик', async () => {
  // Панель 2.0 какое-то время жила с кнопками, отвечавшими «ещё не
  // подключено». Честно, но бесполезно: проверка держит, чтобы такие не
  // заводились снова незаметно
  const { window } = await boot();
  const seen = new Set();

  // Разделы берём из самой навигации, чтобы новый раздел попадал под
  // проверку сам, без правки теста
  const links = [...window.document.querySelectorAll('[data-nav]')].map((a) => a.getAttribute('href'));
  assert.ok(links.length >= 5, 'разделов нашлось подозрительно мало: ' + links.length);

  for (const href of links) {
    window.location.hash = href;
    await until(() => window.document.title.length > 0);
    for (let i = 0; i < 10; i++) await new Promise((r) => setTimeout(r, 0));
    for (const b of window.document.querySelectorAll('[data-act]')) seen.add(b.dataset.act);
  }

  const orphan = [...seen].filter((id) => !window.CH2Actions.has(id) && !window.CH2Flows.has(id));
  assert.deepStrictEqual(orphan, [], 'кнопки без обработчика: ' + orphan.join(', '));
  assert.ok(seen.size > 15, 'кнопок нашлось подозрительно мало: ' + seen.size);
});

/* ---------- Одно действие — одно имя ---------- */

test('одно действие подписано одинаково во всех разделах', async () => {
  // Разные подписи у одной кнопки читаются как разные дела, и человек
  // ищет между ними разницу, которой нет
  const { window } = await boot();
  const labels = new Map();

  for (const href of [...window.document.querySelectorAll('[data-nav]')].map((a) => a.getAttribute('href'))) {
    window.location.hash = href;
    await until(() => window.document.title.length > 0);
    for (let i = 0; i < 10; i++) await new Promise((r) => setTimeout(r, 0));

    for (const b of window.document.querySelectorAll('[data-act]')) {
      const first = b.textContent.trim().split(/\s+/).slice(0, 2).join(' ');
      if (!first) continue;
      if (!labels.has(b.dataset.act)) labels.set(b.dataset.act, new Set());
      labels.get(b.dataset.act).add(first);
    }
  }

  const mixed = [...labels.entries()].filter(([, set]) => set.size > 1);
  assert.deepStrictEqual(
    mixed.map(([id, set]) => id + ': ' + [...set].join(' / ')),
    [],
    'у действия несколько подписей'
  );
});

/* СЕРВЕР ОТВЕЧАЕТ RFC3339 В UTC, А ЧЕЛОВЕК ЧИТАЕТ ЧАСЫ.
   Три колонки — «собрана» у версии лаунчера, «собрано» у модпака и
   «когда» у обращения — выводили строку сервера как есть:
   «2026-09-04T03:12:00Z». Прочитать её трудно, а «Z» ещё и врёт рядом с
   остальными экранами, где время местное. В снимке из data.js даты
   лежали готовыми строками, поэтому проверки этого не видели. */
test('время в таблицах показано часами, а не строкой сервера', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  window.location.hash = '#launcher';
  await until(() => /Версии на сервере/.test(window.document.body.textContent));
  const html = window.document.body.textContent;
  assert.ok(!/\d{4}-\d{2}-\d{2}T/.test(html), 'в таблицу попала строка RFC3339: ' + html.slice(0, 300));
  assert.match(html, /\d{2}\.\d{2}\.2026 \d{2}:\d{2}/, 'даты сборки не видно вовсе');
});
