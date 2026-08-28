// Сколько игр уедет на сервер иначе, чем лежит сейчас.
//
// ЧТО ИМЕННО СЧИТАЕМ. Перетаскивание одной строки с пятого места на первое
// меняет поле order у пяти игр: для человека это одно действие, для реестра —
// пять изменившихся записей. Обе цифры честные, и выбрана вторая: на кнопке
// «Сохранить» стоит то, чем пользователь рискует, а не то, сколько раз он
// что-то сделал.
//
// ЧИСЛО ОБЯЗАНО ВОЗВРАЩАТЬСЯ К НУЛЮ. Напечатал букву в названии и стёр её —
// правок снова нет. Прежний флаг «стало грязно» в такой ситуации оставался
// поднятым, и этого никто не замечал: кнопка просто была активна. А «Сохранить
// (1)» при полном совпадении со снимком — уже видимое враньё, поэтому здесь
// сравнение со снимком, а не счётчик нажатий.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  // Поля, по которым сравниваются записи, — ровно те, что уходят в
  // /admin/games/save. Сравнивать по чему-то ещё значило бы считать правкой то,
  // что на сервер не поедет, или наоборот.
  const FIELDS = ['gameId', 'title', 'iconUrl', 'exeRelativePath', 'order', 'pinned', 'unpublished'];

  function key(item) {
    return String((item && item.gameId) || '').trim().toLowerCase();
  }

  function sameEntry(a, b) {
    for (const f of FIELDS) {
      // Приводим к строке: order приходит числом, pinned — булевым, и
      // строгое сравнение разных типов дало бы вечную «правку» на ровном месте.
      if (String(a[f] === undefined || a[f] === null ? '' : a[f])
        !== String(b[f] === undefined || b[f] === null ? '' : b[f])) {
        return false;
      }
    }
    return true;
  }

  // countRegistryChanges сравнивает текущий список со снимком.
  //
  // Возвращает разбивку, а не одно число: подсказка на кнопке называет вещи
  // своими именами («2 изменены, 1 добавлена»), и склеивать их обратно из
  // суммы было бы нечем.
  function countRegistryChanges(snapshot, current) {
    const was = Array.isArray(snapshot) ? snapshot : [];
    const now = Array.isArray(current) ? current : [];

    const wasByKey = new Map();
    was.forEach(function (it) { wasByKey.set(key(it), it); });
    const nowKeys = new Set(now.map(key));

    let changed = 0;
    let added = 0;
    now.forEach(function (it) {
      const before = wasByKey.get(key(it));
      if (!before) added++;
      else if (!sameEntry(before, it)) changed++;
    });

    let removed = 0;
    was.forEach(function (it) { if (!nowKeys.has(key(it))) removed++; });

    return { changed: changed, added: added, removed: removed, total: changed + added + removed };
  }

  // describeSaveButton — надпись, доступность и подсказка кнопки «Сохранить».
  function describeSaveButton(diff) {
    const d = diff || { changed: 0, added: 0, removed: 0, total: 0 };
    if (d.total === 0) {
      return {
        enabled: false,
        label: 'Сохранено',
        title: 'Список игр совпадает с сохранённым — сохранять нечего',
      };
    }
    const parts = [];
    if (d.changed) parts.push(d.changed + ' ' + plural(d.changed, 'изменена', 'изменены', 'изменены'));
    if (d.added) parts.push(d.added + ' ' + plural(d.added, 'добавлена', 'добавлены', 'добавлены'));
    if (d.removed) parts.push(d.removed + ' ' + plural(d.removed, 'удалена', 'удалены', 'удалены'));
    return {
      enabled: true,
      label: 'Сохранить (' + d.total + ')',
      title: 'Уедет на сервер: ' + parts.join(', '),
    };
  }

  // plural выбирает форму по русским правилам: 1 игра, 2 игры, 5 игр.
  function plural(n, one, few, many) {
    const mod100 = n % 100;
    if (mod100 >= 11 && mod100 <= 14) return many;
    const mod10 = n % 10;
    if (mod10 === 1) return one;
    if (mod10 >= 2 && mod10 <= 4) return few;
    return many;
  }

  return { countRegistryChanges, describeSaveButton, plural };
});
