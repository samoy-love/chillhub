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
        const freePct = D.disk.totalBytes ? Math.round((D.disk.freeBytes / D.disk.totalBytes) * 100) : 100;
        const drafts = D.news.filter((n) => !n.published).length;
        const today = D.days.at(-1) || { date: '', launcherStarts: 0, updates: 0, errors: 0 };
        const errToday = today.errors;

        const decision = (on, title, body, action) => `
          <section class="card decision${on ? ' decision--on' : ''}">
            <header><h2>${esc(title)}</h2>${on ? '<span class="push"></span><span class="badge badge--accent">ждёт решения</span>' : ''}</header>
            <div class="body"><div class="stack stack--tight">${body}${action ? `<div class="btn-row">${action}</div>` : ''}</div></div>
          </section>`;

        const launcherBody = L.pending
          ? `<p>Игроки получают <span class="mono">${esc(L.active)}</span>. Загружена <span class="mono">${esc(L.newest)}</span> — ${D.launcherDiff.length} файлов расходятся.</p>
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
            ${watch('#transfer', 'Свободно', bytes(D.disk.freeBytes), `${100 - freePct}% занято`, freePct < 15 ? 'bad' : freePct < 30 ? 'warn' : 'ok')}
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
        const add = D.launcherDiff.filter((f) => f.diff === 'add').length;
        const mod = D.launcherDiff.filter((f) => f.diff === 'mod').length;
        const del = D.launcherDiff.filter((f) => f.diff === 'del').length;

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
                tree(D.launcherDiff),
                {
                  head: `<span class="badge badge--ok">+${add}</span>
                         <span class="badge badge--warn">~${mod}</span>
                         <span class="badge badge--bad">−${del}</span>`,
                  foot: `${D.launcherDiff.length} файлов из ${D.manifest.length} расходятся между <code>${esc(L.active)}</code> и <code>${esc(L.newest)}</code>. Остальное клиент не скачивает.`,
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
              ${card(
                'Состав будущей сборки',
                list({
                  rows: D.resolved,
                  head: '<th>Мод</th><th>Версия</th><th class="num">Размер</th><th>Откуда</th>',
                  row: (m) => `<tr>
                      <td>${esc(m.name)}<br><span class="faint mono">${esc(m.ns)}</span></td>
                      <td class="mono">${esc(m.version)}</td>
                      <td class="num">${bytes(m.size)}</td>
                      <td class="dim">${esc(m.why)}</td>
                    </tr>`,
                  empty: 'Состав не разрешён',
                  emptyHint: 'Нажми «Пересчитать» — Thunderstore ответит списком, ничего не скачивая.',
                }),
                {
                  flush: true,
                  head: '<button class="btn btn--text" type="button" data-act="resolve">Пересчитать</button>',
                  foot: `${D.resolved.length} пакетов, ${bytes(D.resolved.reduce((a, m) => a + m.size, 0))}. Зависимости разрешены без скачивания.`,
                }
              )}

              ${card(
                'Каталог Thunderstore',
                list({
                  rows: D.catalog,
                  head: '<th>Модпак</th><th>Версия</th><th class="num">Скачиваний</th><th>Обновлён</th><th></th>',
                  row: (c) => `<tr>
                      <td>${esc(c.name)}${c.deprecated ? ' <span class="badge badge--bad">устарел</span>' : ''}<br><span class="faint mono">${esc(c.ns)}</span></td>
                      <td class="mono">${esc(c.version)}</td>
                      <td class="num">${c.downloads.toLocaleString('ru')}</td>
                      <td class="dim">${esc(c.updated)}</td>
                      <td class="act"><button class="btn btn--text" type="button" data-act="choose">Выбрать</button></td>
                    </tr>`,
                  empty: 'Ничего не найдено',
                }),
                {
                  flush: true,
                  head: '<input type="search" placeholder="Поиск по каталогу" style="max-width:200px">',
                  foot: 'Запросы к Thunderstore идут через сервер, а не из браузера: иначе панель светила бы трафик третьей стороне.',
                }
              )}
            </div>

            <div class="stack">
              ${card(
                'Журнал сборки',
                `<div class="log scroll scroll--md">${D.buildLog
                  .map(
                    (l) => `<div class="log-row ${esc(l.k)}">
                        <span class="t">${esc(l.t)}</span>
                        <span class="k">${esc(l.k)}</span>
                        <span class="m">${esc(l.m)}</span>
                      </div>`
                  )
                  .join('')}</div>`,
                {
                  head: '<span class="badge badge--ok"><span class="dot"></span>завершена</span>',
                  foot: 'Поток NDJSON. Сборка тянет до 1,8 ГБ полутора сотнями запросов: молчащий запрос на двадцать минут неотличим от зависшего, поэтому строки идут по мере работы.',
                }
              )}

              ${card(
                'Импорт профиля r2modman',
                `<div class="stack">
                   <div class="field">
                     <label for="imp">Файл профиля</label>
                     <input id="imp" type="text" placeholder="mods.yml или экспорт профиля" readonly>
                     <span class="help">Путь для переезда со старых сборок: в профиле перечислены все моды с точными версиями, поэтому набор, который у игроков уже стоит, публикуется как есть, а не собирается заново на глаз.</span>
                   </div>
                   <div class="btn-row"><button class="btn" type="button" data-act="import">Выбрать файл</button></div>
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
      actions: '<button class="btn btn--accent" type="button" data-act="new-post">Написать</button>',
      render() {
        /* Редактор больше не живёт в разделе: новость набирают минутами,
           и на этот срок ей нужен свой экран, черновик и предпросмотр.
           Раздел показывает то, что есть, и ведёт к правке. */
        return card(
          'Новости',
          list({
            rows: D.news,
            head: '<th>Заголовок</th><th>Состояние</th><th></th>',
            row: (n) => `<tr>
                <td>${esc(n.title)}<br><span class="faint">${esc(n.at)}${n.game ? ` · ${esc(n.game)}` : ' · все игры'}</span></td>
                <td>${
                  n.published
                    ? '<span class="badge badge--ok">на виду</span>'
                    : '<span class="badge badge--warn">черновик</span>'
                }</td>
                <td class="act">
                  <button class="btn btn--text" type="button" data-act="edit-post" data-args='{"id":"${esc(n.id)}"}'>Править</button>
                  <button class="btn btn--text" type="button" data-act="news.publish" data-args='{"id":"${esc(n.id)}","title":"${esc(n.title)}","published":${n.published ? 'false' : 'true'}}'>${n.published ? 'Снять с публикации' : 'Опубликовать'}</button>
                  <button class="btn btn--danger btn--text" type="button" data-act="news.delete" data-args='{"id":"${esc(n.id)}","title":"${esc(n.title)}"}'>Удалить</button>
                </td>
              </tr>`,
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

        return card(
          'Входящие',
          list({
            rows: D.inbox,
            head: '<th>Тип</th><th>Обращение</th><th>Кто</th><th>Когда</th><th></th>',
            row: (f) => `<tr${f.status === 'new' ? ' class="unread"' : ''}>
                <td><span class="badge ${tone[f.type] ? `badge--${tone[f.type]}` : ''}">${t[f.type]}</span></td>
                <td>
                  ${esc(f.comment)}
                  ${f.logBytes ? `<br><button class="btn btn--text" type="button" data-act="logs">Журналы, ${bytes(f.logBytes)}</button>` : ''}
                </td>
                <td class="dim">${f.name ? esc(f.name) : '<span class="faint">без имени</span>'}${
                  f.contact ? `<br><span class="faint mono">${esc(f.contact)}</span>` : '<br><span class="faint">ответить некуда</span>'
                }</td>
                <td class="dim">${esc(f.at)}</td>
                <td class="act">
                  <button class="btn btn--text" type="button" data-act="inbox.important" data-args='{"id":"${esc(f.id)}","important":${f.important ? 'false' : 'true'}}' title="Пометить важным">${f.important ? '★' : '☆'}</button>
                  <button class="btn btn--text" type="button" data-act="inbox.read" data-args='{"id":"${esc(f.id)}","read":${f.status === 'new' ? 'true' : 'false'}}'>${f.status === 'new' ? 'Отметить прочитанным' : 'Вернуть в новые'}</button>
                  <button class="btn btn--danger btn--text" type="button" data-act="inbox.delete" data-args='{"id":"${esc(f.id)}"}'>Удалить</button>
                </td>
              </tr>`,
            empty: 'Обращений нет',
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
        return `
          <div class="cols cols--55">
            ${card(
              'Режим',
              `<div class="stack">
                 <div class="btn-row">
                   <span class="badge badge--${D.maint.on ? 'bad' : 'ok'}"><span class="dot"></span>${D.maint.on ? 'включены' : 'выключены'}</span>
                   <span class="faint">${D.maint.on ? 'лаунчер показывает заглушку и не отдаёт сборки' : 'всё работает обычным образом'}</span>
                 </div>
                 <div class="field">
                   <label for="mt-msg">Что увидит игрок</label>
                   <textarea id="mt-msg" rows="3" placeholder="Переносим сборки на новый диск, вернёмся к 21:00 по Москве."></textarea>
                   <span class="help">Простым языком и с указанием времени. Пустое поле означает общую фразу без подробностей — и поток обращений «а что случилось».</span>
                 </div>
                 <div class="btn-row">
                   ${
                     D.maint.on
                       ? '<button class="btn btn--accent" type="button" data-act="maint.off">Выключить работы</button>'
                       : '<button class="btn btn--danger" type="button" data-act="maint.on">Включить работы</button>'
                   }
                 </div>
               </div>`
            )}
            ${card(
              'Что именно закрывается',
              `<div class="stack stack--tight">
                 <p class="dim">Каталог игр, манифесты сборок и новости перестают отдаваться. Уже скачанные сборки продолжают запускаться: игра стартует локально.</p>
                 <p class="faint">Самообновление лаунчера при включённых работах тоже молчит — иначе клиент уйдёт в цикл проверки версии.</p>
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
        const max = Math.max(...D.days.map((d) => d.gameLaunches));
        const w = 640;
        const h = 140;
        const step = w / (D.days.length - 1);
        const line = (key, color) =>
          `<polyline fill="none" stroke="${color}" stroke-width="1.5" points="${D.days
            .map((d, i) => `${(i * step).toFixed(1)},${(h - (d[key] / max) * h).toFixed(1)}`)
            .join(' ')}"/>`;
        const sum = (k) => D.days.reduce((a, d) => a + d[k], 0);

        return `
          ${card(
            'Коды ошибок за 30 дней',
            list({
              rows: D.errors,
              head: '<th>Код</th><th>Что это значит</th><th>Где чаще</th><th class="num">Случаев</th><th class="num">Доля</th>',
              row: (e) => `<tr>
                  <td class="mono">${esc(e.code)}</td>
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
            <div class="attn-item"><span class="k">Запусков лаунчера</span><span class="v">${sum('launcherStarts')}</span><span class="s">за 30 дней</span></div>
            <div class="attn-item"><span class="k">Установок</span><span class="v">${sum('installs')}</span><span class="s">первых, с нуля</span></div>
            <div class="attn-item"><span class="k">Обновлений</span><span class="v">${sum('updates')}</span><span class="s">докачек разницы</span></div>
            <div class="attn-item" data-tone="${sum('errors') / sum('updates') > 0.1 ? 'warn' : 'ok'}"><span class="k">Доля ошибок</span><span class="v">${dec((sum('errors') / sum('updates')) * 100)}\u00a0%</span><span class="s">от обновлений</span></div>
          </div>

          ${card(
            'Динамика',
            `<svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" preserveAspectRatio="none" role="img" aria-label="Запуски игр, обновления и ошибки за 30 дней">
               ${line('gameLaunches', 'var(--ember)')}
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
        const freePct = Math.round((D.disk.freeBytes / D.disk.totalBytes) * 100);

        return `
          <div class="cols cols--55">
            ${card(
              'Место на диске с контентом',
              `<div class="stack stack--tight">
                 <div class="btn-row">
                   <span class="num" style="font-size:20px">${bytes(D.disk.freeBytes)}</span>
                   <span class="faint">свободно из ${bytes(D.disk.totalBytes)}</span>
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
                   <button class="btn" type="button" data-act="sweep">Убрать старое</button>
                   <button class="btn btn--danger btn--text" type="button" data-act="cache.clear">Очистить полностью</button>
                 </div>
               </div>`
            )}
          </div>

          <div style="margin-top: var(--s3)">
            ${card(
              'Подбор параметров загрузки',
              list({
                rows: D.bench,
                head: '<th>Когда</th><th>Размер куска</th><th class="num">Потоков</th><th class="num">МБ/с</th><th class="num">Повторов</th><th></th>',
                row: (b) => `<tr>
                    <td class="dim">${esc(b.at)}</td>
                    <td class="mono">${esc(b.chunk)}</td>
                    <td class="num">${b.streams}</td>
                    <td class="num">${dec(b.mbps)}</td>
                    <td class="num">${b.retries ? `<span class="badge badge--warn">${b.retries}</span>` : '0'}</td>
                    <td class="act">${b.best ? '<span class="badge badge--ok">выбрано</span>' : '<button class="btn btn--text" type="button" data-act="apply">Применить</button>'}</td>
                  </tr>`,
                empty: 'Прогонов не было',
                emptyHint: 'Запусти прогон, чтобы подобрать кусок и число потоков под текущий канал.',
              }),
              {
                flush: true,
                head: '<button class="btn" type="button" data-act="bench">Запустить прогон</button>',
                foot: 'Больше потоков не всегда быстрее: на 8 потоках канал начал терять куски и переспрашивать их заново.',
              }
            )}
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
  function flowNews(id) {
    const N = window.CH2News;
    let post = { id: id || '', title: '', body: '', gameId: '', published: false };
    const draft = N.readDraft(window.localStorage, post.id);

    const sheet = openSheet({
      title: id ? 'Правка новости' : 'Новая новость',
      lede: 'Черновик сохраняется в браузере, пока новость не отправлена.',
      body: '<div class="sk" style="height:22rem"></div>',
      foot:
        '<button class="btn" type="button" data-flow="preview">Посмотреть глазами игрока</button>' +
        '<span class="push"></span>' +
        '<button class="btn btn--accent" type="button" data-flow="save">Сохранить</button>',
    });

    const draw = (problems) => {
      sheet.body(V().draftNote(draft, post, N) + V().newsForm(post, problems || []));
    };

    const read = () => {
      const q = (n) => sheet.root.querySelector('[name="' + n + '"]');
      if (!q('title')) return;
      post = Object.assign({}, post, { title: q('title').value, body: q('body').value, gameId: q('gameId').value });
    };

    (async () => {
      if (id) {
        try {
          const got = await API.newsGet(id);
          post = Object.assign(post, got || {});
        } catch {
          toast('Новость не прочиталась, открыт пустой черновик', 'warn');
        }
      }
      draw();
    })();

    sheet.root.addEventListener('input', () => {
      read();
      N.saveDraft(window.localStorage, post.id, post);
    });

    sheet.root.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-flow], [data-draft-restore], [data-draft-drop]');
      if (!b) return;

      if (b.hasAttribute('data-draft-restore')) {
        post = Object.assign({}, post, draft.post);
        draw();
        return;
      }
      if (b.hasAttribute('data-draft-drop')) {
        N.dropDraft(window.localStorage, post.id);
        draw();
        return;
      }

      read();
      const problems = N.problems(post);
      if (problems.length) {
        draw(problems);
        toast('Не хватает: ' + problems.map((p) => p.text).join(', '), 'warn');
        return;
      }

      if (b.dataset.flow === 'preview') {
        try {
          const html = await API.newsPreview(N.payload(post));
          const w = window.open('', '_blank');
          if (w) w.document.write((html && html.html) || String(html || ''));
        } catch (err) {
          toast('Предпросмотр не собрался: ' + window.CH2Api.reason(err), 'bad');
        }
        return;
      }

      if (b.dataset.flow === 'save') {
        b.disabled = true;
        try {
          await API.newsSave(N.payload(post));
          N.dropDraft(window.localStorage, post.id);
          toast('Новость сохранена. Игрокам она уйдёт после публикации.', 'ok');
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

  /* --- Галерея --- */

  function flowGallery(gameId) {
    const G = window.CH2Gallery;
    let path = '';
    let entries = [];
    let cover = '';

    const sheet = openSheet({
      title: 'Галерея: ' + gameId,
      lede: 'Обложка попадает на витрину игры. Остальные файлы — на её страницу.',
      body: '<div class="sk" style="height:18rem"></div>',
      foot: '<button class="btn" type="button" data-flow="mkdir">Новая папка</button>',
    });

    async function load() {
      try {
        const got = await API.gallery(gameId, path);
        entries = (got && (got.items || got.entries)) || [];
        cover = (got && got.cover) || cover;
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
        await API.gallerySetCover(gameId, G.entryPath(path, cov.dataset.cover));
        cover = cov.dataset.cover;
        toast('Обложка сменилась. Игроки увидят её сразу.', 'ok');
        load();
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
        await API.galleryRename(gameId, G.entryPath(path, from), G.entryPath(path, to));
        load();
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
        await API.galleryDelete(gameId, G.entryPath(path, file.name));
        load();
        return;
      }

      if (e.target.closest('[data-flow="mkdir"]')) {
        const name = window.prompt('Имя папки', '');
        if (name === null) return;
        const problem = G.nameProblem(name, names());
        if (problem) {
          toast(problem, 'warn');
          return;
        }
        await API.galleryMkdir(gameId, G.entryPath(path, name));
        load();
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

  /* --- Подбор параметров --- */

  /* Наборы для прогона: те же три, между которыми и приходится выбирать.
     Больше восьми потоков не пробуем — на них канал начинает терять
     куски, и выигрыш в скорости съедается повторами. */
  const BENCH = [
    { chunk: '4 МиБ', size: 4 * 1024 * 1024, streams: 2 },
    { chunk: '8 МиБ', size: 8 * 1024 * 1024, streams: 4 },
    { chunk: '16 МиБ', size: 16 * 1024 * 1024, streams: 8 },
  ];

  /* Прогон меряет время ответа сервера на заявку о загрузке и сразу её
     отменяет: ничего не публикуется и на диске ничего не остаётся. */
  async function benchRuns(api) {
    const out = [];
    for (const c of BENCH) {
      const started = Date.now();
      let retries = 0;
      let id = '';
      try {
        const init = await api.uploadInit({
          kind: 'bench',
          zipName: 'bench.bin',
          totalSize: c.size,
          chunkSize: c.size,
        });
        id = (init && init.uploadId) || '';
      } catch {
        retries++;
      }
      const secs = Math.max(0.001, (Date.now() - started) / 1000);
      if (id) await window.CH2Upload.abort(api, id);
      out.push({ chunk: c.chunk, streams: c.streams, mbps: c.size / 1024 / 1024 / secs, retries: retries });
    }
    return out;
  }

  function flowBench() {
    const T = window.CH2Tuning;
    let runs = (D.bench || []).slice();

    const sheet = openSheet({
      title: 'Подбор параметров загрузки',
      lede: 'Прогон занимает около минуты и ничего не публикует.',
      body: V().benchTable(runs, T),
      foot: '<button class="btn btn--accent" type="button" data-flow="run">Запустить прогон</button>',
    });

    sheet.root.addEventListener('click', async (e) => {
      const apply = e.target.closest('[data-apply]');
      if (apply) {
        const pick = runs.find((r) => r.chunk === apply.dataset.apply);
        if (!pick) return;
        window.CH2_UPLOAD_PARAMS = T.apply(pick);
        toast('Применено: ' + pick.chunk + ' на ' + pick.streams + ' потоках', 'ok');
        return;
      }

      const run = e.target.closest('[data-flow="run"]');
      if (!run) return;
      run.disabled = true;
      sheet.body('<div class="empty"><b>Идёт прогон</b><span>Гоняем наборы по очереди</span></div>');
      runs = await benchRuns(API);
      sheet.body(V().benchTable(runs, T));
      run.disabled = false;
    });
  }

  /** Дела, которые панель ведёт сама. Записи в реестре действий — отдельно. */
  const FLOWS = {
    upload: () => flowUpload({ kind: 'launcher' }),
    build: () => flowBuild(packOf(game)),
    'new-post': () => flowNews(''),
    'edit-post': (a) => flowNews(a.id),
    gallery: (a) => flowGallery(a.gameId || (D.games[0] && D.games[0].gameId) || ''),
    'new-game': () => flowOrder(),
    order: () => flowOrder(),
    bench: () => flowBench(),
  };

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
     ждёт отрисовка. Часть кусков (дерево манифеста, каталог Thunderstore,
     журнал сборки, прогоны) пока приходит из снимка — их флоу ещё не
     подключены, и панель об этом говорит, а не выдаёт снимок за прод. */
  const store = window.CH2Store.createStore(window.CH2Sections.LOADERS, { api: API });

  const SNAPSHOT_ONLY = ['manifest', 'launcherDiff', 'resolved', 'catalog', 'buildLog', 'bench'];

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
    for (const k of SNAPSHOT_ONLY) data[k] = demo[k];
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
