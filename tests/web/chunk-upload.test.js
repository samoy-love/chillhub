// Тесты server/admin_ui/chunk-upload.js — byte-level прогресса заливки чанка.
// См. комментарий в шапке файла: заменяет fetch() (без событий прогресса
// отправки) на XMLHttpRequest, событие upload.onprogress которого позволяет
// показывать реальный прогресс внутри одного чанка, а не только по факту
// его завершения.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { putChunkXHR, pendingBytes, uploadChunkWithRetries, runWorkerPool } = require(path.join('..', '..', 'server', 'admin_ui', 'chunk-upload.js'));

test('pendingBytes суммирует байты всех незавершённых чанков', () => {
  const inFlight = new Map([[0, 100], [3, 250], [7, 0]]);
  assert.strictEqual(pendingBytes(inFlight), 350);
});

test('pendingBytes на пустом Map — ноль', () => {
  assert.strictEqual(pendingBytes(new Map()), 0);
});

// fakeXHR имитирует ровно ту часть XMLHttpRequest API, которую использует
// putChunkXHR: open/send, upload.onprogress, onreadystatechange (readyState
// 4), onerror. Конструктор настраивается фабрикой script'а теста, чтобы
// прогонять разные сценарии (успех/HTTP-ошибка/сетевая ошибка) без сети.
function makeFakeXHRClass(script) {
  return class FakeXHR {
    constructor() {
      this.upload = {};
      this.readyState = 0;
    }
    open(method, url) { this.method = method; this.url = url; }
    send(body) {
      this.body = body;
      script(this);
    }
  };
}

test('putChunkXHR репортит прогресс по мере отправки, до финального ответа', async () => {
  const progressCalls = [];
  const FakeXHR = makeFakeXHRClass((xhr) => {
    xhr.upload.onprogress({ lengthComputable: true, loaded: 10 });
    xhr.upload.onprogress({ lengthComputable: true, loaded: 55 });
    xhr.status = 200;
    xhr.responseText = JSON.stringify({ status: 'ok', writeMs: 42 });
    xhr.readyState = 4;
    xhr.onreadystatechange();
  });

  const result = await putChunkXHR('/x', new Uint8Array(10), (loaded) => progressCalls.push(loaded), { XHR: FakeXHR });

  assert.deepStrictEqual(progressCalls, [10, 55], 'прогресс должен прийти ДО финального resolve, не одним числом в конце');
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.status, 200);
  assert.strictEqual(result.json.writeMs, 42);
});

test('putChunkXHR игнорирует событие прогресса без lengthComputable', async () => {
  const progressCalls = [];
  const FakeXHR = makeFakeXHRClass((xhr) => {
    xhr.upload.onprogress({ lengthComputable: false, loaded: 999 });
    xhr.status = 200;
    xhr.responseText = '{}';
    xhr.readyState = 4;
    xhr.onreadystatechange();
  });
  await putChunkXHR('/x', new Uint8Array(1), (loaded) => progressCalls.push(loaded), { XHR: FakeXHR });
  assert.deepStrictEqual(progressCalls, []);
});

test('putChunkXHR отдаёт ok:false на HTTP-ошибке, но всё равно резолвится', async () => {
  const FakeXHR = makeFakeXHRClass((xhr) => {
    xhr.status = 500;
    xhr.responseText = 'boom';
    xhr.readyState = 4;
    xhr.onreadystatechange();
  });
  const result = await putChunkXHR('/x', new Uint8Array(1), null, { XHR: FakeXHR });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.status, 500);
  assert.strictEqual(result.json, null, 'нежурнальный ответ не должен парситься как JSON');
});

test('putChunkXHR отдаёт ok:false на сетевой ошибке (onerror)', async () => {
  const FakeXHR = makeFakeXHRClass((xhr) => { xhr.onerror(); });
  const result = await putChunkXHR('/x', new Uint8Array(1), null, { XHR: FakeXHR });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.status, 0);
});

test('putChunkXHR переживает нежурнальное тело ответа при успехе (409 без JSON)', async () => {
  const FakeXHR = makeFakeXHRClass((xhr) => {
    xhr.status = 409;
    xhr.responseText = '';
    xhr.readyState = 4;
    xhr.onreadystatechange();
  });
  const result = await putChunkXHR('/x', new Uint8Array(1), null, { XHR: FakeXHR });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.status, 409);
  assert.strictEqual(result.json, null);
});

// uploadChunkWithRetries — то самое тело, которое раньше было продублировано
// втроём в admin.js (первая заливка / ретрай сорвавшихся чанков / дозаливка
// недостающих перед complete). deps.put подменяет putChunkXHR, чтобы не
// гонять настоящий XHR.

test('uploadChunkWithRetries: успех с первой попытки', async () => {
  const put = async () => ({ ok: true, status: 200, json: { writeMs: 17 } });
  const r = await uploadChunkWithRetries('u1', 3, 'blob', { put });
  assert.deepStrictEqual(r, { ok: true, attempts: 1, exists: false, writeMs: 17 });
});

test('uploadChunkWithRetries: 409 — успех, exists:true, без повторов', async () => {
  let calls = 0;
  const put = async () => { calls++; return { ok: false, status: 409, json: null }; };
  const r = await uploadChunkWithRetries('u1', 0, 'blob', { put });
  assert.strictEqual(calls, 1);
  assert.deepStrictEqual(r, { ok: true, attempts: 1, exists: true, writeMs: 0 });
});

test('uploadChunkWithRetries: HTTP-ошибка — повторяет и в итоге отдаёт успех', async () => {
  let calls = 0;
  const attemptsSeen = [];
  const put = async () => { calls++; return calls < 3 ? { ok: false, status: 500, json: null } : { ok: true, status: 200, json: {} }; };
  const r = await uploadChunkWithRetries('u1', 5, 'blob', {
    put, retryDelayMs: 0, onAttemptFailed: (info) => attemptsSeen.push(info),
  });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.attempts, 3);
  assert.strictEqual(attemptsSeen.length, 2);
  assert.strictEqual(attemptsSeen[0].status, 500);
});

test('uploadChunkWithRetries: сетевая ошибка — тоже повторяет через onAttemptFailed', async () => {
  let calls = 0;
  const attemptsSeen = [];
  const put = async () => { calls++; if (calls < 2) throw new Error('net down'); return { ok: true, status: 200, json: {} }; };
  const r = await uploadChunkWithRetries('u1', 1, 'blob', { put, retryDelayMs: 0, onAttemptFailed: (info) => attemptsSeen.push(info) });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(attemptsSeen.length, 1);
  assert.ok(attemptsSeen[0].error instanceof Error);
});

test('uploadChunkWithRetries: исчерпывает maxAttempts и отдаёт ok:false', async () => {
  const put = async () => ({ ok: false, status: 500, json: null });
  const r = await uploadChunkWithRetries('u1', 2, 'blob', { put, retryDelayMs: 0, maxAttempts: 3 });
  assert.deepStrictEqual(r, { ok: false, attempts: 3, exists: false, writeMs: 0 });
});

test('uploadChunkWithRetries: по умолчанию идёт через putChunkXHR', async () => {
  const FakeXHR = makeFakeXHRClass((xhr) => {
    xhr.status = 200; xhr.responseText = '{}'; xhr.readyState = 4; xhr.onreadystatechange();
  });
  const r = await uploadChunkWithRetries('u1', 0, new Uint8Array(1), { url: '/x', deps: { XHR: FakeXHR } });
  assert.strictEqual(r.ok, true);
});

// runWorkerPool — общий пул воркеров с ограниченной параллельностью, вместо
// трёх переприглашённых копий одного и того же цикла в admin.js.

test('runWorkerPool: пустой список индексов сразу резолвится без вызовов worker', async () => {
  let calls = 0;
  const failed = await runWorkerPool([], () => 4, async () => { calls++; return true; });
  assert.strictEqual(calls, 0);
  assert.deepStrictEqual(failed, []);
});

test('runWorkerPool: гоняет все индексы, не превышая текущую параллельность', async () => {
  let active = 0; let maxActive = 0;
  const cap = () => 2;
  const worker = async () => {
    active++; maxActive = Math.max(maxActive, active);
    await new Promise((res) => setTimeout(res, 1));
    active--;
    return true;
  };
  const failed = await runWorkerPool([0, 1, 2, 3, 4], cap, worker);
  assert.deepStrictEqual(failed, []);
  assert.ok(maxActive <= 2, 'параллельность не должна превышать concurrencyRef(): ' + maxActive);
});

test('runWorkerPool: собирает индексы, для которых worker вернул false', async () => {
  const worker = async (i) => i % 2 === 0;
  const failed = await runWorkerPool([0, 1, 2, 3], () => 3, worker);
  assert.deepStrictEqual(failed.sort(), [1, 3]);
});

test('runWorkerPool: concurrencyRef читается заново на каждом запуске воркера (можно менять на лету)', async () => {
  let par = 1;
  const seenActive = [];
  const worker = async () => {
    seenActive.push(par);
    if (par === 1) par = 3; // слайдер параллельности подвинули после первого чанка
    await new Promise((res) => setTimeout(res, 0));
    return true;
  };
  await runWorkerPool([0, 1, 2, 3], () => par, worker);
  assert.ok(seenActive.includes(3), 'после увеличения предела пул должен запускать больше воркеров сразу: ' + seenActive.join(','));
});

test('runWorkerPool: onActiveChange репортит текущее число активных воркеров', async () => {
  const seen = [];
  const worker = async () => { await new Promise((res) => setTimeout(res, 0)); return true; };
  await runWorkerPool([0, 1], () => 1, worker, (active) => seen.push(active));
  assert.deepStrictEqual(seen, [1, 0, 1, 0]);
});
