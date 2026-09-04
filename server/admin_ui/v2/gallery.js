// Галерея игры: пути, обложка, проверки перед действием.
//
// ЧТО ЗДЕСЬ РЕШАЕТСЯ. Галерея — единственное место панели, где человек
// ходит по дереву папок и переименовывает файлы. Ошибиться тут легко и
// дорого: путь с `..` уводит операцию из каталога игры, переименование в
// занятое имя молча затирает чужой файл, а удаление обложки оставляет
// витрину игры с градиентом — и заметит это уже игрок.
//
// Сервер эти же проверки делает и он последняя инстанция. Но отказ после
// нажатия — это потерянное действие и вопрос «а что не так»; проверка
// здесь называет причину до того, как что-то произойдёт.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Gallery = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const IMAGE_RE = /\.(png|jpe?g|gif|webp|avif)$/i;
  const isImage = (name) => IMAGE_RE.test(String(name || ''));

  /**
   * Приводит путь внутри галереи к безопасному виду.
   *
   * Возвращает пустую строку для всего, что пытается выйти наружу: `..`
   * в любом виде, ведущий слэш, обратные слэши из проводника. Молча
   * «чинить» такой путь нельзя — он мог быть введён по ошибке, и тихая
   * подмена сделает не то, чего ждали.
   */
  function safePath(p) {
    const raw = String(p || '').replace(/\\/g, '/').replace(/\/{2,}/g, '/');
    if (!raw) return '';
    if (raw.startsWith('/')) return '';
    const parts = raw.split('/');
    for (const part of parts) {
      if (part === '..' || part === '.') return '';
    }
    return parts.filter(Boolean).join('/');
  }

  /** Разбирает путь на крошки для навигации. */
  function crumbs(path) {
    const clean = safePath(path);
    const out = [{ name: 'Галерея', path: '' }];
    if (!clean) return out;
    let acc = '';
    for (const part of clean.split('/')) {
      acc = acc ? acc + '/' + part : part;
      out.push({ name: part, path: acc });
    }
    return out;
  }

  /** На уровень вверх. С верхнего уровня — никуда. */
  function parent(path) {
    const clean = safePath(path);
    if (!clean.includes('/')) return '';
    return clean.slice(0, clean.lastIndexOf('/'));
  }

  /**
   * Что не так с новым именем.
   *
   * Занятое имя проверяется без учёта регистра: файловая система Windows
   * его не различает, и «Cover.png» затрёт «cover.png» молча.
   */
  function nameProblem(name, siblings) {
    const clean = String(name || '').trim();
    if (!clean) return 'Пустое имя';
    if (/[\\/]/.test(clean)) return 'В имени нельзя слэши — это путь, а не имя';
    if (clean === '.' || clean === '..') return 'Такое имя означает папку, а не файл';
    if (/[<>:"|?*]/.test(clean)) return 'В имени есть символы, запрещённые в файловой системе';

    const taken = (siblings || []).some((s) => String(s || '').toLowerCase() === clean.toLowerCase());
    if (taken) return 'Такое имя здесь уже занято';
    return '';
  }

  const canRename = (name, siblings) => nameProblem(name, siblings) === '';

  /**
   * Можно ли сделать файл обложкой.
   *
   * Обложкой становится только картинка: витрина игры покажет её на весь
   * блок, и PDF там будет битой ссылкой.
   */
  function coverProblem(file) {
    const f = file || {};
    if (f.dir) return 'Папка не может быть обложкой';
    if (!isImage(f.name)) return 'Обложкой может быть только картинка';
    return '';
  }

  /**
   * Что случится при удалении.
   *
   * Отдельно предупреждаем про обложку: без неё витрина игры останется с
   * градиентом, и это заметит уже игрок, а не тот, кто удалял.
   */
  function deleteWarning(file, cover) {
    const f = file || {};
    if (f.dir) return 'Папка удалится со всем, что в ней лежит.';
    if (f.name && cover && String(f.name) === String(cover)) {
      return 'Это обложка игры. После удаления витрина останется с градиентом, пока не выберете новую.';
    }
    return '';
  }

  /**
   * Сортировка содержимого: папки сверху, дальше по имени.
   *
   * Сортировка по имени — русская: обычный `sort` ставит «Ящик» перед
   * «арка», потому что сравнивает коды символов.
   */
  function sortEntries(entries) {
    return (entries || []).slice().sort((a, b) => {
      if (Boolean(a.dir) !== Boolean(b.dir)) return a.dir ? -1 : 1;
      return String(a.name || '').localeCompare(String(b.name || ''), 'ru');
    });
  }

  /** Полный путь элемента внутри галереи. */
  const entryPath = (dir, name) => {
    const d = safePath(dir);
    const n = String(name || '').trim();
    return d ? d + '/' + n : n;
  };

  return {
    isImage, safePath, crumbs, parent,
    nameProblem, canRename, coverProblem, deleteWarning,
    sortEntries, entryPath,
  };
});
