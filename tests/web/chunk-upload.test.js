// Тесты server/admin_ui/chunk-upload.js — byte-level прогресса заливки чанка.
// См. комментарий в шапке файла: заменяет fetch() (без событий прогресса
// отправки) на XMLHttpRequest, событие upload.onprogress которого позволяет
// показывать реальный прогресс внутри одного чанка, а не только по факту
// его завершения.

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const { putChunkXHR, pendingBytes } = require(path.join('..', '..', 'server', 'admin_ui', 'chunk-upload.js'));

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
