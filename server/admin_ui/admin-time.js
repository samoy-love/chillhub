// ВРЕМЯ В АДМИНКЕ — ОДНО, И ЭТО МОСКВА.
//
// Сервер пишет все отметки в UTC, и раньше каждое место панели показывало их
// по-своему: список сборок — в зоне браузера и без секунд, обслуживание — в
// зоне браузера длинной строкой, события метрик и список модпаков вовсе
// печатали UTC как есть, отрезав «T» и «Z». Со стороны все четыре выглядели
// одинаково — просто дата и время, — и отличить московское от UTC было нельзя
// ничем, кроме как знать наизусть, какая из вкладок как считает. Ровно на этом
// «собран 2026-08-31 00:43» читалось как местное и расходилось с часами на три
// часа.
//
// Здесь одна функция на всю панель. Зона прибита к Москве, а не берётся из
// браузера: оператор смотрит на прод, который живёт по Москве, и договариваться
// о времени с самим собой в дороге — лишняя работа.
//
// Суффикс «МСК» не украшение. Без него московское время неотличимо от UTC, а
// именно эта неразличимость и привела сюда.
//
// Отдельным CommonJS-модулем по той же причине, что ui-status.js и
// rate-estimator.js: только require()-имый код даёт c8 построчное покрытие.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  const MOSCOW_TZ = 'Europe/Moscow';

  // formatMoscow: отметка времени сервера -> «2026-08-31 00:43:27 МСК».
  //
  // Пустое значение остаётся пустым, а неразобранное возвращается как есть:
  // «Invalid Date» в таблице — это потерянная информация вместо непонятной, и
  // чинить по ней нечего.
  function formatMoscow(value) {
    if (value === null || value === undefined) return '';
    // Число — это миллисекунды: так зовут отсюда Date.now() отметка журнала и
    // метка черновика. Через строку оно бы не прошло: new Date('1755455729000')
    // — это Invalid Date, а не момент времени.
    if (typeof value === 'number' || value instanceof Date) {
      const t = new Date(value);
      return isNaN(t.getTime()) ? '' : parts(t);
    }

    const s = String(value).trim();
    if (!s) return '';
    const d = new Date(s);
    if (isNaN(d.getTime())) return s;
    return parts(d);
  }

  // parts раскладывает момент времени в московскую дату-время строкой.
  function parts(d) {
    const p = {};
    new Intl.DateTimeFormat('ru-RU', {
      timeZone: MOSCOW_TZ,
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    }).formatToParts(d).forEach(function (x) { p[x.type] = x.value; });

    return p.year + '-' + p.month + '-' + p.day + ' '
      + p.hour + ':' + p.minute + ':' + p.second + ' МСК';
  }

  // formatMoscowTime — только часы и минуты с секундами, без даты. Для отметок
  // вроде «черновик сохранён», где день и так сегодняшний.
  function formatMoscowTime(value) {
    const full = formatMoscow(value);
    if (!full) return '';
    const bits = full.split(' ');
    return bits.length === 3 ? bits[1] + ' ' + bits[2] : full;
  }

  // formatMoscowClock — часы:минуты:секунды по Москве и БЕЗ суффикса. Ровно
  // одно применение: журнал на странице, где таких отметок двести подряд и
  // «МСК» в каждой строке — шум. Зона там та же, что и везде, а сомневаться
  // в ней внутри одного столбца одинаковых строк не приходится.
  function formatMoscowClock(value) {
    const time = formatMoscowTime(value);
    return time ? time.replace(' МСК', '') : '';
  }

  return { MOSCOW_TZ, formatMoscow, formatMoscowTime, formatMoscowClock };
});
