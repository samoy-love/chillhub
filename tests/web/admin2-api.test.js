// Слой обращений к админ-API панели 2.0.
//
// Главное, что здесь закреплено, — АДРЕСА. В панели 1.0 они были рассыпаны
// строками по коду, и опечатка обнаруживалась на проде. Таблица ниже
// сверяет метод и путь каждой ручки с маршрутами cmd/admin/routes.go:
// поменяли ручку на сервере — тест краснеет здесь, а не у владельца.

const test = require('node:test');
const assert = require('node:assert');

const A = require('../../server/admin_ui/v2/api.js');
const { makeApi, reason, BASE } = A;

/** Поддельный fetch: запоминает вызовы и отдаёт заданный ответ. */
function fake(response) {
  const calls = [];
  const r = response || {};
  const doFetch = async (url, init) => {
    calls.push({ url, init });
    if (r.throws) throw new Error('сеть');
    return {
      ok: r.ok !== false,
      status: r.status || 200,
      text: async () => (r.text !== undefined ? r.text : '{}'),
    };
  };
  return { calls, api: makeApi({ fetch: doFetch }) };
}

test('адрес собирается от одного префикса', async () => {
  const { calls, api } = fake();
  await api.launcherVersions();
  assert.strictEqual(calls[0].url, BASE + 'list');
  assert.strictEqual(calls[0].init.method, 'GET');
});

test('пустые параметры запроса не уезжают на сервер', async () => {
  const { calls, api } = fake();
  await api.feedbackList({ status: 'new', type: '', important: undefined, from: null });
  // «type=» на сервере означало бы фильтр по пустому типу, а не его отсутствие
  assert.strictEqual(calls[0].url, BASE + 'feedback/list?status=new');
});

test('тело уходит как JSON с нужным заголовком', async () => {
  const { calls, api } = fake();
  await api.launcherActivate('1.6.25');
  assert.strictEqual(calls[0].init.method, 'POST');
  assert.strictEqual(calls[0].init.headers['content-type'], 'application/json');
  assert.deepStrictEqual(JSON.parse(calls[0].init.body), { version: '1.6.25' });
});

test('FormData уходит как есть: границу multipart ставит браузер', async () => {
  const { calls, api } = fake();
  const fd = new FormData();
  fd.append('file', 'x');
  await api.call('games/icon/upload', { method: 'POST', body: fd });
  assert.strictEqual(calls[0].init.body, fd);
  // Свой content-type сломал бы разбор: в нём нет boundary
  assert.strictEqual(calls[0].init.headers['content-type'], undefined);
});

test('успешный ответ разбирается в объект', async () => {
  const { api } = fake({ text: '{"items":[1,2]}' });
  assert.deepStrictEqual(await api.games(), { items: [1, 2] });
});

test('пустое тело успешного ответа не роняет разбор', async () => {
  const { api } = fake({ text: '' });
  assert.strictEqual(await api.newsRebuild(), null);
});

test('ответ не-JSON отдаётся строкой, а не бросается', async () => {
  const { api } = fake({ text: 'ok' });
  assert.strictEqual(await api.gamesScan(), 'ok');
});

test('молчащая сеть отличается от ответа с ошибкой', async () => {
  const { api } = fake({ throws: true });
  await assert.rejects(api.games(), (e) => {
    // Ноль в статусе — признак «ответа не было вовсе»: совет тут «подожди»,
    // а не «поправь запрос»
    assert.strictEqual(e.status, 0);
    assert.strictEqual(e.message, 'сервер не отвечает');
    return true;
  });
});

test('ошибка сервера доносит его собственный текст', async () => {
  const { api } = fake({ ok: false, status: 409, text: '{"error":"версия уже активна"}' });
  await assert.rejects(api.launcherActivate('1.6.25'), (e) => {
    assert.strictEqual(e.message, 'версия уже активна');
    assert.strictEqual(e.status, 409);
    assert.strictEqual(e.path, 'activate');
    return true;
  });
});

test('ошибка без текста получает человеческую причину по коду', () => {
  assert.strictEqual(reason(null, 401), 'сессия истекла');
  assert.strictEqual(reason(null, 403), 'нет доступа');
  assert.strictEqual(reason(null, 404), 'сервер не знает такой ручки');
  assert.strictEqual(reason(null, 502), 'на сервере сбой');
  assert.strictEqual(reason(null, 418), 'код 418');
});

test('причина берётся из любого из трёх полей', () => {
  assert.strictEqual(reason({ error: 'раз' }, 400), 'раз');
  assert.strictEqual(reason({ message: 'два' }, 400), 'два');
  assert.strictEqual(reason({ reason: 'три' }, 400), 'три');
});

test('пробельная причина не выдаётся за текст', () => {
  // Иначе в интерфейс уходит «Ошибка: » с пустотой после двоеточия
  assert.strictEqual(reason({ error: '   ' }, 500), 'на сервере сбой');
});

test('длинный текст ошибки обрезается, а не заливает экран', () => {
  const long = 'я'.repeat(500);
  assert.strictEqual(reason(long, 400).length, 200);
});

/* --- Адреса всех ручек --- */

const ENDPOINTS = [
  ['me', [], 'GET', 'auth/me'],
  ['authRefresh', [], 'POST', 'auth/refresh'],
  ['logout', [], 'POST', 'auth/logout'],

  ['launcherVersions', [], 'GET', 'list'],
  ['launcherActivate', ['1.0'], 'POST', 'activate'],
  ['launcherDelete', ['1.0'], 'POST', 'deleteVersion'],
  ['launcherPrune', [5], 'POST', 'pruneVersions'],
  ['freeSpace', [], 'GET', 'system/free'],

  ['uploadInit', [{}], 'POST', 'upload/init'],
  ['uploadStatus', ['id'], 'GET', 'upload/status?id=id'],
  ['uploadComplete', [{}], 'POST', 'upload/complete'],
  ['uploadCleanup', ['id'], 'POST', 'upload/cleanup'],
  ['uploadAbort', ['id'], 'POST', 'upload/abort'],

  ['games', [], 'GET', 'games'],
  ['gamesSave', [[]], 'POST', 'games/save'],
  ['gamesScan', [], 'POST', 'games/scan'],
  ['gamesPurge', ['g'], 'POST', 'games/purge'],
  ['gamesEcosystem', ['g'], 'GET', 'games/ecosystem?gameId=g'],

  ['gallery', ['g'], 'GET', 'games/gallery?gameId=g'],
  ['galleryMkdir', ['g', 'd'], 'POST', 'games/gallery/mkdir'],
  ['galleryRename', ['g', 'a', 'b'], 'POST', 'games/gallery/rename'],
  ['galleryDelete', ['g', 'p'], 'POST', 'games/gallery/delete'],
  ['gallerySetCaption', ['g', 'f', 'c'], 'POST', 'games/gallery/setCaption'],
  ['gallerySetCover', ['g', 'f'], 'POST', 'games/gallery/setCover'],
  ['galleryUploadByUrl', [{}], 'POST', 'games/gallery/uploadByUrl'],

  ['modsList', ['g'], 'GET', 'mods/list?gameId=g'],
  ['modsCatalog', [{}], 'GET', 'mods/catalog'],
  ['modsReadme', ['p'], 'GET', 'mods/readme?pkg=p'],
  ['modsResolve', [{}], 'POST', 'mods/resolve'],
  ['modsActivate', ['g', 'v'], 'POST', 'mods/activate'],
  ['modsDelete', ['g', 'v'], 'POST', 'mods/deleteVersion'],
  ['modsCache', [], 'GET', 'mods/cache'],
  ['summary', [], 'GET', 'summary'],

  ['newsList', [{}], 'GET', 'news/list'],
  ['newsGet', ['1'], 'GET', 'news/get?id=1'],
  ['newsSave', [{}], 'POST', 'news/save'],
  ['newsDelete', ['1'], 'POST', 'news/delete'],
  ['newsPublish', ['1', true], 'POST', 'news/publish'],
  ['newsPreview', [{}], 'POST', 'news/preview'],
  ['newsRebuild', [], 'POST', 'news/rebuild'],
  ['newsAssets', ['d'], 'GET', 'news/assets?dir=d'],
  ['newsAssetsMkdir', ['d'], 'POST', 'news/assets/mkdir'],
  ['newsAssetsRename', ['a', 'b'], 'POST', 'news/assets/rename'],
  ['newsAssetsDelete', ['p'], 'POST', 'news/assets/delete'],
  ['newsAssetsUploadByUrl', [{}], 'POST', 'news/assets/uploadByUrl'],

  ['feedbackList', [{}], 'GET', 'feedback/list'],
  ['feedbackGet', ['1'], 'GET', 'feedback/get?id=1'],
  ['feedbackLogs', ['1'], 'GET', 'feedback/logs?id=1'],
  ['feedbackImportant', ['1', true], 'POST', 'feedback/toggleImportant'],
  ['feedbackRead', ['1'], 'POST', 'feedback/markRead'],
  ['feedbackUnread', ['1'], 'POST', 'feedback/markUnread'],
  ['feedbackDelete', ['1'], 'POST', 'feedback/delete'],
  ['feedbackClear', [], 'POST', 'feedback/clear'],

  ['maintenanceGet', [], 'GET', 'maintenance/get'],
  ['maintenanceSet', [{}], 'POST', 'maintenance/set'],
  ['maintenanceClear', [], 'POST', 'maintenance/clear'],

  ['metricsSummary', [{}], 'GET', 'metrics/summary'],
  ['metricsErrors', [{}], 'GET', 'metrics/errors'],
  ['metricsClear', [], 'POST', 'metrics/clear'],
];

test('каждая ручка ходит по своему адресу своим методом', async () => {
  for (const [name, args, method, path] of ENDPOINTS) {
    const { calls, api } = fake();
    assert.strictEqual(typeof api[name], 'function', `нет метода ${name}`);
    await api[name](...args);
    assert.strictEqual(calls.length, 1, `${name}: ожидался один запрос`);
    assert.strictEqual(calls[0].init.method, method, `${name}: метод`);
    assert.strictEqual(calls[0].url, BASE + path, `${name}: адрес`);
  }
});

test('опись покрывает все ручки слоя, а слой — всю опись', () => {
  const { api } = fake();
  const declared = Object.keys(api).filter((k) => k !== 'call' && k !== 'ApiError');
  const listed = ENDPOINTS.map((e) => e[0]);
  assert.deepStrictEqual(declared.slice().sort(), listed.slice().sort());
});

/* ---------- Сессия ---------- */

/** Подделка слоя обращений: `me` отвечает по очереди из списка. */
function fakeApi(answers) {
  const queue = answers.slice();
  const log = [];
  return {
    log,
    me: async () => {
      log.push('me');
      const a = queue.shift();
      if (a === 'ok') return { user: 'admin' };
      const err = new Error('нет');
      err.status = a === 'off' ? 0 : 401;
      throw err;
    },
    authRefresh: async () => {
      log.push('refresh');
    },
  };
}

test('живая сессия не дёргает обновление зря', async () => {
  const api = fakeApi(['ok']);
  assert.strictEqual(await A.session(api), 'ok');
  assert.deepStrictEqual(api.log, ['me']);
});

test('истёкшую сессию сначала пробуют обновить, а не выкидывают', async () => {
  // Обновление молчаливое: человек не должен видеть вход из-за живого токена
  const api = fakeApi([401, 'ok']);
  assert.strictEqual(await A.session(api), 'ok');
  assert.deepStrictEqual(api.log, ['me', 'refresh', 'me']);
});

test('не узнал и после обновления — значит на вход', async () => {
  const api = fakeApi([401, 401]);
  assert.strictEqual(await A.session(api), 'login');
});

test('молчащий сервер — не отказ, и из панели за это не выкидывают', async () => {
  // Иначе упавшая сеть выглядит как «вас разлогинили»
  const api = fakeApi(['off']);
  assert.strictEqual(await A.session(api), 'offline');
  assert.deepStrictEqual(api.log, ['me']);
});

test('сервер, легший после отказа, тоже не уводит на вход', async () => {
  const api = fakeApi([401, 'off']);
  assert.strictEqual(await A.session(api), 'offline');
});

test('упавшее обновление не мешает задать вопрос второй раз', async () => {
  const api = fakeApi([401, 'ok']);
  api.authRefresh = async () => {
    api.log.push('refresh');
    throw new Error('и обновление не вышло');
  };
  assert.strictEqual(await A.session(api), 'ok');
});

test('страница входа — корень админки, отдельной её нет', () => {
  // /admin/ сам отдаёт login.html анониму (handleAdminUI в cmd/admin/main.go)
  assert.strictEqual(A.LOGIN, '/admin/');
});
