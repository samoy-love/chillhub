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

  function makeApi(opts) {
    const options = opts || {};
    const f = options.fetch || (typeof fetch !== 'undefined' ? fetch : null);
    if (!f) throw new Error('api: нет fetch');

    async function call(path, cfg) {
      const c = cfg || {};
      const method = c.method || 'GET';
      let url = BASE + path;

      if (c.query) {
        const q = new URLSearchParams();
        for (const k of Object.keys(c.query)) {
          const v = c.query[k];
          if (v !== undefined && v !== null && v !== '') q.set(k, String(v));
        }
        const s = q.toString();
        if (s) url += '?' + s;
      }

      const init = { method: method, signal: c.signal, headers: { accept: 'application/json' } };
      if (c.body !== undefined) {
        if (typeof FormData !== 'undefined' && c.body instanceof FormData) {
          init.body = c.body;
        } else {
          init.headers['content-type'] = 'application/json';
          init.body = JSON.stringify(c.body);
        }
      }

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

    return {
      call: call,
      ApiError: ApiError,

      me: () => get('auth/me'),
      logout: () => post('auth/logout'),

      launcherVersions: () => get('list'),
      launcherActivate: (version) => post('activate', { version: version }),
      launcherDelete: (version) => post('deleteVersion', { version: version }),
      launcherPrune: (keep) => post('pruneVersions', { keep: keep }),
      freeSpace: () => get('system/free'),

      uploadInit: (payload) => post('upload/init', payload),
      uploadStatus: (id) => get('upload/status', { id: id }),
      uploadComplete: (payload) => post('upload/complete', payload),
      uploadCleanup: (id) => post('upload/cleanup', { id: id }),
      uploadAbort: (id) => post('upload/abort', { id: id }),

      games: () => get('games'),
      gamesSave: (items) => post('games/save', { items: items }),
      gamesScan: () => post('games/scan'),
      gamesPurge: (gameId) => post('games/purge', { gameId: gameId }),
      gamesEcosystem: (gameId) => get('games/ecosystem', { gameId: gameId }),

      gallery: (gameId, dir) => get('games/gallery', { gameId: gameId, dir: dir }),
      galleryMkdir: (gameId, dir) => post('games/gallery/mkdir', { gameId: gameId, dir: dir }),
      galleryRename: (gameId, from, to) => post('games/gallery/rename', { gameId: gameId, from: from, to: to }),
      galleryDelete: (gameId, path) => post('games/gallery/delete', { gameId: gameId, path: path }),
      gallerySetCaption: (gameId, file, caption) =>
        post('games/gallery/setCaption', { gameId: gameId, file: file, caption: caption }),
      gallerySetCover: (gameId, file) => post('games/gallery/setCover', { gameId: gameId, file: file }),
      galleryUploadByUrl: (payload) => post('games/gallery/uploadByUrl', payload),

      modsList: (gameId) => get('mods/list', { gameId: gameId }),
      modsCatalog: (query) => get('mods/catalog', query),
      modsReadme: (pkg) => get('mods/readme', { pkg: pkg }),
      modsResolve: (payload) => post('mods/resolve', payload),
      modsActivate: (gameId, version) => post('mods/activate', { gameId: gameId, version: version }),
      modsDelete: (gameId, version) => post('mods/deleteVersion', { gameId: gameId, version: version }),
      modsCache: () => get('mods/cache'),
      summary: () => get('summary'),

      newsList: (query) => get('news/list', query),
      newsGet: (id) => get('news/get', { id: id }),
      newsSave: (payload) => post('news/save', payload),
      newsDelete: (id) => post('news/delete', { id: id }),
      newsPublish: (id, published) => post('news/publish', { id: id, published: published }),
      newsPreview: (payload) => post('news/preview', payload),
      newsRebuild: () => post('news/rebuild'),
      newsAssets: (dir) => get('news/assets', { dir: dir }),
      newsAssetsMkdir: (dir) => post('news/assets/mkdir', { dir: dir }),
      newsAssetsRename: (from, to) => post('news/assets/rename', { from: from, to: to }),
      newsAssetsDelete: (path) => post('news/assets/delete', { path: path }),
      newsAssetsUploadByUrl: (payload) => post('news/assets/uploadByUrl', payload),

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

  return { makeApi: makeApi, ApiError: ApiError, reason: reason, BASE: BASE };
});
