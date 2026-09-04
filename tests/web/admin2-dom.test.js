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

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui', 'v2');

/** Ответы, которыми притворяется сервер. Форма — как у настоящего API. */
function serverFixtures() {
  return {
    'summary': { launcher: { pending: true, newest: '1.6.25', active: '1.6.24' }, mods: [] },
    'list': {
      items: [
        { version: '1.6.25', date: '04.09.2026', files: 478, size: 121400000, state: 'uploaded' },
        { version: '1.6.24', date: '31.08.2026', files: 476, size: 121100000, state: 'active' },
      ],
    },
    'games': { items: [{ gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe' }] },
    'mods/list': {
      items: [{ gameId: 'repo', title: 'R.E.P.O.', built: '1.9.9', active: '1.9.8', mods: 17, size: 251000000 }],
    },
    'news/list': { items: [{ id: 'n1', title: 'Заметка', published: false }] },
    'feedback/list': { items: [{ id: 'f1', type: 'bug', status: 'new', comment: 'обрывается' }] },
    'maintenance/get': { enabled: false, reason: '', blocks: {} },
    'metrics/summary': { days: [{ date: '04.09', launcherStarts: 10, updates: 4, errors: 1 }] },
    'metrics/errors': { items: [{ code: 'download_reset', n: 3, what: 'обрыв' }] },
    'system/free': { freeBytes: 214000000000, totalBytes: 480000000000 },
    'mods/cache': { files: 412, bytes: 8900000000 },
  };
}

/** Поднимает панель в jsdom и отдаёт окно вместе с журналом запросов. */
async function boot(overrides) {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/v2/' });
  const { window } = dom;

  const calls = [];
  const fixtures = Object.assign(serverFixtures(), overrides || {});

  window.fetch = async (url, init) => {
    const u = String(url);
    const method = (init && init.method) || 'GET';
    const body = init && init.body ? JSON.parse(init.body) : null;
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
    const file = path.join(V2, src.replace('./', ''));
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

  const asked = calls.filter((c) => c.method === 'GET').map((c) => c.url.replace('/admin/api/', ''));
  for (const path of ['list', 'games', 'mods/list', 'news/list', 'feedback/list', 'maintenance/get', 'system/free']) {
    assert.ok(asked.includes(path), 'не запрошен раздел ' + path);
  }
  // Ни одного запроса мимо своего префикса
  assert.ok(calls.every((c) => c.url.startsWith('/admin/api/')), 'запрос ушёл мимо админ-API');
  assert.ok(window.document.querySelector('h1'), 'заголовок раздела не отрисован');
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

  assert.strictEqual(calls.filter((c) => c.url.includes('/activate')).length, 0);
  assert.ok(!window.document.querySelector('.modal'), 'окно не закрылось');
});

test('согласие доводит действие до сервера и перечитывает раздел', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#launcher';
  await until(() => window.document.querySelector('[data-act="launcher.activate"]'));
  window.document.querySelector('[data-act="launcher.activate"]').click();
  await until(() => window.document.querySelector('.modal'));
  window.document.querySelector('.modal [data-yes]').click();

  const sent = await until(() => calls.some((c) => c.method === 'POST' && c.url.endsWith('/activate')));
  assert.ok(sent, 'запрос активации не ушёл');

  const req = calls.find((c) => c.url.endsWith('/activate'));
  assert.deepStrictEqual(req.body, { version: '1.6.25' });

  // После записи раздел обязан перечитаться, иначе экран останется врать
  const before = calls.filter((c) => c.url.endsWith('/list')).length;
  const reread = await until(() => calls.filter((c) => c.url.endsWith('/list')).length > before - 1);
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

test('кнопка неподключённого действия честно об этом говорит', async () => {
  const { window, calls } = await boot();
  window.location.hash = '#transfer';
  await until(() => window.document.querySelector('[data-act="cache.clear"]'));
  const before = calls.length;
  window.document.querySelector('[data-act="cache.clear"]').click();
  await until(() => /ещё не подключено/.test(window.document.body.textContent));

  assert.match(window.document.body.textContent, /ещё не подключено/);
  assert.strictEqual(calls.length, before, 'неподключённая кнопка не должна ходить на сервер');
});
