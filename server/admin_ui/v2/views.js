// Вид под длинные дела панели 2.0.
//
// ЧТО ЗДЕСЬ. Правила шести дел — загрузки сборки, сборки модпака,
// новости, галереи, порядка игр и подбора параметров — уже написаны и
// проверены по отдельности (`upload.js`, `build.js`, `news.js`,
// `gallery.js`, `registry.js`, `tuning.js`). Здесь то, что человек в
// это время видит.
//
// ПОЧЕМУ ОТДЕЛЬНЫМ ФАЙЛОМ. Каждое из этих дел идёт минутами и умеет
// закончиться на середине: связь рвётся, пакет пропадает с
// Thunderstore, имя оказывается занято. Показать такое правильно —
// работа не меньшая, чем сделать, и её надо проверять тестами, а не
// глазами. Функции здесь чистые: на входе состояние, на выходе разметка.
//
// В панели 1.0 то же самое лежало внутри обработчиков, и проверить
// «что увидит человек, если связь оборвалась на сороковом проценте»
// было нечем.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Views = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const esc = (s) =>
    String(s === undefined || s === null ? '' : s).replace(
      /[&<>"']/g,
      (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]
    );

  /* Форматирование берётся из общего модуля: числа, размеры и проценты
     в панели должны выглядеть одинаково везде, где их видно. */
  const F = () => (typeof window !== 'undefined' && window.CH2Format) || require('./format.js');
  const M = (name, given) => given || (typeof window !== 'undefined' && window[name]) || require('./' + name.slice(3).toLowerCase() + '.js');

  /* ---------- Оболочка длинного дела ---------- */

  /**
   * Лист: то, что открывается поверх раздела на время дела.
   *
   * Заголовок и подпись остаются на месте всё время, пока дело идёт:
   * человек, вернувшийся к экрану через десять минут, должен узнать по
   * ним, что вообще происходит, — а не по одной полоске прогресса.
   */
  function sheet(o) {
    const s = o || {};
    return (
      '<div class="sheet-back" data-sheet>' +
      '<section class="sheet" role="dialog" aria-modal="true" aria-label="' +
      esc(s.title) +
      '">' +
      '<header><div><h2>' +
      esc(s.title) +
      '</h2>' +
      (s.lede ? '<p>' + esc(s.lede) + '</p>' : '') +
      '</div><span class="push"></span>' +
      '<button class="btn btn--icon" type="button" data-sheet-close aria-label="Закрыть">✕</button></header>' +
      '<div class="sheet-body" data-sheet-body>' +
      (s.body || '') +
      '</div>' +
      (s.foot ? '<footer data-sheet-foot>' + s.foot + '</footer>' : '') +
      '</section></div>'
    );
  }

  /* ---------- Загрузка сборки ---------- */

  /**
   * Ход загрузки словами, а не только полоской.
   *
   * Отдельно называется докачка. Человек, залив половину и потеряв
   * связь, во второй раз видит «148 из 300 уже на сервере» с первой
   * секунды — иначе никак не понять, что заливка не началась заново.
   */
  function uploadStatus(st) {
    const s = st || {};
    const f = F();
    if (!s.phase || s.phase === 'idle') return { text: 'Файл ещё не выбран', tone: '' };
    if (s.phase === 'init') return { text: 'Договариваемся с сервером', tone: '' };
    if (s.phase === 'upload') {
      const head = s.resumed
        ? 'Докачка: ' + s.resumed + ' из ' + (s.total || 0) + ' кусков уже на сервере'
        : 'Заливка: ' + (s.done || 0) + ' из ' + (s.total || 0);
      return { text: head + ' · ' + f.percent(s.progress || 0, 1, 0), tone: '' };
    }
    if (s.phase === 'retry') return { text: 'Повторяем сорвавшиеся куски: ' + (s.count || 0), tone: 'warn' };
    if (s.phase === 'complete') return { text: 'Собираем файл на сервере', tone: '' };
    if (s.phase === 'process') return { text: 'Разбираем архив и считаем хеши', tone: '' };
    if (s.phase === 'done') return { text: 'Готово. Игрокам версия пока не ушла — это отдельное решение.', tone: 'ok' };
    if (s.phase === 'failed') return { text: 'Не вышло: ' + (s.message || 'сбой'), tone: 'bad' };
    if (s.phase === 'aborted') return { text: 'Отменено, недозалитое убрано с сервера', tone: 'warn' };
    return { text: '', tone: '' };
  }

  /**
   * Чем закончить экран загрузки.
   *
   * Пока куски летят, единственное осмысленное действие — отмена:
   * «Закрыть» на этом месте прочитали бы как «свернуть», а закрытие
   * листа загрузку прерывает.
   */
  function uploadButtons(st) {
    const phase = (st && st.phase) || 'idle';
    if (phase === 'idle') return [{ act: 'pick', title: 'Выбрать файл', accent: true }];
    if (phase === 'done') return [{ act: 'close', title: 'Закрыть', accent: true }];
    if (phase === 'failed' || phase === 'aborted') {
      return [
        { act: 'retry', title: 'Повторить', accent: true },
        { act: 'close', title: 'Закрыть', accent: false },
      ];
    }
    return [{ act: 'abort', title: 'Отменить загрузку', accent: false, danger: true }];
  }

  /**
   * Карточка загрузки целиком.
   *
   * Подобранные размер куска и число потоков показываются до нажатия, а
   * не после: это единственное, что здесь можно поправить руками, и
   * узнавать о них постфактум незачем.
   */
  function uploadCard(st) {
    const s = st || {};
    const f = F();
    const status = uploadStatus(s);
    const pct = Math.round(Number(s.progress || 0) * 100);
    const tone = status.tone === 'bad' ? 'bad' : status.tone === 'warn' ? 'warn' : 'ok';

    const head = s.file
      ? '<div class="handoff"><div><span class="k">Файл</span><span class="v">' +
        esc(s.file.name) +
        '</span></div>' +
        '<div><span class="k">Размер</span><span class="v">' +
        esc(f.bytes(s.file.size)) +
        '</span></div>' +
        (s.chunkSize
          ? '<div><span class="k">Кусок</span><span class="v">' +
            esc(f.bytes(s.chunkSize)) +
            '</span></div>' +
            '<div><span class="k">Потоков</span><span class="v">' +
            esc(String(s.streams || 1)) +
            '</span></div>'
          : '') +
        '</div>'
      : '<div class="empty"><b>Файл ещё не выбран</b><span>Архив сборки лаунчера</span></div>';

    const bar =
      s.phase && s.phase !== 'idle'
        ? '<div class="meter" role="progressbar" aria-valuenow="' +
          pct +
          '" aria-valuemin="0" aria-valuemax="100">' +
          '<i class="' +
          tone +
          '" style="width:' +
          pct +
          '%"></i></div>'
        : '';

    /* До выбора файла подпись повторяла бы пустое место слово в слово —
       одна и та же фраза дважды подряд читается как сбой отрисовки. */
    const note =
      s.phase && s.phase !== 'idle'
        ? '<p class="note' +
          (status.tone === 'bad' ? ' note--bad' : '') +
          '" data-upload-status>' +
          esc(status.text) +
          '</p>'
        : '';

    return head + bar + note;
  }

  /* ---------- Журнал сборки ---------- */

  /** Одна строка журнала. */
  function logRow(ev) {
    const e = ev || {};
    const kind = String(e.kind || 'info');
    const cls = kind === 'error' ? 'err' : kind === 'done' ? 'ok' : kind === 'warn' ? 'warn' : '';
    return (
      '<div class="log-row' +
      (cls ? ' ' + esc(cls) : '') +
      '"><span class="t">' +
      esc(e.at || '') +
      '</span>' +
      '<span class="k">' +
      esc(kind) +
      '</span><span class="m">' +
      esc(e.message || '') +
      '</span></div>'
    );
  }

  /**
   * Журнал сборки.
   *
   * Пустой журнал — не пустое место: сборка идёт минутами, и первые
   * секунды до первой строки человек обязан видеть, что она началась, а
   * не гадать, нажалась ли кнопка.
   */
  function buildLog(events, state) {
    const list = events || [];
    if (!list.length) {
      return state === 'running'
        ? '<div class="empty"><b>Сборка началась</b><span>Первые строки появятся через несколько секунд</span></div>'
        : '<div class="empty"><b>Журнала пока нет</b><span>Он появится, когда запустите сборку</span></div>';
    }
    return '<div class="log">' + list.map(logRow).join('') + '</div>';
  }

  /**
   * Итог сборки словами.
   *
   * Оборванный поток — не провал: сервер мог досчитать сборку и умереть
   * на последней строке. Выдать это за ошибку значит заставить собирать
   * второй раз то, что уже собрано.
   */
  function buildOutcome(res) {
    const r = res || {};
    if (r.cancelled) return { text: 'Сборка отменена: пропавшие пакеты остались в списке', tone: 'warn' };
    if (r.ok) return { text: r.message || 'Сборка готова. Игрокам она пока не ушла — это отдельное решение.', tone: 'ok' };
    if (r.kind === 'buffered') {
      return { text: 'Сервер оборвал поток, но сборка могла дойти до конца. Перечитайте список версий.', tone: 'warn' };
    }
    return { text: 'Не собралось: ' + (r.message || 'сбой'), tone: 'bad' };
  }

  /* ---------- Новость ---------- */

  /**
   * Поля новости.
   *
   * Полей ровно столько, сколько знает сервер: имя файла, игра, обложка
   * и сам текст. Заголовка среди них нет намеренно — сервер берёт его
   * первой строкой текста, и отдельное поле «Заголовок» было бы
   * враньём: набранное в нём никуда бы не уехало.
   */
  function newsForm(post, problems) {
    const p = post || {};
    const errs = problems || [];
    const err = (field) => {
      const hit = errs.find((e) => e.field === field);
      return hit ? '<span class="help help--bad">' + esc(hit.text) + '</span>' : '';
    };
    return (
      '<div class="cols cols--2">' +
      '<div class="field"><label for="n-slug">Имя заметки</label>' +
      '<input id="n-slug" name="slug" type="text" value="' +
      esc(p.slug) +
      '" maxlength="128"' +
      (p.existing ? ' readonly' : '') +
      '>' +
      (p.existing
        ? '<span class="help">Имя уже в адресе статьи и не меняется</span>'
        : '<span class="help">Попадёт в адрес статьи. Буквы, цифры, дефис, подчёркивание, точка.</span>') +
      err('slug') +
      '</div>' +
      '<div class="field"><label for="n-game">Игра</label>' +
      '<input id="n-game" name="gameId" type="text" value="' +
      esc(p.gameId) +
      '" placeholder="пусто — новость про лаунчер"' +
      (p.existing ? ' readonly' : '') +
      '>' +
      '<span class="help">Пустое поле означает новость про лаунчер, а не про игру</span></div>' +
      '</div>' +
      '<div class="field"><label for="n-cover">Обложка</label>' +
      '<input id="n-cover" name="coverUrl" type="text" value="' +
      esc(p.coverUrl) +
      '" placeholder="необязательно">' +
      '<span class="help">Без неё сервер возьмёт первую картинку из текста</span></div>' +
      '<div class="field"><label for="n-body">Текст</label>' +
      '<textarea id="n-body" name="markdown" rows="16" placeholder="# Название заметки">' +
      esc(p.markdown) +
      '</textarea>' +
      err('markdown') +
      '</div>'
    );
  }

  /**
   * Заголовок и начало заметки — так, как их прочтёт сервер.
   *
   * Показывается рядом с текстом, потому что заголовок здесь не поле, а
   * первая строка: без подсказки человек не видит, что именно уедет в
   * ленту лаунчера, пока не опубликует.
   */
  function newsHeadline(markdown, news) {
    const N = M('CH2News', news);
    const title = N.titleOf(markdown);
    if (!title) return '<p class="note note--bad">Заголовка нет: первой строкой нужен «# Название заметки»</p>';
    return '<p class="note">В ленте игрок увидит: <b>' + esc(title) + '</b></p>';
  }

  /**
   * Предложение вернуть черновик.
   *
   * Показывается только когда черновик отличается от того, что на
   * сервере: предлагать «восстановить» одинаковый текст — значит пугать
   * потерей там, где терять нечего.
   */
  function draftNote(draft, serverPost, news) {
    const N = M('CH2News', news);
    if (!N.restorable(draft, serverPost)) return '';
    return (
      '<div class="note note--bad">Остался несохранённый черновик этой заметки. ' +
      '<button class="btn btn--text" type="button" data-draft-restore>Вернуть его</button>' +
      '<button class="btn btn--text" type="button" data-draft-drop>Выбросить</button></div>'
    );
  }

  /* ---------- Галерея ---------- */

  /** Крошки пути. Текущая папка — не ссылка: жать её некуда. */
  function galleryCrumbs(path, gallery) {
    const G = M('CH2Gallery', gallery);
    const items = G.crumbs(path);
    return (
      '<nav class="crumbs" aria-label="Путь">' +
      items
        .map((c, i) =>
          i === items.length - 1
            ? '<span aria-current="page">' + esc(c.name) + '</span>'
            : '<button class="btn btn--text" type="button" data-go="' + esc(c.path) + '">' + esc(c.name) + '</button>'
        )
        .join('<span class="sep">/</span>') +
      '</nav>'
    );
  }

  /**
   * Содержимое папки.
   *
   * Обложка помечена прямо в списке: без пометки узнать, какой из
   * восьми снимков попадёт на витрину, можно было только уйдя на другую
   * вкладку.
   */
  function galleryList(entries, opts) {
    const o = opts || {};
    const G = M('CH2Gallery', o.gallery);
    const f = F();
    const rows = G.sortEntries(entries);
    if (!rows.length) {
      return '<div class="empty"><b>Папка пуста</b><span>Перетащите сюда файлы или нажмите «Загрузить»</span></div>';
    }
    return (
      '<table><thead><tr><th>Имя</th><th>Размер</th><th></th></tr></thead><tbody>' +
      rows
        .map((e) => {
          const isCover = !e.dir && o.cover && String(e.name) === String(o.cover);
          const name = e.dir
            ? '<button class="btn btn--text" type="button" data-go="' +
              esc(G.entryPath(o.path, e.name)) +
              '">' +
              esc(e.name) +
              '/</button>'
            : esc(e.name);
          return (
            '<tr data-name="' +
            esc(e.name) +
            '"><td>' +
            name +
            (isCover ? ' <span class="badge badge--accent">обложка</span>' : '') +
            '</td>' +
            '<td class="num">' +
            (e.dir ? '' : esc(f.bytes(e.size || 0))) +
            '</td>' +
            '<td class="act">' +
            (e.dir || isCover || G.coverProblem(e)
              ? ''
              : '<button class="btn btn--text" type="button" data-cover="' + esc(e.name) + '">Сделать обложкой</button>') +
            (e.dir || !G.isImage(e.name)
              ? ''
              : '<button class="btn btn--text" type="button" data-caption="' +
                esc(e.name) +
                '" data-caption-text="' +
                esc(e.caption || '') +
                '">Подпись</button>') +
            '<button class="btn btn--text" type="button" data-rename="' +
            esc(e.name) +
            '">Переименовать</button>' +
            '<button class="btn btn--danger btn--text" type="button" data-remove="' +
            esc(e.name) +
            '">Удалить</button>' +
            '</td></tr>'
          );
        })
        .join('') +
      '</tbody></table>'
    );
  }

  /**
   * Содержимое папки вложений.
   *
   * Отличается от галереи одним: здесь у файла нет роли обложки, зато
   * есть «Вставить» — ради этого лист и открывали.
   */
  function assetList(entries, opts) {
    const o = opts || {};
    const G = M('CH2Gallery', o.gallery);
    const f = F();
    const rows = G.sortEntries(entries);
    if (!rows.length) {
      return '<div class="empty"><b>Папка пуста</b><span>Загрузите файл или создайте папку</span></div>';
    }
    return (
      '<table><thead><tr><th>Имя</th><th>Размер</th><th></th></tr></thead><tbody>' +
      rows
        .map((e) => {
          const name = e.dir
            ? '<button class="btn btn--text" type="button" data-go="' +
              esc(G.entryPath(o.path, e.name)) +
              '">' +
              esc(e.name) +
              '/</button>'
            : esc(e.name);
          return (
            '<tr data-name="' +
            esc(e.name) +
            '"><td>' +
            name +
            '</td><td class="num">' +
            (e.dir ? '' : esc(f.bytes(e.size || 0))) +
            '</td><td class="act">' +
            (e.dir
              ? ''
              : '<button class="btn btn--text" type="button" data-use="' + esc(e.name) + '">Вставить</button>') +
            '<button class="btn btn--danger btn--text" type="button" data-remove="' +
            esc(e.name) +
            '">Удалить</button></td></tr>'
          );
        })
        .join('') +
      '</tbody></table>'
    );
  }

  /* ---------- Порядок игр ---------- */

  /**
   * Список игр с перестановкой.
   *
   * Кнопки «выше/ниже» есть всегда, даже когда работает перетаскивание:
   * мышью в список из двадцати строк попадают не с первого раза, а с
   * клавиатуры не попадают вовсе.
   */
  function orderList(list) {
    const rows = list || [];
    if (!rows.length) return '<div class="empty"><b>Игр нет</b><span>Добавьте первую</span></div>';
    return (
      '<ol class="order" data-order>' +
      rows
        .map(
          (g, i) =>
            '<li draggable="true" data-id="' +
            esc(g.gameId) +
            '" data-index="' +
            i +
            '">' +
            '<span class="n">' +
            (i + 1) +
            '</span>' +
            '<span class="t">' +
            esc(g.title || g.gameId) +
            '</span>' +
            '<span class="push"></span>' +
            '<button class="btn btn--icon" type="button" data-up="' +
            esc(g.gameId) +
            '" aria-label="Выше"' +
            (i === 0 ? ' disabled' : '') +
            '>↑</button>' +
            '<button class="btn btn--icon" type="button" data-down="' +
            esc(g.gameId) +
            '" aria-label="Ниже"' +
            (i === rows.length - 1 ? ' disabled' : '') +
            '>↓</button>' +
            '</li>'
        )
        .join('') +
      '</ol>'
    );
  }

  /**
   * Что изменится при сохранении.
   *
   * Порядок в реестре — это порядок на витрине у игрока, и по одному
   * списку не видно, что именно переехало.
   */
  function orderSummary(before, after) {
    const a = (before || []).map((g) => g.gameId);
    const b = (after || []).map((g) => g.gameId);
    if (a.length === b.length && a.every((id, i) => id === b[i])) {
      return { changed: false, text: 'Порядок тот же, сохранять нечего' };
    }
    const moved = b.filter((id, i) => a[i] !== id).length;
    const f = F();
    return {
      changed: true,
      text: 'Переедет ' + f.count(moved, 'строка', 'строки', 'строк') + '. Игроки увидят новый порядок сразу.',
    };
  }

  /* ---------- График ---------- */

  /**
   * Точки ломаной по ряду чисел.
   *
   * Считается отдельно и проверяется, потому что у графика есть три
   * состояния, в которых деление превращает координаты в NaN, а SVG
   * молча не рисует ничего: пустой ряд, ряд из одной точки и ряд из
   * одних нулей — так выглядит первый день после чистки метрик.
   */
  function sparkPoints(values, width, height) {
    const nums = (values || []).map((v) => {
      const n = Number(v);
      return Number.isFinite(n) ? n : 0;
    });
    if (!nums.length) return '';

    const w = Number(width) || 0;
    const h = Number(height) || 0;
    const max = Math.max(...nums);
    // Ровный ряд рисуем по низу, а не делим на ноль
    const scale = max > 0 ? h / max : 0;
    const step = nums.length > 1 ? w / (nums.length - 1) : 0;

    return nums
      .map((v, i) => (i * step).toFixed(1) + ',' + (h - v * scale).toFixed(1))
      .join(' ');
  }

  /** Ломаная одного ряда. Пустой ряд — пустая строка, а не битый тег. */
  function sparkLine(values, opts) {
    const o = opts || {};
    const points = sparkPoints(values, o.width, o.height);
    if (!points) return '';
    return (
      '<polyline fill="none" stroke="' + esc(o.color || 'currentColor') +
      '" stroke-width="1.5" points="' + points + '"/>'
    );
  }

  /* ---------- Состав сборки ---------- */

  /**
   * План будущей сборки.
   *
   * Пропавшие пакеты называются поимённо и до сборки: узнать о них на
   * середине выкатки — значит откатывать уже отданное игрокам.
   */
  function resolvePlan(plan, mods) {
    const p = plan || {};
    const M2 = M('CH2Mods', mods);
    const f = F();
    const space = M2.planSpace(p, f);
    const missing = Array.isArray(p.missing) ? p.missing : [];

    const head =
      '<div class="handoff">' +
      '<div><span class="k">Модпак</span><span class="v">' + esc(p.displayName || '—') + '</span></div>' +
      '<div><span class="k">Версия</span><span class="v">' + esc(p.version || '—') + '</span></div>' +
      '<div><span class="k">Пакетов</span><span class="v">' + esc(String(p.packages || 0)) + '</span></div>' +
      (p.loader ? '<div><span class="k">Загрузчик</span><span class="v">' + esc(p.loader) + '</span></div>' : '') +
      '</div>';

    const note =
      '<p class="note' + (space.tone === 'bad' ? ' note--bad' : '') + '">' + esc(space.text) + '</p>';

    const gone = missing.length
      ? '<div class="note note--bad"><b>Этих пакетов больше нет на Thunderstore:</b><br>' +
        missing.map((m) => esc(typeof m === 'string' ? m : M2.fullName(m.namespace, m.name))).join('<br>') +
        '<br>Собрать без них можно — сборка спросит об этом отдельно.</div>'
      : '';

    return head + note + gone;
  }

  /* ---------- Каталог ---------- */

  /** Список пакетов каталога. */
  function catalogList(items, opts) {
    const o = opts || {};
    const M2 = M('CH2Mods', o.mods);
    const f = F();
    const rows = M2.entries({ results: items });

    if (!rows.length) {
      return (
        '<div class="empty"><b>' +
        (o.query ? 'По запросу ничего нет' : 'Каталог пуст') +
        '</b><span>Половина модпаков в раздел «Modpacks» не проставлена — такие подставляют ссылкой</span></div>'
      );
    }

    return (
      '<table><thead><tr><th>Модпак</th><th>Версия</th><th class="num">Скачиваний</th><th></th></tr></thead><tbody>' +
      rows
        .map(
          (r) =>
            '<tr><td>' +
            esc(r.name) +
            (r.deprecated ? ' <span class="badge badge--bad">устарел</span>' : '') +
            '<br><span class="faint mono">' +
            esc(r.namespace) +
            '</span></td>' +
            '<td class="mono">' + esc(r.version || '—') + '</td>' +
            '<td class="num">' + esc(f.dec(r.downloads, 0)) + '</td>' +
            '<td class="act">' +
            '<button class="btn btn--text" type="button" data-readme data-ns="' + esc(r.namespace) +
            '" data-name="' + esc(r.name) + '" data-version="' + esc(r.version) + '">Описание</button>' +
            '<button class="btn btn--text" type="button" data-take data-ns="' + esc(r.namespace) +
            '" data-name="' + esc(r.name) + '" data-version="' + esc(r.version) + '">Выбрать</button>' +
            '</td></tr>'
        )
        .join('') +
      '</tbody></table>' +
      '<pre class="log scroll scroll--sm" data-readme-box></pre>'
    );
  }

  /* ---------- Переезд со старой сборки ---------- */

  /** Что получилось из профиля. */
  function importResult(res) {
    const r = res || {};
    const f = F();
    const packages = Number(r.packages || (Array.isArray(r.mods) ? r.mods.length : 0));
    return (
      '<div class="handoff">' +
      '<div><span class="k">Версия</span><span class="v">' + esc(r.version || '—') + '</span></div>' +
      '<div><span class="k">Пакетов</span><span class="v">' + esc(f.dec(packages, 0)) + '</span></div>' +
      '</div>' +
      '<p class="note">Сборка готова, но игрокам не ушла: отдать её — отдельное решение.</p>'
    );
  }

  /* ---------- Журналы обращения ---------- */

  /** Журнал, приложенный игроком. */
  function logsView(text2) {
    const t = String(text2 || '').trim();
    if (!t) {
      return '<div class="empty"><b>Журнала нет</b><span>Игрок его не приложил — обращение от этого не хуже</span></div>';
    }
    return '<pre class="log scroll scroll--lg">' + esc(t) + '</pre>';
  }

  /* ---------- Подсказка из Thunderstore ---------- */

  /** Выбор игры и её имени в терминах Thunderstore. */
  function ecosystemPicker(games) {
    const rows = games || [];
    if (!rows.length) return '<div class="empty"><b>Игр нет</b><span>Сначала добавьте игру в реестр</span></div>';
    return (
      '<div class="field"><label for="e-game">Игра</label><select id="e-game" name="gameId">' +
      rows.map((g) => '<option value="' + esc(g.gameId) + '">' + esc(g.title || g.gameId) + '</option>').join('') +
      '</select></div>' +
      '<div class="field"><label for="e-slug">Имя в Thunderstore</label>' +
      '<input id="e-slug" name="slug" type="text" placeholder="lethal-company">' +
      '<span class="help">Так игра называется в адресе на thunderstore.io — например, thunderstore.io/c/<b>lethal-company</b>/</span></div>'
    );
  }

  /* ---------- Подбор параметров ---------- */

  /** Таблица прогонов с пометкой лучшего и объяснением, почему выбран он. */
  function benchTable(runs, tuning) {
    const T = M('CH2Tuning', tuning);
    const f = F();
    const marked = T.mark(runs);
    if (!marked.length) {
      return '<div class="empty"><b>Прогонов ещё не было</b><span>Прогон занимает около минуты и ничего не публикует</span></div>';
    }
    return (
      '<table><thead><tr><th>Кусок</th><th>Потоков</th><th>Скорость</th><th>Повторов</th><th></th></tr></thead><tbody>' +
      marked
        .map(
          (r) =>
            '<tr' +
            (r.best ? ' class="best"' : '') +
            ' data-chunk="' +
            esc(r.chunk) +
            '">' +
            '<td>' +
            esc(r.chunk) +
            '</td><td class="num">' +
            esc(String(r.streams)) +
            '</td>' +
            '<td class="num">' +
            esc(f.speed(Number(r.mbps || 0) * 1024 * 1024)) +
            '</td>' +
            '<td class="num">' +
            esc(String(r.retries || 0)) +
            '</td>' +
            '<td class="act">' +
            (r.best
              ? '<span class="badge badge--ok">выбрано</span>'
              : '<button class="btn btn--text" type="button" data-apply="' + esc(r.chunk) + '">Применить</button>') +
            '</td></tr>'
        )
        .join('') +
      '</tbody></table>' +
      '<p class="note">' +
      esc(T.why(runs)) +
      '</p>'
    );
  }

  return {
    esc,
    sheet,
    uploadStatus,
    uploadButtons,
    uploadCard,
    logRow,
    buildLog,
    buildOutcome,
    newsForm,
    newsHeadline,
    draftNote,
    galleryCrumbs,
    galleryList,
    assetList,
    sparkPoints,
    sparkLine,
    resolvePlan,
    catalogList,
    importResult,
    logsView,
    ecosystemPicker,
    orderList,
    orderSummary,
    benchTable,
  };
});
