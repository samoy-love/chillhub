// Разница между двумя сборками лаунчера.
//
// ЗАЧЕМ ОНА В ПАНЕЛИ. Решение «отдать версию игрокам» необратимо, а
// принимается оно по двум номерам версий, которые сами по себе не
// говорят ничего. Список расходящихся файлов — единственное, по чему
// видно, что именно поедет на чужие компьютеры: три библиотеки или вся
// сборка целиком.
//
// ОТКУДА БЕРУТСЯ ДАННЫЕ. Манифест каждой версии лежит открытым файлом
// (`/manifests/launcher/<версия>.json`) — тем же, который читает сам
// лаунчер, когда решает, что докачивать. Отдельной ручки в админ-API под
// это нет, и она не нужна: считать разницу двух списков в браузере
// дешевле, чем возить её через сервер.
//
// СРАВНИВАЮТСЯ ХЕШИ, А НЕ РАЗМЕРЫ. Файл, изменившийся без изменения
// размера, — обычное дело для перекомпиляции, и по размеру он выглядит
// прежним. Именно по хешам решает и клиент, так что панель показывает
// ровно то, что игрок будет качать.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Manifest = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const BASE = '/manifests/launcher/';

  /** Список файлов манифеста, как бы он ни был обёрнут. */
  function files(raw) {
    const list = (raw && (raw.files || raw.items)) || (Array.isArray(raw) ? raw : []);
    return list
      .filter((f) => f && f.path)
      .map((f) => ({
        path: String(f.path),
        size: Number(f.size) || 0,
        hash: String(f.blake3 || f.sha256 || ''),
      }));
  }

  /**
   * Разница двух манифестов.
   *
   * Порядок — по пути, чтобы соседние файлы одной папки стояли рядом:
   * так по списку видно, поехала ли одна библиотека или весь каталог.
   */
  function diff(before, after) {
    const was = new Map(files(before).map((f) => [f.path, f]));
    const now = new Map(files(after).map((f) => [f.path, f]));
    const out = [];

    for (const [path, f] of now) {
      const old = was.get(path);
      if (!old) out.push({ path: path, size: f.size, diff: 'add' });
      else if (old.hash !== f.hash) out.push({ path: path, size: f.size, diff: 'mod' });
    }
    for (const [path, f] of was) {
      if (!now.has(path)) out.push({ path: path, size: f.size, diff: 'del' });
    }

    return out.sort((a, b) => a.path.localeCompare(b.path, 'ru'));
  }

  /** Сколько файлов добавилось, изменилось и пропало. */
  function counts(rows) {
    const list = rows || [];
    return {
      add: list.filter((f) => f.diff === 'add').length,
      mod: list.filter((f) => f.diff === 'mod').length,
      del: list.filter((f) => f.diff === 'del').length,
      total: list.length,
    };
  }

  /** Сколько всего придётся скачать игроку. */
  function weight(rows) {
    return (rows || []).filter((f) => f.diff !== 'del').reduce((a, f) => a + (Number(f.size) || 0), 0);
  }

  /**
   * Складывает плоский список в папки.
   *
   * В сборке лаунчера четыре с половиной сотни файлов, и плоский список
   * отвечает на «что изменилось» одним способом: пролистать целиком.
   * Свёрнутая папка с числом внутри отвечает сразу — поехал один
   * каталог или вся сборка.
   */
  function folders(rows) {
    const map = new Map();
    for (const r of rows || []) {
      const path = String(r.path || '');
      const cut = path.lastIndexOf('/');
      const dir = cut < 0 ? '' : path.slice(0, cut);
      if (!map.has(dir)) map.set(dir, []);
      map.get(dir).push(Object.assign({}, r, { name: cut < 0 ? path : path.slice(cut + 1) }));
    }
    return [...map.entries()]
      .map(([dir, files]) => ({
        dir: dir,
        files: files,
        counts: counts(files),
        weight: weight(files),
      }))
      .sort((a, b) => a.dir.localeCompare(b.dir, 'ru'));
  }

  /**
   * Читает манифест версии.
   *
   * Манифест раздаётся публично, поэтому идёт он не через админ-API.
   * Неудача здесь — не беда: разницу просто не покажут, а решение об
   * активации от этого не становится недоступным.
   */
  async function load(version, deps) {
    const d = deps || {};
    const doFetch = d.fetch;
    const base = d.base || BASE;
    if (!doFetch || !version) return null;
    try {
      const res = await doFetch(base + encodeURIComponent(version) + '.json', { headers: { accept: 'application/json' } });
      if (!res.ok) return null;
      return JSON.parse(await res.text());
    } catch {
      return null;
    }
  }

  /**
   * Разница между активной и загруженной версиями.
   *
   * Оба манифеста читаются разом: по одному это два ожидания подряд там,
   * где хватает одного.
   */
  async function between(activeVersion, newVersion, deps) {
    const [a, b] = await Promise.all([load(activeVersion, deps), load(newVersion, deps)]);
    if (!a || !b) return null;
    const rows = diff(a, b);
    return { rows: rows, counts: counts(rows), weight: weight(rows), total: files(b).length };
  }

  return { BASE, files, diff, counts, weight, folders, load, between };
});
