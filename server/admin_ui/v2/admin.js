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
            <span>${f.diff === 'add' ? '+' : f.diff === 'del' ? '−' : f.diff === 'mod' ? '~' : ' '}</span>
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
        const freePct = Math.round((D.disk.freeBytes / D.disk.totalBytes) * 100);
        const drafts = D.news.filter((n) => n.state === 'draft').length;
        const today = D.days.at(-1);
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
                  ${p.deprecated ? 'модпак объявлен устаревшим' : `на Thunderstore <span class="mono">${esc(p.upstream.version)}</span> от ${esc(p.upstream.at)}`}
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
                   <button class="btn btn--accent" type="button" data-act="activate-launcher" data-what="лаунчер ${esc(L.newest)}">
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
                        ${v.state === 'active' ? '' : `<button class="btn btn--text" type="button" data-act="activate-launcher" data-what="лаунчер ${esc(v.version)}">Активировать</button>`}
                        ${v.state === 'active' ? '' : `<button class="btn btn--danger btn--text" type="button" data-act="drop" data-what="версия ${esc(v.version)}">Удалить</button>`}
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
              <span class="v mono">${esc(p.upstream.version)}</span>
              <span class="k">${esc(p.upstream.at)}</span>
            </div>
            <div class="push"></div>
            ${staged ? `<button class="btn btn--accent" type="button" data-act="activate-pack" data-what="${esc(p.title)} ${esc(p.built)}">Отдать игрокам</button>` : ''}
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
      actions: '<button class="btn" type="button" data-act="scan">Просканировать контент</button><button class="btn btn--accent" type="button" data-act="new-game">Добавить игру</button>',
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
                    ${g.cover ? '<span class="badge badge--ok">обложка</span>' : '<span class="badge badge--warn">без обложки</span>'}
                    ${g.icon ? '<span class="badge badge--ok">иконка</span>' : '<span class="badge badge--warn">без иконки</span>'}
                    <span class="badge">${g.gallery} в галерее</span>
                  </td>
                  <td class="act">
                    <button class="btn btn--text" type="button" data-act="gallery">Галерея</button>
                    <button class="btn btn--danger btn--text" type="button" data-act="purge" data-what="весь контент игры ${esc(g.title)}">Удалить контент</button>
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
              const noCover = D.games.filter((g) => !g.cover).map((g) => g.title);
              const gaps = [
                noCover.length ? `без обложки: ${noCover.join(', ')}` : '',
                noIcon.length ? `без иконки: ${noIcon.join(', ')}` : '',
              ].filter(Boolean);
              return card(
                'Что видит игрок',
                `<div class="stack stack--tight">
                   <p class="dim">Обложка показывается в списке слева, галерея — на странице игры. Без обложки карточка выглядит пустым прямоугольником.</p>
                   <p class="faint">${gaps.length ? esc(gaps.join('; ')) + '.' : 'У всех игр есть и обложка, и иконка.'}</p>
                   <div class="btn-row"><button class="btn btn--text" type="button" data-act="gallery">Открыть галерею</button></div>
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
        /* Ширина текстового раздела ограничена читаемой мерой: в 1.0
           редактор растягивался на всю ширину 2K-монитора, а строка в
           2000 px не читается. */
        return `
          <div class="cols cols--37">
            ${card(
              'Опубликованное',
              list({
                rows: D.news,
                head: '<th>Заголовок</th><th></th>',
                row: (n) => `<tr>
                    <td>${esc(n.title)}<br><span class="faint">${esc(n.at)}${n.game ? ` · ${esc(n.game)}` : ' · все игры'}</span></td>
                    <td class="act">${
                      n.state === 'draft'
                        ? '<span class="badge badge--warn">черновик</span>'
                        : '<span class="badge badge--ok">на виду</span>'
                    }</td>
                  </tr>`,
                empty: 'Новостей нет',
              }),
              { flush: true }
            )}
            ${card(
              'Редактор',
              `<div class="stack">
                 <div class="field">
                   <label for="ns-title">Заголовок</label>
                   <input id="ns-title" type="text" value="Сборка R.E.P.O. обновлена до 1.9.9">
                 </div>
                 <div class="field">
                   <label for="ns-md">Текст</label>
                   <textarea id="ns-md" rows="14">Обновили сборку до 1.9.9.

- пять новых объектов и сотни ценностей
- сканер по клавише F теперь видит предметы сквозь стены
- убран мод, из-за которого срывалась загрузка на середине

Обновление приедет само при следующем запуске лаунчера.</textarea>
                   <span class="help">Разметка Markdown. Пиши простым языком: это читают в лаунчере, а не в документации.</span>
                 </div>
                 <div class="btn-row">
                   <button class="btn btn--accent" type="button" data-act="publish">Опубликовать</button>
                   <button class="btn" type="button" data-act="preview">Посмотреть, как увидит игрок</button>
                   <span class="push"></span>
                   <button class="btn btn--danger btn--text" type="button" data-act="drop" data-what="новость">Удалить</button>
                 </div>
               </div>`
            )}
          </div>`;
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
                  <button class="btn btn--text" type="button" data-act="star" title="Пометить важным">${f.important ? '★' : '☆'}</button>
                  <button class="btn btn--text" type="button" data-act="read">${f.status === 'new' ? 'Отметить прочитанным' : 'Вернуть в новые'}</button>
                  <button class="btn btn--danger btn--text" type="button" data-act="drop" data-what="обращение ${esc(f.id)}">Удалить</button>
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
                       ? '<button class="btn btn--accent" type="button" data-act="maint-off">Выключить работы</button>'
                       : '<button class="btn btn--danger" type="button" data-act="maint-on" data-what="технические работы для всех игроков">Включить работы</button>'
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
            <div class="attn-item" data-tone="${sum('errors') / sum('updates') > 0.1 ? 'warn' : 'ok'}"><span class="k">Доля ошибок</span><span class="v">${dec((sum('errors') / sum('updates')) * 100)} %</span><span class="s">от обновлений</span></div>
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
                   <button class="btn btn--danger btn--text" type="button" data-act="drop" data-what="весь кэш архивов">Очистить полностью</button>
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

    /* Необратимое действие называет объект и спрашивает подтверждение.
       В 1.0 «Удалить версию» срабатывала с первого клика, а активации
       в интерфейсе не было вовсе — она пряталась в списке версий. */
    $$('[data-act]').forEach((b) =>
      b.addEventListener('click', () => {
        const what = b.dataset.what;
        if (what) {
          const act = b.dataset.act;
          let msg;
          if (act.startsWith('activate')) msg = `${what} уедет всем игрокам сразу и заменит то, что они получают сейчас.`;
          else if (act === 'maint-on') msg = `Включаются ${what}: вместо каталога игр они увидят заглушку.`;
          else msg = `Будет удалено: ${what}. Вернуть нельзя.`;
          if (!confirm(`${msg}

Продолжить?`)) return;
          toast('Это превью: ничего не изменилось', 'bad');
          return;
        }
        toast(`Это превью: «${b.textContent.trim()}» ничего не меняет`);
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

    addEventListener('keydown', (e) => {
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
    $('[data-logout]').addEventListener('click', () => toast('Это превью: выход отключён'));
  }

  /* ---------- Запуск ---------- */

  async function boot(again = false) {
    D = await window.CHILLHUB_DATA.load();
    if (!again) {
      topbar();
      palette();
      addEventListener('hashchange', route);
    }
    route();
    if (again) {
      toast('Данные перечитаны', 'ok');
      return;
    }
    // Признак «живое или демо» теперь посекционный: часть разделов может
    // читаться с сервера, часть — остаться на снимке, если эндпоинт не
    // ответил. Сказать «всё демо», когда половина настоящая, значит
    // обесценить настоящую половину; промолчать про снимок — хуже вдвое.
    const total = 9;
    const live = (D.live || []).length;
    if (!live) {
      toast('Демо-данные: сервер не отвечает, ничего не сохраняется');
    } else if (live < total) {
      toast(`Часть разделов на снимке: живых ${live} из ${total}`, 'warn');
    }
  }

  boot();
})();
