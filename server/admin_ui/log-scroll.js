// Слежение за концом журнала.
//
// ПОЧЕМУ ЭТО НЕ ОДНА СТРОКА `box.scrollTop = box.scrollHeight`.
//
// Журнал сборки идёт до двадцати минут и набирает сотни строк. Всё это
// время смотрят в его конец: там видно, что работа идёт и на чём она
// сейчас. Значит, экран обязан ехать за последней строкой сам — иначе
// человек либо крутит колесо каждые несколько секунд, либо смотрит на
// неподвижное начало и не понимает, живо ли ещё.
//
// Но ровно посреди этого он отлистывает вверх: прочитать строку, где
// что-то пошло не так. Журнал, который в этот момент дёргает его обратно
// вниз, читать нельзя вовсе — это хуже, чем не ехать никуда. Поэтому
// правило такое: едем за концом, пока человек СМОТРИТ в конец, и
// перестаём, как только он ушёл вверх. Вернулся вниз — снова едем.
//
// Порог нужен, потому что «в конце» редко бывает ровно нулём: дробные
// высоты строк и масштаб браузера дают остаток в пару пикселей, и
// сравнение «впритык» выключало бы слежение само по себе.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /** Насколько далеко от конца всё ещё считается «в конце». */
  const SLACK = 24;

  /**
   * Смотрят ли сейчас в конец журнала.
   *
   * Принимает что угодно с тремя числами: настоящий элемент или разбор
   * его размеров. Второе нужно проверке — размеры настоящего элемента
   * зависят от шрифта и масштаба, и подделать их надёжнее.
   *
   * @param {{scrollTop: number, clientHeight: number, scrollHeight: number}} box Что прокручивается.
   * @param {number} [slack] Порог в пикселях.
   * @returns {boolean} true, если конец журнала на виду.
   */
  function logAtBottom(box, slack) {
    if (!box) return false;
    const gap = Number(box.scrollHeight) - Number(box.scrollTop) - Number(box.clientHeight);
    if (!Number.isFinite(gap)) return false;
    return gap <= (slack === undefined ? SLACK : slack);
  }

  /**
   * Ставит журнал в конец.
   *
   * @param {{scrollTop: number, scrollHeight: number}} box Что прокручивается.
   */
  function logToBottom(box) {
    if (!box) return;
    box.scrollTop = box.scrollHeight;
  }

  /**
   * Дописывает в журнал и едет за концом, если в конец смотрели.
   *
   * Дописывает, а не перерисовывает: перерисовка всего журнала на каждую
   * строку — это и потерянное место прокрутки, и лишняя работа, которая
   * растёт квадратом от числа строк. У сборки их сотни.
   *
   * @param {Element} box Журнал.
   * @param {string} html Разметка новой строки.
   */
  function logAppend(box, html) {
    if (!box) return;
    const follow = logAtBottom(box);
    box.insertAdjacentHTML('beforeend', html);
    if (follow) logToBottom(box);
  }

  return { logAtBottom: logAtBottom, logToBottom: logToBottom, logAppend: logAppend, LOG_SLACK: SLACK };
});
