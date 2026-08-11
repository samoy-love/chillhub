// Тест server/admin_ui/admin.js в настоящем DOM (jsdom), а не через regex+new
// Function, как это делает admin-logic.test.js/admin-sanitize.test.js.
//
// ПОЧЕМУ ТАК: admin.js — не CommonJS-модуль (в отличие от upload-bench.js/
// chunk-upload.js/...), а monolith на 3000+ строк, который просто читает
// document.getElementById(...) и вешает addEventListener на top-level. c8
// умеет построчно атрибутировать покрытие только исполнению настоящего файла
// (vm.runInContext с filename — см. umd-browser-global.test.js), а не коду,
// вырезанному регэкспом и прогнанному через new Function — тот исполняется
// как анонимная функция без привязки к admin.js, и c8 показывает 0%.
// Здесь тот же приём, но полноценно: реальный admin.html грузится в jsdom, и
// все 8 <script> из него выполняются в её vm-контексте в том же порядке, что
// и в браузере — только так runChunkedUpload() (главный непокрытый кусок,
// пайплайн init/chunk/complete/process) можно прогнать целиком.
//
// Единственная внешняя зависимость репозитория — jsdom, добавленная
// специально ради этого теста (см. package.json и комментарий в
// .github/workflows/ci.yml про npm install перед этим job).

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const { TextDecoder, TextEncoder } = require('node:util');

const ADMIN_DIR = path.join(__dirname, '..', '..', 'server', 'admin_ui');
const HTML_PATH = path.join(ADMIN_DIR, 'admin.html');

// Порядок обязателен: он повторяет <script> в admin.html построчно (не
// module-система, каждый файл читает/пишет window напрямую).
const SCRIPT_ORDER = [
  'ui-throttle.js',
  'upload-bench.js',
  'speed-chart.js',
  'line-chart.js',
  'chunk-upload.js',
  'rate-estimator.js',
  'ui-status.js',
  // upload-card.js собирает обе карточки заливки из общего шаблона; без него
  // в разметке на их месте остаются пустые <div data-upload-card>, и ни один
  // из up_*/man_* элементов не существует.
  'upload-card.js',
  'admin.js',
];

// fakeXHR имитирует ровно ту часть XMLHttpRequest, которую использует
// putChunkXHR (см. tests/web/chunk-upload.test.js) — здесь она же нужна
// внутри целого admin.js, потому что chunk-upload.js достаёт конструктор
// как window.XMLHttpRequest, а admin.js патчит XMLHttpRequest.prototype.open/
// send своим CSRF-шимом поверх того, что подставим мы.
function makeFakeXHRClass(script) {
  return class FakeXHR {
    constructor() {
      this.upload = {};
      this.readyState = 0;
    }
    open(method, url) { this.method = method; this.url = url; }
    setRequestHeader() { /* no-op: CSRF-шим admin.js дергает это перед send */ }
    send(body) { this.body = body; script(this); }
  };
}

// По умолчанию каждый PUT чанка сразу и без прогресс-событий отвечает 200 —
// достаточно для сценариев, которым важен не сам чанк, а пайплайн вокруг.
function defaultXHRScript(xhr) {
  xhr.status = 200;
  xhr.responseText = JSON.stringify({ writeMs: 1 });
  xhr.readyState = 4;
  xhr.onreadystatechange();
}

function jsonResponse(json, status) {
  const st = status || 200;
  return { ok: st >= 200 && st < 300, status: st, json: async () => json, text: async () => JSON.stringify(json) };
}

// NDJSON-ответ /admin/api/upload/process — читается через res.body.getReader(),
// как настоящий streaming fetch. Одна строка — один JSON.parse внутри admin.js.
function ndjsonResponse(lines) {
  const enc = new TextEncoder();
  let i = 0;
  return {
    ok: true,
    status: 200,
    body: {
      getReader() {
        return {
          read: async () => {
            if (i < lines.length) {
              const value = enc.encode(lines[i] + '\n');
              i++;
              return { done: false, value };
            }
            return { done: true, value: undefined };
          },
        };
      },
    },
  };
}

// fetchStub перебирает handlers по порядку и берёт первый, чей test()
// совпал с URL — так каждый тест описывает только те эндпоинты, которые ему
// нужны, не переписывая весь пайплайн заново.
function makeFetchStub(handlers) {
  const calls = [];
  const fn = async (input, init) => {
    const url = typeof input === 'string' ? input : String(input && input.url);
    calls.push({ url, method: (init && init.method) || 'GET' });
    for (const h of handlers) {
      if (h.test(url)) return h.respond(url, init);
    }
    throw new Error('неожиданный fetch: ' + url);
  };
  fn.calls = calls;
  return fn;
}

// Собирает jsdom-страницу из настоящего admin.html и исполняет 8 sibling-
// скриптов в её vm-контексте — тот самый приём из umd-browser-global.test.js,
// но на весь admin.js целиком, а не на вырезанных функциях.
//
// admin.js на верхнем уровне вешает setInterval(periodicVisibleTick, 60000)
// (проверка видимости вкладки) — если не закрыть dom.window по окончании
// теста, этот таймер держит event loop живым, и `node --test` зависает
// навсегда после последнего теста вместо завершения процесса. `t` — это
// TestContext текущего теста node:test, его t.after() и есть то место, где
// это можно сделать одинаково для каждого теста, не повторяя try/finally.
function loadAdminPage(t, { fetchImpl, xhrScript } = {}) {
  let html = fs.readFileSync(HTML_PATH, 'utf8');
  // Единственный внешний <script> — CDN-бандл bootstrap. jsdom без сети его
  // не загрузит (runScripts: 'outside-only' и не пытается сам), но оставлять
  // тег незачем — он не участвует ни в одном сценарии ниже.
  html = html.replace(/<script src="https:\/\/cdn\.jsdelivr\.net[^<]*<\/script>\s*/, '');

  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost/admin/' });
  const { window } = dom;

  // jsdom не тащит TextDecoder/TextEncoder и fetch/Request в window — в
  // браузере они есть всегда, поэтому admin.js их не проверяет на существование.
  window.TextDecoder = TextDecoder;
  window.TextEncoder = TextEncoder;
  window.fetch = fetchImpl || makeFetchStub([]);
  window.XMLHttpRequest = makeFakeXHRClass(xhrScript || defaultXHRScript);
  // window.confirm у jsdom не реализован (печатает "Not implemented" и
  // возвращает undefined) — для сценариев с up_cleanup/man_cleanup это и
  // так работает как "отмена", но явный stub читается понятнее.
  window.confirm = () => true;

  const ctx = dom.getInternalVMContext();
  for (const file of SCRIPT_ORDER) {
    const abs = path.join(ADMIN_DIR, file);
    const src = fs.readFileSync(abs, 'utf8');
    vm.runInContext(src, ctx, { filename: abs });
  }

  t.after(() => dom.window.close());

  return { dom, window, document: window.document };
}

function setValue(document, id, value) {
  const el = document.getElementById(id);
  el.value = value;
  return el;
}

// ---- (a) runChunkedUpload: полный успешный путь через manifestsUpload() ----

test('runChunkedUpload: полный успех — init/status/chunk/complete/process, прогресс и вкладка man', async (t) => {
  const initJson = { uploadId: 'u-ok', chunkSize: 1024, totalChunks: 1 };
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/init'), respond: () => jsonResponse(initJson) },
    { test: (u) => u.includes('/admin/api/upload/status'), respond: () => jsonResponse({ received: [] }) },
    { test: (u) => u.includes('/admin/api/upload/complete'), respond: () => jsonResponse({}) },
    { test: (u) => u.includes('/admin/api/upload/process'), respond: () => ndjsonResponse([
      JSON.stringify({ type: 'start' }),
      JSON.stringify({ type: 'unzip', path: 'a.txt' }),
      JSON.stringify({ type: 'composeStart', totalFiles: 1 }),
      JSON.stringify({ type: 'file', idx: 1, path: 'a.txt', bytesDone: 20 }),
      JSON.stringify({ type: 'done', outPath: '/x' }),
    ]) },
  ]);

  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub });

  setValue(document, 'gid', 'mygame');
  setValue(document, 'ver', '1.2.3');
  document.getElementById('man_latest').checked = false;
  window.__manDroppedFile = new window.File(['x'.repeat(20)], 'build.zip', { type: 'application/zip' });

  const ok = await window.manifestsUpload();
  assert.strictEqual(ok, undefined, 'manifestsUpload ничего не возвращает, но не должна бросать');

  assert.strictEqual(document.getElementById('man_prog_pct').textContent, 'Загружено 100%');
  assert.strictEqual(document.getElementById('man_pb').style.width, '100%');
  assert.strictEqual(document.getElementById('man_prog_text').textContent, 'Готово. Манифест записан');

  const urls = fetchStub.calls.map((c) => c.url.split('?')[0]);
  assert.ok(urls.includes('/admin/api/upload/init'), 'init должен быть вызван');
  assert.ok(urls.includes('/admin/api/upload/complete'), 'complete должен быть вызван');
  assert.ok(urls.includes('/admin/api/upload/process'), 'process должен быть вызван');
  assert.ok(urls.indexOf('/admin/api/upload/init') < urls.indexOf('/admin/api/upload/complete'),
    'init должен идти раньше complete');
  assert.ok(urls.indexOf('/admin/api/upload/complete') < urls.indexOf('/admin/api/upload/process'),
    'complete должен идти раньше process');

  // dropped-файл сбрасывается после использования — иначе следующая заливка
  // молча повторила бы старый файл вместо выбранного в <input type=file>.
  assert.strictEqual(window.__manDroppedFile, null);
});

test('runChunkedUpload: вызванный напрямую с prefix=up — тоже проходит и обновляет DOM лаунчера', async (t) => {
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/init'), respond: () => jsonResponse({ uploadId: 'u2', chunkSize: 1024, totalChunks: 1 }) },
    { test: (u) => u.includes('/admin/api/upload/status'), respond: () => jsonResponse({ received: [] }) },
    { test: (u) => u.includes('/admin/api/upload/complete'), respond: () => jsonResponse({}) },
    { test: (u) => u.includes('/admin/api/upload/process'), respond: () => ndjsonResponse([JSON.stringify({ type: 'done', outPath: '/x' })]) },
  ]);
  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub });
  const file = new window.File(['y'.repeat(5)], 'launcher.zip', { type: 'application/zip' });

  const result = await window.runChunkedUpload('up', 'launcher', 'launcher', '9.9.9', file);

  assert.strictEqual(result, true);
  assert.strictEqual(document.getElementById('up_pb').style.width, '100%');
  assert.strictEqual(document.getElementById('up_prog_wrap').style.display, 'block');
});

// ---- (b) ошибочные пути ----

test('runChunkedUpload: init отвечает не ok — возвращает false и пишет статус ошибки', async (t) => {
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/init'), respond: () => jsonResponse({ error: 'boom' }, 500) },
  ]);
  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub });
  const file = new window.File(['z'], 'bad.zip', { type: 'application/zip' });

  const result = await window.runChunkedUpload('man', 'game', 'g', '1.0.0', file);

  assert.strictEqual(result, false);
  assert.match(document.getElementById('man_prog_text').textContent, /HTTP 500 init/);
});

test('runChunkedUpload: NDJSON-строка {type:"error"} на process — возвращает false и показывает сообщение', async (t) => {
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/init'), respond: () => jsonResponse({ uploadId: 'u3', chunkSize: 1024, totalChunks: 1 }) },
    { test: (u) => u.includes('/admin/api/upload/status'), respond: () => jsonResponse({ received: [] }) },
    { test: (u) => u.includes('/admin/api/upload/complete'), respond: () => jsonResponse({}) },
    { test: (u) => u.includes('/admin/api/upload/process'), respond: () => ndjsonResponse([
      JSON.stringify({ type: 'start' }),
      JSON.stringify({ type: 'error', message: 'распаковка не удалась' }),
    ]) },
  ]);
  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub });
  const file = new window.File(['q'.repeat(3)], 'g.zip', { type: 'application/zip' });

  const result = await window.runChunkedUpload('man', 'game', 'g', '1.0.0', file);

  assert.strictEqual(result, false, 'ev.type===error должен переворачивать processOk в false');
  assert.match(document.getElementById('man_prog_text').textContent, /Ошибка обработки: распаковка не удалась/);
});

test('runChunkedUpload: чанк не заливается ни с одной попытки — complete не вызывается, возвращает false', async (t) => {
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/init'), respond: () => jsonResponse({ uploadId: 'u4', chunkSize: 4, totalChunks: 1 }) },
    { test: (u) => u.includes('/admin/api/upload/status'), respond: () => jsonResponse({ received: [] }) },
    { test: (u) => u.includes('/admin/api/upload/complete'), respond: () => { throw new Error('complete не должен вызываться'); } },
  ]);
  // Каждая попытка PUT падает по сети — putChunkXHR резолвится ok:false, retry
  // исчерпывает попытки (5 штук по умолчанию), уходит в failedChunks и
  // остаётся неудачным на повторном проходе тоже.
  const xhrScript = (xhr) => { xhr.onerror(); };
  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub, xhrScript });
  const file = new window.File(['abcd'], 'g.zip', { type: 'application/zip' });

  const result = await window.runChunkedUpload('man', 'game', 'g', '1.0.0', file);

  assert.strictEqual(result, false);
  assert.match(document.getElementById('man_prog_text').textContent, /Повторная загрузка неудачных чанков завершилась с ошибкой/);
}, 20000);

// ---- (c) недорогая DOM-обвязка вокруг заливки ----

test('up_conc слайдер параллельности обновляет подпись up_conc_val при input', (t) => {
  const { window, document } = loadAdminPage(t);
  const slider = document.getElementById('up_conc');
  const label = document.getElementById('up_conc_val');
  slider.value = '42';
  slider.dispatchEvent(new window.Event('input'));
  assert.strictEqual(label.textContent, '42');
});

test('man_conc слайдер параллельности обновляет подпись man_conc_val при input', (t) => {
  const { window, document } = loadAdminPage(t);
  const slider = document.getElementById('man_conc');
  const label = document.getElementById('man_conc_val');
  slider.value = '17';
  slider.dispatchEvent(new window.Event('input'));
  assert.strictEqual(label.textContent, '17');
});

test('up_cleanup бьёт по /admin/api/upload/cleanup и пишет результат в #out', async (t) => {
  const fetchStub = makeFetchStub([
    { test: (u) => u.includes('/admin/api/upload/cleanup'), respond: () => jsonResponse({ removed: 3 }) },
  ]);
  const { window, document } = loadAdminPage(t, { fetchImpl: fetchStub });
  const btn = document.getElementById('up_cleanup');
  btn.dispatchEvent(new window.Event('click'));
  // Обработчик асинхронный (await fetch внутри) — дать микротаскам прогнаться.
  await new Promise((res) => setTimeout(res, 0));
  await new Promise((res) => setTimeout(res, 0));
  assert.strictEqual(document.getElementById('out').textContent, 'Удалено: 3');
});
