// Форматирование чисел, размеров и дат для панели.
//
// Вынесено отдельным UMD-модулем по той же причине, что и модули версии
// 1.0 (см. шапку ndjson.js): только require()-имый код c8 связывает с
// исходником построчно, а функции, объявленные внутри admin.js, дают ноль
// покрытия.
//
// ГЛАВНОЕ ПРАВИЛО ЗДЕСЬ — РУССКАЯ ЗАПИСЬ ЧИСЛА. `toFixed` даёт точку, и
// по всей панели 1.0 шли «8.3 ГБ» и «92.4 МБ/с»: английская запись в
// русском интерфейсе. Разделитель ставится один раз и в одном месте.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Format = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const NBSP = '\u00a0';

  /** Дробное число с запятой вместо точки. */
  function dec(n, digits = 1) {
    if (!Number.isFinite(n)) return '—';
    return n.toFixed(digits).replace('.', ',');
  }

  /**
   * Размер в байтах человеческой строкой.
   *
   * Десятая доля показывается до сотни единиц, а не до десятка: на этой
   * же функции печатается скорость, и «10,5 МБ/с» округлённое до «11 МБ/с»
   * теряет ровно ту точность, ради которой скорость и смотрят.
   *
   * Целое число не получает дописанный ноль: «1,0 КБ» читается как сбой
   * форматирования, а не как единица.
   */
  function bytes(n) {
    const v = Number(n);
    if (!Number.isFinite(v) || v < 0) return '—';
    const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
    let i = 0;
    let x = v;
    while (x >= 1024 && i < units.length - 1) {
      x /= 1024;
      i++;
    }
    let num;
    if (i === 0) num = String(Math.round(x));
    else if (x < 100) num = dec(x).replace(/,0$/, '');
    else num = String(Math.round(x));
    return num + NBSP + units[i];
  }

  /**
   * Русское склонение по числу: plural(5, 'файл', 'файла', 'файлов').
   * Отдельная функция, а не «шт.»: «1 файлов» в интерфейсе читается как
   * недоделка, а такие строки в панели на каждом шагу.
   */
  function plural(n, one, few, many) {
    const abs = Math.abs(Math.trunc(n));
    const n10 = abs % 10;
    const n100 = abs % 100;
    if (n10 === 1 && n100 !== 11) return one;
    if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return few;
    return many;
  }

  /** Число со словом: «3 файла». */
  const count = (n, one, few, many) => `${n}${NBSP}${plural(n, one, few, many)}`;

  /** Доля в процентах с запятой и неразрывным пробелом перед знаком. */
  function percent(part, whole, digits = 1) {
    if (!Number.isFinite(part) || !Number.isFinite(whole) || whole === 0) return '—';
    return dec((part / whole) * 100, digits) + NBSP + '%';
  }

  /** Дата вида 04.09.2026. Пустое значение отдаёт прочерк, а не «Invalid Date». */
  function date(v) {
    if (v === null || v === undefined || v === '') return '—';
    const d = v instanceof Date ? v : new Date(v);
    if (Number.isNaN(d.getTime())) return '—';
    const p = (x) => String(x).padStart(2, '0');
    return `${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
  }

  /**
   * Дата, если её удаётся прочитать, иначе — как пришла.
   *
   * Сервер отдаёт день то полной датой, то коротким «04.09». Гнать
   * второе через разбор значит показать прочерк там, где день известен.
   */
  function dateLoose(v) {
    if (v instanceof Date) return date(v);
    const s2 = String(v === undefined || v === null ? '' : v).trim();
    if (!s2) return '';

    /* Разбираем только то, что заведомо дата: `new Date('04.09')` в JS
       даёт 9 апреля 2001 года — не ошибку, а другую дату, и подпись оси
       врала бы молча. */
    return /^\d{4}-\d{2}-\d{2}/.test(s2) ? date(s2) : s2;
  }

  /** Дата со временем: 04.09.2026 03:12. */
  function dateTime(v) {
    // Пустое — это не эпоха: `new Date(null)` даёт 01.01.1970, и раздел
    // показывал бы дату там, где даты нет
    if (v === null || v === undefined || v === '') return '—';
    const d = v instanceof Date ? v : new Date(v);
    if (Number.isNaN(d.getTime())) return '—';
    const p = (x) => String(x).padStart(2, '0');
    return `${date(d)} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  /**
   * Зона, в которой показано время.
   *
   * Сервер хранит всё в UTC, а показывается местное. Время без подписи
   * рядом с серверными данными читается как серверное — и назначенные
   * работы уезжают на три часа. Подписываем смещением браузера, а не
   * «МСК»: подпись обязана быть верной и там, куда человек уехал.
   */
  function zone(d) {
    /* Принимаем и Date, и что угодно со смещением: подпись зоны надо
       уметь проверить, не переводя машину в чужой часовой пояс. */
    const src = d && typeof d.getTimezoneOffset === 'function' ? d : new Date();
    const off = -src.getTimezoneOffset();
    if (off === 0) return 'UTC';
    const sign = off > 0 ? '+' : '−';
    const h = Math.floor(Math.abs(off) / 60);
    const m = Math.abs(off) % 60;
    return 'UTC' + sign + h + (m ? ':' + String(m).padStart(2, '0') : '');
  }

  /** Дата со временем и подписанной зоной: «04.09.2026 03:12 UTC+3». */
  function dateTimeZoned(v) {
    if (v === null || v === undefined || v === '') return '—';
    const d = v instanceof Date ? v : new Date(v);
    if (Number.isNaN(d.getTime())) return '—';
    return dateTime(d) + ' ' + zone(d);
  }

  /**
   * Оставшееся время. Порт HomeFormat.FormatEta из лаунчера: игрок и
   * администратор должны читать одинаковые строки, иначе в переписке о
   * поломке они говорят на разных языках.
   */
  function eta(seconds) {
    if (!Number.isFinite(seconds)) return '—';
    const total = Math.max(0, Math.ceil(seconds));
    const d = Math.floor(total / 86400);
    const h = Math.floor((total % 86400) / 3600);
    const m = Math.floor((total % 3600) / 60);
    if (d >= 1) return h > 0 ? `${d}${NBSP}${plural(d, 'день', 'дня', 'дней')} ${h}${NBSP}ч` : `${d}${NBSP}${plural(d, 'день', 'дня', 'дней')}`;
    if (total >= 3600) return `${h}${NBSP}ч ${String(m).padStart(2, '0')}${NBSP}мин`;
    if (total >= 60) return `${Math.ceil(total / 60)}${NBSP}мин`;
    return `${total}${NBSP}с`;
  }

  /** Скорость: «10,5 МБ/с». */
  const speed = (bytesPerSec) => `${bytes(bytesPerSec)}/с`;

  return { NBSP, dec, bytes, plural, count, percent, date, dateLoose, dateTime, zone, dateTimeZoned, eta, speed };
});
