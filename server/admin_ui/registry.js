// Реестр игр: правка списка, порядок, проверки перед сохранением.
//
// ЧТО СЧИТАЕТСЯ ИЗМЕНЕНИЕМ — не здесь: это `registry-diff.js` версии 1.0,
// он сравнивает ровно те поля, которые уезжают в `games/save`, и уже
// покрыт тестами. Здесь — правка: перестановка, добавление, удаление и
// проверки, без которых сохранение молча испортит реестр.
//
// ПОЧЕМУ ПРОВЕРКИ ЗДЕСЬ, А НЕ НА СЕРВЕРЕ. Сервер их тоже делает, и это
// правильно. Но реестр сохраняется целиком одним запросом: одна пустая
// строка в середине откатывает весь список, и человек узнаёт об этом
// после нажатия, потеряв двадцать минут правок. Проверка перед отправкой
// называет строку и поле.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Registry = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /* Идентификатор игры уезжает в пути на диске и в адресах API. Пробелы,
     слэши и кириллица там ломают всё молча, поэтому набор жёсткий. */
  const ID_RE = /^[a-z0-9][a-z0-9_-]*$/;

  const norm = (v) => String(v === undefined || v === null ? '' : v).trim();

  /** Переставляет игру. Список пересчитывает `order` целиком. */
  function move(list, gameId, dir) {
    const items = (list || []).slice();
    const i = items.findIndex((x) => norm(x.gameId) === norm(gameId));
    const j = i + dir;
    if (i < 0 || j < 0 || j >= items.length) return items;
    const t = items[i];
    items[i] = items[j];
    items[j] = t;
    return reorder(items);
  }

  /** Переносит игру на произвольное место — то же, что перетаскивание. */
  function moveTo(list, gameId, index) {
    const items = (list || []).slice();
    const i = items.findIndex((x) => norm(x.gameId) === norm(gameId));
    if (i < 0) return items;
    const to = Math.max(0, Math.min(items.length - 1, index));
    const [item] = items.splice(i, 1);
    items.splice(to, 0, item);
    return reorder(items);
  }

  /**
   * Проставляет `order` по месту в списке.
   *
   * Порядок значим до самого лаунчера: он запоминает игру номером в
   * массиве ответа. Перетащить строку и не пересчитать номера — значит
   * отдать игрокам прежний порядок при новом виде в панели.
   */
  function reorder(list) {
    return (list || []).map((item, i) => Object.assign({}, item, { order: i }));
  }

  /** Добавляет пустую строку — её ещё предстоит заполнить. */
  function add(list, gameId) {
    const items = (list || []).slice();
    items.push({ gameId: norm(gameId), title: '', exeRelativePath: '', iconUrl: '', order: items.length });
    return reorder(items);
  }

  /** Убирает строку из списка. Файлы игры это не трогает. */
  function remove(list, gameId) {
    return reorder((list || []).filter((x) => norm(x.gameId) !== norm(gameId)));
  }

  /** Меняет одно поле одной строки. */
  function patch(list, gameId, field, value) {
    return (list || []).map((x) =>
      norm(x.gameId) === norm(gameId) ? Object.assign({}, x, { [field]: value }) : x
    );
  }

  /**
   * Что не так перед сохранением.
   *
   * Возвращает список замечаний с именем строки и поля, а не одно
   * «проверьте данные»: реестр сохраняется целиком, и найти виноватую
   * строку глазами среди двадцати — отдельная работа.
   */
  function problems(list) {
    const items = list || [];
    const out = [];
    const seen = new Map();

    items.forEach((item, i) => {
      const id = norm(item.gameId);
      const where = id || 'строка ' + (i + 1);

      if (!id) {
        out.push({ gameId: '', field: 'gameId', message: where + ': не задан идентификатор' });
      } else if (!ID_RE.test(id)) {
        out.push({
          gameId: id,
          field: 'gameId',
          message: where + ': в идентификаторе можно только латиницу в нижнем регистре, цифры, дефис и подчёркивание',
        });
      } else if (seen.has(id)) {
        out.push({ gameId: id, field: 'gameId', message: where + ': такой идентификатор уже есть в строке ' + (seen.get(id) + 1) });
      } else {
        seen.set(id, i);
      }

      if (!norm(item.title)) {
        out.push({ gameId: id, field: 'title', message: where + ': пустое название — игрок увидит идентификатор' });
      }
      if (!norm(item.exeRelativePath)) {
        out.push({ gameId: id, field: 'exeRelativePath', message: where + ': не указан исполняемый файл — запускать будет нечего' });
      }
    });

    return out;
  }

  /** Можно ли сохранять. */
  const canSave = (list) => problems(list).length === 0;

  /**
   * Что подставить из экосистемы Thunderstore.
   *
   * Руками это копирование трёх значений на игру, и папка, вложенная
   * внутрь каталога установки, с первого раза угадывается неправильно.
   * Пустые поля ответа не затирают заполненные: подсказка не должна
   * стирать то, что человек уже поправил.
   */
  function applyEcosystem(item, eco) {
    const e = eco || {};
    const out = Object.assign({}, item);
    const take = (field, value) => {
      const v = norm(value);
      if (v && !norm(out[field])) out[field] = v;
    };
    take('exeRelativePath', e.exeName || (Array.isArray(e.exeNames) ? e.exeNames[0] : ''));
    take('steamAppId', e.steamAppId);
    take('steamFolder', e.steamFolder);
    take('title', e.displayName || e.title);
    return out;
  }

  return { ID_RE, move, moveTo, reorder, add, remove, patch, problems, canSave, applyEcosystem };
});
