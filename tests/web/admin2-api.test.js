// Слой обращений к админ-API панели 2.0.
//
// Главное, что здесь закреплено, — АДРЕСА. В панели 1.0 они были рассыпаны
// строками по коду, и опечатка обнаруживалась на проде. Таблица ниже
// сверяет метод и путь каждой ручки с маршрутами cmd/admin/routes.go:
// поменяли ручку на сервере — тест краснеет здесь, а не у владельца.

const test = require('node:test');
const assert = require('node:assert');

const A = require('../../server/admin_ui/api.js');
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
  assert.ok(calls[0].url.startsWith(BASE + 'list'), calls[0].url);
  assert.strictEqual(calls[0].init.method, 'GET');
});

test('пустые параметры запроса не уезжают на сервер', async () => {
  const { calls, api } = fake();
  await api.feedbackList({ status: 'new', type: '', important: undefined, from: null });
  // «type=» на сервере означало бы фильтр по пустому типу, а не его отсутствие
  assert.strictEqual(calls[0].url, BASE + 'feedback/list?status=new');
});

test('запись уходит формой, а не JSON: так её читает сервер', async () => {
  // Обработчик берёт параметры из r.URL.Query() и r.FormValue; тело JSON
  // для него не существует вовсе, и версия доехала бы пустой
  const { calls, api } = fake();
  await api.launcherActivate('1.6.25');
  assert.strictEqual(calls[0].init.method, 'POST');
  assert.match(calls[0].init.headers['content-type'], /x-www-form-urlencoded/);
  assert.match(calls[0].url, /gameId=launcher&version=1\.6\.25/);
  assert.strictEqual(new URLSearchParams(calls[0].init.body).get('version'), '1.6.25');
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
  ['launcherPrune', [], 'POST', 'pruneVersions'],
  ['freeSpace', [], 'GET', 'system/free'],

  ['uploadInit', [{}], 'POST', 'upload/init'],
  ['uploadStatus', ['id'], 'GET', 'upload/status'],
  ['uploadComplete', [{}], 'POST', 'upload/complete'],
  ['uploadCleanup', ['id'], 'POST', 'upload/cleanup'],
  ['uploadAbort', ['id'], 'POST', 'upload/abort'],

  ['games', [], 'GET', 'games'],
  ['gamesSave', [[]], 'POST', 'games/save'],
  ['gamesScan', [], 'POST', 'games/scan'],
  ['gamesPurge', ['g'], 'POST', 'games/purge'],
  ['gamesEcosystem', ['g', 's'], 'POST', 'games/ecosystem'],
  ['gamesIconUpload', ['g', 'f'], 'POST', 'games/icon/upload'],

  ['gallery', ['g', ''], 'GET', 'games/gallery'],
  ['galleryMkdir', ['g', '', 'd'], 'POST', 'games/gallery/mkdir'],
  ['galleryRename', ['g', '', 'a', 'b'], 'POST', 'games/gallery/rename'],
  ['galleryDelete', ['g', '', 'n'], 'POST', 'games/gallery/delete'],
  ['gallerySetCaption', ['g', 'f', 'c'], 'POST', 'games/gallery/setCaption'],
  ['gallerySetCover', ['g', 'f'], 'POST', 'games/gallery/setCover'],
  ['galleryUpload', ['g', '', 'f'], 'POST', 'games/gallery/upload'],
  ['galleryUploadByUrl', ['g', '', 'u', 'n'], 'POST', 'games/gallery/uploadByUrl'],

  ['modsList', ['g'], 'GET', 'mods/list'],
  ['modsCatalog', [{}], 'GET', 'mods/catalog'],
  ['modsReadme', ['ns', 'n', 'v'], 'GET', 'mods/readme'],
  ['modsResolve', [{}], 'POST', 'mods/resolve'],
  ['modsActivate', ['g', 'v'], 'POST', 'mods/activate'],
  ['modsDelete', ['g', 'v'], 'POST', 'mods/deleteVersion'],
  ['modsImport', ['g', 'f'], 'POST', 'mods/import'],
  ['modsDiff', ['g', 'a', 'b'], 'GET', 'mods/diff'],
  ['modsCache', [], 'GET', 'mods/cache'],
  ['modsCacheSweep', [], 'POST', 'mods/cache'],
  ['modsCacheClear', [], 'POST', 'mods/cache'],
  ['summary', [], 'GET', 'summary'],

  ['newsList', ['launcher', ''], 'GET', 'news/list'],
  ['newsGet', ['launcher', '', 's'], 'GET', 'news/get'],
  ['newsSave', [{}], 'POST', 'news/save'],
  ['newsDelete', ['launcher', '', 's'], 'POST', 'news/delete'],
  ['newsPublish', ['launcher', '', 's', true], 'POST', 'news/publish'],
  ['newsPreview', ['# т', 'launcher', ''], 'POST', 'news/preview'],
  ['newsRebuild', ['launcher', ''], 'POST', 'news/rebuild'],
  ['newsAssets', [''], 'GET', 'news/assets'],
  ['newsAssetsMkdir', ['', 'd'], 'POST', 'news/assets/mkdir'],
  ['newsAssetsRename', ['', 'a', 'b'], 'POST', 'news/assets/rename'],
  ['newsAssetsDelete', ['', 'n'], 'POST', 'news/assets/delete'],
  ['newsAssetsUpload', ['', 'f'], 'POST', 'news/assets/upload'],
  ['newsCoverUpload', ['launcher', '', 's', 'f'], 'POST', 'news/uploadCover'],
  ['newsAssetsUploadByUrl', ['', 'u', 'n'], 'POST', 'news/assets/uploadByUrl'],

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
    // Параметры сверяются отдельно: здесь важен только адрес ручки
    assert.strictEqual(calls[0].url.split('?')[0], (BASE + path).split('?')[0], `${name}: адрес`);
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

/* ---------- Как параметры доезжают до сервера ---------- */

/** Слой поверх подменённого fetch, запоминающий, что ушло. */
function spy() {
  const calls = [];
  const api = A.makeApi({
    fetch: async (url, init) => {
      calls.push({
        url: String(url),
        method: (init && init.method) || 'GET',
        type: (init && init.headers && init.headers['content-type']) || '',
        body: init ? init.body : undefined,
      });
      return { ok: true, status: 200, text: async () => '{}' };
    },
  });
  return { api, calls, last: () => calls[calls.length - 1] };
}

test('параметры записи уезжают строкой запроса, а не только телом', async () => {
  // Обработчики читают r.URL.Query() — тело JSON для них не существует
  const s = spy();
  await s.api.launcherActivate('1.6.25');
  assert.match(s.last().url, /\?.*gameId=launcher/);
  assert.match(s.last().url, /version=1\.6\.25/);
});

test('те же параметры уезжают и телом формы', async () => {
  // Другие обработчики читают r.FormValue — им нужна форма, а не адрес
  const s = spy();
  await s.api.modsActivate('repo', '1.9.9');
  assert.match(s.last().type, /x-www-form-urlencoded/);
  assert.match(s.last().body, /gameId=repo/);
  assert.match(s.last().body, /version=1\.9\.9/);
});

test('лаунчер называет себя сервером зарезервированным идентификатором', async () => {
  // Без gameId ручки версий отвечают про пустой идентификатор
  const s = spy();
  await s.api.launcherVersions();
  await s.api.launcherDelete('1.6.20');
  await s.api.launcherPrune();
  for (const c of s.calls) assert.match(c.url, /gameId=launcher/, c.url);
});

test('две ручки, которые разбирают именно JSON, получают JSON', async () => {
  const s = spy();
  await s.api.gamesSave([{ gameId: 'repo' }]);
  assert.match(s.last().type, /application\/json/);
  assert.deepStrictEqual(JSON.parse(s.last().body), { items: [{ gameId: 'repo' }] });

  await s.api.uploadInit({ kind: 'launcher', totalSize: 5 });
  assert.match(s.last().type, /application\/json/);
  assert.strictEqual(JSON.parse(s.last().body).kind, 'launcher');
});

test('список ручек с JSON закрыт: остальным JSON не годится', () => {
  // Он же документация контракта — расширять его можно только по коду сервера
  assert.deepStrictEqual([...A.JSON_BODY].sort(), ['games/save', 'maintenance/set', 'upload/init']);
});

test('длинный текст в адрес не лезет, но телом уезжает целиком', async () => {
  // Адрес не резиновый: на длинном тексте запрос упрётся в ограничение сервера
  const s = spy();
  const long = 'я'.repeat(A.URL_VALUE_LIMIT + 1);
  await s.api.newsSave({ title: 'Заметка', body: long });
  assert.ok(!s.last().url.includes('body='), 'длинный текст ушёл в адрес');
  assert.match(s.last().url, /title=/, 'короткий заголовок в адрес не попал');
  assert.strictEqual(new URLSearchParams(s.last().body).get('body'), long);
});

test('«нет» доезжает как «нет», а не пропадает', async () => {
  // Пропавший false на сервере неотличим от «поля не прислали»
  const s = spy();
  await s.api.newsPublish('launcher', '', 'note', false);
  assert.match(s.last().url, /published=false/);
  assert.strictEqual(new URLSearchParams(s.last().body).get('published'), 'false');
});

test('пустые значения не засоряют запрос', async () => {
  const s = spy();
  await s.api.gallery('repo', '');
  assert.match(s.last().url, /gameId=repo/);
  assert.ok(!s.last().url.includes('dir='), 'пустая папка уехала как параметр');
});

test('чтение остаётся чтением: тела у него нет', async () => {
  const s = spy();
  await s.api.games();
  assert.strictEqual(s.last().method, 'GET');
  assert.strictEqual(s.last().body, undefined);
});
