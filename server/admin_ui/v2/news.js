// Редактор новостей: черновик, проверки, публикация.
//
// ЧТО ЗДЕСЬ РЕШАЕТСЯ. Заметку пишут в поле, а публикуют отдельным
// действием — и между этими двумя моментами теряется больше всего. В
// панели 1.0 набранный текст жил только в поле: закрытая вкладка,
// перезагрузка после ошибки сети, случайный переход по ссылке — и работа
// пропадала. Поэтому черновик пишется на диск браузера на каждой правке,
// а восстановление предлагается, а не случается само: подсунуть вчерашний
// текст поверх сегодняшнего — хуже, чем его потерять.
//
// ПУБЛИКАЦИЯ — ОТДЕЛЬНОЕ РЕШЕНИЕ. Сохранить и опубликовать это разные
// вещи: первое обратимо, второе видно всем игрокам на главном экране.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2News = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const KEY = 'ch2:news:draft:';
  const norm = (v) => String(v === undefined || v === null ? '' : v).trim();

  /** Пустая заметка — та, в которой нечего сохранять. */
  const isEmpty = (post) => !norm(post && post.title) && !norm(post && post.body);

  /**
   * Что не так с заметкой.
   *
   * Заголовок обязателен: без него в ленте лаунчера строка без имени, и
   * игрок не знает, открывать ли её. Текст — тоже: пустая заметка
   * выглядит как сбой загрузки, а не как заметка.
   */
  function problems(post) {
    const p = post || {};
    const out = [];
    if (!norm(p.title)) out.push({ field: 'title', message: 'Без заголовка заметка в ленте выглядит сбоем загрузки' });
    if (!norm(p.body)) out.push({ field: 'body', message: 'Пустую заметку игрок откроет и закроет' });
    return out;
  }

  const canSave = (post) => problems(post).length === 0;

  /** Что уедет на сервер. Пустая игра означает «новость лаунчера». */
  function payload(post) {
    const p = post || {};
    const out = {
      title: norm(p.title),
      body: String(p.body === undefined || p.body === null ? '' : p.body),
    };
    if (norm(p.id)) out.id = norm(p.id);
    if (norm(p.game)) out.game = norm(p.game);
    if (norm(p.coverUrl)) out.coverUrl = norm(p.coverUrl);
    return out;
  }

  /* ---------- Черновик ---------- */

  const draftKey = (id) => KEY + (norm(id) || 'new');

  /** Пишет черновик. Пустое не сохраняем: это не работа, а очищенное поле. */
  function saveDraft(storage, id, post) {
    if (!storage) return false;
    try {
      if (isEmpty(post)) {
        storage.removeItem(draftKey(id));
        return false;
      }
      storage.setItem(draftKey(id), JSON.stringify({ at: Date.now(), post: payload(post) }));
      return true;
    } catch {
      // Приватный режим и переполненное хранилище — не повод ронять редактор
      return false;
    }
  }

  /** Читает черновик. Мусор в хранилище — это его отсутствие. */
  function readDraft(storage, id) {
    if (!storage) return null;
    try {
      const raw = storage.getItem(draftKey(id));
      if (!raw) return null;
      const data = JSON.parse(raw);
      if (!data || !data.post || isEmpty(data.post)) return null;
      return data;
    } catch {
      return null;
    }
  }

  function dropDraft(storage, id) {
    if (!storage) return;
    try {
      storage.removeItem(draftKey(id));
    } catch {
      /* нечего чистить */
    }
  }

  /**
   * Предлагать ли восстановление.
   *
   * Только когда черновик отличается от того, что пришло с сервера:
   * иначе панель предлагала бы восстановить ровно то, что уже открыто, и
   * это предложение быстро перестают читать.
   */
  function restorable(draft, serverPost) {
    if (!draft || !draft.post) return false;
    const a = payload(draft.post);
    const b = payload(serverPost || {});
    return a.title !== b.title || a.body !== b.body;
  }

  /* ---------- Вложения ---------- */

  const IMAGE_RE = /\.(png|jpe?g|gif|webp|avif|svg)$/i;
  const isImage = (name) => IMAGE_RE.test(String(name || ''));

  /**
   * Приводит путь вложения к виду, пригодному для вставки.
   *
   * Обратные слэши и ведущие точки приезжают из проводника Windows и с
   * сервера не открываются, а двойные слэши превращают адрес в чужой
   * хост.
   */
  function normalizePath(p) {
    return String(p || '')
      .replace(/\\/g, '/')
      .replace(/^\.+\//, '')
      .replace(/\/{2,}/g, '/')
      .replace(/^\/+/, '');
  }

  /** Markdown для вставки вложения в текст. */
  function insertMarkup(path, alt) {
    const clean = normalizePath(path);
    const name = alt || clean.split('/').pop() || 'вложение';
    return isImage(clean) ? '![' + name + '](' + clean + ')' : '[' + name + '](' + clean + ')';
  }

  /** Вставка в позицию курсора — с сохранением того, что уже набрано. */
  function insertAt(text, position, markup) {
    const s = String(text === undefined || text === null ? '' : text);
    const i = Math.max(0, Math.min(s.length, Number(position) || 0));
    return s.slice(0, i) + markup + s.slice(i);
  }

  return {
    KEY, isEmpty, problems, canSave, payload,
    draftKey, saveDraft, readDraft, dropDraft, restorable,
    isImage, normalizePath, insertMarkup, insertAt,
  };
});
