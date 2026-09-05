/* Админ-панель Chill Hub 2.0 — оболочка и разделы
   ------------------------------------------------------------------
   Разделы собраны не по названиям старых вкладок, а по тому, что через
   эту панель на самом деле делают. Из кода админ-API видно, что решений,
   которые принимает человек, ровно два, и оба необратимы для игроков:

     1. Сделать загруженную сборку лаунчера активной.
     2. Пересобрать модпак под вышедшее обновление и активировать его.

   Всё остальное — либо подготовка к этим двум (загрузка, разрешение
   зависимостей, дифф), либо наблюдение (ошибки, обращения, место).
   Поэтому «Обзор» начинается с этих двух решений, а не со счётчиков,
   а кнопка «Сделать активной» — единственная в панели, которая
   спрашивает подтверждение с названием версии.

   Бизнес-логика (чанковая загрузка, NDJSON-поток сборки, публикация
   новостей) осталась в admin.js версии 1.0 и переносится отдельно:
   превью показывает, КАК это выглядит, а не заново реализует, ЧТО это
   делает.
   ------------------------------------------------------------------ */

(() => {
  'use strict';

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const main = $('[data-main]');

  let D = null;
  let game = 'lethal'; // выбранная игра в разделе сборок

  /* Отборы держатся между перерисовками: раздел перечитывается после
     каждой записи, и сбрасывать отбор на «пометил прочитанным» значило
     бы терять место в списке из двухсот обращений. */
  let inboxFilter = {};
  let metricsFilter = { days: 30, gameId: '' };

  /* ---------- Помощники ---------- */

  const esc = (s) =>
    String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  /* Дробная часть отделяется запятой, а не точкой: «8.3 ГБ» и «92.4 МБ/с»
     — английская запись, попавшая в русский интерфейс из `toFixed`. */
  const dec = (n, d = 1) => n.toFixed(d).replace('.', ',');

  function bytes(n) {
    const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) {
      n /= 1024;
      i++;
    }
    return `${n < 10 && i ? dec(n) : Math.round(n)} ${u[i]}`;
  }

  /* Вопрос перед необратимым действием.

     `window.confirm` не годился: он не умеет ни выделить объект, ни
     подписать кнопку глаголом, и в нём нельзя сказать, что именно
     произойдёт с игроками. А безымянное «Вы уверены?» приучает жать
     «да» не читая. */
  function ask(q) {
    return new Promise((resolve) => {
      const back = document.createElement('div');
      back.className = 'modal-back';
      back.innerHTML = `
        <div class="modal" role="dialog" aria-modal="true" aria-labelledby="ask-t">
          <h2 id="ask-t"></h2>
          <p></p>
          <div class="btn-row">
            <button class="btn" type="button" data-no></button>
            <button class="btn btn--danger" type="button" data-yes></button>
          </div>
        </div>`;
      back.querySelector('h2').textContent = q.title;
      back.querySelector('p').textContent = q.body;
      back.querySelector('[data-no]').textContent = q.cancel;
      back.querySelector('[data-yes]').textContent = q.ok;

      const close = (answer) => {
        back.remove();
        document.removeEventListener('keydown', onKey);
        resolve(answer);
      };
      const onKey = (e) => {
        if (e.key === 'Escape') close(false);
      };

      back.querySelector('[data-no]').addEventListener('click', () => close(false));
      back.querySelector('[data-yes]').addEventListener('click', () => close(true));
      back.addEventListener('click', (e) => {
        if (e.target === back) close(false);
      });
      document.addEventListener('keydown', onKey);

      document.body.append(back);
      // Фокус на отказе: опасная кнопка не должна срабатывать пробелом
      back.querySelector('[data-no]').focus();
    });
  }

  function toast(text, tone = '') {
    const el = document.createElement('div');
    el.className = `toast ${tone}`;
    el.textContent = text;
    $('[data-toasts]').append(el);
    setTimeout(() => el.remove(), 4200);
  }

  /* Три состояния списка вместо двух. В панели 1.0 пустое место значило
     и «ничего нет», и «запрос упал», и «ещё грузится». */
  function list({ rows, head, row, empty, emptyHint }) {
    if (!rows.length) {
      return `<div class="empty"><b>${esc(empty)}</b>${emptyHint ? `<span>${esc(emptyHint)}</span>` : ''}</div>`;
    }
    return `<table><thead><tr>${head}</tr></thead><tbody>${rows.map(row).join('')}</tbody></table>`;
  }

  const card = (title, body, { head = '', foot = '', flush = false } = {}) => `
    <section class="card">
      <header><h2>${esc(title)}</h2>${head ? `<span class="push"></span>${head}` : ''}</header>
      <div class="body${flush ? ' flush' : ''}">${body}</div>
      ${foot ? `<footer>${foot}</footer>` : ''}
    </section>`;

  const tree = (files, { id = '' } = {}) =>
    `<div class="tree scroll scroll--lg" ${id ? `data-tree="${id}"` : 'data-tree'}>${files
      .map(
        (f) => `<div class="row ${f.diff || 'file'}" data-path="${esc(f.path)}">
            <span>${f.diff === 'add' ? '+' : f.diff === 'del' ? '−' : f.diff === 'mod' ? '~' : '\u00a0'}</span>
            <span>${esc(f.path)}</span>
            <span class="size">${bytes(f.size)}</span>
          </div>`
      )
      .join('')}</div>`;

  const packOf = (id) => D.packs.find((p) => p.gameId === id) || D.packs[0];

  /* ---------- Разделы ---------- */

  const SECTIONS = {
    /* Обзора в панели 1.0 не было: она открывалась на «Лаунчере», а два
       решения выше узнавались по значкам на двух вкладках из восьми.
       Здесь они — первое, что видно, и каждое ведёт прямо в действие. */
    overview: {
      title: 'Что решить',
      lede: 'Два решения принимает человек. Остальное панель показывает, чтобы не пропустить.',
      render() {
        const L = D.launcher;
        const behind = D.packs.filter((p) => p.behind || p.deprecated);
        const staged = D.packs.filter((p) => p.built !== p.active);
        const unread = D.inbox.filter((f) => f.status === 'new');
        const important = unread.filter((f) => f.important);
        /* Пустая метрика — это «сегодня ещё ничего не случилось», а не
           повод уронить весь обзор: первый день после чистки метрик и
           первый запуск нового сервера выглядят именно так. */
        const freePct = D.disk.total ? Math.round((D.disk.free / D.disk.total) * 100) : 100;
        const drafts = D.news.filter((n) => !n.published).length;
        const today = D.days.at(-1) || { date: '', starts: 0, updates: 0, errors: 0 };
        const errToday = today.errors;

        const decision = (on, title, body, action) => `
          <section class="card decision${on ? ' decision--on' : ''}">
            <header><h2>${esc(title)}</h2>${on ? '<span class="push"></span><span class="badge badge--accent">ждёт решения</span>' : ''}</header>
            <div class="body"><div class="stack stack--tight">${body}${action ? `<div class="btn-row">${action}</div>` : ''}</div></div>
          </section>`;

        const launcherBody = L.pending
          ? `<p>Игроки получают <span class="mono">${esc(L.active)}</span>. Загружена <span class="mono">${esc(L.newest)}</span>${D.diff ? ` — ${D.diff.counts.total} файлов расходятся` : ''}.</p>
             <p class="faint">Пока не активируешь, новая версия лежит на сервере и никому не отдаётся.</p>`
          : `<p>Игроки получают <span class="mono">${esc(L.active)}</span>. Ничего свежее не загружено.</p>`;

        const packsBody = behind.length
          ? `<ul class="plain">${behind
              .map(
                (p) => `<li>
                  <b>${esc(p.title)}</b> — собрана <span class="mono">${esc(p.built)}</span>,
                  ${
                    p.deprecated
                      ? 'модпак объявлен устаревшим'
                      : `на Thunderstore <span class="mono">${esc(p.latest)}</span>${p.latestAt ? ` от ${esc(p.latestAt)}` : ''}`
                  }
                </li>`
              )
              .join('')}</ul>
             <p class="faint">Пересборка тянет до 1,8 ГБ и идёт до двадцати минут. Запускать имеет смысл, когда есть время досмотреть журнал.</p>`
          : '<p>Все сборки собраны из последних версий. Устаревших пакетов нет.</p>';

        const watch = (href, k, v, s, tone) => `
          <a class="attn-item" href="${href}" ${tone ? `data-tone="${tone}"` : ''}>
            <span class="k">${esc(k)}</span><span class="v">${esc(v)}</span><span class="s">${esc(s)}</span>
          </a>`;

        return `
          <div class="cols cols--55">
            ${decision(
              L.pending,
              'Лаунчер',
              launcherBody,
              L.pending
                ? '<a class="btn btn--accent" href="#launcher">Посмотреть, что изменилось</a>'
                : '<a class="btn btn--text" href="#launcher">Открыть раздел</a>'
            )}
            ${decision(
              behind.length > 0,
              'Сборки модов',
              packsBody,
              behind.length ? `<a class="btn btn--accent" href="#packs">Открыть ${esc(behind[0].title)}</a>` : '<a class="btn btn--text" href="#packs">Открыть раздел</a>'
            )}
          </div>

          ${staged.length ? `<div class="note" style="margin-top: var(--s4)">
            Собрано, но не активировано: ${staged.map((p) => `<b>${esc(p.title)}</b> <span class="mono">${esc(p.built)}</span>`).join(', ')}.
            Игроки пока получают предыдущую версию.
          </div>` : ''}

          <h2 style="margin: var(--s5) 0 var(--s3)">За чем следить</h2>
          <div class="attn">
            ${watch('#inbox', 'Обращения', unread.length, important.length ? `${important.length} помечено важным` : 'новых', unread.length ? 'accent' : 'ok')}
            ${watch('#errors', 'Ошибок за сутки', errToday, `при ${today.updates} обновлениях`, errToday > 3 ? 'warn' : 'ok')}
            ${watch('#news', 'Черновики', drafts, 'не опубликованы', drafts ? 'warn' : '')}
            ${watch('#maint', 'Техработы', D.maint.on ? 'включены' : 'выключены', D.maint.on ? 'игроки видят заглушку' : 'сервис отдаёт всё', D.maint.on ? 'bad' : 'ok')}
            ${watch('#transfer', 'Свободно', bytes(D.disk.free), `${100 - freePct}% занято`, freePct < 15 ? 'bad' : freePct < 30 ? 'warn' : 'ok')}
            ${watch('#transfer', 'Кэш архивов', bytes(D.cache.bytes), `${D.cache.files} файлов`, '')}
          </div>`;
      },
    },

    /* Раздел построен вокруг активации, а не вокруг манифеста. Дерево
       файлов здесь не «текущее состояние», а разница между тем, что
       игроки получают сейчас, и тем, что получат после нажатия. */
    launcher: {
      title: 'Лаунчер',
      lede: 'Выкатка самого лаунчера: загрузить, сравнить, отдать игрокам.',
      render() {
        const L = D.launcher;
        /* Разница считается из настоящих манифестов и приезжает уже
           после отрисовки: два файла по мегабайту каждый — не повод
           держать раздел пустым. До неё стоит скелет. */
        const dif = D.diff;
        const diffPair = D.diffPair || {};

        const stateBadge = {
          active: '<span class="badge badge--ok"><span class="dot"></span>у игроков</span>',
          uploaded: '<span class="badge badge--accent"><span class="dot"></span>загружена</span>',
          old: '<span class="badge">старая</span>',
        };

        return `
          ${
            L.pending
              ? `<div class="handoff">
                   <div>
                     <span class="k">Игроки получают</span>
                     <span class="v mono">${esc(L.active)}</span>
                   </div>
                   <span class="arrow" aria-hidden="true">→</span>
                   <div>
                     <span class="k">Загружена и ждёт</span>
                     <span class="v mono">${esc(L.newest)}</span>
                   </div>
                   <div class="push"></div>
                   <button class="btn btn--accent" type="button" data-act="launcher.activate" data-args='{"version":"${esc(L.newest)}"}'>
                     Сделать активной
                   </button>
                 </div>`
              : `<div class="note">Игроки получают <span class="mono">${esc(L.active)}</span>. Загруженных версий новее нет — активировать нечего.</div>`
          }

          <div class="cols cols--55" style="margin-top: var(--s4)">
            <div class="sticky">
              ${card(
                `Что изменится у игрока`,
                `${V().versionPicker(L.versions, diffPair.from || L.active, diffPair.to || L.newest)}
                 <div data-diff>${
                   dif === undefined
                     ? '<div class="sk" style="height:12rem"></div>'
                     : V().launcherDiff(dif, { active: diffPair.from || L.active })
                 }</div>`,
                {
                  head: `<span data-diff-counts>${dif ? V().diffCounts(dif) : ''}</span>`,
                  foot: dif
                    ? `${dif.counts.total} файлов из ${dif.total} расходятся между <code>${esc(L.active)}</code> и <code>${esc(L.newest)}</code>. Остальное клиент не скачивает.`
                    : 'Клиент качает только расходящиеся файлы, а не сборку целиком.',
                }
              )}
            </div>

            <div class="stack">
              ${card(
                'Загрузить новую версию',
                `<div class="stack">
                   <div class="field">
                     <label for="up-file">ZIP со сборкой</label>
                     <input id="up-file" type="text" placeholder="перетащи файл или выбери" readonly>
                     <span class="help">Загрузка идёт кусками и продолжается после обрыва. Загруженная версия никому не отдаётся, пока её не активируют.</span>
                   </div>
                   <div class="btn-row">
                     <button class="btn btn--accent" type="button" data-act="upload">Выбрать файл</button>
                   </div>
                 </div>`
              )}
              ${card(
                'Версии на сервере',
                list({
                  rows: L.versions,
                  head: '<th>Версия</th><th>Собрана</th><th class="num">Файлов</th><th class="num">Размер</th><th>Состояние</th><th></th>',
                  row: (v) => `<tr>
                      <td class="mono">${esc(v.version)}</td>
                      <td class="dim">${esc(v.date)}</td>
                      <td class="num">${v.files}</td>
                      <td class="num">${bytes(v.size)}</td>
                      <td>${stateBadge[v.state]}</td>
                      <td class="act">
                        ${v.state === 'active' ? '' : `<button class="btn btn--text" type="button" data-act="launcher.activate" data-args='{"version":"${esc(v.version)}"}'>Активировать</button>`}
                        ${v.state === 'active' ? '' : `<button class="btn btn--danger btn--text" type="button" data-act="launcher.delete" data-args='{"version":"${esc(v.version)}"}'>Удалить</button>`}
                      </td>
                    </tr>`,
                  empty: 'Ни одной версии не загружено',
                }),
                {
                  flush: true,
                  foot: 'Активную версию удалить нельзя: клиенты, которые её докачивают, потеряют файлы на середине.',
                }
              )}
            </div>
          </div>`;
      },
    },

    /* Никакой «модерации» здесь нет и не было. Панель собирает модпак
       из Thunderstore: каталог → состав → сборка потоком → дифф →
       активация. Раздел показывает эту цепочку целиком, потому что
       человек проходит её подряд и в одном заходе. */
    packs: {
      title: 'Сборки модов',
      lede: 'Наборы модов, которые лаунчер ставит игроку. Собираются из Thunderstore.',
      /* Пересборка доступна всегда, а не только когда на Thunderstore
         вышло новое: пересобрать после правки состава мод-листа нужно и
         тогда, когда сам Thunderstore не менялся. */
      actions: '<button class="btn" type="button" data-act="build">Собрать заново</button>',
      render() {
        const p = packOf(game);
        const stale = p.behind || p.deprecated;
        const staged = p.built !== p.active;

        const tabs = D.packs
          .map(
            (g) => `<button class="seg${g.gameId === game ? ' on' : ''}" type="button" data-game="${g.gameId}">
                ${esc(g.title)}${g.behind || g.deprecated ? '<span class="dot warn"></span>' : ''}
              </button>`
          )
          .join('');

        return `
          <div class="segs">${tabs}</div>

          <div class="handoff" style="margin-top: var(--s3)">
            <div><span class="k">Игроки получают</span><span class="v mono">${esc(p.active)}</span></div>
            <span class="arrow" aria-hidden="true">→</span>
            <div><span class="k">Собрано</span><span class="v mono">${esc(p.built)}</span><span class="k">${esc(p.builtAt)}</span></div>
            <span class="arrow" aria-hidden="true">→</span>
            <div>
              <span class="k">На Thunderstore</span>
              <span class="v mono">${esc(p.latest || '—')}</span>
              <span class="k">${esc(p.latestAt || 'дата не приходит с Thunderstore')}</span>
            </div>
            <div class="push"></div>
            ${staged ? `<button class="btn" type="button" data-act="mods-diff" data-args='{"gameId":"${esc(p.gameId)}","from":"${esc(p.active)}","to":"${esc(p.built)}","title":"${esc(p.title)}"}'>Что изменится</button>` : ''}
            ${staged ? `<button class="btn btn--accent" type="button" data-act="mods.activate" data-args='{"gameId":"${esc(p.gameId)}","version":"${esc(p.built)}"}'>Отдать игрокам</button>` : ''}
            ${stale ? '<button class="btn btn--accent" type="button" data-act="build">Пересобрать</button>' : ''}
          </div>

          ${
            p.deprecated
              ? `<div class="note note--bad">Модпак <code>${esc(p.pack)}</code> объявлен автором устаревшим. Пересборка возьмёт последнюю доступную версию, но стоит подобрать замену.</div>`
              : ''
          }

          <div class="cols cols--55" style="margin-top: var(--s4)">
            <div class="stack">
              ${/* Состав, каталог и журнал живут в своих листах, а не
                    таблицами прямо здесь. Причина одна на три: каждое из
                    этих дел ходит к Thunderstore и идёт секундами, а то
                    и минутами. Таблица в разделе показывала бы их
                    прошлый результат — то есть числа, к которым сейчас
                    никто не обращался, и по которым не видно, свежие они
                    или позавчерашние. */ ''}
              ${card(
                'Состав будущей сборки',
                `<div class="stack stack--tight">
                   <p class="dim">Thunderstore отвечает списком: сколько пакетов, какой загрузчик, сколько качать с учётом кэша и каких пакетов больше нет.</p>
                   <p class="faint">Ничего не скачивается и никуда не уходит. Пропавший пакет лучше увидеть здесь, чем на середине выкатки.</p>
                   <div class="btn-row"><button class="btn" type="button" data-act="resolve">Посчитать состав</button></div>
                 </div>`
              )}

              ${card(
                'Каталог Thunderstore',
                `<div class="stack stack--tight">
                   <p class="dim">Поиск по модпакам этой игры. Половина из них в раздел «Modpacks» не проставлена и не находится — такие подставляются ссылкой на страницу пакета.</p>
                   <p class="faint">Запросы идут через сервер, а не из браузера: иначе панель светила бы трафик третьей стороне.</p>
                   <div class="btn-row"><button class="btn" type="button" data-act="choose">Открыть каталог</button></div>
                 </div>`
              )}
            </div>

            <div class="stack">
              ${card(
                'Сборка',
                `<div class="stack stack--tight">
                   <p class="dim">Сборка тянет до 1,8 ГБ полутора сотнями запросов и идёт минутами, поэтому журнал показывается строка за строкой, пока она работает.</p>
                   <p class="faint">Собранное игрокам само не уходит — отдать его отдельное решение.</p>
                   <div class="btn-row"><button class="btn btn--accent" type="button" data-act="build">Собрать</button></div>
                 </div>`
              )}

              ${card(
                'Переезд со старой сборки',
                `<div class="stack stack--tight">
                   <p class="dim">В профиле r2modman перечислены все моды с точными версиями, поэтому набор, который у игроков уже стоит, публикуется как есть, а не собирается заново на глаз.</p>
                   <div class="btn-row"><button class="btn" type="button" data-act="import">Выбрать файл профиля</button></div>
                 </div>`
              )}
            </div>
          </div>`;
      },
    },

    /* Реестр игр — это не таблица «для красоты», а то, что лаунчер
       читает при старте: идентификатор Steam, имя исполняемого файла,
       обложка и галерея. Ошибка здесь ломает запуск у всех сразу. */
    games: {
      title: 'Игры',
      lede: 'Реестр, который лаунчер читает при старте: чем игра запускается и как выглядит.',
      actions:
        '<button class="btn" type="button" data-act="games.scan">Просканировать контент</button>' +
        '<button class="btn" type="button" data-act="order">Порядок в лаунчере</button>' +
        '<button class="btn btn--accent" type="button" data-act="new-game">Добавить игру</button>',
      render() {
        return `
          ${card(
            'Реестр',
            list({
              rows: D.games,
              head: '<th>Игра</th><th>Идентификатор</th><th>Steam</th><th>Исполняемый файл</th><th>Оформление</th><th></th>',
              row: (g) => `<tr>
                  <td>${esc(g.title)}</td>
                  <td><code>${esc(g.gameId)}</code></td>
                  <td class="mono">${esc(g.steamId)}</td>
                  <td class="mono dim">${esc(g.exe)}</td>
                  <td>
                    ${
                      /* Про иконку реестр знает — она лежит в нём полем.
                         Про обложку и снимки не знает: они живут в
                         галерее, и придумывать за них значок здесь
                         значило бы показывать «всё в порядке» у игры без
                         единой картинки. */
                      g.icon
                        ? '<span class="badge badge--ok">иконка есть</span>'
                        : '<span class="badge badge--warn">без иконки</span>'
                    }
                    ${g.published ? '' : '<span class="badge badge--warn">скрыта от игроков</span>'}
                  </td>
                  <td class="act">
                    <button class="btn btn--text" type="button" data-act="edit-game" data-args='{"gameId":"${esc(g.gameId)}"}'>Править</button>
                    <button class="btn btn--text" type="button" data-act="gallery" data-args='{"gameId":"${esc(g.gameId)}"}'>Галерея</button>
                    <button class="btn btn--danger btn--text" type="button" data-act="games.purge" data-args='{"gameId":"${esc(g.gameId)}","title":"${esc(g.title)}"}'>Удалить контент</button>
                  </td>
                </tr>`,
              empty: 'Реестр пуст',
              emptyHint: 'Пока здесь ничего нет, лаунчер показывает игроку пустую библиотеку.',
            }),
            { flush: true }
          )}

          <div class="cols cols--55" style="margin-top: var(--s3)">
            ${card(
              'Подтянуть из Thunderstore',
              `<div class="stack stack--tight">
                 <p class="dim">Заполняет идентификатор Steam, имя исполняемого файла и папку установки из схемы экосистемы Thunderstore.</p>
                 <p class="faint">Руками это копирование трёх значений на игру, и папка, вложенная внутрь каталога установки, с первого раза угадывается неправильно.</p>
                 <div class="btn-row"><button class="btn" type="button" data-act="ecosystem">Подтянуть</button></div>
               </div>`
            )}
            ${(() => {
              // Фраза считается из реестра, а не вписана руками: вписанная
              // устареет на первой же правке данных и будет врать молча.
              const noIcon = D.games.filter((g) => !g.icon).map((g) => g.title);
              const hidden = D.games.filter((g) => !g.published).map((g) => g.title);
              const gaps = [
                noIcon.length ? `без иконки: ${noIcon.join(', ')}` : '',
                hidden.length ? `скрыты от игроков: ${hidden.join(', ')}` : '',
              ].filter(Boolean);
              return card(
                'Что видит игрок',
                `<div class="stack stack--tight">
                   <p class="dim">Иконка стоит в списке слева, обложка и снимки — на странице игры. Без обложки карточка выглядит пустым прямоугольником.</p>
                   <p class="faint">${gaps.length ? esc(gaps.join('; ')) + '.' : 'У всех игр есть иконка, и все они видны игрокам.'}</p>
                   <p class="faint">Что лежит в галерее, реестр не знает — это видно только в ней самой.</p>
                 </div>`
              );
            })()}
          </div>`;
      },
    },

    news: {
      title: 'Новости',
      lede: 'То, что игрок читает на главном экране лаунчера.',
      actions:
        '<button class="btn" type="button" data-act="news.rebuild">Пересобрать индекс</button>' +
        '<button class="btn btn--accent" type="button" data-act="new-post">Написать</button>',
      render() {
        /* Редактор больше не живёт в разделе: новость набирают минутами,
           и на этот срок ей нужен свой экран, черновик и предпросмотр.
           Раздел показывает то, что есть, и ведёт к правке. */
        return card(
          'Новости',
          list({
            rows: D.news,
            head: '<th>Заголовок</th><th>Состояние</th><th></th>',
            /* Заметка называется адресом целиком — scope, игра и имя
               файла. По одному номеру сервер её не найдёт. */
            row: (n) => {
              const at = `"scope":"${esc(n.scope || 'launcher')}","gameId":"${esc(n.game)}","slug":"${esc(n.slug)}"`;
              return `<tr>
                <td>${esc(n.title)}<br><span class="faint">${esc(n.at)}${n.game ? ` · ${esc(n.game)}` : ' · лаунчер'}</span></td>
                <td>${
                  n.published
                    ? '<span class="badge badge--ok">на виду</span>'
                    : '<span class="badge badge--warn">черновик</span>'
                }</td>
                <td class="act">
                  <button class="btn btn--text" type="button" data-act="edit-post" data-args='{${at},"published":${n.published}}'>Править</button>
                  <button class="btn btn--text" type="button" data-act="news.publish" data-args='{${at},"title":"${esc(n.title)}","published":${n.published ? 'false' : 'true'}}'>${n.published ? 'Снять с публикации' : 'Опубликовать'}</button>
                  <button class="btn btn--danger btn--text" type="button" data-act="news.delete" data-args='{${at},"title":"${esc(n.title)}"}'>Удалить</button>
                </td>
              </tr>`;
            },
            empty: 'Новостей нет',
            emptyHint: 'Лаунчер покажет игроку пустую ленту, пока здесь ничего не написано.',
          }),
          { flush: true }
        );
      },
    },

    inbox: {
      title: 'Обращения',
      lede: 'Что пишут из лаунчера. Контакт необязателен, поэтому ответить получится не на всё.',
      render() {
        const t = { bug: 'поломка', question: 'вопрос', idea: 'идея', other: 'прочее' };
        const tone = { bug: 'bad', question: 'accent', idea: 'ok', other: '' };
        const news = D.inbox.filter((f) => f.status === 'new').length;

        const rows = window.CH2Sections.filterInbox(D.inbox, inboxFilter);
        return V().inboxFilter(inboxFilter) + card(
          'Входящие',
          list({
            rows: rows,
            head: '<th>Тип</th><th>Обращение</th><th>Кто</th><th>Когда</th><th></th>',
            row: (f) => `<tr${f.status === 'new' ? ' class="unread"' : ''}>
                <td><span class="badge ${tone[f.type] ? `badge--${tone[f.type]}` : ''}">${t[f.type]}</span></td>
                <td>
                  ${esc(f.comment)}
                  ${f.logBytes ? `<br><button class="btn btn--text" type="button" data-act="logs" data-args='{"id":"${esc(f.id)}"}'>Журналы, ${bytes(f.logBytes)}</button>` : ''}
                </td>
                <td class="dim">${f.name ? esc(f.name) : '<span class="faint">без имени</span>'}${
                  f.contact ? `<br><span class="faint mono">${esc(f.contact)}</span>` : '<br><span class="faint">ответить некуда</span>'
                }</td>
                <td class="dim">${esc(f.at)}</td>
                <td class="act">
                  <button class="btn btn--text" type="button" data-act="feedback" data-args='{"id":"${esc(f.id)}"}'>Открыть</button>
                  <button class="btn btn--text" type="button" data-act="inbox.important" data-args='{"id":"${esc(f.id)}","important":${f.important ? 'false' : 'true'}}' title="Пометить важным">${f.important ? '★' : '☆'}</button>
                  <button class="btn btn--text" type="button" data-act="inbox.read" data-args='{"id":"${esc(f.id)}","read":${f.status === 'new' ? 'true' : 'false'}}'>${f.status === 'new' ? 'Отметить прочитанным' : 'Вернуть в новые'}</button>
                  <button class="btn btn--danger btn--text" type="button" data-act="inbox.delete" data-args='{"id":"${esc(f.id)}"}'>Удалить</button>
                </td>
              </tr>`,
            empty: V().anyFilter(inboxFilter) ? 'Под отбор ничего не попало' : 'Обращений нет',
            emptyHint: 'Пусто — это хорошая новость.',
          }),
          {
            flush: true,
            head: `<span class="badge ${news ? 'badge--accent' : ''}">${news} новых</span>`,
            foot: 'Журналы прикладывает сам игрок отдельной галочкой. В них есть пути к папкам — не пересылай их дальше.',
          }
        );
      },
    },

    maint: {
      title: 'Технические работы',
      lede: 'Заглушка вместо каталога — у всех игроков сразу.',
      render() {
        const m = D.maint;
        return `
          <div class="cols cols--55">
            ${card(
              'Режим',
              `<div class="stack" data-maint>
                 <div class="btn-row">
                   <span class="badge badge--${m.on ? 'bad' : 'ok'}"><span class="dot"></span>${m.on ? 'включены' : 'выключены'}</span>
                   <span class="faint">${m.on ? 'лаунчер показывает заглушку и не отдаёт сборки' : 'всё работает обычным образом'}</span>
                 </div>
                 ${V().maintForm(m)}
                 <div class="btn-row">
                   ${
                     m.on
                       ? '<button class="btn" type="button" data-act="maint.save">Сохранить</button>' +
                         '<span class="push"></span>' +
                         '<button class="btn btn--accent" type="button" data-act="maint.off">Выключить работы</button>'
                       : '<button class="btn btn--danger" type="button" data-act="maint.on">Включить работы</button>'
                   }
                 </div>
               </div>`
            )}
            ${card(
              'Что происходит с игроком',
              `<div class="stack stack--tight">
                 <p class="dim">Каталог игр, манифесты сборок и новости перестают отдаваться. Уже скачанные сборки продолжают запускаться: игра стартует локально — если не закрыть и запуск.</p>
                 <p class="faint">Самообновление лаунчера при включённых работах тоже молчит — иначе клиент уйдёт в цикл проверки версии.</p>
                 ${
                   m.on && !m.endsAt
                     ? '<div class="note note--bad">Окончание не назначено: работы придётся выключать руками. Забытые включёнными — это тихо не работающий лаунчер у всех сразу.</div>'
                     : ''
                 }
               </div>`
            )}
          </div>`;
      },
    },

    /* Раздел назывался «Метрики» и показывал графики. Но собирают
       события ради одного: понять, где у игроков рвётся загрузка.
       Поэтому сверху коды ошибок, а динамика — под ними. */
    errors: {
      title: 'Ошибки у игроков',
      lede: 'Ради чего собираются события: где именно ломается загрузка и запуск.',
      render() {
        /* Имена полей — те, что отдаёт разбор ответа, а не те, что в
           JSON сервера: иначе весь раздел считает undefined и рисует
           NaN. Геометрия графика — в views.js, там же её и проверяют:
           пустой ряд, один день и ряд из одних нулей ломали её молча. */
        const w = 640;
        const h = 140;
        const line = (key, color) => V().sparkLine(D.days.map((d) => d[key]), { width: w, height: h, color: color });
        const sum = (k) => D.days.reduce((a, d) => a + (Number(d[k]) || 0), 0);
        const share = sum('updates') > 0 ? sum('errors') / sum('updates') : 0;

        return `
          ${V().metricsFilter(metricsFilter, D.games)}
          ${card(
            `Коды ошибок за ${metricsFilter.days} дней${metricsFilter.gameId ? ': ' + esc(metricsFilter.gameId) : ''}`,
            list({
              rows: D.errors,
              head: '<th>Код</th><th>Что это значит</th><th>Где чаще</th><th class="num">Случаев</th><th class="num">Доля</th>',
              row: (e) => `<tr>
                  <td class="mono"><button class="btn btn--text" type="button" data-act="error-events" data-args='{"code":"${esc(e.code)}"}'>${esc(e.code)}</button></td>
                  <td class="dim">${esc(e.what)}</td>
                  <td class="mono faint">${esc(e.where)}</td>
                  <td class="num">${e.n}</td>
                  <td class="num" style="width:120px">
                    <div class="meter"><i class="${e.share > 0.3 ? 'bad' : 'warn'}" style="width:${Math.round(e.share * 100)}%"></i></div>
                  </td>
                </tr>`,
              empty: 'Ошибок не было',
              emptyHint: 'Либо всё работает, либо сбор событий выключен у всех.',
            }),
            {
              flush: true,
              foot: 'Событие не содержит ни имён файлов на диске игрока, ни путей установки — только код, игру и версию.',
            }
          )}

          <div class="attn" style="margin: var(--s4) 0">
            <div class="attn-item"><span class="k">Запусков лаунчера</span><span class="v">${sum('starts')}</span><span class="s">за 30 дней</span></div>
            <div class="attn-item"><span class="k">Установок</span><span class="v">${sum('installs')}</span><span class="s">первых, с нуля</span></div>
            <div class="attn-item"><span class="k">Обновлений</span><span class="v">${sum('updates')}</span><span class="s">докачек разницы</span></div>
            <div class="attn-item" data-tone="${sum('errors') / sum('updates') > 0.1 ? 'warn' : 'ok'}"><span class="k">Доля ошибок</span><span class="v">${dec((sum('errors') / sum('updates')) * 100)}\u00a0%</span><span class="s">от обновлений</span></div>
          </div>

          ${card(
            'Динамика',
            `<svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" preserveAspectRatio="none" role="img" aria-label="Запуски игр, обновления и ошибки за 30 дней">
               ${line('launches', 'var(--ember)')}
               ${line('updates', 'var(--ok)')}
               ${line('errors', 'var(--bad)')}
             </svg>
             <div class="btn-row" style="margin-top: var(--s2)">
               <span class="badge badge--accent"><span class="dot"></span>запуски игр</span>
               <span class="badge badge--ok"><span class="dot"></span>обновления</span>
               <span class="badge badge--bad"><span class="dot"></span>ошибки</span>
             </div>`
          )}`;
      },
    },

    /* «Бенчмарки» ничего не говорили о том, что меряют. Меряют
       параметры отдачи файлов, и рядом с ними место — кэш архивов,
       который эти же файлы и занимает. */
    transfer: {
      title: 'Диск и загрузки',
      lede: 'Параметры загрузки, кэш архивов и свободное место на диске с контентом.',
      render() {
        /* Ноль в знаменателе — не «сто процентов свободно», а «места
           не посчитали»: сервер мог не ответить, и делить тут нечего. */
        const freePct = D.disk.total ? Math.round((D.disk.free / D.disk.total) * 100) : 0;

        return `
          <div class="cols cols--55">
            ${card(
              'Место на диске с контентом',
              `<div class="stack stack--tight">
                 <div class="btn-row">
                   <span class="num" style="font-size:20px">${bytes(D.disk.free)}</span>
                   <span class="faint">свободно из ${bytes(D.disk.total)}</span>
                 </div>
                 <div class="meter"><i class="${freePct < 15 ? 'bad' : freePct < 30 ? 'warn' : 'ok'}" style="width:${100 - freePct}%"></i></div>
                 <p class="faint">Сборки и манифесты лежат здесь же. Загрузка новой версии на заполненный диск падает на середине и оставляет обрывок.</p>
               </div>`
            )}
            ${card(
              'Кэш скачанных архивов',
              `<div class="stack stack--tight">
                 <div class="btn-row">
                   <span class="num" style="font-size:20px">${bytes(D.cache.bytes)}</span>
                   <span class="faint">${D.cache.files} файлов, старейший от ${esc(D.cache.oldest)}</span>
                 </div>
                 <p class="faint">Кэш экономит время пересборки: те же архивы Thunderstore не качаются повторно. Чистить имеет смысл, когда место кончается, а не по расписанию.</p>
                 <div class="btn-row">
                   <button class="btn" type="button" data-act="cache.sweep">Убрать старое</button>
                   <button class="btn btn--danger btn--text" type="button" data-act="cache.clear">Очистить полностью</button>
                 </div>
               </div>`
            )}
          </div>

          <div style="margin-top: var(--s3)">
            ${(() => {
              /* Таблица прогонов живёт в своём листе, а не здесь: она
                 нужна ровно в тот момент, когда подбирают параметры, и
                 второй её копией в разделе управлять было нечем —
                 кнопка «Применить» тут ни к чему не вела. */
              const T = window.CH2Tuning;
              /* Прогон меряет канал ЭТОГО компьютера, поэтому и лежит
                 он в этом браузере: с другой машины его число не значит
                 ничего, а показанное как общее — сбивает с толку. */
              const runs = T.recall(window.localStorage);
              const best = T.best(runs);
              return card(
                'Подбор параметров загрузки',
                `<div class="stack stack--tight">
                   <p class="dim">${best ? esc(T.why(runs)) : 'Прогонов ещё не было. Прогон занимает около минуты и ничего не публикует.'}</p>
                   <p class="faint">Больше потоков не всегда быстрее: на восьми канал начинает терять куски и переспрашивать их заново.</p>
                   <div class="btn-row"><button class="btn" type="button" data-act="bench">${best ? 'Прогнать заново' : 'Запустить прогон'}</button></div>
                 </div>`
              );
            })()}
          </div>`;
      },
    },
  };

  /* ---------- Длинные дела ---------- */

  /* Шесть дел панели идут минутами и умеют оборваться на середине:
     загрузка сборки, сборка модпака, новость, галерея, порядок игр и
     подбор параметров. Каждое открывается листом поверх раздела — так
     видно, что дело идёт, и видно, к чему вернуться, когда оно кончится.

     Правила внутри дел сюда не переехали: они лежат в своих модулях
     (`upload.js`, `build.js`, `news.js`, `gallery.js`, `registry.js`,
     `tuning.js`), а вид — в `views.js`. Здесь только связывание. */

  const V = () => window.CH2Views;

  function openSheet(o) {
    const host = document.createElement('div');
    host.innerHTML = V().sheet(o);
    const back = host.firstElementChild;
    document.body.append(back);

    const h = {
      root: back,
      body: (html) => {
        back.querySelector('[data-sheet-body]').innerHTML = html;
      },
      foot: (html) => {
        const f = back.querySelector('[data-sheet-foot]');
        if (f) f.innerHTML = html;
      },
      close: () => {
        if (h.onClose) h.onClose();
        back.remove();
        document.removeEventListener('keydown', onKey);
      },
      onClose: null,
    };

    const onKey = (e) => {
      if (e.key === 'Escape') h.close();
    };
    back.querySelector('[data-sheet-close]').addEventListener('click', () => h.close());
    back.addEventListener('click', (e) => {
      if (e.target === back) h.close();
    });
    document.addEventListener('keydown', onKey);
    back.querySelector('[data-sheet-close]').focus();
    return h;
  }

  /** Кнопки подвала листа по описанию из `views.js`. */
  const footButtons = (items) =>
    items
      .map(
        (b) =>
          `<button class="btn${b.accent ? ' btn--accent' : ''}${b.danger ? ' btn--danger' : ''}" type="button" data-flow="${b.act}">${esc(b.title)}</button>`
      )
      .join('');

  /* --- Загрузка сборки --- */

  /* Кусок и число потоков подбираются от размера файла тем же модулем,
     что и в панели 1.0, — и показываются до нажатия, а не после. */
  function flowUpload(meta) {
    const sheet = openSheet({
      title: meta.kind === 'mods' ? 'Загрузка модпака' : 'Загрузка сборки лаунчера',
      lede: 'Файл заливается кусками и переживает обрыв связи. Игрокам он сам не уйдёт.',
      body: V().uploadCard({}),
      foot: footButtons(V().uploadButtons({})),
    });

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.zip';

    let st = { phase: 'idle' };
    let ctrl = null;
    let uploadId = '';

    const draw = () => {
      sheet.body(V().uploadCard(st));
      sheet.foot(footButtons(V().uploadButtons(st)));
    };

    /* Закрытие листа посреди заливки — это отмена, а не сворачивание:
       брошенная загрузка оставила бы на сервере недособранный архив. */
    sheet.onClose = () => {
      if (ctrl) ctrl.abort();
      if (uploadId && st.phase !== 'done') window.CH2Upload.abort(API, uploadId);
    };

    async function start(file) {
      const params = window.pickUploadParams
        ? window.pickUploadParams(file.size, {})
        : { chunkSize: window.CH2Upload.DEFAULT_CHUNK, concurrency: 4 };
      ctrl = new AbortController();
      st = {
        phase: 'init',
        file: { name: file.name, size: file.size },
        chunkSize: params.chunkSize,
        streams: params.concurrency,
        progress: 0,
      };
      draw();

      try {
        const res = await window.CH2Upload.run(
          file,
          { kind: meta.kind, gameId: meta.gameId, version: meta.version, chunkSize: params.chunkSize },
          {
            api: API,
            chunks: {
              uploadChunkWithRetries: window.uploadChunkWithRetries,
              runWorkerPool: window.runWorkerPool,
              pendingBytes: window.pendingBytes,
            },
            concurrency: () => params.concurrency,
            signal: ctrl.signal,
            on: (ev) => {
              st = Object.assign({}, st, ev);
              draw();
            },
          }
        );
        uploadId = res.uploadId;

        st = Object.assign({}, st, { phase: 'process', progress: 1 });
        draw();
        const done = await window.CH2Upload.process(uploadId, {
          fetch: window.fetch.bind(window),
          ndjson: { readNdjsonStream: window.readNdjsonStream },
          format: window.CH2Format,
          on: (m) => {
            const line = sheet.root.querySelector('[data-upload-status]');
            if (line) line.textContent = m.text;
          },
        });

        st = done.ok
          ? Object.assign({}, st, { phase: 'done' })
          : Object.assign({}, st, { phase: 'failed', message: done.message });
        draw();
        if (done.ok) await store.invalidate(['launcher', 'overview', 'disk']);
      } catch (e) {
        st = Object.assign({}, st, { phase: 'failed', message: (e && e.message) || 'сбой' });
        draw();
      }
    }

    input.addEventListener('change', () => {
      if (input.files && input.files[0]) start(input.files[0]);
    });

    sheet.root.addEventListener('click', (e) => {
      const b = e.target.closest('[data-flow]');
      if (!b) return;
      const act = b.dataset.flow;
      if (act === 'pick' || act === 'retry') input.click();
      if (act === 'abort') {
        if (ctrl) ctrl.abort();
        if (uploadId) window.CH2Upload.abort(API, uploadId);
        st = Object.assign({}, st, { phase: 'aborted' });
        draw();
      }
      if (act === 'close') {
        sheet.onClose = null;
        sheet.close();
        route();
      }
    });

    return { pick: (file) => start(file), sheet };
  }

  /* --- Сборка модпака --- */

  function flowBuild(pack) {
    const sheet = openSheet({
      title: 'Сборка модпака: ' + (pack.title || pack.gameId),
      lede: 'Идёт минутами. Собранное игрокам само не уходит — отдать его отдельное решение.',
      body: V().buildLog([], 'running'),
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    const events = [];

    window.CH2Build.run(
      { gameId: pack.gameId, namespace: pack.namespace, name: pack.name },
      {
        fetch: window.fetch.bind(window),
        ndjson: { readNdjsonStream: window.readNdjsonStream },
        confirm: ask,
        on: (ev) => {
          events.push(ev);
          sheet.body(V().buildLog(events, 'running'));
          const log = sheet.root.querySelector('.log');
          if (log) log.scrollTop = log.scrollHeight;
        },
      }
    ).then(async (res) => {
      const out = V().buildOutcome(res);
      sheet.body(
        V().buildLog(events, 'done') +
          `<p class="note${out.tone === 'bad' ? ' note--bad' : ''}" data-build-outcome>${esc(out.text)}</p>`
      );
      toast(out.text, out.tone);
      await store.invalidate(['packs', 'overview']);
    });

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="close"]')) {
        sheet.close();
        route();
      }
    });
  }

  /* --- Новость --- */

  /* Черновик пишется в браузер на каждый ввод. Новость набирают минутами,
     и терять её из-за случайно закрытой вкладки нельзя. */
  function flowNews(where) {
    const N = window.CH2News;
    const w = where || {};
    const existing = Boolean(w.slug);

    let post = {
      slug: w.slug || '',
      gameId: w.gameId || '',
      markdown: '',
      coverUrl: '',
      published: Boolean(w.published),
      existing: existing,
    };
    let draft = N.readDraft(window.localStorage, post);

    const sheet = openSheet({
      title: existing ? 'Правка заметки: ' + post.slug : 'Новая заметка',
      lede: 'Заголовок — первая строка текста. Черновик хранится в браузере, пока заметка не отправлена.',
      body: '<div class="sk" style="height:22rem"></div>',
      foot:
        '<button class="btn" type="button" data-flow="assets">Вложения</button>' +
        '<button class="btn" type="button" data-flow="preview">Посмотреть глазами игрока</button>' +
        '<span class="push"></span>' +
        '<button class="btn btn--accent" type="button" data-flow="save">Сохранить</button>',
    });

    const draw = (problems) => {
      sheet.body(
        V().draftNote(draft, post, N) +
          V().newsForm(post, problems || []) +
          V().newsHeadline(post.markdown, N)
      );
    };

    const read = () => {
      const q = (n) => sheet.root.querySelector('[name="' + n + '"]');
      if (!q('markdown')) return;
      post = Object.assign({}, post, {
        slug: q('slug').value,
        gameId: q('gameId').value,
        coverUrl: q('coverUrl').value,
        markdown: q('markdown').value,
      });
    };

    (async () => {
      if (existing) {
        try {
          const got = await API.newsGet(post.gameId ? 'game' : 'launcher', post.gameId, post.slug);
          post = Object.assign(post, {
            markdown: (got && got.markdown) || '',
            coverUrl: (got && got.coverUrl) || '',
            published: Boolean(got && got.published),
          });
          draft = N.readDraft(window.localStorage, post);
        } catch (err) {
          toast('Заметка не прочиталась: ' + window.CH2Api.reason(err), 'warn');
        }
      }
      draw();
    })();

    /* Заголовок здесь не поле, а первая строка текста, поэтому строка
       «в ленте игрок увидит» пересчитывается на каждый ввод. */
    /* Обложку можно загрузить файлом, но только у сохранённой заметки:
       сервер кладёт её рядом с самой заметкой, а той ещё нет. */
    const coverInput = document.createElement('input');
    coverInput.type = 'file';
    coverInput.accept = 'image/*';
    coverInput.addEventListener('change', async () => {
      if (!coverInput.files || !coverInput.files[0]) return;
      try {
        const got = await API.newsCoverUpload(
          post.gameId ? 'game' : 'launcher',
          post.gameId,
          post.slug,
          coverInput.files[0]
        );
        post.coverUrl = (got && (got.coverUrl || got.url)) || post.coverUrl;
        draw();
        toast('Обложка загружена', 'ok');
      } catch (err) {
        toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
      }
    });

    sheet.root.addEventListener('input', () => {
      read();
      N.saveDraft(window.localStorage, post);
      const head = sheet.root.querySelector('.note:last-of-type');
      if (head) head.outerHTML = V().newsHeadline(post.markdown, N);
    });

    /* Имя файла предлагается из заголовка, пока его не тронули руками:
       у новой заметки оно всё равно нужно, а придумывать его дважды
       (заголовок и имя) — работа на пустом месте. */
    sheet.root.addEventListener('input', (e) => {
      if (existing || !e.target.matches('[name="markdown"]')) return;
      const slugField = sheet.root.querySelector('[name="slug"]');
      if (!slugField || slugField.dataset.touched) return;
      slugField.value = N.suggestSlug(N.titleOf(post.markdown));
      post.slug = slugField.value;
    });
    sheet.root.addEventListener('change', (e) => {
      if (e.target.matches('[name="slug"]')) e.target.dataset.touched = '1';
    });

    sheet.root.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-flow], [data-draft-restore], [data-draft-drop]');
      if (!b) return;

      if (b.hasAttribute('data-draft-restore')) {
        post = Object.assign({}, post, draft.post);
        draft = null;
        draw();
        return;
      }
      if (b.hasAttribute('data-draft-drop')) {
        N.dropDraft(window.localStorage, post);
        draft = null;
        draw();
        return;
      }

      read();

      if (b.dataset.flow === 'cover') {
        coverInput.click();
        return;
      }

      if (b.dataset.flow === 'assets') {
        flowAssets((markup) => {
          const area = sheet.root.querySelector('[name="markdown"]');
          const at = area ? area.selectionStart : post.markdown.length;
          post.markdown = N.insertAt(post.markdown, at, markup);
          N.saveDraft(window.localStorage, post);
          draw();
        });
        return;
      }

      const problems = N.problems(post);
      if (problems.length) {
        draw(problems);
        toast('Не хватает: ' + problems.map((p) => p.text).join('; '), 'warn');
        return;
      }

      if (b.dataset.flow === 'preview') {
        try {
          const got = await API.newsPreview(post.markdown, post.gameId ? 'game' : 'launcher', post.gameId);
          const w2 = window.open('', '_blank');
          if (w2) w2.document.write((got && (got.html || got.markdown)) || String(got || ''));
        } catch (err) {
          toast('Предпросмотр не собрался: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      if (b.dataset.flow === 'save') {
        b.disabled = true;
        try {
          await API.newsSave(N.payload(post));
          N.dropDraft(window.localStorage, post);
          toast(
            post.published
              ? 'Заметка сохранена и осталась опубликованной'
              : 'Заметка сохранена. Игроки увидят её после публикации.',
            'ok'
          );
          sheet.close();
          await store.invalidate(['news']);
          route();
        } catch (err) {
          toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
          b.disabled = false;
        }
      }
    });
  }

  /* --- Вложения новостей --- */

  /* Отдельным листом поверх редактора: складывать управление файлами в
     ту же форму значит потерять набранный текст на первом же переходе
     по папкам. */
  function flowAssets(onPick) {
    const N = window.CH2News;
    const G = window.CH2Gallery;
    let path = '';
    let entries = [];

    const sheet = openSheet({
      title: 'Вложения новостей',
      lede: 'Файлы раздаются игрокам по адресу /news/assets/. Выбранный вставится в текст.',
      body: '<div class="sk" style="height:16rem"></div>',
      foot:
        '<button class="btn" type="button" data-flow="mkdir">Новая папка</button>' +
        '<button class="btn" type="button" data-flow="pick">Загрузить файл</button>' +
        '<button class="btn" type="button" data-flow="byUrl">Загрузить по ссылке</button>',
    });

    const input = document.createElement('input');
    input.type = 'file';
    input.addEventListener('change', async () => {
      if (!input.files || !input.files[0]) return;
      try {
        await API.newsAssetsUpload(path, input.files[0]);
        load();
      } catch (err) {
        toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
      }
    });

    async function load() {
      try {
        const got = await API.newsAssets(path);
        entries = (got && (got.items || got.entries)) || [];
      } catch (err) {
        sheet.body('<div class="empty"><b>Не прочиталось</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
        return;
      }
      sheet.body(V().galleryCrumbs(path, G) + V().assetList(entries, { path: path, gallery: G }));
    }
    load();

    sheet.root.addEventListener('click', async (e) => {
      const go = e.target.closest('[data-go]');
      if (go) {
        path = G.safePath(go.dataset.go);
        load();
        return;
      }

      const use = e.target.closest('[data-use]');
      if (use) {
        onPick(N.insertMarkup(G.entryPath(path, use.dataset.use)));
        sheet.close();
        return;
      }

      const rm = e.target.closest('[data-remove]');
      if (rm) {
        const agreed = await ask({
          title: 'Удалить ' + rm.dataset.remove + '?',
          body: 'Файл пропадёт из всех заметок, где на него ссылались.',
          ok: 'Удалить',
          cancel: 'Отмена',
        });
        if (!agreed) return;
        await API.newsAssetsDelete(path, rm.dataset.remove);
        load();
        return;
      }

      const act = e.target.closest('[data-flow]');
      if (!act) return;
      if (act.dataset.flow === 'pick') input.click();
      if (act.dataset.flow === 'mkdir') {
        const name = window.prompt('Имя папки', '');
        if (name === null) return;
        const problem = G.nameProblem(name, entries.map((x) => x.name));
        if (problem) {
          toast(problem, 'warn');
          return;
        }
        await API.newsAssetsMkdir(path, name);
        load();
      }
      if (act.dataset.flow === 'byUrl') {
        const url = window.prompt('Ссылка на файл', '');
        if (!url) return;
        const name = window.prompt('Под каким именем сохранить', url.split('/').pop() || 'file');
        if (name === null) return;
        try {
          await API.newsAssetsUploadByUrl(path, url, name);
          load();
        } catch (err) {
          toast('Не скачалось: ' + window.CH2Api.reason(err), 'bad');
        }
      }
    });
  }

  /* --- Галерея --- */

  /* Галерея адресуется папкой и именем по отдельности: сервер режет
     `path` своим SanitizeAssetPath, а имя проверяет сам, и склеенный
     путь одной строкой ушёл бы в никуда. */
  function flowGallery(gameId) {
    const G = window.CH2Gallery;
    let path = '';
    let entries = [];
    let cover = '';

    const sheet = openSheet({
      title: 'Галерея: ' + gameId,
      lede: 'Обложка попадает на витрину игры. Остальные файлы — на её страницу.',
      body: '<div class="sk" style="height:18rem"></div>',
      foot:
        '<button class="btn" type="button" data-flow="mkdir">Новая папка</button>' +
        '<button class="btn" type="button" data-flow="pick">Загрузить файл</button>' +
        '<button class="btn" type="button" data-flow="byUrl">Загрузить по ссылке</button>',
    });

    const input = document.createElement('input');
    input.type = 'file';
    input.addEventListener('change', async () => {
      if (!input.files || !input.files[0]) return;
      try {
        await API.galleryUpload(gameId, path, input.files[0]);
        load();
      } catch (err) {
        toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
      }
    });

    async function load() {
      try {
        const got = await API.gallery(gameId, path);
        entries = (got && (got.items || got.entries)) || [];
        cover = got && got.cover !== undefined ? got.cover : cover;
      } catch (err) {
        sheet.body('<div class="empty"><b>Не прочиталось</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
        return;
      }
      sheet.body(V().galleryCrumbs(path, G) + V().galleryList(entries, { path: path, cover: cover, gallery: G }));
    }
    load();

    const names = () => entries.map((e) => e.name);

    sheet.root.addEventListener('click', async (e) => {
      const go = e.target.closest('[data-go]');
      if (go) {
        path = G.safePath(go.dataset.go);
        load();
        return;
      }

      const cov = e.target.closest('[data-cover]');
      if (cov) {
        try {
          await API.gallerySetCover(gameId, G.entryPath(path, cov.dataset.cover));
          cover = cov.dataset.cover;
          toast('Обложка сменилась. Игроки увидят её сразу.', 'ok');
          load();
        } catch (err) {
          toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      const cap = e.target.closest('[data-caption]');
      if (cap) {
        const text = window.prompt('Подпись под кадром', cap.dataset.captionText || '');
        if (text === null) return;
        try {
          await API.gallerySetCaption(gameId, G.entryPath(path, cap.dataset.caption), text);
          load();
        } catch (err) {
          toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      const ren = e.target.closest('[data-rename]');
      if (ren) {
        const from = ren.dataset.rename;
        const to = window.prompt('Новое имя', from);
        if (to === null) return;
        /* Проверка здесь, а не только на сервере: отказ после нажатия —
           это потерянное действие и вопрос «а что не так». */
        const problem = G.nameProblem(to, names().filter((n) => n !== from));
        if (problem) {
          toast(problem, 'warn');
          return;
        }
        try {
          await API.galleryRename(gameId, path, from, to);
          load();
        } catch (err) {
          toast('Не переименовалось: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      const rm = e.target.closest('[data-remove]');
      if (rm) {
        const file = entries.find((x) => x.name === rm.dataset.remove) || { name: rm.dataset.remove };
        const warn = G.deleteWarning(file, cover);
        const agreed = await ask({
          title: 'Удалить ' + file.name + '?',
          body: warn || 'Файл удалится с сервера. Вернуть его можно только загрузив заново.',
          ok: 'Удалить',
          cancel: 'Отмена',
        });
        if (!agreed) return;
        try {
          await API.galleryDelete(gameId, path, file.name);
          load();
        } catch (err) {
          toast('Не удалилось: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      const act = e.target.closest('[data-flow]');
      if (!act) return;
      if (act.dataset.flow === 'pick') input.click();
      if (act.dataset.flow === 'mkdir') {
        const name = window.prompt('Имя папки', '');
        if (name === null) return;
        const problem = G.nameProblem(name, names());
        if (problem) {
          toast(problem, 'warn');
          return;
        }
        try {
          await API.galleryMkdir(gameId, path, name);
          load();
        } catch (err) {
          toast('Не создалось: ' + window.CH2Api.reason(err), 'bad');
        }
      }
      if (act.dataset.flow === 'byUrl') {
        const url = window.prompt('Ссылка на картинку', '');
        if (!url) return;
        const name = window.prompt('Под каким именем сохранить', url.split('/').pop() || 'image.png');
        if (name === null) return;
        const problem = G.nameProblem(name, names());
        if (problem) {
          toast(problem, 'warn');
          return;
        }
        try {
          await API.galleryUploadByUrl(gameId, path, url, name);
          load();
        } catch (err) {
          toast('Не скачалось: ' + window.CH2Api.reason(err), 'bad');
        }
      }
    });
  }

  /* --- Карточка игры --- */

  /* Реестр — это то, что лаунчер читает при старте. Сохраняется он
     целиком, поэтому правка одной игры уезжает вместе со всем списком:
     отправить одну строку сервер не умеет, а собирать список из
     отрисованной таблицы значило бы потерять всё, чего в ней не видно. */
  function flowGame(where) {
    const R = window.CH2Registry;
    const w = where || {};
    const existing = Boolean(w.gameId);

    const source = D.games.find((g) => g.gameId === w.gameId) || {};
    let item = {
      gameId: w.gameId || '',
      title: source.title || '',
      exeRelativePath: source.exe || '',
      steamAppId: source.steamId || '',
      steamFolder: source.steamFolder || '',
      iconUrl: source.iconUrl || '',
      unpublished: source.published === false,
      existing: existing,
    };

    const sheet = openSheet({
      title: existing ? 'Игра: ' + (item.title || item.gameId) : 'Новая игра',
      lede: 'Это читает лаунчер при старте у каждого игрока. Ошибка здесь ломает запуск сразу у всех.',
      body: V().gameForm(item, []),
      foot:
        (existing
          ? '<button class="btn btn--danger btn--text" type="button" data-flow="remove">Убрать из реестра</button>'
          : '') +
        '<span class="push"></span>' +
        '<button class="btn btn--accent" type="button" data-flow="save">Сохранить</button>',
    });

    const read = () => {
      const q = (n) => sheet.root.querySelector('[name="' + n + '"]');
      if (!q('title')) return;
      item = Object.assign({}, item, {
        gameId: existing ? item.gameId : q('gameId').value.trim(),
        title: q('title').value,
        exeRelativePath: q('exeRelativePath').value,
        steamAppId: q('steamAppId').value,
        steamFolder: q('steamFolder').value,
        iconUrl: q('iconUrl').value,
        unpublished: !q('published').checked,
      });
    };

    const draw = (problems) => {
      sheet.body(V().gameForm(item, problems || []));
    };

    /* Иконку загружают файлом, но только у существующей игры: сервер
       кладёт её в каталог манифестов, а того ещё нет. */
    const iconInput = document.createElement('input');
    iconInput.type = 'file';
    iconInput.accept = 'image/*';
    iconInput.addEventListener('change', async () => {
      if (!iconInput.files || !iconInput.files[0]) return;
      try {
        const got = await API.gamesIconUpload(item.gameId, iconInput.files[0]);
        item.iconUrl = (got && (got.iconUrl || got.url)) || '/manifests/' + item.gameId + '/icon.png';
        draw();
        toast('Иконка загружена', 'ok');
        await store.invalidate(['games']);
      } catch (err) {
        toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
      }
    });

    /* Список для сохранения собирается из того, что прочитано с
       сервера, а не из таблицы на экране: в реестре есть поля, которых
       таблица не показывает, и собранный из неё список их бы стёр. */
    const merged = () => {
      const rows = D.raw.games.slice();
      const at = rows.findIndex((g) => g.gameId === item.gameId);
      const row = Object.assign({}, at >= 0 ? rows[at] : {}, {
        gameId: item.gameId,
        title: item.title.trim(),
        exeRelativePath: item.exeRelativePath.trim(),
        steamAppId: item.steamAppId.trim(),
        steamFolder: item.steamFolder.trim(),
        iconUrl: item.iconUrl.trim(),
        unpublished: item.unpublished,
      });
      if (at >= 0) rows[at] = row;
      else rows.push(row);
      return R.reorder(rows);
    };

    sheet.root.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-flow]');
      if (!b) return;
      read();

      if (b.dataset.flow === 'icon') {
        iconInput.click();
        return;
      }
      if (b.dataset.flow === 'icon-default') {
        item.iconUrl = '';
        draw();
        return;
      }

      if (b.dataset.flow === 'remove') {
        const agreed = await ask({
          title: 'Убрать «' + (item.title || item.gameId) + '» из реестра?',
          body: 'Игра пропадёт из лаунчера. Её манифесты, версии и галерея останутся на диске — удалить их можно отдельно, кнопкой «Удалить контент».',
          ok: 'Убрать из реестра',
          cancel: 'Отмена',
        });
        if (!agreed) return;
        try {
          await API.gamesSave(R.reorder(R.remove(D.raw.games, item.gameId)));
          toast('Игра убрана из реестра', 'ok');
          sheet.close();
          await store.invalidate(['games', 'overview']);
          route();
        } catch (err) {
          toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      if (b.dataset.flow !== 'save') return;

      const list = merged();
      const problems = R.problems(list).filter((p) => p.gameId === item.gameId || !p.gameId);
      if (problems.length) {
        draw(problems);
        toast(problems[0].message, 'warn');
        return;
      }

      b.disabled = true;
      try {
        await API.gamesSave(list);
        toast(existing ? 'Сохранено. Лаунчер увидит это при следующем старте.' : 'Игра заведена', 'ok');
        sheet.close();
        await store.invalidate(['games', 'overview']);
        route();
      } catch (err) {
        toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
        b.disabled = false;
      }
    });
  }

  /* --- Порядок игр --- */

  /* Порядок в реестре — это порядок на витрине у игрока, и лаунчер
     помнит игру по её месту в списке. Поэтому `reorder` пересчитывает
     номера целиком, а лист говорит, сколько строк переедет. */
  function flowOrder() {
    const R = window.CH2Registry;
    const before = D.games.slice();
    let list = D.games.slice();

    const sheet = openSheet({
      title: 'Порядок игр',
      lede: 'В этом порядке игры стоят в лаунчере у игрока.',
      body: V().orderList(list),
      foot:
        '<span data-order-note class="dim"></span><span class="push"></span>' +
        '<button class="btn btn--accent" type="button" data-flow="save">Сохранить порядок</button>',
    });

    const draw = () => {
      sheet.body(V().orderList(list));
      const sum = V().orderSummary(before, list);
      const note = sheet.root.querySelector('[data-order-note]');
      if (note) note.textContent = sum.text;
      const save = sheet.root.querySelector('[data-flow="save"]');
      if (save) save.disabled = !sum.changed;
    };
    draw();

    sheet.root.addEventListener('click', async (e) => {
      const up = e.target.closest('[data-up]');
      if (up) {
        list = R.move(list, up.dataset.up, -1);
        draw();
        return;
      }
      const down = e.target.closest('[data-down]');
      if (down) {
        list = R.move(list, down.dataset.down, 1);
        draw();
        return;
      }
      if (e.target.closest('[data-flow="save"]')) {
        try {
          await API.gamesSave(R.reorder(list));
          toast('Порядок сохранён. Игроки увидят его сразу.', 'ok');
          sheet.close();
          await store.invalidate(['games']);
          route();
        } catch (err) {
          toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
        }
      }
    });

    /* Перетаскивание — поверх кнопок, а не вместо них: с клавиатуры в
       него не попасть, а в список из двадцати строк мышью попадают не с
       первого раза. */
    let dragged = '';
    sheet.root.addEventListener('dragstart', (e) => {
      const li = e.target.closest('li[data-id]');
      if (li) dragged = li.dataset.id;
    });
    sheet.root.addEventListener('dragover', (e) => {
      if (e.target.closest('li[data-id]')) e.preventDefault();
    });
    sheet.root.addEventListener('drop', (e) => {
      const li = e.target.closest('li[data-id]');
      if (!li || !dragged) return;
      e.preventDefault();
      list = R.moveTo(list, dragged, Number(li.dataset.index));
      dragged = '';
      draw();
    });
  }

  /* --- Состав будущей сборки --- */

  /* Пересчёт спрашивает у Thunderstore, из чего соберётся модпак, и не
     качает ни байта. Нужен он затем, что после сборки список менять
     поздно: пропавший пакет виден здесь, а не на середине выкатки. */
  function flowResolve(pack) {
    const sheet = openSheet({
      title: 'Состав сборки: ' + (pack.title || pack.gameId),
      lede: 'Thunderstore отвечает списком. Ничего не скачивается и никуда не уходит.',
      body: '<div class="sk" style="height:14rem"></div>',
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    (async () => {
      try {
        const plan = await API.modsResolve({
          gameId: pack.gameId,
          namespace: pack.namespace,
          name: pack.name,
          version: pack.version || '',
        });
        sheet.body(V().resolvePlan(plan));
      } catch (err) {
        sheet.body('<div class="empty"><b>Не посчиталось</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
      }
    })();

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="close"]')) sheet.close();
    });
  }

  /* --- Каталог Thunderstore --- */

  /* Половина модпаков в раздел «Modpacks» не проставлена и в каталоге не
     находится вовсе, поэтому рядом с поиском живёт приём ссылки на
     страницу пакета: сервер разберёт её сам. */
  function flowCatalog(pack) {
    let items = [];
    const st = { q: '', ordering: 'most-downloaded', page: 1, perPage: 20, count: 0, hasMore: false };

    const sheet = openSheet({
      title: 'Каталог Thunderstore: ' + (pack.title || pack.gameId),
      lede: 'Выбранный пакет подставится в сборку. Ничего не собирается и не публикуется.',
      body: '<div class="sk" style="height:16rem"></div>',
      foot: '<button class="btn" type="button" data-flow="byUrl">Вставить ссылку на пакет</button>',
    });

    async function load() {
      const bar = sheet.root.querySelector('[data-catalog-bar]');
      if (bar) bar.setAttribute('aria-busy', 'true');
      try {
        const got = await API.modsCatalog({ gameId: pack.gameId, q: st.q, ordering: st.ordering, page: st.page });
        items = (got && got.results) || [];
        st.count = Number((got && got.count) || 0);
        /* «Есть ли ещё» считается по полной странице, а не по счётчику:
           счётчик Thunderstore иногда отстаёт, а пустая следующая
           страница обиднее лишней стрелки. */
        st.hasMore = items.length >= st.perPage;
      } catch (err) {
        sheet.body(
          V().catalogBar(st) +
            '<div class="empty"><b>Каталог недоступен</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>'
        );
        return;
      }
      sheet.body(V().catalogBar(st) + V().catalogList(items, { query: st.q }));
    }
    load();

    const choose = (ns, name, version) => {
      toast('Выбрано: ' + ns + '/' + name, 'ok');
      sheet.close();
      flowResolve({ title: pack.title, gameId: pack.gameId, namespace: ns, name: name, version: version || '' });
    };

    sheet.root.addEventListener('click', async (e) => {
      const take = e.target.closest('[data-take]');
      if (take) {
        choose(take.dataset.ns, take.dataset.name, take.dataset.version);
        return;
      }

      const readme = e.target.closest('[data-readme]');
      if (readme) {
        try {
          const got = await API.modsReadme(readme.dataset.ns, readme.dataset.name, readme.dataset.version || '');
          const box = sheet.root.querySelector('[data-readme-box]');
          if (box) box.textContent = (got && got.markdown) || 'Описания нет';
        } catch (err) {
          toast('Описание не пришло: ' + window.CH2Api.reason(err), 'warn');
        }
        return;
      }

      const page = e.target.closest('[data-page]');
      if (page) {
        st.page = Math.max(1, st.page + Number(page.dataset.page));
        load();
        return;
      }

      const b = e.target.closest('[data-flow]');
      if (!b || b.dataset.flow !== 'byUrl') return;

      const link = window.prompt('Ссылка на страницу пакета Thunderstore', '');
      if (!link) return;
      const parsed = window.CH2Mods.parsePackageUrl(link);
      if (!parsed) {
        toast('Это не похоже на страницу пакета Thunderstore', 'warn');
        return;
      }
      choose(parsed.namespace, parsed.name, '');
    });

    /* Смена поиска или порядка возвращает на первую страницу: остаться
       на седьмой странице другого запроса — верный способ увидеть
       «ничего не найдено» там, где всё нашлось. */
    const restart = () => {
      const bar = sheet.root.querySelector('[data-catalog-bar]');
      if (!bar) return;
      st.q = bar.querySelector('[name="q"]').value.trim();
      st.ordering = bar.querySelector('[name="ordering"]').value;
      st.page = 1;
      load();
    };
    sheet.root.addEventListener('change', (e) => {
      if (e.target.matches('[name="ordering"], [name="q"]')) restart();
    });
    sheet.root.addEventListener('search', (e) => {
      if (e.target.matches('[name="q"]')) restart();
    });
    sheet.root.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && e.target.matches('[name="q"]')) restart();
    });
  }

  /* --- Обращение целиком --- */

  /* В списке видно первую строку, а починить по ней нельзя. Здесь —
     весь текст, диагностика с компьютера игрока и то, что с ней можно
     сделать: ответить письмом, скопировать в задачу, открыть журналы. */
  function flowFeedback(a) {
    let item = D.inbox.find((f) => f.id === a.id) || { id: a.id };

    const sheet = openSheet({
      title: 'Обращение',
      lede: 'Прислал игрок из лаунчера. Ответить получится не на всё: контакт оставляют по желанию.',
      body: '<div class="sk" style="height:14rem"></div>',
      foot: '<span data-fb-actions></span>',
    });

    const draw = () => {
      sheet.body(V().feedbackCard(item));
      const reply = V().replyLink(item);
      sheet.foot(
        (reply ? '<a class="btn" href="' + esc(reply) + '">Ответить письмом</a>' : '') +
          '<button class="btn" type="button" data-flow="copy">Скопировать диагностику</button>' +
          (item.logBytes
            ? '<button class="btn" type="button" data-flow="logs">Журналы, ' + esc(bytes(item.logBytes)) + '</button>'
            : '') +
          '<span class="push"></span>' +
          (reply ? '' : '<span class="faint">Контакта нет — ответить некуда</span>')
      );
    };
    draw();

    /* Диагностика приходит только с одним обращением: в списке сервер
       её не отдаёт, иначе список весил бы мегабайты. */
    (async () => {
      try {
        const got = await API.feedbackGet(a.id);
        item = Object.assign({}, item, window.CH2Sections.inbox({ items: [got] })[0] || {});
        draw();
      } catch {
        // Список у нас уже есть — покажем хотя бы его
      }
    })();

    sheet.root.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-flow]');
      if (!b) return;
      if (b.dataset.flow === 'logs') {
        flowLogs(item);
        return;
      }
      if (b.dataset.flow === 'copy') {
        try {
          await navigator.clipboard.writeText(V().diagnosticsText(item));
          toast('Диагностика скопирована', 'ok');
        } catch {
          toast('Браузер не дал скопировать — выделите текст руками', 'warn');
        }
      }
    });
  }

  /* --- События одного кода ошибки --- */

  /* Счётчик в таблице говорит, что ломается часто. Кто именно на это
     напоролся — отдельный вопрос, и отвечает на него `metrics/errors`:
     она отдаёт события ОДНОГО кода, а без кода честно даёт 400. */
  function flowErrorEvents(a) {
    const sheet = openSheet({
      title: 'Ошибка ' + a.code,
      lede: 'У кого она случалась. Если весь код собрался на одной версии клиента, чинить надо её.',
      body: '<div class="sk" style="height:14rem"></div>',
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    (async () => {
      try {
        sheet.body(V().errorEvents(await API.metricsErrors({ code: a.code })));
      } catch (err) {
        sheet.body('<div class="empty"><b>Не прочиталось</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
      }
    })();

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="close"]')) sheet.close();
    });
  }

  /* --- Что изменится в модпаке --- */

  /* Читают это перед тем, как отдать пересборку игрокам: «какие моды
     изменились» — вопрос, на который список из полутора сотен полных
     имён до и после не отвечает. */
  function flowModsDiff(a) {
    const sheet = openSheet({
      title: 'Что изменится: ' + (a.title || a.gameId),
      lede: 'Между версией у игроков (' + a.from + ') и собранной (' + a.to + ').',
      body: '<div class="sk" style="height:14rem"></div>',
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    (async () => {
      try {
        const got = await API.modsDiff(a.gameId, a.from, a.to);
        sheet.body(V().modsDiff((got && (got.items || got.list)) || []));
      } catch (err) {
        sheet.body('<div class="empty"><b>Не сравнилось</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
      }
    })();

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="close"]')) sheet.close();
    });
  }

  /* --- Переезд со старых сборок --- */

  /* Профиль r2modman перечисляет моды с точными версиями. Набор, который
     у игроков уже стоит, публикуется как есть — а не собирается заново
     на глаз, с риском разойтись по версиям и развалить лобби. */
  function flowImport(pack) {
    const sheet = openSheet({
      title: 'Переезд со старой сборки: ' + (pack.title || pack.gameId),
      lede: 'Файл профиля r2modman или mods.yml. Сборка не публикуется — её ещё надо отдать игрокам.',
      body:
        '<div class="empty"><b>Файл не выбран</b><span>В профиле перечислены моды с точными версиями</span></div>',
      foot: '<button class="btn btn--accent" type="button" data-flow="pick">Выбрать файл</button>',
    });

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.yml,.yaml,.r2z,.zip,.json';
    input.addEventListener('change', async () => {
      if (!input.files || !input.files[0]) return;
      sheet.body('<div class="empty"><b>Разбираем профиль</b><span>' + esc(input.files[0].name) + '</span></div>');
      try {
        const got = await API.modsImport(pack.gameId, input.files[0]);
        sheet.body(V().importResult(got));
        toast('Профиль разобран. Игрокам сборка пока не ушла.', 'ok');
        await store.invalidate(['packs']);
      } catch (err) {
        sheet.body('<div class="empty"><b>Не разобрался</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
      }
    });

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="pick"]')) input.click();
    });
  }

  /* --- Журналы обращения --- */

  /* Журнал прикладывает сам игрок, и это единственное место, где видно,
     что у него на самом деле происходило. Сервер отдаёт его текстом. */
  function flowLogs(feedback) {
    const sheet = openSheet({
      title: 'Журналы обращения',
      lede: 'Прислал игрок вместе с обращением. Ничего никуда не отправляется.',
      body: '<div class="sk" style="height:18rem"></div>',
      foot: '<button class="btn" type="button" data-flow="copy">Скопировать</button>',
    });

    let text = '';
    (async () => {
      try {
        const got = await API.feedbackLogs(feedback.id);
        text = typeof got === 'string' ? got : '';
        sheet.body(V().logsView(text));
      } catch (err) {
        sheet.body('<div class="empty"><b>Журнал не пришёл</b><span>' + esc(window.CH2Api.reason(err)) + '</span></div>');
      }
    })();

    sheet.root.addEventListener('click', async (e) => {
      if (!e.target.closest('[data-flow="copy"]')) return;
      try {
        await navigator.clipboard.writeText(text);
        toast('Журнал скопирован', 'ok');
      } catch {
        toast('Браузер не дал скопировать — выделите текст руками', 'warn');
      }
    });
  }

  /* --- Подсказка из Thunderstore --- */

  /* Заполняет идентификатор Steam, имя исполняемого файла и папку
     установки из схемы экосистемы. Руками это копирование трёх значений
     на игру, и папка, вложенная внутрь каталога установки, с первого
     раза угадывается неправильно. */
  function flowEcosystem() {
    const rows = D.games;
    const sheet = openSheet({
      title: 'Подтянуть из Thunderstore',
      lede: 'Перезапишет настройки модов у выбранной игры. Файлы игр это не трогает.',
      body: V().ecosystemPicker(rows),
      foot: '<button class="btn btn--accent" type="button" data-flow="pull">Подтянуть</button>',
    });

    sheet.root.addEventListener('click', async (e) => {
      if (!e.target.closest('[data-flow="pull"]')) return;
      const gameId = sheet.root.querySelector('[name="gameId"]').value;
      const slug = sheet.root.querySelector('[name="slug"]').value.trim();
      if (!slug) {
        toast('Нужно имя игры в терминах Thunderstore, например lethal-company', 'warn');
        return;
      }

      const game = rows.find((g) => g.gameId === gameId) || { title: gameId };
      const agreed = await ask({
        title: 'Перезаписать настройки «' + game.title + '»?',
        body: 'Идентификатор Steam, имя исполняемого файла и папка установки заменятся тем, что в схеме Thunderstore.',
        ok: 'Перезаписать',
        cancel: 'Отмена',
      });
      if (!agreed) return;

      try {
        await API.gamesEcosystem(gameId, slug);
        toast('Настройки подтянуты', 'ok');
        sheet.close();
        await store.invalidate(['games']);
        route();
      } catch (err) {
        toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
      }
    });
  }

  /* Собирает форму технических работ.

     Пустое окно — это «сразу» и «пока не выключат», а не нулевые даты:
     сервер отличает отсутствие поля от пустой строки. */
  function maintPayload(enabled) {
    const q = (n) => $(`[data-maint] [name="${n}"]`);
    const val = (n) => (q(n) ? q(n).value : '');
    const on = (n) => Boolean(q(n) && q(n).checked);

    const out = {
      enabled: Boolean(enabled),
      reason: val('reason').trim(),
      blocks: { install: on('install'), update: on('update'), launch: on('launch') },
    };
    const from = V().isoTime(val('startsAt'));
    const to = V().isoTime(val('endsAt'));
    if (from) out.startsAt = from;
    if (to) out.endsAt = to;
    return out;
  }

  /* --- Подбор параметров --- */

  /* САМ ПРОГОН — ИЗ ВЕРСИИ 1.0. `upload-bench.js` умеет разобрать списки
     наборов, посчитать размер пробы, залить её и остановиться по сигналу
     — всё это написано и покрыто тестами. Здесь только экран вокруг:
     что спрашивают до, что показывают во время и что делают с итогом. */
  function flowBench() {
    const T = window.CH2Tuning;
    const B = {
      parseBenchList: window.parseBenchList,
      benchCombos: window.benchCombos,
      benchPlan: window.benchPlan,
      benchUploadOnce: window.benchUploadOnce,
    };

    let runs = T.recall(window.localStorage);
    let setup = { chunks: '4, 8, 16', concurrency: '2, 4, 8', probe: 64, file: null };
    let ctrl = null;

    const sheet = openSheet({
      title: 'Подбор параметров загрузки',
      lede: 'Проба заливается и сразу отменяется: на сервере ничего не остаётся и никуда не публикуется.',
      body: '',
      foot: '',
    });

    const draw = (progress) => {
      sheet.body(
        V().benchSetup(setup) +
          (progress ? V().benchProgress(progress) : '') +
          V().benchTable(runs, T)
      );
      sheet.foot(
        ctrl
          ? '<button class="btn btn--danger" type="button" data-flow="stop">Остановить</button>'
          : '<button class="btn" type="button" data-flow="pick">Выбрать файл пробы</button>' +
            '<span class="push"></span>' +
            '<button class="btn btn--accent" type="button" data-flow="run"' +
            (setup.file ? '' : ' disabled') +
            '>Запустить прогон</button>'
      );
    };
    draw();

    const input = document.createElement('input');
    input.type = 'file';
    input.addEventListener('change', () => {
      if (input.files && input.files[0]) {
        setup.file = input.files[0];
        draw();
      }
    });

    const read = () => {
      const q = (n) => sheet.root.querySelector('[name="' + n + '"]');
      if (!q('chunks')) return;
      setup = Object.assign({}, setup, {
        chunks: q('chunks').value,
        concurrency: q('concurrency').value,
        probe: Number(q('probe').value) || 64,
      });
    };

    async function run() {
      read();
      const chunkList = B.parseBenchList(setup.chunks);
      const concList = B.parseBenchList(setup.concurrency);
      const combos = B.benchCombos(chunkList, concList);
      if (!combos.length) {
        toast('Наборы не разобрались: нужны числа через запятую', 'warn');
        return;
      }

      ctrl = new AbortController();
      runs = [];
      draw({ done: 0, total: combos.length });

      for (let i = 0; i < combos.length; i++) {
        if (ctrl.signal.aborted) break;
        const c = combos[i];
        draw({ done: i, total: combos.length, current: { chunk: c.chunkMB + ' МиБ', streams: c.conc } });

        const res = await B.benchUploadOnce(setup.file, c.chunkMB, c.conc, setup.probe * 1024 * 1024, {
          fetch: window.fetch.bind(window),
          signal: ctrl.signal,
        });

        /* Сорвавшийся набор не выбрасываем: ноль скорости с пометкой —
           тоже ответ, и он объясняет, почему выбран не он. */
        runs.push({
          chunk: c.chunkMB + ' МиБ',
          streams: c.conc,
          mbps: res.ok ? res.speed / 1024 / 1024 : 0,
          retries: res.ok ? 0 : 1,
          note: res.ok ? '' : res.error || 'не прошёл',
        });
        draw({ done: i + 1, total: combos.length, speed: res.ok ? res.speed : 0 });
      }

      const stopped = ctrl.signal.aborted;
      ctrl = null;
      if (runs.some((r) => r.mbps > 0)) T.remember(window.localStorage, runs);
      draw();
      toast(stopped ? 'Прогон остановлен, что успели — в таблице' : 'Прогон закончен', stopped ? 'warn' : 'ok');
    }

    /* Закрытие листа посреди прогона — это остановка: брошенный прогон
       продолжал бы лить пробы в фон и занимать канал. */
    sheet.onClose = () => {
      if (ctrl) ctrl.abort();
    };

    sheet.root.addEventListener('click', (e) => {
      const apply = e.target.closest('[data-apply]');
      if (apply) {
        const pick = runs.find((r) => r.chunk === apply.dataset.apply);
        if (!pick) return;
        window.CH2_UPLOAD_PARAMS = T.apply(pick);
        toast('Применено: ' + pick.chunk + ' на ' + pick.streams + ' потоках', 'ok');
        return;
      }

      const b = e.target.closest('[data-flow]');
      if (!b) return;
      if (b.dataset.flow === 'pick') input.click();
      if (b.dataset.flow === 'stop' && ctrl) ctrl.abort();
      if (b.dataset.flow === 'run') run();
    });
  }

  /** Дела, которые панель ведёт сама. Записи в реестре действий — отдельно. */
  const FLOWS = {
    upload: () => flowUpload({ kind: 'launcher' }),
    build: () => flowBuild(packOf(game)),
    resolve: () => flowResolve(packOf(game)),
    choose: () => flowCatalog(packOf(game)),
    import: () => flowImport(packOf(game)),
    'mods-diff': (a) => flowModsDiff(a),
    'error-events': (a) => flowErrorEvents(a),
    'new-post': () => flowNews({}),
    'edit-post': (a) => flowNews(a),
    gallery: (a) => flowGallery(a.gameId || (D.games[0] && D.games[0].gameId) || ''),
    'new-game': () => flowGame({}),
    'edit-game': (a) => flowGame(a),
    order: () => flowOrder(),
    ecosystem: () => flowEcosystem(),
    logs: (a) => flowLogs(a),
    feedback: (a) => flowFeedback(a),
    bench: () => flowBench(),
  };

  /* Наружу — только вопрос «есть ли такое дело». Нужен он проверке,
     которая следит, чтобы в панели не заводились кнопки без
     обработчика: такие честно говорят «ещё не подключено», но делать от
     этого ничего не начинают. */
  window.CH2Flows = { has: (id) => Object.prototype.hasOwnProperty.call(FLOWS, id) };

  /* ---------- Навигация ---------- */

  function route() {
    const id = (location.hash.slice(1) || 'overview').split('?')[0];
    const sec = SECTIONS[id] ? id : 'overview';

    $$('[data-nav]').forEach((a) => {
      if (a.getAttribute('href') === `#${sec}`) a.setAttribute('aria-current', 'page');
      else a.removeAttribute('aria-current');
    });

    const S = SECTIONS[sec];
    main.innerHTML = `
      <div class="page-head">
        <div>
          <h1>${esc(S.title)}</h1>
          <p>${esc(S.lede)}</p>
        </div>
        ${S.actions ? `<div class="actions">${S.actions}</div>` : ''}
      </div>
      ${S.render()}`;

    document.title = `${S.title} — админ-панель Chill Hub`;
    wireSection();
  }

  function wireSection() {
    /* Разница между сборками считается из двух настоящих манифестов и
       приезжает уже после отрисовки: это два файла по мегабайту, и
       держать из-за них весь раздел пустым незачем. Считается она один
       раз на пару версий — второй заход берёт готовое. */
    if ($('[data-diff]') && D.diff === undefined) diffLoad(D.diffPair);

    const go = $('[data-diff-go]');
    if (go) {
      go.addEventListener('click', () => {
        D.diff = undefined;
        const box = $('[data-diff]');
        if (box) box.innerHTML = '<div class="sk" style="height:12rem"></div>';
        diffLoad({ from: $('[data-diff-from]').value, to: $('[data-diff-to]').value });
      });
    }

    /* Отбор обращений считается на месте: сервер отдаёт инбокс целиком,
       и гонять его туда-обратно ради фильтра по типу незачем. */
    const inboxBar = $('[data-inbox-filter]');
    if (inboxBar) {
      const collect = () => {
        const q = (n) => inboxBar.querySelector('[name="' + n + '"]');
        inboxFilter = {
          query: q('query').value.trim(),
          type: q('type').value,
          status: q('status').value,
          important: q('important').checked,
          from: q('from').value,
          to: q('to').value,
        };
        route();
      };
      inboxBar.addEventListener('change', collect);
      inboxBar.addEventListener('search', collect);
      const reset = $('[data-inbox-reset]');
      if (reset) {
        reset.addEventListener('click', () => {
          inboxFilter = {};
          route();
        });
      }
    }

    /* Метрики считает сервер, поэтому смена периода — это перечитывание
       раздела, а не пересчёт на месте. */
    const metricsBar = $('[data-metrics-filter]');
    if (metricsBar) {
      metricsBar.addEventListener('click', async (e) => {
        const b = e.target.closest('[data-days]');
        if (!b) return;
        metricsFilter = Object.assign({}, metricsFilter, { days: Number(b.dataset.days) });
        await reloadMetrics();
      });
      metricsBar.addEventListener('change', async (e) => {
        if (!e.target.matches('[name="gameId"]')) return;
        metricsFilter = Object.assign({}, metricsFilter, { gameId: e.target.value });
        await reloadMetrics();
      });
    }

    const filter = $('[data-tree-filter]');
    const t = $('[data-tree]');
    if (filter && t) {
      filter.addEventListener('input', () => {
        const q = filter.value.trim().toLowerCase();
        $$('.row', t).forEach((r) => (r.hidden = q ? !r.dataset.path.toLowerCase().includes(q) : false));
      });
    }

    $$('[data-game]').forEach((b) =>
      b.addEventListener('click', () => {
        game = b.dataset.game;
        route();
      })
    );

    /* Кнопка называет действие — и только. Спрашивать ли, как звучит
       вопрос и что перечитать после, знает реестр (actions.js), а не
       разметка. В 1.0 это решала каждая кнопка сама, и «Удалить версию»
       спрашивало, а «Удалить игру и все версии» — нет. */
    $$('[data-act]').forEach((b) =>
      b.addEventListener('click', async () => {
        const id = b.dataset.act;

        let args = {};
        try {
          args = b.dataset.args ? JSON.parse(b.dataset.args) : {};
        } catch {
          args = {};
        }

        /* Технические работы собираются формой прямо в разделе: у них
           есть причина, окно и набор блоков, и кнопкой без них
           обошлась бы только заглушка без объяснения. */
        if (id === 'maint.on' || id === 'maint.save') {
          args.payload = maintPayload(true);
          const problem = V().maintProblem(args.payload);
          if (problem) {
            toast(problem, 'warn');
            return;
          }
        }

        /* Длинные дела панель ведёт сама: у них свой лист, свой ход и
           своё окончание, и в реестр записей они не помещаются. */
        if (FLOWS[id]) {
          FLOWS[id](args);
          return;
        }

        if (!window.CH2Actions.has(id)) {
          toast('Это действие ещё не подключено', 'warn');
          return;
        }

        b.disabled = true;
        const res = await window.CH2Actions.run(id, args, { api: API, confirm: ask });
        b.disabled = false;

        if (res.cancelled) return;
        if (!res.ok) {
          toast('Не вышло: ' + res.message, 'bad');
          return;
        }
        toast(res.message, 'ok');
        await store.invalidate(res.stale);
        route();
      })
    );
  }

  /* Разница между активной и загруженной версиями лаунчера.

     `null` — это «сравнить не с чем», и от «файлы совпадают» его надо
     отличать: старые манифесты на сервере подчищаются, и пустое дерево
     вместо честного «нет манифеста» означало бы, что решение об
     активации принимают вслепую, думая, что видят всё. */
  async function diffLoad(pair) {
    const L = D.launcher;
    const from = (pair && pair.from) || L.active;
    const to = (pair && pair.to) || L.newest;
    D.diffPair = { from: from, to: to };

    if (!from || !to || from === to) {
      D.diff = null;
      return;
    }

    D.diff = await window.CH2Manifest.between(from, to, { fetch: window.fetch.bind(window) });

    const box = $('[data-diff]');
    if (box) box.innerHTML = V().launcherDiff(D.diff, { active: from });
    const counts = $('[data-diff-counts]');
    if (counts) counts.innerHTML = D.diff ? V().diffCounts(D.diff) : '';
  }

  /* Перечитывает метрики под выбранный период и игру.

     Границы считаются в браузере и уезжают в UTC: сервер понимает
     RFC3339, а «за 7 дней» у человека — это его последние семь дней, не
     чужие. */
  async function reloadMetrics() {
    const box = $('[data-metrics-filter]');
    if (box) box.setAttribute('aria-busy', 'true');

    const p = V().period(metricsFilter.days);
    const query = { from: p.from, to: p.to, gameId: metricsFilter.gameId };
    try {
      const raw = await API.metricsSummary(query);
      D.days = window.CH2Sections.metrics(raw);
      D.errors = window.CH2Sections.errors(raw);
    } catch (err) {
      toast('Метрики не перечитались: ' + window.CH2Api.reason(err), 'bad');
    }
    route();
  }

  /* ---------- Палитра ---------- */

  function palette() {
    const box = $('[data-palette]');
    const input = $('[data-palette-input]');
    const ul = $('[data-palette-list]');
    let items = [];
    let sel = 0;

    function index() {
      const out = Object.entries(SECTIONS).map(([id, s]) => ({ label: s.title, where: 'раздел', href: `#${id}` }));
      D.games.forEach((g) => out.push({ label: g.title, where: 'игра', href: '#games' }));
      D.packs.forEach((p) => out.push({ label: `${p.title} — сборка ${p.built}`, where: 'сборка', href: '#packs' }));
      D.launcher.versions.forEach((v) => out.push({ label: `Лаунчер ${v.version}`, where: 'версия', href: '#launcher' }));
      D.errors.forEach((e) => out.push({ label: e.code, where: 'ошибка', href: '#errors' }));
      D.inbox.forEach((f) => out.push({ label: f.comment.slice(0, 60), where: 'обращение', href: '#inbox' }));
      D.news.forEach((n) => out.push({ label: n.title, where: 'новость', href: '#news' }));
      return out;
    }

    function draw() {
      const q = input.value.trim().toLowerCase();
      items = index().filter((i) => !q || i.label.toLowerCase().includes(q)).slice(0, 12);
      sel = 0;
      ul.innerHTML = items.length
        ? items
            .map(
              (i, n) =>
                `<li role="option" aria-selected="${n === 0}" data-href="${i.href}">${esc(i.label)}<span class="where">${esc(i.where)}</span></li>`
            )
            .join('')
        : '<li role="option" aria-selected="false" class="faint">Ничего не найдено</li>';
    }

    const open = () => {
      box.hidden = false;
      input.value = '';
      draw();
      input.focus();
    };
    const close = () => (box.hidden = true);
    const go = (i) => {
      if (!items[i]) return;
      location.hash = items[i].href;
      close();
    };

    $('[data-open-palette]').addEventListener('click', open);
    input.addEventListener('input', draw);
    ul.addEventListener('click', (e) => {
      const li = e.target.closest('li[data-href]');
      if (li) go([...ul.children].indexOf(li));
    });
    box.addEventListener('click', (e) => e.target === box && close());

    input.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') close();
      if (e.key === 'Enter') go(sel);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        sel = Math.max(0, Math.min(items.length - 1, sel + (e.key === 'ArrowDown' ? 1 : -1)));
        [...ul.children].forEach((li, n) => li.setAttribute('aria-selected', String(n === sel)));
      }
    });

    window.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        box.hidden ? open() : close();
        return;
      }
      if (!box.hidden || /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName)) return;
      const n = Number(e.key);
      const ids = Object.keys(SECTIONS);
      if (n >= 1 && n <= ids.length) location.hash = `#${ids[n - 1]}`;
    });
  }

  /* ---------- Шапка ---------- */

  function topbar() {
    const L = D.launcher;
    const behind = D.packs.filter((p) => p.behind || p.deprecated).length;
    const waiting = (L.pending ? 1 : 0) + behind;

    const w = $('[data-waiting]');
    w.className = `badge badge--${waiting ? 'accent' : 'ok'}`;
    $('[data-waiting-text]').textContent = waiting ? `${waiting} ждёт решения` : 'решений нет';

    const m = $('[data-maint]');
    m.className = `badge badge--${D.maint.on ? 'bad' : 'ok'}`;
    $('[data-maint-text]').textContent = D.maint.on ? 'техработы включены' : 'техработы выключены';

    $('[data-refresh]').addEventListener('click', () => boot(true));

    /* Выход уводит на страницу входа независимо от того, ответил ли
       сервер успехом: держать человека в панели, из которой он попросил
       выйти, хуже, чем лишний раз показать вход. */
    $('[data-logout]').addEventListener('click', async (e) => {
      e.target.disabled = true;
      try {
        await API.logout();
      } catch {
        // Сеть отвалилась — всё равно уводим на вход
      }
      window.CH2Api.goLogin();
    });
  }

  /* ---------- Запуск ---------- */

  const API = window.CH2Api.makeApi();

  /* Разделы читаются порознь и складываются в ту же плоскую форму, что
     ждёт отрисовка. Снимок остался ровно для одного: показать панель,
     когда сервер не отвечает вовсе, — и панель тогда об этом говорит, а
     не выдаёт снимок за прод. */
  const store = window.CH2Store.createStore(window.CH2Sections.LOADERS, { api: API });

  /* Ответ реестра без разбора. Неудача — пустой список, а не падение:
     без него нельзя только править игру, всё остальное работает. */
  async function rawGames() {
    try {
      return await API.games();
    } catch {
      return { items: [] };
    }
  }

  async function collect() {
    await store.loadAll();
    const demo = await window.CHILLHUB_DATA.load();

    /* Снимок проходит через тот же разбор, что и ответ сервера.
       Иначе в панели живут две формы одних и тех же данных, и отрисовка
       обязана уметь обе — ровно так в 1.0 одна игра выглядела по-разному
       на двух вкладках. */
    const S = window.CH2Sections;
    const val = (name, raw, parse) => {
      const st = store.get(name);
      if (st && st.status === window.CH2Store.READY) return st.data;
      return parse ? parse(raw) : raw;
    };

    const data = {
      launcher: val('launcher', { items: demo.launcher.versions }, S.launcher),
      games: val('games', demo.games, S.games),
      packs: val('packs', demo.packs, S.packs),
      news: val('news', demo.news, S.news),
      inbox: val('inbox', demo.inbox, S.inbox),
      maint: val('maint', { enabled: demo.maint.on, reason: demo.maint.reason }, S.maintenance),
      days: val('metrics', demo.days, S.metrics),
      errors: val('errors', demo.errors, S.errors),
      disk: val('disk', demo.disk, S.disk),
      cache: val('cache', demo.cache, S.cache),
    };

    /* Реестр целиком, как он лежит на сервере. Правка игры уезжает
       вместе со всем списком, а в списке есть поля, которых таблица не
       показывает: собранный из неё список их бы стёр. */
    data.raw = { games: window.CH2Sections.items(await rawGames()) };
    return data;
  }

  async function boot(again = false) {
    /* Сессия проверяется до того, как панель что-то покажет. Аноним,
       увидевший разделы и получивший 401 на первом же нажатии, решит,
       что сломалась панель, а не что его не пустили. Оборванная сеть —
       не отказ: панель покажет снимок и скажет, что записывать нельзя. */
    if (!again) {
      const state = await window.CH2Api.session(API);
      if (state === 'login') {
        window.CH2Api.goLogin();
        return;
      }
    }

    D = await collect();
    if (!again) {
      topbar();
      palette();
      window.addEventListener('hashchange', route);
    }
    route();

    const h = store.health();
    if (again) {
      toast('Данные перечитаны', 'ok');
      return;
    }
    if (!h.live.length) {
      toast('Сервер не отвечает: показан снимок, записывать нельзя', 'bad');
    } else if (h.failed.length) {
      toast(`Не ответили разделы: ${h.failed.join(', ')}`, 'warn');
    }
  }

  boot();
})();
