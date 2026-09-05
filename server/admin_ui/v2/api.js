// Обращения к админ-API одним местом.
//
// ЗАЧЕМ ОТДЕЛЬНЫЙ СЛОЙ. В панели 1.0 адреса были рассыпаны по коду
// строками, и одна и та же ручка звалась то `/admin/mods/list`, то
// `/admin/api/mods/list` — работало только потому, что транспорт
// переписывал первое во второе. Здесь адрес каждой ручки записан один
// раз, и опечатка в нём ловится тестом, а не на проде.
//
// Разбор ответа тоже общий. Админ-API отвечает ошибкой двумя способами —
// кодом HTTP и полем в теле, — и раньше каждый вызов разбирал это
// по-своему: часть мест показывала «[object Object]», часть молчала.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Api = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const BASE = '/admin/api/';

  /* КАК СЕРВЕР ЧИТАЕТ ПАРАМЕТРЫ ЗАПИСИ.
     ------------------------------------------------------------------
     Почти все обработчики админ-API берут параметры из строки запроса
     (`r.URL.Query()`) либо из формы (`r.ParseForm` / `r.FormValue`), и
     тело JSON для них не существует вовсе: `r.FormValue("version")`
     вернёт пустую строку, а `ParseForm` разберёт только строку запроса.
     Панель 1.0 поэтому и вешала параметры на адрес — `POST
     /admin/activate?gameId=…&version=…` без тела.

     Значит, по умолчанию параметры записи уезжают ДВУМЯ путями сразу:
     в строке запроса (её видит `Query()`) и телом
     `application/x-www-form-urlencoded` (его видит `ParseForm`). Оба
     несут одно и то же, так что читающий любым способом получит одно
     значение, а не два разных.

     Длинные значения — текст новости, разметка предпросмотра — в адрес
     не кладём: он не резиновый, и на длинном тексте запрос упрётся в
     ограничение сервера. Такие уезжают только телом; обработчиков,
     которые читали бы длинное значение из `Query()`, в API нет.

     Исключения перечислены поимённо: две ручки разбирают именно JSON. */
  const JSON_BODY = new Set(['upload/init', 'games/save', 'maintenance/set']);

  /** Длиннее этого в адрес не кладём. */
  const URL_VALUE_LIMIT = 512;

  /** Идентификатор, под которым сервер держит сборки самого лаунчера. */
  const LAUNCHER = 'launcher';

  /** Ошибка запроса, у которой есть человеческий текст и код HTTP. */
  class ApiError extends Error {
    constructor(message, status, path) {
      super(message);
      this.name = 'ApiError';
      this.status = status;
      this.path = path;
    }
  }

  /**
   * Достаёт из ответа человеческую причину.
   *
   * Порядок важен: сервер кладёт текст то в `error`, то в `message`, то
   * отдаёт голый текст без JSON. Пустая строка не годится — вместо неё
   * подставляется код, иначе в интерфейс уходит «Ошибка: ».
   */
  function reason(body, status) {
    if (body && typeof body === 'object') {
      const t = body.error || body.message || body.reason;
      if (typeof t === 'string' && t.trim()) return t.trim();
    }
    if (typeof body === 'string' && body.trim()) return body.trim().slice(0, 200);
    if (status === 401) return 'сессия истекла';
    if (status === 403) return 'нет доступа';
    if (status === 404) return 'сервер не знает такой ручки';
    if (status >= 500) return 'на сервере сбой';
    return 'код ' + status;
  }

  /* Куда ведёт панель анонима. Отдельной страницы входа нет: /admin/ сам
     отдаёт login.html тому, у кого нет сессии (см. handleAdminUI в
     cmd/admin/main.go). */
  const LOGIN = '/admin/';

  /* Уход на вход — отдельной функцией, а не строчкой в двух местах.
     Так у него одно имя на всю панель, и так его можно подменить в
     тесте: настоящий переход jsdom не выполняет и проверить его иначе
     нечем. */
  function goLogin() {
    if (typeof window !== 'undefined') window.location.href = LOGIN;
  }

  /**
   * Что делать с сессией на входе.
   *
   * Три исхода, и путать их нельзя. `ok` — сессия жива. `login` — сервер
   * ответил «не узнаю»: сначала пробуем обновить, и только если он не
   * узнаёт и после этого, уводим на вход. `offline` — сервер не ответил
   * вовсе; это не то же самое, что отказ, и выкидывать человека из
   * панели потому, что упала сеть, нельзя — панель покажет снимок и
   * скажет, что записывать нельзя.
   */
  async function session(api) {
    const ask = async () => {
      try {
        await api.me();
        return 'ok';
      } catch (e) {
        if (e && e.status === 401) return 'login';
        if (e && e.status === 0) return 'offline';
        return 'offline';
      }
    };

    const first = await ask();
    if (first !== 'login') return first;

    try {
      await api.authRefresh();
    } catch {
      // Обновить не вышло — решает следующий вопрос, а не этот
    }
    return ask();
  }

  function makeApi(opts) {
    const options = opts || {};
    const f = options.fetch || (typeof fetch !== 'undefined' ? fetch : null);
    if (!f) throw new Error('api: нет fetch');

    async function call(path, cfg) {
      const c = cfg || {};
      const method = c.method || 'GET';
      let url = BASE + path;

      /* Значения приводим к строкам одинаково во всех трёх местах:
         `false` обязано уехать как «false», а не пропасть, иначе снятие
         галочки на сервере выглядит как её отсутствие. */
      const usable = (v) => v !== undefined && v !== null && v !== '';
      const asText = (v) => (typeof v === 'boolean' ? String(v) : String(v));

      const q = new URLSearchParams();
      const add = (src) => {
        for (const k of Object.keys(src || {})) {
          const v = src[k];
          if (usable(v) && asText(v).length <= URL_VALUE_LIMIT) q.set(k, asText(v));
        }
      };
      add(c.query);

      const init = { method: method, signal: c.signal, headers: { accept: 'application/json' } };

      if (c.body !== undefined) {
        if (typeof FormData !== 'undefined' && c.body instanceof FormData) {
          init.body = c.body;
        } else if (JSON_BODY.has(path)) {
          init.headers['content-type'] = 'application/json';
          init.body = JSON.stringify(c.body);
        } else {
          /* Обычный путь: то же самое и в адрес, и телом формы. */
          add(c.body);
          const form = new URLSearchParams();
          for (const k of Object.keys(c.body || {})) {
            const v = c.body[k];
            if (usable(v)) form.set(k, asText(v));
          }
          init.headers['content-type'] = 'application/x-www-form-urlencoded;charset=UTF-8';
          init.body = form.toString();
        }
      }

      const qs = q.toString();
      if (qs) url += '?' + qs;

      let res;
      try {
        res = await f(url, init);
      } catch {
        // Сеть не ответила вовсе. Отличать это от ответа с ошибкой
        // обязательно: советы разные — «подожди» против «поправь».
        throw new ApiError('сервер не отвечает', 0, path);
      }

      const text = await res.text();
      let data = null;
      if (text) {
        try {
          data = JSON.parse(text);
        } catch {
          data = text;
        }
      }

      if (!res.ok) throw new ApiError(reason(data, res.status), res.status, path);
      return data;
    }

    const get = (path, query, signal) => call(path, { query: query, signal: signal });
    const post = (path, body, query) => call(path, { method: 'POST', body: body, query: query });

    /* Файл уезжает multipart: сервер читает его через `r.FormFile`.
       Свой content-type здесь ставить нельзя — в нём нет границы, и
       разбор на сервере разваливается. Остальные поля кладутся в ту же
       форму, а не в адрес: имя файла бывает длинным. */
    function upload(path, fields, fileField, file) {
      const fd = new FormData();
      for (const k of Object.keys(fields || {})) {
        const v = fields[k];
        if (v !== undefined && v !== null && v !== '') fd.set(k, String(v));
      }
      fd.set(fileField, file);
      return call(path, { method: 'POST', body: fd });
    }

    return {
      call: call,
      ApiError: ApiError,

      me: () => get('auth/me'),
      authRefresh: () => post('auth/refresh'),
      logout: () => post('auth/logout'),

      /* Лаунчер для сервера — такая же «игра», как остальные, только с
         зарезервированным идентификатором. Без него ручки версий
         отвечают про пустой идентификатор, а не про лаунчер. */
      launcherVersions: () => get('list', { gameId: LAUNCHER }),
      launcherActivate: (version) => post('activate', { gameId: LAUNCHER, version: version }),
      launcherDelete: (version) => post('deleteVersion', { gameId: LAUNCHER, version: version }),
      /* Сколько оставить, сервер решает сам: всё старше активной,
         кроме двух перед ней. Параметра `keep` у ручки нет, и слать
         его — притворяться, будто панель этим управляет. */
      launcherPrune: () => post('pruneVersions', { gameId: LAUNCHER }),
      freeSpace: () => get('system/free'),

      uploadInit: (payload) => post('upload/init', payload),
      /* Номер загрузки сервер зовёт `uploadId` во всех четырёх ручках
         (`uploadID(r)` в chunked.go). Под именем `id` он его не видит, и
         докачка, завершение и отмена молча отвечали «missing id». */
      uploadStatus: (id) => get('upload/status', { uploadId: id }),
      uploadComplete: (payload) => post('upload/complete', { uploadId: (payload && payload.uploadId) || payload }),
      uploadCleanup: (id) => post('upload/cleanup', { uploadId: id }),
      uploadAbort: (id) => post('upload/abort', { uploadId: id }),

      games: () => get('games'),
      gamesSave: (items) => post('games/save', { items: items }),
      gamesScan: () => post('games/scan'),
      gamesPurge: (gameId) => post('games/purge', { gameId: gameId }),
      /* Это запись, а не чтение: ручка сама сохраняет запись реестра
         тем, что нашла в схеме Thunderstore. `slug` — игра в их
         терминах, например «lethal-company». */
      gamesEcosystem: (gameId, slug) => post('games/ecosystem', { gameId: gameId, slug: slug }),
      gamesIconUpload: (gameId, file) => upload('games/icon/upload', { gameId: gameId }, 'file', file),

      /* ГАЛЕРЕЯ АДРЕСУЕТСЯ ПАПКОЙ И ИМЕНЕМ, А НЕ ПУТЁМ ЦЕЛИКОМ.
         `path` — папка внутри галереи игры, `name` — файл в ней. Полный
         путь одной строкой сервер не принимает: он режет `path` своим
         `SanitizeAssetPath`, а имя проверяет отдельно, и склейка ушла бы
         в никуда. */
      gallery: (gameId, path) => get('games/gallery', { gameId: gameId, path: path }),
      galleryMkdir: (gameId, path, name) => post('games/gallery/mkdir', { gameId: gameId, path: path, name: name }),
      galleryRename: (gameId, path, from, to) =>
        post('games/gallery/rename', { gameId: gameId, path: path, from: from, to: to }),
      galleryDelete: (gameId, path, name) => post('games/gallery/delete', { gameId: gameId, path: path, name: name }),
      gallerySetCaption: (gameId, file, caption) =>
        post('games/gallery/setCaption', { gameId: gameId, file: file, caption: caption }),
      gallerySetCover: (gameId, file) => post('games/gallery/setCover', { gameId: gameId, file: file }),
      galleryUpload: (gameId, path, file) => upload('games/gallery/upload', { gameId: gameId, path: path }, 'file', file),
      galleryUploadByUrl: (gameId, path, url, filename) =>
        post('games/gallery/uploadByUrl', { gameId: gameId, path: path, url: url, filename: filename }),

      modsList: (gameId) => get('mods/list', { gameId: gameId }),
      modsCatalog: (query) => get('mods/catalog', query),
      modsReadme: (namespace, name, version) =>
        get('mods/readme', { namespace: namespace, name: name, version: version }),
      modsResolve: (payload) => post('mods/resolve', payload),
      modsActivate: (gameId, version) => post('mods/activate', { gameId: gameId, version: version }),
      modsDelete: (gameId, version) => post('mods/deleteVersion', { gameId: gameId, version: version }),
      modsImport: (gameId, file) => upload('mods/import', { gameId: gameId }, 'file', file),

      /* Разница между двумя собранными версиями модпака. Читают её перед
         тем, как отдать пересборку игрокам: «какие моды изменились» —
         это вопрос, на который список из полутора сотен полных имён до и
         после не отвечает. */
      modsDiff: (gameId, from, to) => get('mods/diff', { gameId: gameId, from: from, to: to }),

      /* Кэш архивов: одна ручка на чтение и на чистку. Без `all` сервер
         убирает только просроченное, с `all=1` — всё. */
      modsCache: () => get('mods/cache'),
      modsCacheSweep: () => post('mods/cache'),
      modsCacheClear: () => post('mods/cache', { all: '1' }),
      summary: () => get('summary'),

      /* НОВОСТЬ АДРЕСУЕТСЯ ТРОЙКОЙ, А НЕ ОДНИМ НОМЕРОМ.
         `scope` — «launcher» или «game», `gameId` нужен только второму,
         `slug` — имя самой заметки. Заголовок отдельным полем сервер не
         знает: он живёт первой строкой markdown. */
      newsList: (scope, gameId) => get('news/list', { scope: scope, gameId: gameId }),
      newsGet: (scope, gameId, slug) => get('news/get', { scope: scope, gameId: gameId, slug: slug }),
      newsSave: (payload) => post('news/save', payload),
      newsDelete: (scope, gameId, slug) => post('news/delete', { scope: scope, gameId: gameId, slug: slug }),
      newsPublish: (scope, gameId, slug, published) =>
        post('news/publish', { scope: scope, gameId: gameId, slug: slug, published: published }),
      newsPreview: (markdown, scope, gameId) =>
        post('news/preview', { markdown: markdown, scope: scope, gameId: gameId }),
      newsRebuild: (scope, gameId) => post('news/rebuild', { scope: scope, gameId: gameId }),
      newsAssets: (path) => get('news/assets', { path: path }),
      newsAssetsMkdir: (path, name) => post('news/assets/mkdir', { path: path, name: name }),
      newsAssetsRename: (path, from, to) => post('news/assets/rename', { path: path, from: from, to: to }),
      newsAssetsDelete: (path, name) => post('news/assets/delete', { path: path, name: name }),
      newsAssetsUpload: (path, file) => upload('news/assets/upload', { path: path }, 'file', file),
      newsCoverUpload: (scope, gameId, slug, file) =>
        upload('news/uploadCover', { scope: scope, gameId: gameId, slug: slug }, 'file', file),
      newsAssetsUploadByUrl: (path, url, filename) =>
        post('news/assets/uploadByUrl', { path: path, url: url, filename: filename }),

      feedbackList: (query) => get('feedback/list', query),
      feedbackGet: (id) => get('feedback/get', { id: id }),
      feedbackLogs: (id) => get('feedback/logs', { id: id }),
      feedbackImportant: (id, important) => post('feedback/toggleImportant', { id: id, important: important }),
      feedbackRead: (id) => post('feedback/markRead', { id: id }),
      feedbackUnread: (id) => post('feedback/markUnread', { id: id }),
      feedbackDelete: (id) => post('feedback/delete', { id: id }),
      feedbackClear: () => post('feedback/clear'),

      maintenanceGet: () => get('maintenance/get'),
      maintenanceSet: (payload) => post('maintenance/set', payload),
      maintenanceClear: () => post('maintenance/clear'),

      metricsSummary: (query) => get('metrics/summary', query),
      metricsErrors: (query) => get('metrics/errors', query),
      metricsClear: () => post('metrics/clear'),
    };
  }

  return {
    makeApi: makeApi, ApiError: ApiError, reason: reason, session: session, goLogin: goLogin,
    BASE: BASE, LOGIN: LOGIN, JSON_BODY: JSON_BODY, URL_VALUE_LIMIT: URL_VALUE_LIMIT, LAUNCHER: LAUNCHER,
  };
});
