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

      /* Скорость и остаток — не украшение. Заливка на 1,8 ГБ идёт
         минутами, и один процент не отвечает на единственный вопрос,
         который в это время задают: ждать ещё минуту или уйти пить чай.
         Пока скорость неизвестна, их не показываем вовсе: выдумывать
         остаток по двум точкам — то же самое, что соврать. */
      const parts = [head, f.percent(s.progress || 0, 1, 0)];
      if (s.speed > 0) {
        parts.push(f.speed(s.speed));
        if (s.left > 0) parts.push('осталось ' + f.eta(s.left / s.speed));
      }
      return { text: parts.join(' · '), tone: '' };
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
      '<div class="btn-row"><input id="n-cover" name="coverUrl" type="text" value="' +
      esc(p.coverUrl) +
      '" placeholder="необязательно">' +
      (p.existing
        ? '<button class="btn" type="button" data-flow="cover">Загрузить файл</button>'
        : '') +
      '</div>' +
      '<span class="help">Без неё сервер возьмёт первую картинку из текста' +
      (p.existing ? '' : '. Загрузить файлом можно после первого сохранения') +
      '</span></div>' +
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

  /* ---------- Карточка игры ---------- */

  /**
   * Поля одной строки реестра.
   *
   * Реестр — это то, что лаунчер читает при старте: чем игра
   * запускается и как называется. Ошибка здесь ломает запуск у всех
   * сразу, поэтому у каждого поля написано, на что оно влияет, а не
   * просто как называется.
   *
   * Идентификатор у существующей игры не правится: он уже стал именем
   * папки в манифестах и в контенте, и переименование в панели оставило
   * бы файлы под старым именем — игра просто исчезла бы у игроков.
   */
  function gameForm(item, problems) {
    const g = item || {};
    const errs = problems || [];
    const err = (field) => {
      const hit = errs.find((e) => e.field === field);
      return hit ? '<span class="help help--bad">' + esc(hit.message || hit.text) + '</span>' : '';
    };
    const field = (name, label, value, help, extra) =>
      '<div class="field"><label for="g-' + name + '">' + esc(label) + '</label>' +
      '<input id="g-' + name + '" name="' + name + '" type="text" value="' + esc(value) + '"' + (extra || '') + '>' +
      (help ? '<span class="help">' + esc(help) + '</span>' : '') +
      err(name) + '</div>';

    return (
      '<div class="cols cols--2">' +
      field(
        'gameId',
        'Идентификатор',
        g.gameId,
        g.existing
          ? 'Уже стал именем папки в манифестах и контенте — не меняется'
          : 'Латиница в нижнем регистре, цифры, дефис и подчёркивание',
        g.existing ? ' readonly' : ''
      ) +
      field('title', 'Название', g.title, 'Его игрок видит в списке слева') +
      '</div>' +
      field('exeRelativePath', 'Исполняемый файл', g.exeRelativePath, 'Путь внутри папки игры, например REPO.exe. Без него запускать нечего') +
      '<div class="cols cols--2">' +
      field('steamAppId', 'Steam AppID', g.steamAppId, 'Нужен, чтобы запустить игру через Steam') +
      field('steamFolder', 'Папка в Steam', g.steamFolder, 'Как называется каталог игры внутри steamapps/common') +
      '</div>' +
      '<div class="field"><label for="g-icon">Иконка</label>' +
      '<div class="btn-row"><input id="g-icon" name="iconUrl" type="text" value="' + esc(g.iconUrl) + '" placeholder="/manifests/' + esc(g.gameId || 'gameId') + '/icon.png">' +
      (g.existing ? '<button class="btn" type="button" data-flow="icon">Загрузить</button>' : '') +
      (g.iconUrl ? '<button class="btn btn--text" type="button" data-flow="icon-default">Вернуть стандартную</button>' : '') +
      '</div>' +
      '<span class="help">Без неё в списке будет буква на цветном квадрате</span></div>' +
      '<label class="check"><input type="checkbox" name="published"' + (g.unpublished ? '' : ' checked') + '>' +
      '<span>Показывать игрокам</span>' +
      '<span class="help">Снятая галочка убирает игру из лаунчера, но файлы и версии остаются на месте</span></label>'
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

  /* ---------- Откуда данные ---------- */

  /**
   * Пометка о том, что раздел показывает не живые данные.
   *
   * Панель обещает не выдавать снимок за прод, и одного всплывающего
   * сообщения при запуске для этого мало: оно живёт четыре секунды, а
   * раздел открывают через полчаса. Пометка стоит в самом разделе,
   * пока причина не ушла.
   *
   * «Не ответил» и «ещё не спрашивали» — разные вещи: первое повод
   * насторожиться, второе просто ожидание.
   */
  function staleNote(failed, loading) {
    const bad = failed || [];
    if (bad.length) {
      return (
        '<div class="note note--bad" data-stale>Здесь показан снимок, а не то, что на сервере: ' +
        esc(bad.join(', ')) +
        ' не ответил. Записывать в этом состоянии нельзя — запись уйдёт, а список останется прежним.</div>'
      );
    }
    if (loading && loading.length) {
      return '<div class="note" data-stale>Читаем с сервера…</div>';
    }
    return '';
  }

  /* ---------- Отборы ---------- */

  /**
   * Полоса отбора над списком обращений.
   *
   * Правило отбора давно написано и проверено (`filterInbox`), не было
   * только полосы. Без неё инбокс из двухсот обращений читается ровно
   * одним способом — сверху вниз, каждый раз заново.
   */
  function inboxFilter(f) {
    const flt = f || {};
    const sel = (name, label, options) =>
      '<label class="inline-label" for="fi-' + name + '">' + esc(label) + '</label>' +
      '<select id="fi-' + name + '" name="' + name + '">' +
      options
        .map(
          ([v, t]) =>
            '<option value="' + esc(v) + '"' + (String(flt[name] || '') === v ? ' selected' : '') + '>' + esc(t) + '</option>'
        )
        .join('') +
      '</select>';

    return (
      '<div class="filters" data-inbox-filter>' +
      '<input type="search" name="query" value="' + esc(flt.query) + '" placeholder="Поиск по тексту, имени и контакту" aria-label="Поиск по обращениям">' +
      sel('type', 'Тип', [['', 'любой'], ['bug', 'поломка'], ['idea', 'идея'], ['question', 'вопрос'], ['other', 'прочее']]) +
      sel('status', 'Состояние', [['', 'любое'], ['new', 'новые'], ['read', 'прочитанные']]) +
      '<label class="check check--inline"><input type="checkbox" name="important"' +
      (flt.important ? ' checked' : '') +
      '><span>только важные</span></label>' +
      '<label class="inline-label" for="fi-from">С</label><input id="fi-from" name="from" type="date" value="' + esc(flt.from) + '">' +
      '<label class="inline-label" for="fi-to">по</label><input id="fi-to" name="to" type="date" value="' + esc(flt.to) + '">' +
      (anyFilter(flt) ? '<button class="btn btn--text" type="button" data-inbox-reset>Сбросить</button>' : '') +
      '</div>'
    );
  }

  const anyFilter = (f) =>
    Boolean(f && (f.query || f.type || f.status || f.important || f.from || f.to));

  /**
   * Полоса отбора над метриками.
   *
   * Период — не украшение: «за 30 дней» и «за 7 дней» отвечают на разные
   * вопросы, и после выкатки смотрят именно вчерашний день, а не месяц,
   * в котором он растворился.
   */
  function metricsFilter(state, games) {
    const st = state || {};
    const days = Number(st.days || 30);
    const btn = (n, label) =>
      '<button class="seg' + (days === n ? ' on' : '') + '" type="button" data-days="' + n + '">' + esc(label) + '</button>';
    return (
      '<div class="filters" data-metrics-filter>' +
      '<div class="segs">' + btn(7, '7 дней') + btn(30, '30 дней') + btn(90, '90 дней') + '</div>' +
      '<label class="inline-label" for="mf-game">Игра</label>' +
      '<select id="mf-game" name="gameId"><option value="">все</option>' +
      (games || [])
        .map(
          (g) =>
            '<option value="' + esc(g.gameId) + '"' + (st.gameId === g.gameId ? ' selected' : '') + '>' +
            esc(g.title || g.gameId) +
            '</option>'
        )
        .join('') +
      '</select></div>'
    );
  }

  /** Границы периода в том виде, который понимает сервер. */
  function period(days) {
    const n = Math.max(1, Number(days) || 30);
    const to = new Date();
    const from = new Date(to.getTime() - n * 24 * 60 * 60 * 1000);
    return { from: from.toISOString(), to: to.toISOString() };
  }

  /* ---------- Обращение ---------- */

  /**
   * Обращение целиком.
   *
   * В списке видно первую строку, а починить по ней нельзя: важна
   * диагностика — версия клиента, система, место на диске. Её присылает
   * сам игрок, и это единственная зацепка под «у меня не качается».
   */
  function feedbackCard(item) {
    const f = item || {};
    const fm = F();
    const sys = f.system && typeof f.system === 'object' ? f.system : null;

    const head =
      '<div class="handoff">' +
      '<div><span class="k">Тип</span><span class="v">' + esc(f.type || 'other') + '</span></div>' +
      '<div><span class="k">Когда</span><span class="v">' + esc(fm.dateTimeZoned(f.at)) + '</span></div>' +
      (f.name ? '<div><span class="k">Кто</span><span class="v">' + esc(f.name) + '</span></div>' : '') +
      (f.contact ? '<div><span class="k">Связь</span><span class="v">' + esc(f.contact) + '</span></div>' : '') +
      '</div>';

    const body = '<p class="quote">' + esc(f.comment || '') + '</p>';

    const diag = sys
      ? '<table><tbody>' +
        Object.keys(sys)
          .sort()
          .map((k) => '<tr><td class="dim">' + esc(k) + '</td><td class="mono">' + esc(sys[k]) + '</td></tr>')
          .join('') +
        '</tbody></table>'
      : '<div class="empty"><b>Диагностики нет</b><span>Игрок отправил обращение без неё</span></div>';

    return head + body + '<h3 class="sub">Что за компьютер</h3>' + diag;
  }

  /**
   * Ссылка для ответа.
   *
   * Своей почты у панели нет, и заводить её ради этого незачем: письмо
   * пишется в обычном почтовом клиенте. Контакт игрок оставляет по
   * желанию, поэтому ответить получится не на всё.
   */
  function replyLink(item) {
    const f = item || {};
    const contact = String(f.contact || '').trim();
    if (!contact || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(contact)) return '';
    const subject = 'Chill Hub: ответ на ваше обращение';
    const quoted = String(f.comment || '')
      .split('\n')
      .map((l) => '> ' + l)
      .join('\n');
    return (
      'mailto:' + encodeURIComponent(contact) +
      '?subject=' + encodeURIComponent(subject) +
      '&body=' + encodeURIComponent('\n\n' + quoted + '\n')
    );
  }

  /** Диагностика одной строкой — чтобы вставить в переписку или задачу. */
  function diagnosticsText(item) {
    const f = item || {};
    const sys = f.system && typeof f.system === 'object' ? f.system : {};
    const lines = Object.keys(sys)
      .sort()
      .map((k) => k + ': ' + sys[k]);
    return ['Обращение ' + (f.id || ''), 'Тип: ' + (f.type || ''), ''].concat(lines).join('\n');
  }

  /* ---------- Технические работы ---------- */

  /**
   * Форма работ: причина, окно и что именно закрывается.
   *
   * Всё это было в панели 1.0 и не декоративно. Причина — единственное,
   * что игрок увидит вместо каталога; без неё он видит общую фразу и
   * идёт спрашивать «а что случилось». Окончание сервер отрабатывает
   * сам, и без него забытые включёнными работы — это тихо не работающий
   * лаунчер у всех сразу. Блоки решают, что именно перестаёт отдаваться:
   * закрыть установку, но оставить запуск уже скачанного — обычный
   * случай, и одной кнопкой его не выразить.
   */
  function maintForm(state) {
    const m = state || {};
    const b = m.blocks || {};
    const box = (name, label, on, hint) =>
      '<label class="check"><input type="checkbox" name="' + name + '"' + (on ? ' checked' : '') + '>' +
      '<span>' + esc(label) + '</span>' +
      (hint ? '<span class="help">' + esc(hint) + '</span>' : '') +
      '</label>';

    return (
      '<div class="field"><label for="mt-reason">Что увидит игрок</label>' +
      '<textarea id="mt-reason" name="reason" rows="3" maxlength="500" placeholder="Переносим сборки на новый диск, вернёмся к 21:00 по Москве.">' +
      esc(m.reason) +
      '</textarea>' +
      '<span class="help">Простым языком и с указанием времени. Пустое поле означает общую фразу без подробностей — и поток обращений «а что случилось».</span></div>' +

      '<div class="cols cols--2">' +
      '<div class="field"><label for="mt-from">Начало</label>' +
      '<input id="mt-from" name="startsAt" type="datetime-local" value="' + esc(localTime(m.startsAt)) + '">' +
      '<span class="help">Пусто — начинается сразу</span></div>' +
      '<div class="field"><label for="mt-to">Окончание</label>' +
      '<input id="mt-to" name="endsAt" type="datetime-local" value="' + esc(localTime(m.endsAt)) + '">' +
      '<span class="help">Сервер выключит работы сам. Пусто — выключать придётся руками</span></div>' +
      '</div>' +

      '<div class="field"><label>Что закрывается</label>' +
      box('install', 'Установку новых игр', b.install !== false) +
      box('update', 'Обновление уже установленных', b.update !== false) +
      box('launch', 'Запуск игр', b.launch === true, 'Обычно оставляют открытым: игра стартует локально и серверу не мешает') +
      '</div>'
    );
  }

  /* Время в поле ввода — местное, а на сервере RFC3339 в UTC. Показывать
     UTC человеку, который назначает работы на свой вечер, — верный
     способ ошибиться на три часа. */
  function localTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const p = (n) => String(n).padStart(2, '0');
    return (
      d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) +
      'T' + p(d.getHours()) + ':' + p(d.getMinutes())
    );
  }

  /** Обратно: из поля ввода в то, что понимает сервер. */
  function isoTime(local) {
    if (!local) return '';
    const d = new Date(local);
    return Number.isNaN(d.getTime()) ? '' : d.toISOString();
  }

  /**
   * Что не так с назначенным окном.
   *
   * Сервер откажет теми же словами, но уже после нажатия — а работы
   * назначают заранее и на конкретное время.
   */
  function maintProblem(payload) {
    const p = payload || {};
    if (p.startsAt && p.endsAt && new Date(p.endsAt) <= new Date(p.startsAt)) {
      return 'Окончание должно быть позже начала';
    }
    if (p.enabled && !p.blocks.install && !p.blocks.update && !p.blocks.launch) {
      return 'Работы, которые ничего не закрывают, ничего и не делают';
    }
    return '';
  }

  /* ---------- События одного кода ---------- */

  /**
   * Из чего складывается код ошибки.
   *
   * Счётчик говорит, что ломается часто; события — у кого именно.
   * Версия клиента и игра здесь важнее времени: если весь код собрался
   * на одной версии, чинить надо её, а не всё подряд.
   */
  function errorEvents(res) {
    const r = res || {};
    const rows = (r.items || []).slice(0, 200);
    const f = F();
    if (!rows.length) {
      return '<div class="empty"><b>Событий не осталось</b><span>Счётчик считает за другой период либо метрики чистили</span></div>';
    }

    const по = (key) => {
      const m = new Map();
      for (const e of rows) {
        const k = String(e[key] || '—');
        m.set(k, (m.get(k) || 0) + 1);
      }
      return [...m.entries()].sort((a, b) => b[1] - a[1]);
    };

    const chips = (title, pairs) =>
      '<div class="btn-row"><span class="k">' + esc(title) + '</span>' +
      pairs.slice(0, 6).map(([k, n]) => '<span class="badge">' + esc(k) + ' · ' + n + '</span>').join('') +
      '</div>';

    return (
      chips('Версии клиента', по('appVersion')) +
      chips('Игры', по('gameId')) +
      '<table><thead><tr><th>Когда, ' + esc(f.zone()) + '</th><th>Версия</th><th>Игра</th><th>Что делал</th></tr></thead><tbody>' +
      rows
        .map(
          (e) =>
            '<tr><td class="dim">' + esc(f.dateTime(e.ts)) + '</td>' +
            '<td class="mono">' + esc(e.appVersion || '—') + '</td>' +
            '<td>' + esc(e.gameId || '—') + '</td>' +
            '<td class="dim">' + esc(e.event || '') + '</td></tr>'
        )
        .join('') +
      '</tbody></table>' +
      (r.capped ? '<p class="note">Показаны последние ' + rows.length + ' — их было больше.</p>' : '')
    );
  }

  /* ---------- Разница сборок ---------- */

  /**
   * Что поедет игроку при активации.
   *
   * Пустой результат и отсутствие манифеста — разные вещи, и путать их
   * нельзя. Пустое дерево читается как «ничего не изменилось», а на деле
   * это чаще всего «старый манифест уже подчищен, сравнить не с чем», и
   * решение об активации принимают вслепую, думая, что видят всё.
   */
  function launcherDiff(result, opts) {
    const o = opts || {};
    const f = F();
    const M2 = M('CH2Manifest', o.manifest);

    if (!result) {
      return (
        '<div class="empty"><b>Сравнить не с чем</b><span>Манифест версии ' +
        esc(o.active || '') +
        ' на сервере уже не лежит — старые подчищаются. Список файлов покажется после активации.</span></div>'
      );
    }

    const c = result.counts || {};
    if (!c.total) {
      return '<div class="empty"><b>Файлы совпадают</b><span>Между этими версиями качать нечего</span></div>';
    }

    /* Свёрнутые папки, а не плоский список: в сборке четыре с половиной
       сотни файлов, и на «что изменилось» плоский отвечает единственным
       способом — пролистать целиком. Папка с числом отвечает сразу. */
    const groups = M2.folders(result.rows || []);
    const open = groups.length <= 3;

    return (
      '<div class="tree scroll scroll--lg" data-tree>' +
      groups
        .map((g) => {
          const rows = g.files
            .map(
              (r) =>
                '<div class="row ' + esc(r.diff) + '" data-path="' + esc(g.dir ? g.dir + '/' + r.name : r.name) + '">' +
                '<span>' + (r.diff === 'add' ? '+' : r.diff === 'del' ? '−' : '~') + '</span>' +
                '<span>' + esc(r.name) + '</span>' +
                '<span class="size">' + (r.diff === 'del' ? '—' : esc(f.bytes(r.size))) + '</span>' +
                '</div>'
            )
            .join('');
          return (
            '<details class="folder"' + (open ? ' open' : '') + '>' +
            '<summary><span>' + esc(g.dir || 'корень сборки') + '</span>' +
            '<span class="size">' + g.counts.total + '</span></summary>' +
            rows +
            '</details>'
          );
        })
        .join('') +
      '</div>'
    );
  }

  /**
   * Выбор двух версий для сравнения.
   *
   * По умолчанию сравнивается активная с загруженной — это то решение,
   * которое сейчас на столе. Но иногда нужен другой вопрос: «что
   * набежало за три выпуска», и ответить на него без выбора нельзя.
   */
  function versionPicker(versions, from, to) {
    const list = versions || [];
    if (list.length < 2) return '';
    const opts = (selected) =>
      list
        .map(
          (v) =>
            '<option value="' + esc(v.version) + '"' + (v.version === selected ? ' selected' : '') + '>' +
            esc(v.version) +
            (v.state === 'active' ? ' — у игроков' : v.state === 'uploaded' ? ' — загружена' : '') +
            '</option>'
        )
        .join('');
    return (
      '<div class="btn-row">' +
      '<label class="inline-label" for="v-from">С</label><select id="v-from" data-diff-from>' + opts(from) + '</select>' +
      '<label class="inline-label" for="v-to">на</label><select id="v-to" data-diff-to>' + opts(to) + '</select>' +
      '<button class="btn btn--text" type="button" data-diff-go>Сравнить</button>' +
      '</div>'
    );
  }

  /** Счётчики над деревом: сколько добавилось, изменилось и пропало. */
  function diffCounts(result) {
    if (!result) return '';
    const c = result.counts || {};
    const f = F();
    return (
      '<span class="badge badge--ok">+' + c.add + '</span>' +
      '<span class="badge badge--warn">~' + c.mod + '</span>' +
      '<span class="badge badge--bad">−' + c.del + '</span>' +
      '<span class="badge">' + esc(f.bytes(result.weight || 0)) + ' игроку</span>'
    );
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

  /**
   * Разница между двумя собранными версиями модпака.
   *
   * Читают её перед тем, как отдать пересборку игрокам: «какие моды
   * изменились» — это вопрос, на который список из полутора сотен полных
   * имён до и после не отвечает.
   */
  function modsDiff(items) {
    const rows = items || [];
    if (!rows.length) {
      return '<div class="empty"><b>Состав не изменился</b><span>Между этими версиями у игрока не поменяется ничего</span></div>';
    }
    const word = { added: 'появилось', removed: 'пропало', updated: 'обновилось' };
    const cls = { added: 'add', removed: 'del', updated: 'mod' };
    return (
      '<div class="tree" data-tree>' +
      rows
        .map((r) => {
          const change = String(r.change || '');
          const versions =
            change === 'updated' ? esc(r.from || '') + ' → ' + esc(r.to || '') : esc(r.to || r.from || '');
          return (
            '<div class="row ' + esc(cls[change] || '') + '" data-path="' + esc(r.package) + '">' +
            '<span>' + esc(r.package) + '</span>' +
            '<span class="size">' + versions + '</span>' +
            '</div>'
          );
        })
        .join('') +
      '</div>' +
      /* Пустые разряды не перечисляем: «0 пропал» — не по-русски и
         вдобавок мешает увидеть то, что изменилось на самом деле. */
      '<p class="note">' +
      esc(
        ['added', 'updated', 'removed']
          .map((k) => ({ n: rows.filter((r) => r.change === k).length, k: k }))
          .filter((x) => x.n > 0)
          .map((x) => x.n + ' ' + word[x.k])
          .join(', ')
      ) +
      '</p>'
    );
  }

  /**
   * Собранные версии модпака.
   *
   * В панели 1.0 это была таблица на вкладке игры, и она отвечала на два
   * вопроса, на которые строка раздела не отвечает: какая версия сейчас
   * у игроков и без каких модов собрана каждая. Пропавшие называются
   * поимённо — «пропущено 2» не говорит, потерялся ли твик текстур или
   * мод, ради которого пакет и собирали.
   */
  function modVersions(list, opts) {
    const rows = list || [];
    const o = opts || {};
    const f = F();
    if (!rows.length) {
      return '<div class="empty"><b>Собранных версий нет</b><span>Соберите первую — игрокам она сама не уйдёт</span></div>';
    }
    return (
      '<table><thead><tr><th>Версия</th><th>Когда</th><th class="num">Модов</th><th class="num">Размер</th><th></th></tr></thead><tbody>' +
      rows
        .map((v) => {
          const active = String(v.version) === String(o.active);
          const missing = Array.isArray(v.missing) ? v.missing : [];
          return (
            '<tr' + (active ? ' class="best"' : '') + '><td class="mono">' + esc(v.version) +
            (active ? ' <span class="badge badge--ok">у игроков</span>' : '') +
            (missing.length
              ? '<br><span class="faint">собрана без: ' + esc(missing.join(', ')) + '</span>'
              : '') +
            '</td>' +
            '<td class="dim">' + esc(f.dateTime(v.createdAt)) + '</td>' +
            '<td class="num">' + esc(String(v.packages || 0)) + '</td>' +
            '<td class="num">' + esc(f.bytes(v.bytes || 0)) + '</td>' +
            '<td class="act">' +
            (active
              ? ''
              : '<button class="btn btn--text" type="button" data-act="mods.activate" data-args=\'{"gameId":"' +
                esc(o.gameId) + '","version":"' + esc(v.version) + '"}\'>Отдать игрокам</button>') +
            (active
              ? ''
              : '<button class="btn btn--danger btn--text" type="button" data-act="mods.delete" data-args=\'{"gameId":"' +
                esc(o.gameId) + '","version":"' + esc(v.version) + '"}\'>Удалить</button>') +
            '</td></tr>'
          );
        })
        .join('') +
      '</tbody></table>' +
      '<p class="note">Активную версию удалить нельзя: игроки останутся без модпака посреди сессии.</p>'
    );
  }

  /* ---------- Каталог ---------- */

  /**
   * Полоса каталога: поиск, сортировка, страницы.
   *
   * Сортировка не украшение: по умолчанию Thunderstore отдаёт самые
   * скачиваемые, а ищут обычно свежее — «что вышло на этой неделе».
   * Страницы тоже: в каталоге игры сотни модпаков, и первая двадцатка
   * отвечает далеко не всегда.
   */
  function catalogBar(state) {
    const st = state || {};
    const orderings = [
      ['most-downloaded', 'по скачиваниям'],
      ['newest', 'сначала новые'],
      ['last-updated', 'по обновлению'],
      ['top-rated', 'по оценке'],
    ];
    return (
      '<div class="filters" data-catalog-bar>' +
      '<input type="search" name="q" value="' + esc(st.q) + '" placeholder="Название модпака" aria-label="Поиск по каталогу">' +
      '<label class="inline-label" for="c-ord">Порядок</label>' +
      '<select id="c-ord" name="ordering">' +
      orderings
        .map(
          ([v, t]) =>
            '<option value="' + esc(v) + '"' + ((st.ordering || 'most-downloaded') === v ? ' selected' : '') + '>' +
            esc(t) +
            '</option>'
        )
        .join('') +
      '</select>' +
      '<span class="push"></span>' +
      '<button class="btn btn--icon" type="button" data-page="-1" aria-label="Предыдущая страница"' +
      (Number(st.page || 1) <= 1 ? ' disabled' : '') +
      '>←</button>' +
      '<span class="inline-label">' + esc(pageLabel(st)) + '</span>' +
      '<button class="btn btn--icon" type="button" data-page="1" aria-label="Следующая страница"' +
      (st.hasMore ? '' : ' disabled') +
      '>→</button>' +
      '</div>'
    );
  }

  /* Номер страницы вместе с тем, из скольких: «страница 3» без «из 12»
     не говорит, много ли ещё осталось. */
  function pageLabel(st) {
    const page = Number(st.page || 1);
    const total = Number(st.count || 0);
    const per = Number(st.perPage || 20);
    if (!total) return 'страница ' + page;
    return 'страница ' + page + ' из ' + Math.max(1, Math.ceil(total / per));
  }

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

  /**
   * Настройки прогона.
   *
   * Наборы задаются списком, а не тремя кнопками: канал у всех разный, и
   * то, что на одном упирается в восемь потоков, на другом только
   * начинает разгоняться. Проба — настоящий файл: гонять синтетику
   * бесполезно, мерить надо ровно то, что потом и поедет.
   */
  function benchSetup(st) {
    const s = st || {};
    return (
      '<div class="cols cols--2">' +
      '<div class="field"><label for="b-chunks">Размеры куска, МБ</label>' +
      '<input id="b-chunks" name="chunks" type="text" value="' + esc(s.chunks || '4, 8, 16') + '">' +
      '<span class="help">Через запятую. Сервер принимает от 1 до 32</span></div>' +
      '<div class="field"><label for="b-conc">Потоки</label>' +
      '<input id="b-conc" name="concurrency" type="text" value="' + esc(s.concurrency || '2, 4, 8') + '">' +
      '<span class="help">Больше не значит быстрее: на восьми канал начинает терять куски</span></div>' +
      '</div>' +
      '<div class="field"><label for="b-probe">Сколько лить на пробу, МБ</label>' +
      '<input id="b-probe" name="probe" type="number" min="8" max="512" value="' + esc(String(s.probe || 64)) + '">' +
      '<span class="help">Меньше 32 МБ меряет не канал, а задержку до сервера</span></div>' +
      (s.file
        ? '<div class="handoff"><div><span class="k">Файл пробы</span><span class="v">' + esc(s.file.name) + '</span></div></div>'
        : '<div class="empty"><b>Файл не выбран</b><span>Нужен настоящий архив: синтетика сжимается и меряет не то</span></div>')
    );
  }

  /** Ход прогона: какой набор сейчас и сколько осталось. */
  function benchProgress(state) {
    const s = state || {};
    if (!s.total) return '';
    const f = F();
    return (
      '<div class="meter"><i class="ok" style="width:' + Math.round((s.done / s.total) * 100) + '%"></i></div>' +
      '<p class="note">Набор ' + (s.done + 1) + ' из ' + s.total +
      (s.current ? ': ' + esc(s.current.chunk) + ' на ' + s.current.streams + ' потоках' : '') +
      (s.speed ? ' · ' + esc(f.speed(s.speed)) : '') +
      '</p>'
    );
  }

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
    staleNote,
    inboxFilter,
    anyFilter,
    metricsFilter,
    period,
    feedbackCard,
    replyLink,
    diagnosticsText,
    maintForm,
    localTime,
    isoTime,
    maintProblem,
    errorEvents,
    launcherDiff,
    versionPicker,
    diffCounts,
    sparkPoints,
    sparkLine,
    resolvePlan,
    modVersions,
    modsDiff,
    catalogBar,
    pageLabel,
    catalogList,
    importResult,
    logsView,
    ecosystemPicker,
    gameForm,
    orderList,
    orderSummary,
    benchSetup,
    benchProgress,
    benchTable,
  };
});
