// CSRF-шим admin.js в настоящем DOM: кому уходит X-CSRF-Token, а кому нет.
//
// ПОЧЕМУ ОТДЕЛЬНЫМ ФАЙЛОМ: шим в шапке admin.js — единственное место клиента,
// которое знает про этот заголовок, и ошибиться в нём можно в обе стороны.
// Потеряется проверка origin — токен сессии админки уедет в заголовке на
// чужой хост; разойдётся имя куки или условие по методу — записи в панели
// начнут получать 401 от серверного рубежа (server/internal/adminapi/auth),
// который про клиента ничего не знает и оттестирован отдельно.
// Комментарий над XMLHttpRequest.send в admin.js описывает первый случай как
// уже случившийся, но ни один тест его не удерживал: харнесс admin-dom.test.js
// подставляет XHR с пустым setRequestHeader, то есть глушит ровно то место, за
// которым надо смотреть. Здесь и стаб fetch, и подставной XHR заголовки
// запоминают.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { TextDecoder, TextEncoder } = require('node:util');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');
const HTML_PATH = path.join(ADMIN_DIR, 'admin.html');
const PAGE_URL = 'http://localhost/admin/';
const FOREIGN_URL = 'https://example.invalid/collect';

// Кука лежит в процентной кодировке, а getCsrf() обязан отдать раскодированное
// значение — иначе на сервер уедет не тот токен, что он выдал.
const CSRF_COOKIE_RAW = 't0k%2Fen';
const CSRF_VALUE = 't0k/en';

// Список скриптов страницы берём из самой разметки, а не переписываем руками:
// admin.js читает window.* от соседей, и порядок здесь обязан совпадать с
// браузерным. Вендорный бандл пропускаем — в сценариях он не участвует.
function pageScripts(html) {
  const out = [];
  const re = /<script src="\/admin\/ui\/([^"]+)"/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    if (!m[1].startsWith('vendor/')) out.push(m[1]);
  }
  return out;
}

// Заголовки шим складывает в Headers, а на безопасных запросах не создаёт
// вовсе — читаем оба вида и «нет заголовка» одинаково отдаём как null.
function headerOf(headers, name) {
  if (!headers) return null;
  if (typeof headers.get === 'function') return headers.get(name);
  for (const k of Object.keys(headers)) {
    if (k.toLowerCase() === name.toLowerCase()) return headers[k];
  }
  return null;
}

function makeFetchRecorder() {
  const calls = [];
  const fn = async (input, init) => {
    const url = typeof input === 'string' ? input : String(input && input.url);
    calls.push({
      url,
      method: String((init && init.method) || 'GET').toUpperCase(),
      csrf: headerOf(init && init.headers, 'X-CSRF-Token'),
    });
    return { ok: true, status: 200, json: async () => ({}), text: async () => '{}' };
  };
  fn.calls = calls;
  return fn;
}

// Подставной XHR: отвечает 200 сразу в send(), но, в отличие от харнесса
// admin-dom.test.js, запоминает всё, что шим успел выставить до отправки.
function makeRecordingXHRClass() {
  return class FakeXHR {
    constructor() {
      this.upload = {};
      this.readyState = 0;
      this.headers = {};
    }
    open(method, url) { this.method = method; this.url = url; }
    setRequestHeader(name, value) { this.headers[String(name).toLowerCase()] = value; }
    send(body) {
      this.body = body;
      this.status = 200;
      this.responseText = '{}';
      this.readyState = 4;
      if (this.onreadystatechange) this.onreadystatechange();
    }
  };
}

// admin.js на верхнем уровне заводит setInterval, поэтому окно закрывается
// через t.after() — иначе `node --test` не завершится после последнего теста.
function loadAdminPage(t) {
  const html = fs.readFileSync(HTML_PATH, 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: PAGE_URL });
  const { window } = dom;

  // TextDecoder/TextEncoder в window у jsdom нет, а confirm() он не
  // реализует — в браузере и то и другое есть, поэтому admin.js их не
  // проверяет.
  window.TextDecoder = TextDecoder;
  window.TextEncoder = TextEncoder;
  window.confirm = () => true;
  // fetch и XMLHttpRequest подменяем до скриптов: шим патчит именно то, что
  // лежит в window на момент его исполнения.
  const fetchRecorder = makeFetchRecorder();
  window.fetch = fetchRecorder;
  const XHR = makeRecordingXHRClass();
  window.XMLHttpRequest = XHR;

  // Куку ставим до скриптов: в браузере она приходит с ответом сервера и есть
  // на странице с первой же строки.
  window.document.cookie = `csrf_token=${CSRF_COOKIE_RAW}`;

  const ctx = dom.getInternalVMContext();
  for (const file of pageScripts(html)) {
    const abs = path.join(ADMIN_DIR, file);
    vm.runInContext(fs.readFileSync(abs, 'utf8'), ctx, { filename: abs });
  }

  // Перед закрытием даём догореть тому, что страница затеяла на старте: её
  // обработчики ответов лезут в document, а у закрытого окна его уже нет — и
  // node --test засчитывает такую ошибку следующему тесту.
  t.after(async () => {
    for (let i = 0; i < 20; i++) await new Promise((r) => setTimeout(r, 0));
    dom.window.close();
  });

  return { window, fetchCalls: fetchRecorder.calls };
}

// Загрузка страницы сама дёргает панель по тем же адресам, поэтому свой запрос
// ищем и по адресу, и по методу.
function lastCallTo(calls, url, method) {
  const found = calls.filter((c) => c.url === url && c.method === method);
  assert.ok(found.length > 0, `стаб fetch не увидел ${method} на ${url}`);
  return found[found.length - 1];
}

test('CSRF: POST на свой origin несёт X-CSRF-Token со значением куки', async (t) => {
  const { window, fetchCalls } = loadAdminPage(t);

  await window.fetch('/admin/api/games', { method: 'POST', body: '{}' });

  const call = lastCallTo(fetchCalls, '/admin/api/games', 'POST');
  assert.strictEqual(call.csrf, CSRF_VALUE);
});

test('CSRF: абсолютный адрес своего origin — тоже с токеном', async (t) => {
  const { window, fetchCalls } = loadAdminPage(t);

  await window.fetch(PAGE_URL + 'api/games', { method: 'DELETE' });

  assert.strictEqual(lastCallTo(fetchCalls, PAGE_URL + 'api/games', 'DELETE').csrf, CSRF_VALUE);
});

test('CSRF: GET идёт без токена', async (t) => {
  const { window, fetchCalls } = loadAdminPage(t);

  await window.fetch('/admin/api/games?probe=1');

  assert.strictEqual(lastCallTo(fetchCalls, '/admin/api/games?probe=1', 'GET').csrf, null);
});

test('CSRF: POST на чужой origin идёт без токена', async (t) => {
  const { window, fetchCalls } = loadAdminPage(t);

  await window.fetch(FOREIGN_URL, { method: 'POST', body: '{}' });

  const call = lastCallTo(fetchCalls, FOREIGN_URL, 'POST');
  assert.strictEqual(call.csrf, null);
});

test('CSRF: XHR-POST на свой origin несёт токен', async (t) => {
  const { window } = loadAdminPage(t);

  const xhr = new window.XMLHttpRequest();
  xhr.open('POST', '/admin/api/upload/chunk');
  xhr.send('chunk');

  assert.strictEqual(xhr.headers['x-csrf-token'], CSRF_VALUE);
});

test('CSRF: XHR-GET идёт без токена', async (t) => {
  const { window } = loadAdminPage(t);

  const xhr = new window.XMLHttpRequest();
  xhr.open('GET', '/admin/api/games');
  xhr.send();

  assert.strictEqual(xhr.headers['x-csrf-token'], undefined);
});

test('CSRF: XHR-POST на чужой origin идёт без токена', async (t) => {
  const { window } = loadAdminPage(t);

  const xhr = new window.XMLHttpRequest();
  xhr.open('POST', FOREIGN_URL);
  xhr.send('chunk');

  assert.strictEqual(xhr.readyState, 4, 'запрос должен был уйти, но без заголовка');
  assert.strictEqual(xhr.headers['x-csrf-token'], undefined);
});
