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
        /* Правило «собрано, но не отдано» одно на панель и лежит в
           разборе: два одинаковых условия в разных местах расходятся
           молча, а расходятся они как раз на краях — у игры без единой
           сборки и у игры без активной версии. */
        const staged = D.packs.filter((p) => p.staged);
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
      /* Каждая версия — сотня мегабайт, и место кончается тихо: диск
         забивается сборками, о которых никто не помнит. Оставляем пять
         свежих — этого хватает, чтобы откатиться на пару выпусков. */
      /* Кнопка появляется, только когда есть что удалять, и называет
         объём: «убрать старые» при нечего убирать — обещание, которое
         не выполнится, и человек идёт искать, что пошло не так. */
      actions: () => {
        const victims = window.CH2Upload.prunable(
          D.launcher.versions.map((v) => v.version),
          D.launcher.active
        );
        if (!victims.length) return '';
        return `<button class="btn" type="button" data-act="launcher.prune" data-args='${esc(
          JSON.stringify({ victims: victims, active: D.launcher.active })
        )}'>Убрать ${victims.length} старых</button>`;
      },
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
                     Отдать игрокам
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
                      <td class="dim">${esc(when(v.date))}</td>
                      <td class="num">${v.files}</td>
                      <td class="num">${bytes(v.size)}</td>
                      <td>${stateBadge[v.state]}</td>
                      <td class="act">
                        ${v.state === 'active' ? '' : `<button class="btn btn--text" type="button" data-act="launcher.activate" data-args='{"version":"${esc(v.version)}"}'>Отдать игрокам</button>`}
                        ${v.state === 'active' ? '' : `<button class="btn btn--danger btn--text" type="button" data-act="launcher.delete" data-args='{"version":"${esc(v.version)}","active":${v.state === 'active'}}'>Удалить</button>`}
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
      /* Кнопки в заголовке нет намеренно: сборка живёт в своей карточке
         ниже, где сказано, что она делает и сколько идёт. Три кнопки
         одного действия на одном экране — это три разных названия
         одного и того же, и читатель ищет между ними разницу. */
      render() {
        /* Игра без модпака из раздела просто исчезала, и подключить ей
           моды было негде: панель показывала только те, у которых они
           уже есть. Раздел обязан объяснить, чего у игры нет и что
           даст подключение. */
        const off = D.games.filter((g) => !D.packs.some((x) => x.gameId === g.gameId));

        if (!D.packs.length) {
          return card(
            'Модпаков ещё нет',
            `<div class="stack stack--tight">
               <p class="dim">Модпак — это набор модов с Thunderstore, который лаунчер ставит игроку вместе с игрой и держит одинаковым у всей компании.</p>
               <p class="faint">Чтобы подключить его игре, нужно назвать её так, как она зовётся на Thunderstore: оттуда придут идентификатор Steam, папка установки и раздел с модпаками.</p>
               <div class="btn-row"><button class="btn btn--accent" type="button" data-act="ecosystem">Подключить моды</button></div>
             </div>`
          );
        }

        const p = packOf(game);
        const stale = p.behind || p.deprecated;
        const staged = p.staged;

        const tabs = D.packs
          .map(
            (g) => `<button class="seg${g.gameId === game ? ' on' : ''}" type="button" data-game="${g.gameId}">
                ${esc(g.title)}${g.behind || g.deprecated ? '<span class="dot warn"></span>' : ''}
              </button>`
          )
          .join('');

        return `
          <div class="segs">${tabs}</div>
          ${
            off.length
              ? `<p class="faint" style="margin-top: var(--s2)">Без модпака: ${off
                  .map((g) => esc(g.title))
                  .join(', ')}. <button class="btn btn--text" type="button" data-act="ecosystem">Подключить моды</button></p>`
              : ''
          }

          <div class="handoff" style="margin-top: var(--s3)">
            <div><span class="k">Игроки получают</span><span class="v mono">${esc(p.active)}</span></div>
            <span class="arrow" aria-hidden="true">→</span>
            <div><span class="k">Собрано</span><span class="v mono">${esc(p.built)}</span><span class="k">${esc(when(p.builtAt))}</span></div>
            <span class="arrow" aria-hidden="true">→</span>
            <div>
              <span class="k">На Thunderstore</span>
              <span class="v mono">${esc(p.latest || '—')}</span>
              <span class="k">${esc(p.latestAt || 'дата не приходит с Thunderstore')}</span>
            </div>
            <div class="push"></div>
            <button class="btn btn--text" type="button" data-act="versions" data-args='{"gameId":"${esc(p.gameId)}","title":"${esc(p.title)}"}'>Все версии</button>
            ${staged ? `<button class="btn" type="button" data-act="mods-diff" data-args='{"gameId":"${esc(p.gameId)}","from":"${esc(p.active)}","to":"${esc(p.built)}","title":"${esc(p.title)}"}'>Что изменится</button>` : ''}
            ${staged ? `<button class="btn btn--accent" type="button" data-act="mods.activate" data-args='{"gameId":"${esc(p.gameId)}","version":"${esc(p.built)}"}'>Отдать игрокам</button>` : ''}
            ${stale && p.latest ? `<button class="btn btn--accent" type="button" data-act="build">Собрать ${esc(p.latest)}</button>` : ''}
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
                   <div class="btn-row"><button class="btn${stale && p.latest ? '' : ' btn--accent'}" type="button" data-act="build">Собрать</button></div>
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
    /* ИГРЫ: СПИСОК СЛЕВА, РАБОТА СПРАВА.
       Реестр был таблицей на всю ширину, а правка, галерея и удаление
       открывались листами поверх неё. Чтобы перейти к соседней игре,
       лист закрывали, искали строку глазами и открывали снова, — а
       версии сборок игры панель не показывала вовсе. Здесь выбор и
       работа стоят рядом, как в 1.0. */
    games: {
      title: 'Игры',
      lede: 'Реестр, который лаунчер читает при старте: чем игра запускается и как выглядит.',
      actions:
        '<button class="btn" type="button" data-act="games.scan">Найти новые</button>' +
        '<button class="btn" type="button" data-act="order">Порядок в лаунчере</button>' +
        '<button class="btn btn--accent" type="button" data-act="new-game">Добавить игру</button>',
      render() {
        pickGameIfNeeded();
        return `
          <div class="cols cols--master" data-games>
            ${card(
              'Реестр',
              `<div class="stack stack--tight">
                 <input type="search" data-game-search placeholder="Поиск по названию или идентификатору" aria-label="Поиск игры">
                 ${V().pickList(D.games.map(gameRow), {
                   selected: gameEdit.adding ? '' : gameEdit.gameId,
                   empty: 'Реестр пуст',
                   emptyHint: 'Пока здесь ничего нет, лаунчер показывает игроку пустую библиотеку.',
                 })}
               </div>`
            )}
            <div data-game-detail>${gameDetail()}</div>
          </div>`;
      },
    },

    /* НОВОСТИ: СПИСОК, РЕДАКТОР И ПРЕДПРОСМОТР РЯДОМ.
       Раздел был таблицей, а заметку правили в листе поверх неё. Значит,
       чтобы взглянуть на соседнюю, лист закрывали; чтобы увидеть, что
       выйдет у игрока, открывали новое окно браузера. Здесь всё три
       вещи стоят рядом, как в 1.0: выбор слева, текст посередине, вид
       игрока справа. */
    news: {
      title: 'Новости',
      lede: 'То, что игрок читает на главном экране лаунчера.',
      actions:
        '<button class="btn" type="button" data-act="news.rebuild">Пересобрать индекс</button>' +
        '<button class="btn btn--accent" type="button" data-act="new-post">Написать</button>',
      render() {
        pickPostIfNeeded();
        const shown = visibleNews();
        return `
          <div class="cols cols--master3" data-news>
            ${card(
              'Заметки',
              `<div class="stack stack--tight">
                 ${V().newsFilter(newsEdit, D.games)}
                 ${V().pickList(shown.map(newsRow), {
                   selected: newsEdit.adding ? '' : newsKey(newsEdit),
                   empty: 'Заметок нет',
                   emptyHint: 'Лаунчер покажет игроку пустую ленту, пока здесь ничего не написано.',
                 })}
               </div>`
            )}
            <div data-news-editor>${newsEditor()}</div>
            <div data-news-preview>${newsPreview()}</div>
          </div>`;
      },
    },

    inbox: {
      title: 'Обращения',
      lede: 'Что пишут из лаунчера. Контакт необязателен, поэтому ответить получится не на всё.',
      /* Функцией, а не строкой: счётчик берётся из данных, а их на
         момент создания раздела ещё нет. */
      actions: () =>
        `<button class="btn btn--danger btn--text" type="button" data-act="inbox.clear" data-args='{"count":${D.inbox.length}}'>Очистить всё</button>`,
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
                <td class="dim">${esc(when(f.at))}</td>
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
      actions:
        '<button class="btn btn--danger btn--text" type="button" data-act="metrics.clear">Удалить все метрики</button>',
      render() {
        /* Имена полей — те, что отдаёт разбор ответа, а не те, что в
           JSON сервера: иначе весь раздел считает undefined и рисует
           NaN. Геометрия графика — в views.js, там же её и проверяют:
           пустой ряд, один день и ряд из одних нулей ломали её молча. */
        const w = 640;
        const h = 140;
        const sum = (k) => D.days.reduce((a, d) => a + (Number(d[k]) || 0), 0);
        const share = sum('updates') > 0 ? sum('errors') / sum('updates') : 0;

        return `
          ${V().metricsFilter(metricsFilter, D.games)}
          ${card(
            `Коды ошибок за ${metricsFilter.days} дней${metricsFilter.gameId ? ': ' + esc(metricsFilter.gameId) : ''}`,
            list({
              rows: D.errors,
              /* Колонки «Где чаще» здесь не было никогда: сводка отдаёт
                 код и число, и колонка стояла пустой у каждой строки. */
              head: '<th>Код</th><th>Что это значит</th><th class="num">Случаев</th><th class="num">Доля</th>',
              row: (e) => `<tr>
                  <td class="mono"><button class="btn btn--text" type="button" data-act="error-events" data-args='{"code":"${esc(e.code)}"}'>${esc(e.code)}</button></td>
                  <td class="dim">${esc(e.what)}</td>
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
            <div class="attn-item"><span class="k">Запусков лаунчера</span><span class="v">${sum('starts')}</span><span class="s">за ${metricsFilter.days} дней</span></div>
            <div class="attn-item"><span class="k">Установок</span><span class="v">${sum('installs')}</span><span class="s">первых, с нуля</span></div>
            <div class="attn-item"><span class="k">Обновлений</span><span class="v">${sum('updates')}</span><span class="s">докачек разницы</span></div>
          </div>

          ${/* Ради этих трёх чисел события и собирают: счётчики выше
                говорят, сколько всего было, а эти — что из этого вышло. */ ''}
          ${V().metricsTotals(D.totals)}

          ${card(
            'Динамика',
            V().chart(
              [
                { title: 'запуски игр', color: 'var(--ember)', values: D.days.map((d) => d.launches) },
                { title: 'обновления', color: 'var(--ok)', values: D.days.map((d) => d.updates) },
                { title: 'ошибки', color: 'var(--bad)', values: D.days.map((d) => d.errors) },
              ],
              {
                width: w,
                height: h,
                from: (D.days[0] || {}).date || '',
                to: (D.days.at(-1) || {}).date || '',
                label: `Запуски игр, обновления и ошибки за ${metricsFilter.days} дней`,
              }
            )
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
                   <span class="faint">${D.cache.files} файлов${D.cache.ttlDays ? `, хранятся ${D.cache.ttlDays} дней` : ''}</span>
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

  /* ВРЕМЯ ПОКАЗЫВАЕМ ЛЮДЯМ, А НЕ ОТДАЁМ КАК ЕСТЬ.
     Сервер отвечает RFC3339 в UTC («2026-09-06T12:53:24Z»), и три
     колонки выводили эту строку в таблицу. Читать её неудобно, а «Z» в
     конце ещё и врёт рядом с остальными экранами: там время местное.
     Снимок из data.js держал даты уже готовыми строками, поэтому в
     проверках это не всплывало ни разу. */
  const when = (v) => window.CH2Format.dateTime(v);

  /* Верхний открытый лист; null — открытых нет. Ведётся ради одного
     правила: нажатие в РАЗДЕЛЕ не должно класть второй лист поверх
     первого. Лист, открытый из другого листа (выбор вложения поверх
     редактора заметки), — не то же самое, и его не трогаем. */
  let openedSheet = null;

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
        if (openedSheet === h) openedSheet = null;
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
    openedSheet = h;
    return h;
  }

  /* Сторож опоздавших ответов.

     Листы со списками читают сервер на каждый шаг: перешли в папку,
     сменили страницу, нажали поиск. Ответы возвращаются не в том
     порядке, в каком уходили, и опоздавший затирает свежий — на экране
     оказывается содержимое прошлой папки, а подпись пути говорит про
     новую. Считаем запросы и отбрасываем всё, кроме последнего. */
  function latest() {
    let seq = 0;
    return {
      start: () => ++seq,
      fresh: (n) => n === seq,
    };
  }

  /** Кнопки подвала листа по описанию из `views.js`. */
  const footButtons = (items) =>
    items
      .map(
        (b) =>
          `<button class="btn${b.accent ? ' btn--accent' : ''}${b.danger ? ' btn--danger' : ''}" type="button" data-flow="${b.act}"${b.off ? ' disabled' : ''}>${esc(b.title)}</button>`
      )
      .join('');

  /* --- Загрузка сборки --- */

  /* Кусок и число потоков подбираются от размера файла тем же модулем,
     что и в панели 1.0, — и показываются до нажатия, а не после. */
  function flowUpload(meta) {
    const U = window.CH2Upload;
    const L = D.launcher;

    /* Что и какой версией грузим — спрашиваем до выбора файла: сервер
       без игры и номера отвечает отказом, и узнавать это, выбрав архив
       на полтора гигабайта, значит потерять время дважды. */
    let st = {
      phase: 'idle',
      gameId: meta.gameId || '',
      version: U.nextVersion(meta.gameId ? '' : L.active || L.newest),
      current: meta.gameId ? '' : L.active,
    };

    const sheet = openSheet({
      title: 'Загрузка сборки',
      lede: 'Файл заливается кусками и переживает обрыв связи. Игрокам он сам не уйдёт.',
      body: V().uploadTarget(st, D.games, U) + V().uploadCard(st),
      foot: footButtons(V().uploadButtons(st, U)),
    });

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.zip';

    let ctrl = null;
    let uploadId = '';

    /* Скорость считает тот же оценщик, что и в панели 1.0. Окно и
       минимальная ширина взяты оттуда же и не с потолка: в первые
       мгновения заливки байты, посчитанные по onprogress, — это не
       переданное, а принятое буферами, и узкое окно честно делит
       мегабайты на миллисекунды, выдавая «>100 МБ/с» на канале, где
       столько не бывает. */
    const rate = window.makeRateEstimator(4000, { minSpanMs: 1200 });

    const paint = () => {
      /* Выбор цели показываем, только пока дело не началось: менять
         игру и версию посреди заливки некуда, а поле на экране это
         предлагает. */
      sheet.body((st.phase === 'idle' ? V().uploadTarget(st, D.games, U) : '') + V().uploadCard(st));
      sheet.foot(footButtons(V().uploadButtons(st, U)));
    };

    /* Отрисовка не чаще четырёх раз в секунду. Событий прогресса летят
       сотни в секунду — по одному на каждый принятый кусок каждого
       потока, — и перерисовывать на каждое значит занять весь кадр
       перерисовкой вместо загрузки. */
    const throttled = window.makeUiThrottler(250, paint);
    const draw = (now) => (now ? paint() : throttled.schedule());

    /* Закрытие листа посреди заливки — это отмена, а не сворачивание:
       брошенная загрузка оставила бы на сервере недособранный архив. */
    sheet.onClose = () => {
      if (ctrl) ctrl.abort();
      if (uploadId && st.phase !== 'done') window.CH2Upload.abort(API, uploadId);
    };

    async function start(file) {
      const params = window.CH2_UPLOAD_PARAMS
        ? { chunkSize: window.CH2_UPLOAD_PARAMS.size || window.CH2Upload.DEFAULT_CHUNK, concurrency: window.CH2_UPLOAD_PARAMS.streams }
        : window.pickUploadParams
          ? window.pickUploadParams(file.size, {})
          : { chunkSize: window.CH2Upload.DEFAULT_CHUNK, concurrency: 4 };
      ctrl = new AbortController();
      st = Object.assign({}, st, {
        phase: 'init',
        file: { name: file.name, size: file.size },
        chunkSize: params.chunkSize,
        streams: params.concurrency,
        progress: 0,
      });
      draw(true);

      try {
        const res = await window.CH2Upload.run(
          file,
          {
            /* Лаунчер для сервера — такая же «игра» с зарезервированным
               идентификатором, поэтому вид один, а различает их gameId. */
            kind: st.gameId ? 'game' : 'launcher',
            gameId: st.gameId || window.CH2Api.LAUNCHER,
            version: st.version.trim(),
            chunkSize: params.chunkSize,
          },
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

              /* Оценщику скармливаем байты, а не куски: кусок в 8 МБ
                 меняет счётчик рывком, и по нему скорость выглядит
                 пилой вместо ровной линии. */
              if (ev.phase === 'upload') {
                const sent = Math.round((Number(ev.progress) || 0) * file.size);
                // push(время, байты) — порядок как в модуле 1.0
                st.speed = rate.push(Date.now(), sent);
                st.left = Math.max(0, file.size - sent);
              }

              /* Смена шага — событие, а не тик: её показывают сразу. */
              draw(ev.phase !== 'upload');
            },
          }
        );
        uploadId = res.uploadId;

        st = Object.assign({}, st, { phase: 'process', progress: 1 });
        draw(true);
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
        draw(true);
        if (done.ok) await refresh(['launcher', 'overview', 'disk'], false);
      } catch (e) {
        st = Object.assign({}, st, { phase: 'failed', message: (e && e.message) || 'сбой' });
        draw(true);
      }
    }

    input.addEventListener('change', () => {
      if (input.files && input.files[0]) start(input.files[0]);
    });

    sheet.root.addEventListener('change', (e) => {
      if (!e.target.matches('[name="target"], [name="version"]')) return;
      const target = sheet.root.querySelector('[name="target"]').value;
      const version = sheet.root.querySelector('[name="version"]').value;
      const switched = (target === 'launcher' ? '' : target) !== st.gameId;
      st = Object.assign({}, st, {
        gameId: target === 'launcher' ? '' : target,
        version: version,
        current: target === 'launcher' ? L.active : '',
      });
      /* Сменили цель — номер предлагаем заново: версия лаунчера и
         версия игры между собой не связаны никак. */
      if (switched) st.version = U.nextVersion(st.gameId ? '' : L.active || L.newest);
      draw(true);
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
        draw(true);
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

  /* Пересборка КОНКРЕТНОЙ версии — не то же самое, что сборка свежей.
     Сервер собирает под тем же номером и кладёт на то же место, поэтому
     пересборка активной заменяет то, что игроки уже качают: у половины
     окажется старый набор, у половины новый под одним номером. */
  async function flowRebuild(a) {
    if (a.active) {
      const agreed = await ask({
        title: 'Пересобрать версию ' + a.version + ', которая сейчас у игроков?',
        body:
          'Сборка ляжет под тем же номером и заменит то, что игроки уже качают. У тех, кто скачал раньше, останется прежний набор — под тем же номером версии. Обычно вместо этого собирают следующую.',
        ok: 'Всё равно пересобрать',
        cancel: 'Отмена',
      });
      if (!agreed) return;
    }
    /* Пересборка идёт СВОИМ путём на сервере: он читает состав из записи
       рядом с манифестом и собирает ровно его. Отправь мы сюда обычную
       сборку — сервер разложил бы сегодняшний состав модпака под старым
       номером, а у сборки, приехавшей профилем r2modman, ещё и не нашёл бы
       по имени вообще ничего. */
    flowBuild(Object.assign({}, packOf(a.gameId) || {}, { gameId: a.gameId, version: a.version, rebuild: true }));
  }

  function flowBuild(pack) {
    if (!pack.rebuild && !knowsPack(pack)) return;

    const sheet = openSheet({
      title: (pack.version ? 'Пересборка ' + pack.version + ': ' : 'Сборка модпака: ') + (pack.title || pack.gameId),
      lede: 'Идёт минутами. Собранное игрокам само не уходит — отдать его отдельное решение.',
      body: V().buildLog([], 'running'),
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    const events = [];

    window.CH2Build.run(
      {
        gameId: pack.gameId,
        namespace: pack.namespace,
        name: pack.name,
        /* Адрес страницы пакета сервер разбирает сам, и он есть там, где
           проверка обновлений молчит: у свежего модпака её попросту нет. */
        packageUrl: pack.packageUrl,
        version: pack.version || '',
        rebuild: Boolean(pack.rebuild),
      },
      {
        fetch: window.fetch.bind(window),
        ndjson: { readNdjsonStream: window.readNdjsonStream },
        confirm: ask,
        /* Строка ДОПИСЫВАЕТСЯ, а не перерисовывает журнал целиком.
           Перерисовка сбрасывала прокрутку в начало на каждую строку —
           и «прокрутить в конец» после неё ничего не давало, потому что
           прокручивался не журнал, а лист вокруг него. Заодно это
           работа, растущая квадратом от числа строк, а у сборки их
           сотни. */
        on: (ev) => {
          events.push(ev);
          const log = sheet.root.querySelector('[data-log]');
          if (log) window.logAppend(log, V().logRow(ev));
          else sheet.body(V().buildLog(events, 'running'));
        },
      }
    ).then(async (res) => {
      const out = V().buildOutcome(res);
      sheet.body(
        V().buildLog(events, 'done') +
          `<p class="note${out.tone === 'bad' ? ' note--bad' : ''}" data-build-outcome>${esc(out.text)}</p>`
      );
      /* Конец журнала — это причина отказа. Открывать его началом значит
         показывать «читаем список модов» там, где спрашивают «почему не
         собралось». */
      window.logToBottom(sheet.root.querySelector('[data-log]'));
      toast(out.text, out.tone);
      await refresh(['packs', 'overview'], false);
    });

    sheet.root.addEventListener('click', (e) => {
      if (e.target.closest('[data-flow="close"]')) {
        sheet.close();
        route();
      }
    });
  }

  /* ---------- Экран новостей: выбор, правка и вид игрока ---------- */

  /** Что открыто в редакторе и по чему отобран список слева. */
  let newsEdit = {
    scope: 'launcher',
    gameId: '',
    slug: '',
    adding: false,
    post: null,
    draft: null,
    problems: [],
    loading: false,
  };

  /* ВИД ГЛАЗАМИ ИГРОКА СОБИРАЕТ СЕРВЕР, И ОТДАЁТ ОН ДВЕ ЧАСТИ.
     `news/preview` отвечает `{listHtml, contentHtml}`: карточка в ленте и
     сама статья. Панель искала в ответе `html` и `markdown` — таких
     полей там нет, — и открывала пустое окно браузера со строкой
     «[object Object]». Разметку в текст превращает Markdig на сервере,
     теми же правилами, что и для лаунчера; собирать её в браузере
     второй раз значило бы показывать не то, что увидит игрок. */
  let newsShow = null;

  /** Заметка адресуется тройкой: раздел, игра и имя файла. */
  const newsKey = (n) => (n.gameId ? 'game/' + n.gameId + '/' : 'launcher//') + n.slug;

  function newsRow(n) {
    return {
      id: newsKey({ gameId: n.game, slug: n.slug }),
      title: n.title || n.slug,
      sub: window.CH2Format.dateTime(n.at) + (n.game ? ' · ' + n.game : ' · лаунчер'),
      badge: n.published
        ? '<span class="badge badge--ok">на виду</span>'
        : '<span class="badge badge--warn">черновик</span>',
    };
  }

  /* Отбор считается на месте: ленты всех игр уже прочитаны, и ходить за
     ними ещё раз ради переключателя незачем. */
  function visibleNews() {
    return D.news.filter((n) =>
      newsEdit.scope === 'game' ? n.game === newsEdit.gameId : !n.game
    );
  }

  /* Открытой остаётся та заметка, которую открыли. Пропала из списка —
     открываем первую видимую: экран не должен показывать поля того,
     чего уже нет. */
  function pickPostIfNeeded() {
    if (newsEdit.adding) return;
    const shown = visibleNews();
    const alive = shown.some((n) => newsKey({ gameId: n.game, slug: n.slug }) === newsKey(newsEdit));
    if (!alive) {
      const first = shown[0];
      openPost(first ? { scope: first.game ? 'game' : 'launcher', gameId: first.game, slug: first.slug } : null, true);
    }
  }

  /** Средняя колонка: имя, действия и текст. */
  function newsEditor() {
    if (!newsEdit.post) {
      return '<div class="empty"><b>Заметка не выбрана</b><span>Выберите её слева или напишите новую</span></div>';
    }
    const N = window.CH2News;
    const post = newsEdit.post;
    return card(
      newsEdit.adding ? 'Новая заметка' : 'Заметка: ' + post.slug,
      (newsEdit.draft ? V().draftNote(newsEdit.draft, post, N) : '') +
        V().newsForm(post, newsEdit.problems),
      {
        foot:
          '<button class="btn" type="button" data-post="assets">Вложения</button>' +
          (post.existing
            ? '<button class="btn" type="button" data-post="publish">' +
              (post.published ? 'Снять с публикации' : 'Опубликовать') +
              '</button>' +
              '<button class="btn btn--danger btn--text" type="button" data-post="delete">Удалить</button>'
            : '') +
          '<span class="push"></span>' +
          '<button class="btn btn--accent" type="button" data-post="save">Сохранить</button>',
      }
    );
  }

  /** Правая колонка: как это увидит игрок. */
  function newsPreview() {
    if (!newsEdit.post) return '';
    const N = window.CH2News;
    const post = newsEdit.post;
    return `
      <div class="stack">
        ${card(
          'Обложка',
          post.coverUrl
            ? `<img src="${esc(post.coverUrl)}" alt="" style="width:100%;border-radius:var(--r)">`
            : '<p class="faint">Обложки нет — сервер возьмёт первую картинку из текста.</p>'
        )}
        ${card(
          'В ленте',
          newsShow && newsShow.list
            ? `<div data-news-card>${newsShow.list}</div>`
            : V().newsHeadline(post.markdown, N)
        )}
        ${card(
          'Внутри заметки',
          newsShow === null
            ? '<p class="faint">Показать, что выйдет у игрока, можно кнопкой ниже: разметку в текст превращает сервер, а не браузер.</p>'
            : `<div class="scroll scroll--sm" data-news-body>${newsShow.content}</div>`,
          { foot: '<button class="btn" type="button" data-post="preview">Посмотреть глазами игрока</button>' }
        )}
      </div>`;
  }

  function drawNewsEditor() {
    const box = $('[data-news-editor]');
    if (box) box.innerHTML = newsEditor();
    drawNewsPreview();
  }

  function drawNewsPreview() {
    const box = $('[data-news-preview]');
    if (box) box.innerHTML = newsPreview();
  }

  /** Открыть заметку. `where === null` — редактор пуст. */
  function openPost(where, quiet) {
    const N = window.CH2News;
    newsShow = null;
    newsEdit.problems = [];
    newsEdit.adding = false;

    if (!where) {
      newsEdit.slug = '';
      newsEdit.post = null;
      newsEdit.draft = null;
      if (!quiet) route();
      return;
    }

    newsEdit.scope = where.scope || (where.gameId ? 'game' : 'launcher');
    newsEdit.gameId = where.gameId || '';
    newsEdit.slug = where.slug || '';
    newsEdit.post = {
      slug: newsEdit.slug,
      gameId: newsEdit.gameId,
      markdown: '',
      coverUrl: '',
      published: false,
      existing: true,
    };
    newsEdit.draft = null;
    newsEdit.loading = true;

    const want = newsKey(newsEdit);
    (async () => {
      try {
        const got = await API.newsGet(newsEdit.gameId ? 'game' : 'launcher', newsEdit.gameId, newsEdit.slug);
        if (newsKey(newsEdit) !== want) return;
        newsEdit.post = Object.assign({}, newsEdit.post, {
          markdown: (got && got.markdown) || '',
          coverUrl: (got && got.coverUrl) || '',
          published: Boolean(got && got.published),
        });
        newsEdit.draft = N.readDraft(window.localStorage, newsEdit.post);
      } catch (err) {
        toast('Заметка не прочиталась: ' + window.CH2Api.reason(err), 'warn');
      }
      newsEdit.loading = false;
      drawNewsEditor();
    })();

    if (!quiet) route();
  }

  function openNewPost() {
    newsShow = null;
    newsEdit.adding = true;
    newsEdit.problems = [];
    newsEdit.slug = '';
    newsEdit.post = {
      slug: '',
      gameId: newsEdit.scope === 'game' ? newsEdit.gameId : '',
      markdown: '',
      coverUrl: '',
      published: false,
      existing: false,
    };
    newsEdit.draft = null;
    if (location.hash.slice(1).split('?')[0] === 'news') route();
    else location.hash = '#news';
  }

  /** Читает поля со страницы: перерисовки на каждый ввод здесь нет. */
  function readPostForm() {
    const box = $('[data-news-editor]');
    if (!box || !newsEdit.post) return;
    const q = (n) => box.querySelector('[name="' + n + '"]');
    if (!q('markdown')) return;
    newsEdit.post = Object.assign({}, newsEdit.post, {
      slug: q('slug').value,
      gameId: q('gameId').value,
      coverUrl: q('coverUrl').value,
      markdown: q('markdown').value,
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

    const seq = latest();

    async function load() {
      const mine = seq.start();
      try {
        const got = await API.newsAssets(path);
        if (!seq.fresh(mine)) return;
        entries = (got && (got.items || got.entries)) || [];
      } catch (err) {
        if (!seq.fresh(mine)) return;
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
  /**
   * Галерея игры.
   *
   * `host` — куда рисовать: у него есть `root` (элемент, на котором висят
   * нажатия) и `body(html)` (перерисовать содержимое). Лист даёт и то, и
   * другое; вкладка «Галерея» на экране игр — тоже. Работа с файлами от
   * этого не зависит, а второй копии её здесь быть не должно.
   */
  function flowGallery(gameId, host) {
    const G = window.CH2Gallery;
    let path = '';
    let entries = [];
    let cover = '';

    const sheet =
      host ||
      openSheet({
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

    const seq = latest();

    async function load() {
      const mine = seq.start();
      try {
        const got = await API.gallery(gameId, path);
        if (!seq.fresh(mine)) return;
        entries = (got && (got.items || got.entries)) || [];
        cover = got && got.cover !== undefined ? got.cover : cover;
      } catch (err) {
        if (!seq.fresh(mine)) return;
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

  /* ---------- Экран игр: выбор слева, работа справа ---------- */

  /** Какая игра открыта, какой стороной и не заводят ли новую. */
  let gameEdit = { gameId: '', tab: 'overview', adding: false, item: null, problems: [] };

  /** Версии сборок выбранной игры: читаются по открытию вкладки. */
  let gameBuilds = null;

  const GAME_TABS = [
    { id: 'overview', title: 'Обзор' },
    { id: 'versions', title: 'Версии' },
    { id: 'gallery', title: 'Галерея' },
    { id: 'danger', title: 'Публикация и удаление' },
  ];

  /** Строка списка игр: что видно, не открывая её. */
  function gameRow(g) {
    const marks =
      (g.icon ? '' : '<span class="badge badge--warn">без иконки</span>') +
      (g.published ? '' : '<span class="badge badge--warn">скрыта</span>') +
      (g.modsEnabled ? '<span class="badge">моды</span>' : '');
    return { id: g.gameId, title: g.title || g.gameId, sub: g.gameId, badge: marks };
  }

  /** Поля правки — из того, что прочитано с сервера. */
  function gameFields(gameId) {
    const src = D.games.find((g) => g.gameId === gameId) || {};
    return {
      gameId: gameId,
      title: src.title || '',
      exeRelativePath: src.exe || '',
      steamAppId: src.steamId || '',
      steamFolder: src.steamFolder || '',
      iconUrl: src.iconUrl || '',
      unpublished: src.published === false,
      existing: Boolean(gameId),
    };
  }

  /* Открытым остаётся то, что открыли. Но выбранная игра могла пропасть
     из реестра — тогда открываем первую, иначе экран показывал бы поля
     того, чего уже нет. */
  function pickGameIfNeeded() {
    if (gameEdit.adding) return;
    const alive = D.games.some((g) => g.gameId === gameEdit.gameId);
    if (!alive) {
      const first = D.games[0];
      gameEdit = {
        gameId: first ? first.gameId : '',
        tab: 'overview',
        adding: false,
        item: first ? gameFields(first.gameId) : null,
        problems: [],
      };
      gameBuilds = null;
      return;
    }
    if (!gameEdit.item) gameEdit.item = gameFields(gameEdit.gameId);
  }

  /** Правая половина экрана целиком. */
  function gameDetail() {
    if (gameEdit.adding) {
      return card('Новая игра', V().gameForm(gameEdit.item, gameEdit.problems), {
        foot:
          '<button class="btn btn--text" type="button" data-game-do="cancel">Отмена</button>' +
          '<span class="push"></span>' +
          '<button class="btn btn--accent" type="button" data-game-do="save">Завести игру</button>',
      });
    }
    if (!gameEdit.item) {
      return '<div class="empty"><b>Реестр пуст</b><span>Заведите первую игру — лаунчер покажет её игрокам</span></div>';
    }
    return card(
      'Игра: ' + (gameEdit.item.title || gameEdit.item.gameId),
      V().tabs(GAME_TABS, gameEdit.tab) + gameTabBody(),
      { foot: gameTabFoot() }
    );
  }

  function gameTabBody() {
    if (gameEdit.tab === 'overview') {
      return V().gameForm(gameEdit.item, gameEdit.problems);
    }

    if (gameEdit.tab === 'versions') {
      if (gameBuilds === null) return '<div class="sk" style="height:12rem"></div>';
      return (
        list({
          rows: gameBuilds.versions,
          head: '<th>Версия</th><th>Собрана</th><th class="num">Файлов</th><th class="num">Размер</th><th>Состояние</th><th></th>',
          row: (v) => `<tr>
              <td class="mono">${esc(v.version)}</td>
              <td class="dim">${esc(window.CH2Format.dateTime(v.date))}</td>
              <td class="num">${v.files}</td>
              <td class="num">${bytes(v.size)}</td>
              <td>${
                v.state === 'active'
                  ? '<span class="badge badge--ok">у игроков</span>'
                  : v.state === 'uploaded'
                    ? '<span class="badge badge--accent">загружена</span>'
                    : '<span class="badge">старая</span>'
              }</td>
              <td class="act">${
                v.state === 'active'
                  ? ''
                  : `<button class="btn btn--text" type="button" data-game-do="activate" data-version="${esc(v.version)}">Отдать игрокам</button>` +
                    `<button class="btn btn--danger btn--text" type="button" data-game-do="delete" data-version="${esc(v.version)}">Удалить</button>`
              }</td>
            </tr>`,
          empty: 'Сборок нет',
          emptyHint: 'Игроки увидят игру в списке, но скачать им будет нечего.',
        }) +
        '<p class="note">Активную версию удалить нельзя: клиенты, которые её докачивают, потеряют файлы на середине.</p>'
      );
    }

    if (gameEdit.tab === 'gallery') {
      /* У галереи свой корень: на нём висят её нажатия, и создаётся он
         заново на каждую отрисовку. Вешать их на общий ящик нельзя —
         подписки копились бы с каждым открытием вкладки. */
      return (
        '<div data-gallery>' +
        '<div class="btn-row" style="margin-bottom: var(--s3)">' +
        '<button class="btn" type="button" data-flow="mkdir">Новая папка</button>' +
        '<button class="btn" type="button" data-flow="pick">Загрузить файл</button>' +
        '<button class="btn" type="button" data-flow="byUrl">Загрузить по ссылке</button>' +
        '</div>' +
        '<div data-gallery-body><div class="sk" style="height:14rem"></div></div>' +
        '</div>'
      );
    }

    const g = gameEdit.item;
    return `
      <div class="stack">
        <div class="note">${
          g.unpublished
            ? 'Игра скрыта от игроков: в лаунчере её нет, файлы и версии лежат на месте.'
            : 'Игра видна игрокам. Убрать её с витрины можно галочкой на вкладке «Обзор» — файлы при этом останутся.'
        }</div>
        <div class="stack stack--tight">
          <p class="dim">Убрать из реестра — игра пропадает из лаунчера, а её манифесты, версии и галерея остаются на диске.</p>
          <div class="btn-row"><button class="btn btn--danger" type="button" data-game-do="remove">Убрать из реестра</button></div>
        </div>
        <div class="stack stack--tight">
          <p class="dim">Удалить контент — с диска уходят все сборки, манифесты и картинки этой игры. Вернуть их можно только заливкой заново.</p>
          <div class="btn-row"><button class="btn btn--danger" type="button" data-act="games.purge" data-args='{"gameId":"${esc(g.gameId)}","title":"${esc(g.title || g.gameId)}"}'>Удалить контент</button></div>
        </div>
      </div>`;
  }

  function gameTabFoot() {
    if (gameEdit.tab === 'overview') {
      return '<span class="push"></span><button class="btn btn--accent" type="button" data-game-do="save">Сохранить</button>';
    }
    if (gameEdit.tab === 'versions') {
      return (
        '<span class="faint">Старее активной сервер оставляет две — на случай отката</span>' +
        '<span class="push"></span>' +
        '<button class="btn" type="button" data-game-do="prune">Убрать старые</button>'
      );
    }
    return '';
  }

  /** Перерисовывает правую половину, не трогая список слева. */
  function drawGameDetail() {
    const box = $('[data-game-detail]');
    if (!box) return;
    box.innerHTML = gameDetail();
    if (gameEdit.tab === 'gallery' && !gameEdit.adding) mountGameGallery();
  }

  /* Галерея рисует себя во вкладку тем же кодом, что и в листе: второй
     копии работы с файлами здесь быть не должно. */
  function mountGameGallery() {
    const box = $('[data-game-detail]');
    if (!box) return;
    const pane = box.querySelector('[data-gallery]');
    if (!pane) return;
    flowGallery(gameEdit.gameId, {
      root: pane,
      body: (html) => {
        const slot = pane.querySelector('[data-gallery-body]');
        if (slot) slot.innerHTML = html;
      },
    });
  }

  async function loadGameBuilds() {
    const want = gameEdit.gameId;
    try {
      const got = await API.versions(want);
      if (gameEdit.gameId !== want) return;
      gameBuilds = window.CH2Sections.launcher(got);
    } catch {
      /* Сборок у игры может не быть вовсе — сервер отвечает 404, и это не
         отказ: игра заведена, заливать ей ещё нечего. */
      if (gameEdit.gameId !== want) return;
      gameBuilds = { versions: [], active: '', newest: '', uploaded: [], pending: false };
    }
    if (gameEdit.tab === 'versions') drawGameDetail();
  }

  /** Читает поля со страницы в состояние: перерисовки на ввод здесь нет. */
  function readGameForm() {
    const box = $('[data-game-detail]');
    if (!box || !gameEdit.item) return;
    const q = (n) => box.querySelector('[name="' + n + '"]');
    if (!q('title')) return;
    gameEdit.item = Object.assign({}, gameEdit.item, {
      gameId: gameEdit.adding ? q('gameId').value.trim() : gameEdit.item.gameId,
      title: q('title').value,
      exeRelativePath: q('exeRelativePath').value,
      steamAppId: q('steamAppId').value,
      steamFolder: q('steamFolder').value,
      iconUrl: q('iconUrl').value,
      unpublished: !q('published').checked,
    });
  }

  /* Список для сохранения собирается из того, что прочитано с сервера, а
     не из строк на экране: в реестре есть поля, которых экран не
     показывает, и собранный из него список их бы стёр. */
  function mergedRegistry() {
    const R = window.CH2Registry;
    const item = gameEdit.item;
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
  }

  /** Открыть игру нужной стороной. Зовут и с экрана, и из палитры. */
  function openGame(gameId, tab) {
    gameEdit = {
      gameId: gameId,
      tab: tab || 'overview',
      adding: false,
      item: gameFields(gameId),
      problems: [],
    };
    gameBuilds = null;
    goGames();
  }

  function openNewGame() {
    gameEdit = {
      gameId: '',
      tab: 'overview',
      adding: true,
      item: {
        gameId: '',
        title: '',
        exeRelativePath: '',
        steamAppId: '',
        steamFolder: '',
        iconUrl: '',
        unpublished: false,
        existing: false,
      },
      problems: [],
    };
    gameBuilds = null;
    goGames();
  }

  /* Смена хэша сама позовёт отрисовку; если мы уже здесь, зовём её сами. */
  function goGames() {
    if (location.hash.slice(1).split('?')[0] === 'games') route();
    else location.hash = '#games';
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
          await refresh(['games']);
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

  /* ИЗ ЧЕГО СОБИРАТЬ — ЭТО ПАКЕТ, А НЕ ИГРА.
     Сервер собирает названный пакет на Thunderstore; из одной игры он
     собрать не может и отвечает «не указан модпак». У игры, которой
     ничего ещё не собирали, пакета и правда нет — и правильный ответ на
     нажатие тут не отказ в листе, а каталог, где его выбирают. */
  function knowsPack(pack) {
    const p = pack || {};
    if (p.namespace && p.name) return true;
    if (p.packageUrl) return true;

    toast('Сначала выберите модпак в каталоге', 'warn');
    flowCatalog(p);
    return false;
  }

  /* --- Состав будущей сборки --- */

  /* Пересчёт спрашивает у Thunderstore, из чего соберётся модпак, и не
     качает ни байта. Нужен он затем, что после сборки список менять
     поздно: пропавший пакет виден здесь, а не на середине выкатки. */
  function flowResolve(pack) {
    if (!knowsPack(pack)) return;

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

    const seq = latest();

    async function load() {
      const mine = seq.start();
      const bar = sheet.root.querySelector('[data-catalog-bar]');
      if (bar) bar.setAttribute('aria-busy', 'true');
      try {
        const got = await API.modsCatalog({ gameId: pack.gameId, q: st.q, ordering: st.ordering, page: st.page });
        if (!seq.fresh(mine)) return;
        items = (got && got.results) || [];
        st.count = Number((got && got.count) || 0);
        /* «Есть ли ещё» считается по полной странице, а не по счётчику:
           счётчик Thunderstore иногда отстаёт, а пустая следующая
           страница обиднее лишней стрелки. */
        st.hasMore = items.length >= st.perPage;
      } catch (err) {
        if (!seq.fresh(mine)) return;
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

  /* --- Собранные версии модпака --- */

  /* Строка раздела говорит про свежую сборку, а вопросов у списка версий
     два других: какая сейчас у игроков и без каких модов собрана каждая.
     Отвечать на них строкой нельзя, поэтому список — отдельным листом. */
  function flowVersions(a) {
    const sheet = openSheet({
      title: 'Версии модпака: ' + (a.title || a.gameId),
      lede: 'Отдать игрокам можно любую, удалить — любую, кроме той, что у них сейчас.',
      body: '<div class="sk" style="height:14rem"></div>',
      foot: '<button class="btn" type="button" data-flow="close">Закрыть</button>',
    });

    (async () => {
      try {
        const got = await API.modsList(a.gameId);
        sheet.body(
          V().modVersions(window.CH2Sections.items(got), { active: (got && got.active) || '', gameId: a.gameId })
        );
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
        await refresh(['packs'], false);
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
        sheet.body(V().logsView({ logs: text, logBytes: feedback.logBytes }));
        /* Журнал открывается концом, а не началом: спрашивают про то,
           что случилось перед обращением, а это последние строки. */
        window.logToBottom(sheet.root.querySelector('.log'));
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
        await refresh(['games']);
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
    rebuild: (a) => flowRebuild(a),
    versions: (a) => flowVersions(a),
    'error-events': (a) => flowErrorEvents(a),
    /* Заметку правят на экране «Новости», рядом со списком и видом
       игрока, — не в листе поверх раздела. */
    'new-post': () => openNewPost(),
    'edit-post': (a) => openPost({ scope: a.gameId ? 'game' : 'launcher', gameId: a.gameId, slug: a.slug }),
    /* Правка игры, её галерея и версии живут на экране «Игры» — не в
       листе поверх него. Дело этих трёх кнопок теперь одно: открыть там
       нужную игру нужной стороной. */
    gallery: (a) => openGame(a.gameId || (D.games[0] && D.games[0].gameId) || '', 'gallery'),
    'new-game': () => openNewGame(),
    'edit-game': (a) => openGame(a.gameId, 'overview'),
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

  /* Из каких разделов хранилища собран каждый экран. Нужно ровно для
     одного: сказать в самом разделе, что он показывает снимок, а не то,
     что на сервере. Имена — те, что человек видит в навигации, а не
     ключи хранилища: «metrics не ответил» ему ничего не говорит. */
  const SOURCES = {
    overview: { launcher: 'лаунчер', packs: 'сборки', news: 'новости', inbox: 'обращения', disk: 'диск' },
    launcher: { launcher: 'версии лаунчера' },
    packs: { packs: 'сборки модов' },
    games: { games: 'реестр игр' },
    news: { news: 'новости' },
    inbox: { inbox: 'обращения' },
    maint: { maint: 'технические работы' },
    errors: { metrics: 'метрики', errors: 'коды ошибок' },
    transfer: { disk: 'место на диске', cache: 'кэш архивов' },
  };

  /* Что сказать про свежесть открытого раздела. */
  function freshness(sectionId) {
    const map = SOURCES[sectionId] || {};
    const failed = [];
    const loading = [];
    for (const name of Object.keys(map)) {
      const st = store.get(name);
      if (!st) continue;
      if (st.status === window.CH2Store.FAILED) failed.push(map[name]);
      if (st.status === window.CH2Store.LOADING) loading.push(map[name]);
    }
    return V().staleNote(failed, loading);
  }

  /* РАЗБОР НАЖАТИЙ — ОДИН НА ВСЮ ПАНЕЛЬ, А НЕ НА КАЖДУЮ ОТРИСОВКУ.
     Раньше обработчики вешались на кнопки текущего раздела, и всё, что
     появлялось позже — а появляется в листах, — оказывалось мёртвым:
     кнопка есть, нажимается, не делает ничего. Слушаем документ и
     находим ближайшую кнопку от места нажатия. */
  async function onAct(e) {
    const b = e.target.closest && e.target.closest('[data-act]');
    if (!b || b.disabled) return;

    const id = b.dataset.act;

    let args = {};
    try {
      args = b.dataset.args ? JSON.parse(b.dataset.args) : {};
    } catch {
      args = {};
    }

    /* Технические работы собираются формой прямо в разделе: у них есть
       причина, окно и набор блоков, и кнопкой без них обошлась бы
       только заглушка без объяснения. */
    if (id === 'maint.on' || id === 'maint.save') {
      args.payload = maintPayload(true);
      const problem = V().maintProblem(args.payload);
      if (problem) {
        toast(problem, 'warn');
        return;
      }
    }

    /* Длинные дела панель ведёт сама: у них свой лист, свой ход и своё
       окончание, и в реестр записей они не помещаются. */
    if (FLOWS[id]) {
      /* ВТОРОЙ ЛИСТ ПОВЕРХ ПЕРВОГО — ЭТО НЕ ДВА ОКНА, А ОДНО ЗАБЫТОЕ.
         Два нажатия «Написать» подряд открывали два наложенных
         редактора: человек правит верхний, нижний ждёт под ним со своим
         черновиком и своими обработчиками, а Escape закрывает только
         верхний. Закрываем прежний его же `close` — на закрытии у листа
         висит уборка, и вырванный из страницы узел её не выполнит.

         Только для нажатий В РАЗДЕЛЕ: лист, открытый из другого листа,
         обязан ложиться поверх — так работает выбор вложения поверх
         редактора заметки. */
      if (openedSheet && !b.closest('[data-sheet]')) {
        openedSheet.close();
      }
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
    await refresh(res.stale);
  }

  /**
   * Перечитать разделы и пересобрать данные, по которым рисуются экраны.
   *
   * ПОЧЕМУ НЕ ХВАТАЕТ store.invalidate. Хранилище держит разделы, а
   * рисуют экраны из `D` — снимка, собранного из хранилища один раз на
   * запуске. Обновив хранилище и перерисовав экран, панель показывала
   * то же самое, что и до действия: сохранённое название игры,
   * отданная игрокам версия, включённые работы — всё оставалось
   * прежним до перезагрузки страницы. Со стороны это «кнопка ничего не
   * делает», и второе нажатие на такую кнопку стоит дороже первого.
   */
  async function refresh(stale, redraw) {
    await store.invalidate(stale);
    D = await collect();
    if (redraw !== false) { route(); }
  }

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
        ${(() => {
          /* Кнопки раздела бывают строкой и функцией: функция нужна там,
             где в подписи стоит число из данных. */
          const acts = typeof S.actions === 'function' ? S.actions() : S.actions;
          return acts ? `<div class="actions">${acts}</div>` : '';
        })()}
      </div>
      ${freshness(sec)}
      ${S.render()}`;

    document.title = `${S.title} — админ-панель Chill Hub`;
    wireSection();
  }

  /* ---------- Экран игр: нажатия ---------- */

  function wireGames() {
    const box = $('[data-games]');
    if (!box) return;

    /* Поиск прячет строки на месте, а не перерисовывает список: иначе
       поле теряет и текст, и курсор на каждой букве. */
    const search = box.querySelector('[data-game-search]');
    if (search) {
      search.addEventListener('input', () => {
        const q = search.value.trim().toLowerCase();
        box.querySelectorAll('[data-pick]').forEach((b) => {
          b.hidden = q ? !b.textContent.toLowerCase().includes(q) : false;
        });
      });
    }

    if (gameEdit.tab === 'versions' && gameBuilds === null && !gameEdit.adding) loadGameBuilds();
    if (gameEdit.tab === 'gallery' && !gameEdit.adding) mountGameGallery();

    /* Кнопки экрана помечены `data-game-do`, а не `data-game`: второе имя
       уже занято переключателем игр на экране сборок, и его обработчик
       перерисовывал раздел на каждое нажатие — вместе с формой, из
       которой мы в этот момент читаем поля. Правка при этом молча
       терялась: на сервер уезжало то, что было до неё. */
    box.addEventListener('click', async (e) => {
      const pick = e.target.closest('[data-pick]');
      if (pick) {
        openGame(pick.dataset.pick, gameEdit.tab === 'versions' ? 'versions' : gameEdit.tab);
        return;
      }

      const tab = e.target.closest('[data-tab]');
      if (tab) {
        readGameForm();
        gameEdit.tab = tab.dataset.tab;
        gameEdit.problems = [];
        if (gameEdit.tab === 'versions' && gameBuilds === null) loadGameBuilds();
        drawGameDetail();
        return;
      }

      /* Иконка живёт в форме «Обзора», а нажатия галереи — внутри её
         собственного корня: сюда они не доходят. */
      const icon = e.target.closest('[data-flow]');
      if (icon && !e.target.closest('[data-gallery]')) {
        readGameForm();
        if (icon.dataset.flow === 'icon') gameIconInput.click();
        if (icon.dataset.flow === 'icon-default') {
          gameEdit.item.iconUrl = '';
          drawGameDetail();
        }
        return;
      }

      const b = e.target.closest('[data-game-do]');
      if (!b) return;
      await onGameAction(b);
    });
  }

  async function onGameAction(b) {
    const R = window.CH2Registry;
    const kind = b.dataset.gameDo;

    if (kind === 'cancel') {
      gameEdit.adding = false;
      gameEdit.item = null;
      route();
      return;
    }

    if (kind === 'activate' || kind === 'delete') {
      const version = b.dataset.version;
      const question =
        kind === 'activate'
          ? {
              title: 'Отдать игрокам версию ' + version + '?',
              body: 'Лаунчер начнёт качать её всем, кто запустит игру. Прежняя останется на сервере — вернуться к ней можно тем же способом.',
              ok: 'Отдать игрокам',
              cancel: 'Отмена',
            }
          : {
              title: 'Удалить версию ' + version + '?',
              body: 'Сборка уйдёт с диска вместе с манифестом. Вернуть её можно только заливкой заново.',
              ok: 'Удалить',
              cancel: 'Отмена',
            };
      if (!(await ask(question))) return;
      try {
        if (kind === 'activate') await API.activate(gameEdit.gameId, version);
        else await API.deleteVersion(gameEdit.gameId, version);
        toast(kind === 'activate' ? 'Версия отдана игрокам' : 'Версия удалена', 'ok');
        gameBuilds = null;
        await loadGameBuilds();
        drawGameDetail();
      } catch (err) {
        toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    if (kind === 'prune') {
      const agreed = await ask({
        title: 'Убрать старые версии?',
        body: 'Уйдёт всё, что старше версии у игроков, кроме двух непосредственно перед ней: откатиться на шаг-два останется возможным.',
        ok: 'Убрать старые',
        cancel: 'Отмена',
      });
      if (!agreed) return;
      try {
        await API.pruneVersions(gameEdit.gameId);
        toast('Старые версии убраны', 'ok');
        gameBuilds = null;
        await loadGameBuilds();
        drawGameDetail();
      } catch (err) {
        toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    if (kind === 'remove') {
      const item = gameEdit.item;
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
        gameEdit.item = null;
        gameEdit.gameId = '';
        await refresh(['games', 'overview']);
      } catch (err) {
        toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    if (kind !== 'save') return;

    readGameForm();
    const rows = mergedRegistry();
    const problems = R.problems(rows).filter((x) => x.gameId === gameEdit.item.gameId || !x.gameId);
    if (problems.length) {
      gameEdit.problems = problems;
      drawGameDetail();
      toast(problems[0].message, 'warn');
      return;
    }

    b.disabled = true;
    const wasNew = gameEdit.adding;
    const savedId = gameEdit.item.gameId;
    try {
      await API.gamesSave(rows);
      toast(wasNew ? 'Игра заведена' : 'Сохранено. Лаунчер увидит это при следующем старте.', 'ok');
      gameEdit.adding = false;
      gameEdit.gameId = savedId;
      gameEdit.problems = [];
      gameEdit.item = null;
      await refresh(['games', 'overview']);
    } catch (err) {
      toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
      b.disabled = false;
    }
  }

  /* Иконку загружают файлом, но только у заведённой игры: сервер кладёт
     её в каталог манифестов, а того у новой ещё нет. */
  const gameIconInput = document.createElement('input');
  gameIconInput.type = 'file';
  gameIconInput.accept = 'image/*';
  gameIconInput.addEventListener('change', async () => {
    if (!gameIconInput.files || !gameIconInput.files[0]) return;
    try {
      const got = await API.gamesIconUpload(gameEdit.gameId, gameIconInput.files[0]);
      gameEdit.item.iconUrl = (got && (got.iconUrl || got.url)) || '/manifests/' + gameEdit.gameId + '/icon.png';
      drawGameDetail();
      toast('Иконка загружена', 'ok');
      await refresh(['games'], false);
    } catch (err) {
      toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
    }
  });

  /* ---------- Экран новостей: нажатия ---------- */

  function wireNews() {
    const box = $('[data-news]');
    if (!box) return;

    const N = window.CH2News;

    /* Смена ленты сразу показывает её содержимое: выбор — это и есть
       запрос, кнопки «применить» рядом с ним быть не должно. */
    box.addEventListener('change', (e) => {
      if (e.target.matches('[name="scope"]')) {
        newsEdit.scope = e.target.value;
        if (newsEdit.scope === 'game' && !newsEdit.gameId) {
          const withGame = D.games[0];
          newsEdit.gameId = withGame ? withGame.gameId : '';
        }
        newsEdit.adding = false;
        newsEdit.post = null;
        route();
        return;
      }
      if (e.target.matches('[name="gameId"]') && e.target.closest('[data-news-filter]')) {
        newsEdit.gameId = e.target.value;
        newsEdit.adding = false;
        newsEdit.post = null;
        route();
        return;
      }
      /* Имя файла предлагается из заголовка, пока его не тронули руками. */
      if (e.target.matches('[name="slug"]')) e.target.dataset.touched = '1';
    });

    /* Черновик пишется в браузер на каждый ввод: заметку набирают
       минутами, и терять её из-за случайно закрытой вкладки нельзя. */
    box.addEventListener('input', (e) => {
      if (!e.target.closest('[data-news-editor]')) return;
      readPostForm();
      N.saveDraft(window.localStorage, newsEdit.post);

      if (!newsEdit.adding || !e.target.matches('[name="markdown"]')) {
        drawNewsHeadline();
        return;
      }
      const slugField = box.querySelector('[name="slug"]');
      if (slugField && !slugField.dataset.touched) {
        slugField.value = N.suggestSlug(N.titleOf(newsEdit.post.markdown));
        newsEdit.post.slug = slugField.value;
      }
      drawNewsHeadline();
    });

    box.addEventListener('click', async (e) => {
      const pick = e.target.closest('[data-pick]');
      if (pick) {
        const parts = pick.dataset.pick.split('/');
        openPost({ scope: parts[0], gameId: parts[1], slug: parts.slice(2).join('/') });
        return;
      }

      if (e.target.closest('[data-draft-restore]')) {
        newsEdit.post = Object.assign({}, newsEdit.post, newsEdit.draft.post);
        newsEdit.draft = null;
        drawNewsEditor();
        return;
      }
      if (e.target.closest('[data-draft-drop]')) {
        N.dropDraft(window.localStorage, newsEdit.post);
        newsEdit.draft = null;
        drawNewsEditor();
        return;
      }

      const cover = e.target.closest('[data-flow="cover"]');
      if (cover) {
        readPostForm();
        newsCoverInput.click();
        return;
      }

      const b = e.target.closest('[data-post]');
      if (!b) return;
      await onPostAction(b);
    });
  }

  /* Строка «в ленте игрок увидит» пересчитывается на каждый ввод:
     заголовок здесь не поле, а первая строка текста. */
  function drawNewsHeadline() {
    const box = $('[data-news-preview]');
    if (box) box.innerHTML = newsPreview();
  }

  async function onPostAction(b) {
    const N = window.CH2News;
    readPostForm();
    const post = newsEdit.post;
    const scope = post.gameId ? 'game' : 'launcher';

    if (b.dataset.post === 'assets') {
      flowAssets((markup) => {
        const area = $('[data-news-editor] [name="markdown"]');
        const at = area ? area.selectionStart : post.markdown.length;
        newsEdit.post.markdown = N.insertAt(post.markdown, at, markup);
        N.saveDraft(window.localStorage, newsEdit.post);
        drawNewsEditor();
      });
      return;
    }

    if (b.dataset.post === 'delete') {
      const agreed = await ask({
        title: 'Удалить заметку «' + (N.titleOf(post.markdown) || post.slug) + '»?',
        body: 'Она пропадёт из ленты у всех игроков. Вернуть её можно только написав заново.',
        ok: 'Удалить',
        cancel: 'Отмена',
      });
      if (!agreed) return;
      try {
        await API.newsDelete(scope, post.gameId, post.slug);
        N.dropDraft(window.localStorage, post);
        toast('Заметка удалена', 'ok');
        newsEdit.post = null;
        newsEdit.slug = '';
        await refresh(['news', 'overview']);
      } catch (err) {
        toast('Не удалилось: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    if (b.dataset.post === 'publish') {
      try {
        await API.newsPublish(scope, post.gameId, post.slug, !post.published);
        newsEdit.post.published = !post.published;
        toast(newsEdit.post.published ? 'Заметка на виду у игроков' : 'Заметка снята с публикации', 'ok');
        await refresh(['news', 'overview']);
      } catch (err) {
        toast('Не вышло: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    const problems = N.problems(post);
    if (problems.length) {
      newsEdit.problems = problems;
      drawNewsEditor();
      toast('Не хватает: ' + problems.map((x) => x.text).join('; '), 'warn');
      return;
    }
    newsEdit.problems = [];

    if (b.dataset.post === 'preview') {
      try {
        const got = await API.newsPreview(post.markdown, scope, post.gameId);
        newsShow = { list: (got && got.listHtml) || '', content: (got && got.contentHtml) || '' };
        drawNewsPreview();
      } catch (err) {
        toast('Предпросмотр не собрался: ' + window.CH2Api.reason(err), 'bad');
      }
      return;
    }

    if (b.dataset.post !== 'save') return;

    b.disabled = true;
    const wasNew = newsEdit.adding;
    try {
      await API.newsSave(N.payload(post));
      N.dropDraft(window.localStorage, post);
      toast(
        post.published
          ? 'Заметка сохранена и осталась опубликованной'
          : 'Заметка сохранена. Игроки увидят её после публикации.',
        'ok'
      );
      newsEdit.adding = false;
      newsEdit.draft = null;
      newsEdit.slug = post.slug;
      newsEdit.gameId = post.gameId;
      newsEdit.scope = post.gameId ? 'game' : 'launcher';
      newsEdit.post = Object.assign({}, post, { existing: true });
      await refresh(['news', 'overview']);
      if (wasNew) openPost({ scope: newsEdit.scope, gameId: newsEdit.gameId, slug: newsEdit.slug });
    } catch (err) {
      toast('Не сохранилось: ' + window.CH2Api.reason(err), 'bad');
      b.disabled = false;
    }
  }

  /* Обложку загружают файлом, но только у сохранённой заметки: сервер
     кладёт её рядом с самой заметкой, а той ещё нет. */
  const newsCoverInput = document.createElement('input');
  newsCoverInput.type = 'file';
  newsCoverInput.accept = 'image/*';
  newsCoverInput.addEventListener('change', async () => {
    if (!newsCoverInput.files || !newsCoverInput.files[0]) return;
    const post = newsEdit.post;
    try {
      const got = await API.newsCoverUpload(
        post.gameId ? 'game' : 'launcher',
        post.gameId,
        post.slug,
        newsCoverInput.files[0]
      );
      newsEdit.post.coverUrl = (got && (got.coverUrl || got.url)) || post.coverUrl;
      drawNewsEditor();
      toast('Обложка загружена', 'ok');
    } catch (err) {
      toast('Не загрузилось: ' + window.CH2Api.reason(err), 'bad');
    }
  });

  function wireSection() {
    wireGames();
    wireNews();

    /* Разница между сборками считается из двух настоящих манифестов и
       приезжает уже после отрисовки: это два файла по мегабайту, и
       держать из-за них весь раздел пустым незачем. Считается она один
       раз на пару версий — второй заход берёт готовое. */
    if ($('[data-diff]') && D.diff === undefined) diffLoad(D.diffPair);

    /* Выбор версии сразу и считает разницу: выбор — это и есть запрос,
       а кнопка «Сравнить» рядом только откладывала его до второго
       нажатия. Пока считается, на месте дерева стоит скелет: иначе
       непонятно, устарел показанный список или ещё нет. */
    const fromBox = $('[data-diff-from]');
    const toBox = $('[data-diff-to]');
    if (fromBox && toBox) {
      const recompare = () => {
        D.diff = undefined;
        const box = $('[data-diff]');
        if (box) box.innerHTML = '<div class="sk" style="height:12rem"></div>';
        diffLoad({ from: fromBox.value, to: toBox.value });
      };
      fromBox.addEventListener('change', recompare);
      toBox.addEventListener('change', recompare);
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
      D.totals = window.CH2Sections.totals(raw);
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
    /* Реестр целиком спрашиваем ДО загрузки разделов, а не после.
       Тот же запрос делают три загрузчика (games, packs, news), и слой
       запросов склеивает одинаковые GET, пока они в полёте. Запрошенный
       после — уже не в полёте, и панель ходила за реестром дважды на
       каждый запуск и каждое обновление данных. */
    const rawGamesSoon = rawGames();
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
      metrics: val('metrics', demo.days, (raw) => ({ days: S.metrics(raw), totals: S.totals(raw) })),
      errors: val('errors', demo.errors, S.errors),
      disk: val('disk', demo.disk, S.disk),
      cache: val('cache', demo.cache, S.cache),
    };

    /* Дни и итоги приходят одной сводкой — раскладываем их по местам,
       чтобы отрисовка не знала, что они ехали вместе. */
    data.days = data.metrics.days;
    data.totals = data.metrics.totals;

    /* Реестр целиком, как он лежит на сервере. Правка игры уезжает
       вместе со всем списком, а в списке есть поля, которых таблица не
       показывает: собранный из неё список их бы стёр. */
    data.raw = { games: window.CH2Sections.items(await rawGamesSoon) };
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
      /* Один слушатель на документ: кнопки листов создаются позже
         раздела, и подписка «на текущую разметку» их не видит. */
      document.addEventListener('click', onAct);
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
