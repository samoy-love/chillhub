// Новость: адресация, проверки, черновик, вложения.
//
// КАК НОВОСТЬ УСТРОЕНА НА СЕРВЕРЕ. Заметка — это один markdown-файл.
// Заголовок отдельным полем не хранится: сервер берёт его первой
// строкой вида `# Заголовок` (`ExtractMeta` в news/markdown.go), оттуда
// же вытягивает краткое описание и, если обложка не задана руками,
// первую картинку. Отдельно от текста лежат только `published` и
// `coverUrl`.
//
// Адресуется заметка тройкой, а не одним номером: `scope` — «launcher»
// или «game», `gameId` нужен только второму, `slug` — имя файла. Панель
// 1.0 знала это, потому что писала запросы руками; здесь это записано
// один раз и проверено.
//
// ПОЧЕМУ ПРОВЕРКИ ЗДЕСЬ, А НЕ ТОЛЬКО НА СЕРВЕРЕ. Сервер откажет, но уже
// после нажатия — а заметку набирают минутами, и «invalid slug» в ответ
// на сохранение не говорит, что именно поправить.
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
  const text = (v) => String(v === undefined || v === null ? '' : v);

  /* ---------- Адрес заметки ---------- */

  const LAUNCHER = 'launcher';
  const GAME = 'game';

  /**
   * Тройка, которой заметка называется на сервере.
   *
   * Пустая игра означает новость про лаунчер: у неё своя лента, и
   * подставлять туда идентификатор игры нельзя.
   */
  function address(post) {
    const p = post || {};
    const gameId = norm(p.gameId);
    return {
      scope: gameId ? GAME : LAUNCHER,
      gameId: gameId,
      slug: norm(p.slug),
    };
  }

  /**
   * Правило имени файла — то же, что `IsSafeNewsSlug` на сервере.
   *
   * Имя становится частью адреса статьи и путём к файлу, поэтому в нём
   * только буквы, цифры, дефис, подчёркивание и точка; ведущие точка и
   * дефис запрещены, как и две точки подряд.
   */
  const SLUG_RE = /^[^.-][\p{L}\p{N}._-]*$/u;
  function slugProblem(slug) {
    const s = norm(slug);
    if (!s) return 'Без имени заметку некуда положить';
    if (s.length > 128) return 'Имя длиннее 128 символов сервер не примет';
    if (s.includes('..')) return 'Две точки подряд в имени запрещены';
    if (!SLUG_RE.test(s)) return 'В имени только буквы, цифры, дефис, подчёркивание и точка';
    return '';
  }

  /**
   * Имя файла из заголовка.
   *
   * Предлагается, а не навязывается: имя попадает в адрес статьи и
   * потом не меняется, а заголовок правят свободно.
   */
  function suggestSlug(title) {
    const s = norm(title)
      .toLowerCase()
      .replace(/[^\p{L}\p{N}]+/gu, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 128);
    return s.replace(/^[.-]+/, '');
  }

  /* ---------- Текст ---------- */

  /** Заголовок так, как его прочтёт сервер: первая строка вида `# ...`. */
  function titleOf(markdown) {
    for (const line of text(markdown).split('\n')) {
      const s = line.trim();
      if (s.startsWith('# ')) return s.slice(2).trim();
    }
    return '';
  }

  /** Есть ли в тексте что-нибудь, кроме заголовка. */
  function bodyOf(markdown) {
    return text(markdown)
      .split('\n')
      .filter((l) => !l.trim().startsWith('# '))
      .join('\n')
      .trim();
  }

  /** Пустая заметка — та, в которой нечего сохранять. */
  const isEmpty = (post) => !text(post && post.markdown).trim() && !norm(post && post.slug);

  /**
   * Что не так с заметкой.
   *
   * Заголовок обязателен и обязан быть первой строкой с решёткой: без
   * него сервер положит в ленту строку без имени, и игрок не поймёт,
   * открывать ли её. Текст — тоже: заметка из одного заголовка
   * выглядит как сбой загрузки.
   */
  function problems(post) {
    const p = post || {};
    const out = [];

    const slug = slugProblem(p.slug);
    if (slug) out.push({ field: 'slug', text: slug });

    if (!titleOf(p.markdown)) {
      out.push({ field: 'markdown', text: 'Первой строкой нужен заголовок: «# Название заметки»' });
    } else if (!bodyOf(p.markdown)) {
      out.push({ field: 'markdown', text: 'Заметку из одного заголовка игрок откроет и закроет' });
    }
    return out;
  }

  const canSave = (post) => problems(post).length === 0;

  /**
   * Что уедет на сервер.
   *
   * Имена полей — контракт `news/save`: `scope`, `gameId`, `slug`,
   * `markdown`, `coverUrl`, `published`. Заголовка среди них нет.
   */
  function payload(post) {
    const p = post || {};
    const a = address(p);
    const out = {
      scope: a.scope,
      slug: a.slug,
      markdown: text(p.markdown),
      published: p.published ? 'true' : 'false',
    };
    if (a.gameId) out.gameId = a.gameId;
    if (norm(p.coverUrl)) out.coverUrl = norm(p.coverUrl);
    return out;
  }

  /* ---------- Черновик ---------- */

  /* Ключ включает адрес целиком: заметка про игру и заметка лаунчера с
     одинаковым именем — разные заметки, и путать их черновики нельзя. */
  function draftKey(post) {
    const a = address(post);
    return KEY + a.scope + ':' + (a.gameId || '-') + ':' + (a.slug || 'new');
  }

  /** Пишет черновик. Пустое не сохраняем: это не работа, а очищенное поле. */
  function saveDraft(storage, post) {
    if (!storage) return false;
    try {
      if (isEmpty(post)) {
        storage.removeItem(draftKey(post));
        return false;
      }
      storage.setItem(
        draftKey(post),
        JSON.stringify({
          at: Date.now(),
          post: { slug: norm(post.slug), gameId: norm(post.gameId), markdown: text(post.markdown), coverUrl: norm(post.coverUrl) },
        })
      );
      return true;
    } catch {
      // Хранилище может быть закрыто настройками браузера — это не повод
      // ронять редактор: черновик приятен, но не обязателен
      return false;
    }
  }

  /** Читает черновик. Мусор в хранилище — это его отсутствие. */
  function readDraft(storage, post) {
    if (!storage) return null;
    try {
      const raw = storage.getItem(draftKey(post));
      if (!raw) return null;
      const d = JSON.parse(raw);
      return d && d.post ? d : null;
    } catch {
      return null;
    }
  }

  function dropDraft(storage, post) {
    if (!storage) return;
    try {
      storage.removeItem(draftKey(post));
    } catch {
      // см. saveDraft
    }
  }

  /**
   * Предлагать ли восстановление.
   *
   * Только когда черновик отличается от того, что пришло с сервера:
   * иначе панель предлагала бы восстановить ровно то, что уже открыто,
   * и это предложение быстро перестают читать.
   */
  function restorable(draft, serverPost) {
    if (!draft || !draft.post) return false;
    return text(draft.post.markdown).trim() !== text(serverPost && serverPost.markdown).trim();
  }

  /* ---------- Вложения ---------- */

  const IMAGE_RE = /\.(png|jpe?g|gif|webp|avif|svg)$/i;
  const isImage = (name) => IMAGE_RE.test(String(name || ''));

  /**
   * Путь вложения так, как его увидит игрок.
   *
   * Вложения раздаются с `/news/assets/`, и в текст должен попасть
   * именно этот адрес, а не путь внутри админки.
   */
  function normalizePath(p) {
    const clean = String(p || '')
      .replace(/\\/g, '/')
      .replace(/\/{2,}/g, '/')
      .replace(/^\/+/, '');
    if (!clean || clean.split('/').some((part) => part === '..' || part === '.')) return '';
    return '/news/assets/' + clean;
  }

  /** Markdown для вставки вложения в текст. */
  function insertMarkup(path, alt) {
    const url = normalizePath(path);
    if (!url) return '';
    const name = String(alt || path).split('/').pop();
    return isImage(path) ? '![' + name + '](' + url + ')' : '[' + name + '](' + url + ')';
  }

  /** Вставка в позицию курсора — с сохранением того, что уже набрано. */
  function insertAt(body, position, markup) {
    const s = text(body);
    const at = Math.max(0, Math.min(s.length, Number(position) || 0));
    return s.slice(0, at) + markup + s.slice(at);
  }

  return {
    LAUNCHER, GAME, SLUG_RE,
    address, slugProblem, suggestSlug,
    titleOf, bodyOf, isEmpty, problems, canSave, payload,
    draftKey, saveDraft, readDraft, dropDraft, restorable,
    isImage, normalizePath, insertMarkup, insertAt,
  };
});
