// Подписи колонок у ячеек таблиц.
//
// ЗАЧЕМ. На телефоне таблица в шесть колонок не помещается никак: либо
// листается вбок внутри карточки и режет текст в столбик по три слова,
// либо сжимается до нечитаемого. Поэтому на узком экране строка таблицы
// рисуется карточкой: значение под значением, и у каждого своя подпись.
// Подпись берётся из заголовка колонки — её CSS достаёт через
// `attr(data-label)`, а проставляет этот модуль.
//
// ПОЧЕМУ НЕ В КАЖДОЙ ТАБЛИЦЕ РУКАМИ. Таблиц в панели полтора десятка, и
// рисуют их разные места: разделы, листы, вкладки. Подпись, которую
// забыли в одной из них, выглядит как значение без смысла. Общий проход
// по готовой разметке не забывает ни одну, в том числе будущие.
//
// Ячейка без заголовка — колонка кнопок — подписи не получает: «Действия»
// над двумя кнопками только занимает строку.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /**
   * Проставляет ячейкам таблицы подписи из её заголовка.
   *
   * Колонку считаем с учётом `colspan` и у заголовка, и у ячейки: иначе
   * одна объединённая ячейка сдвигала бы подписи всей строки.
   *
   * @param {HTMLTableElement} table Таблица.
   * @returns {number} Сколько ячеек подписано.
   */
  function labelTable(table) {
    const headRow = table.tHead && table.tHead.rows[0];
    if (!headRow) return 0;

    const titles = [];
    for (const th of headRow.cells) {
      const span = Math.max(1, Number(th.colSpan) || 1);
      const title = th.textContent.replace(/\s+/g, ' ').trim();
      for (let i = 0; i < span; i++) titles.push(title);
    }

    let n = 0;
    for (const body of table.tBodies) {
      for (const tr of body.rows) {
        let col = 0;
        for (const td of tr.cells) {
          const title = titles[col] || '';
          if (title) {
            if (td.getAttribute('data-label') !== title) td.setAttribute('data-label', title);
            n++;
          } else if (td.hasAttribute('data-label')) {
            td.removeAttribute('data-label');
          }
          col += Math.max(1, Number(td.colSpan) || 1);
        }
      }
    }
    table.classList.add('labelled');
    return n;
  }

  /** Все таблицы внутри узла, включая сам узел. */
  function labelTables(node) {
    if (!node || typeof node.querySelectorAll !== 'function') return 0;
    let n = 0;
    if (node.tagName === 'TABLE') n += labelTable(node);
    for (const t of node.querySelectorAll('table')) n += labelTable(t);
    return n;
  }

  /**
   * Следит за страницей и подписывает каждую новую таблицу.
   *
   * Разделы и листы перерисовываются через `innerHTML`, поэтому следим за
   * вставками, а не зовём проход из каждого места отрисовки. Свои правки
   * атрибутов наблюдатель не видит: он слушает только `childList`.
   *
   * @param {Node} root Корень страницы.
   * @param {typeof MutationObserver} [Observer] Для проверки.
   */
  function watchTables(root, Observer) {
    const O = Observer || globalThis.MutationObserver || null;
    labelTables(root);
    if (!O) return null;
    const obs = new O((records) => {
      const seen = new Set();
      for (const r of records) {
        for (const added of r.addedNodes) {
          const table = added.nodeType === 1 && added.closest ? added.closest('table') : null;
          if (table) seen.add(table);
          else labelTables(added);
        }
        const host = r.target && r.target.closest ? r.target.closest('table') : null;
        if (host) seen.add(host);
      }
      seen.forEach(labelTable);
    });
    obs.observe(root, { childList: true, subtree: true });
    return obs;
  }

  return { labelTable, labelTables, watchTables };
});
