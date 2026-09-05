// Транспорт панели 2.0: CSRF, переписывание адресов, продление сессии.
//
// ПОЧЕМУ ЭТО ПРОВЕРЯЕТСЯ ОТДЕЛЬНО И ПОЧЕМУ ВООБЩЕ. `transport.js`
// перенесён из панели 1.0 без изменений, но перенесён — не значит
// проверен: у 2.0 своя разметка, свой порядок скриптов и своя обёртка
// над fetch, и любая из них может незаметно обойти защиту. В 1.0 эти
// случаи были покрыты (`admin-csrf.test.js`), у 2.0 не было ни одного.
//
// ЧТО ИМЕННО ОХРАНЯЕТСЯ. CSRF-токен — это секрет сессии админки.
// Уехавший на чужой хост, он позволяет тому хосту писать в админку от
// имени владельца. Поэтому правил три и все три проверяются: токен
// уходит ТОЛЬКО с небезопасными методами, ТОЛЬКО на свой origin, и
// ТОЛЬКО раскодированным — кука лежит в процентной кодировке, и
// отправленный как есть токен просто не совпадёт с выданным.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui');
const PAGE_URL = 'http://localhost/admin/ui/';
const FOREIGN = 'https://example.invalid/collect';

/* Кука хранится в процентной кодировке, а на сервер обязано уехать
   раскодированное значение — иначе токен не совпадёт с выданным. */
const COOKIE_RAW = 't0k%2Fen';
const TOKEN = 't0k/en';

/** Поднимает ТОЛЬКО транспорт: остальная панель здесь ни при чём. */
function boot(opts) {
  const o = opts || {};
  const dom = new JSDOM('<!doctype html><html><body></body></html>', {
    runScripts: 'outside-only',
    url: o.url || PAGE_URL,
  });
  const { window } = dom;

  window.document.cookie = 'csrf_token=' + COOKIE_RAW;

  const calls = [];
  window.fetch = async (input, init) => {
    const url = typeof input === 'string' ? input : input.url;
    const headers = (init && init.headers) || {};
    const get = (n) => (typeof headers.get === 'function' ? headers.get(n) : headers[n]);
    calls.push({ url, method: (init && init.method) || 'GET', csrf: get('X-CSRF-Token') || null });
    const status = o.status && calls.length === 1 ? o.status : 200;
    return { ok: status < 400, status, text: async () => '{}' };
  };

  /* Подставной XHR запоминает заголовки: настоящий jsdom их проглатывает,
     и глушить надо не то место, за которым смотрим. */
  const xhrCalls = [];
  class FakeXHR {
    open(method, url) {
      this._method = method;
      this._url = url;
      this._headers = {};
    }
    setRequestHeader(n, v) {
      this._headers[n] = v;
    }
    send() {
      xhrCalls.push({ url: this._url, method: this._method, csrf: this._headers['X-CSRF-Token'] || null });
    }
  }
  window.XMLHttpRequest = FakeXHR;

  vm.runInContext(fs.readFileSync(path.join(V2, 'transport.js'), 'utf8'), dom.getInternalVMContext(), {
    filename: 'transport.js',
  });

  return { window, calls, xhrCalls, dom };
}

/* ---------- Токен через fetch ---------- */

test('запись на свой origin несёт токен, и раскодированный', async (t) => {
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('/admin/api/activate?gameId=launcher', { method: 'POST' });
  assert.strictEqual(calls[0].csrf, TOKEN);
});

test('абсолютный адрес своего origin — тоже с токеном', async (t) => {
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('http://localhost/admin/api/activate', { method: 'POST' });
  assert.strictEqual(calls[0].csrf, TOKEN);
});

test('чтение идёт без токена', async (t) => {
  // Секрет сессии не нужен там, где ничего не меняется
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('/admin/api/games');
  assert.strictEqual(calls[0].csrf, null);
});

test('запись на чужой origin идёт без токена', async (t) => {
  // Иначе секрет сессии уедет к тому хосту в заголовке
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch(FOREIGN, { method: 'POST' });
  assert.strictEqual(calls[0].csrf, null);
});

test('метод из объекта Request тоже считается небезопасным', async (t) => {
  // Метод можно задать и в init, и в самом Request — учитываются оба
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch({ url: '/admin/api/activate', method: 'POST' });
  assert.strictEqual(calls[0].csrf, TOKEN);
});

/* ---------- Токен через XHR ---------- */

test('запись через XHR на свой origin несёт токен', async (t) => {
  // Чанковая загрузка идёт через XHR ради побайтового прогресса
  const { window, xhrCalls } = boot();
  t.after(() => window.close());

  const x = new window.XMLHttpRequest();
  x.open('PUT', '/admin/api/upload/chunk?uploadId=abc&index=0');
  x.send('кусок');
  assert.strictEqual(xhrCalls[0].csrf, TOKEN);
});

test('чтение через XHR идёт без токена', async (t) => {
  const { window, xhrCalls } = boot();
  t.after(() => window.close());

  const x = new window.XMLHttpRequest();
  x.open('GET', '/admin/api/games');
  x.send();
  assert.strictEqual(xhrCalls[0].csrf, null);
});

test('запись через XHR на чужой origin идёт без токена', async (t) => {
  const { window, xhrCalls } = boot();
  t.after(() => window.close());

  const x = new window.XMLHttpRequest();
  x.open('POST', FOREIGN);
  x.send('что угодно');
  assert.strictEqual(xhrCalls[0].csrf, null);
});

/* ---------- Адреса ---------- */

test('короткая форма адреса переписывается на админ-API', async (t) => {
  // У морды один префикс, и со статикой в nginx он не конфликтует
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('/admin/list?gameId=launcher');
  assert.strictEqual(calls[0].url, '/admin/api/list?gameId=launcher');
});

test('адрес самой морды не переписывается', async (t) => {
  // Иначе панель начала бы просить свои же скрипты у админ-API
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('/admin/ui/views.js');
  assert.strictEqual(calls[0].url, '/admin/ui/views.js');
});

test('уже полный адрес переписывается один раз, а не дважды', async (t) => {
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch('/admin/api/games');
  assert.strictEqual(calls[0].url, '/admin/api/games');
});

test('чужие адреса не трогаются вовсе', async (t) => {
  const { window, calls } = boot();
  t.after(() => window.close());

  await window.fetch(FOREIGN);
  assert.strictEqual(calls[0].url, FOREIGN);
});

/* ---------- Продление сессии ---------- */

test('на 401 сессия продлевается один раз, и запрос повторяется', async (t) => {
  // Сессия истекает посреди работы, и терять из-за этого набранное — то
  // же самое, что не иметь продления вовсе
  const { window, calls } = boot({ status: 401 });
  t.after(() => window.close());

  await window.fetch('/admin/api/games');

  assert.strictEqual(calls.length, 3, 'ожидались запрос, продление и повтор');
  assert.strictEqual(calls[1].url, '/admin/api/auth/refresh');
  assert.strictEqual(calls[1].method, 'POST');
  assert.strictEqual(calls[2].url, '/admin/api/games');
});

test('повтор идёт с тем же методом и теми же заголовками', async (t) => {
  const { window, calls } = boot({ status: 401 });
  t.after(() => window.close());

  await window.fetch('/admin/api/activate', { method: 'POST' });
  const retry = calls[calls.length - 1];
  assert.strictEqual(retry.method, 'POST');
  assert.strictEqual(retry.csrf, TOKEN, 'повтор ушёл без токена');
});
