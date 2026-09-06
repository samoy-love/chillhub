/* Слежение за концом журнала.
   ------------------------------------------------------------------
   Сборка модпака идёт до двадцати минут и набирает сотни строк. Всё это
   время смотрят в её конец, и уезжать туда экран обязан сам. Но ровно
   посреди этого человек отлистывает вверх — прочитать строку, где что-то
   пошло не так, — и журнал, который в этот момент дёргает его обратно
   вниз, читать нельзя вовсе.

   Размеры здесь подделаны намеренно: у настоящего элемента они зависят
   от шрифта и масштаба браузера, и проверка на них рассказывала бы про
   окружение, а не про правило. */

const test = require('node:test');
const assert = require('node:assert');

const L = require('../../server/admin_ui/log-scroll.js');

/** Журнал заданных размеров: только три числа, которые и решают. */
const box = (scrollTop, clientHeight, scrollHeight) => ({
  scrollTop: scrollTop,
  clientHeight: clientHeight,
  scrollHeight: scrollHeight,
});

test('конец журнала на виду — когда прокрутили до упора', () => {
  assert.strictEqual(L.logAtBottom(box(600, 400, 1000)), true);
});

test('пара пикселей до упора — это всё ещё конец', () => {
  // Дробные высоты строк и масштаб браузера оставляют остаток, и
  // сравнение «впритык» выключало бы слежение само по себе
  assert.strictEqual(L.logAtBottom(box(598, 400, 1000)), true);
});

test('отлистали вверх — за концом больше не едем', () => {
  assert.strictEqual(L.logAtBottom(box(100, 400, 1000)), false);
});

test('вернулись вниз — снова едем', () => {
  const b = box(100, 400, 1000);
  assert.strictEqual(L.logAtBottom(b), false);
  b.scrollTop = 600;
  assert.strictEqual(L.logAtBottom(b), true);
});

test('журнал короче окна — это конец, а не начало', () => {
  // Пока строк меньше, чем помещается, прокручивать нечего, и слежение
  // обязано остаться включённым: иначе первая же строка его выключит
  assert.strictEqual(L.logAtBottom(box(0, 400, 120)), true);
});

test('журнала нет — ехать некуда, и это не падение', () => {
  assert.strictEqual(L.logAtBottom(null), false);
  assert.doesNotThrow(() => L.logToBottom(null));
  assert.doesNotThrow(() => L.logAppend(null, '<div></div>'));
});

/* ---------- Дописывание ---------- */

/** Журнал, который умеет принять строку и посчитать свою высоту. */
function fakeLog(clientHeight) {
  return {
    html: '',
    scrollTop: 0,
    clientHeight: clientHeight,
    get scrollHeight() {
      return this.html.length;
    },
    insertAdjacentHTML(_where, html) {
      this.html += html;
    },
  };
}

test('строка дописывается, а не перерисовывает журнал заново', () => {
  // Перерисовка всего журнала на каждую строку теряет место прокрутки, а
  // работы в ней тем больше, чем длиннее журнал
  const log = fakeLog(10);
  L.logAppend(log, 'первая');
  L.logAppend(log, 'вторая');
  assert.strictEqual(log.html, 'перваявторая');
});

test('пока смотрят в конец, журнал едет за последней строкой', () => {
  const log = fakeLog(4);
  log.html = '12345678';
  log.scrollTop = 4; // конец на виду
  L.logAppend(log, 'ещё');
  assert.strictEqual(log.scrollTop, log.scrollHeight, 'экран не поехал за строкой');
});

test('отлистали вверх — новая строка экран не дёргает', () => {
  const log = fakeLog(4);
  log.html = '1234567890123456789012345678901234567890';
  log.scrollTop = 0; // человек читает начало
  L.logAppend(log, 'ещё');
  assert.strictEqual(log.scrollTop, 0, 'журнал утащил читающего вниз');
});
