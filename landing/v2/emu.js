/* Лаунчер Chill Hub в браузере
   ------------------------------------------------------------------
   Не «похожая картинка», а работающая копия главного экрана: те же
   состояния, те же подписи, те же правила. Тексты и логика перенесены
   из исходников лаунчера, а не сочинены заново:

     Core/Home/ActionButtonState.cs  — какой кнопке быть на витрине
     Core/Mods/LaunchButtons.cs      — сколько кнопок запуска и какая залита
     Core/Mods/ModsLaunch.cs         — четыре варианта запуска
     Core/Game/GameState.cs          — «Установлена» / «Не установлена»
     Core/UI/GameStatusConverters.cs — подпись игры в списке
     Core/UI/QueueDockLayout.cs      — «Свернуть очередь»
     Core/Home/HomeFormat.cs         — размеры и оставшееся время
     Core/Home/SpaceHint.cs          — «Нужно: … (… доступно)»
     Core/Net/OfflineMessage.cs      — что показать без связи

   Игры и версии берутся из того же /api/games, что читает настоящий
   лаунчер (см. api.js). Скачивание, разумеется, поддельное: качать
   гигабайты в браузер незачем, а вот показать, КАК это выглядит и в
   каком порядке происходит, — ровно то, зачем эмулятор нужен.

   Одного здесь нет намеренно: в шапке лаунчера бежит караоке по песне.
   Чужой текст на своей странице не воспроизводим, поэтому полоса на
   месте, а строка в ней — нейтральная.
   ------------------------------------------------------------------ */

(() => {
  'use strict';

  const $ = (s, r = document) => r.querySelector(s);
  /* Копия десктопного окна на телефоне превращается в 1054 px мёртвой
     высоты: список, витрина, лента и очередь встают друг под друга и
     каждая ужата до нечитаемости. Ниже 720 px показываем короткий
     вариант — то же поведение, но только главное. */
  const wide = window.matchMedia('(min-width: 720px)');
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];

  /* ---------- Правила ---------- */

  /* Вся логика — в emu-core.js: там она без DOM и покрыта тестами
     (tests/web/landing2-emu.test.js). Здесь остаётся только рисование,
     иначе поведение копии проверить нечем. */
  const C = window.CHEmuCore;
  const { formatSize, formatEta, MB, GB } = C;

  /* ---------- Состояние ---------- */

  /* У каждой игры своё правдоподобное исходное состояние, чтобы на
     витрине встретились все ветки кнопки, а не одна. Размеры сборок —
     порядок настоящих: от 240 МБ до 1,6 ГБ. */
  const SEED = {
    'how-to-fish': { installed: true, needsUpdate: true, bytes: 620 * MB, playtimeMin: 0 },
    peak: { installed: true, needsUpdate: true, bytes: 940 * MB, playtimeMin: 132 },
    repo: { installed: true, needsUpdate: false, bytes: 1.6 * GB, playtimeMin: 8 },
    'lethal-company': { installed: false, needsUpdate: false, bytes: 1.6 * GB, playtimeMin: 47 },
    'drive-beyond-horizons': { installed: false, needsUpdate: false, bytes: 1.2 * GB, playtimeMin: 0 },
    bodycam: { installed: true, needsUpdate: false, bytes: 2.1 * GB, playtimeMin: 96 },
    farfarwest: { installed: false, needsUpdate: false, bytes: 780 * MB, playtimeMin: 0 },
    'machine-party': { installed: false, needsUpdate: false, bytes: 410 * MB, playtimeMin: 0 },
  };

  const FALLBACK = { installed: false, needsUpdate: false, bytes: 800 * MB, playtimeMin: 0 };

  const state = {
    games: [],
    news: [],
    maintenance: { enabled: false, blocks: {} },
    selected: 0,
    queue: [], // [{gameId, done, total, speed, state:'run'|'wait'}]
    tab: 'game',
    freeBytes: 164.1 * GB, // 164,1 ГБ — как на скриншоте лаунчера
    covers: {}, // gameId -> адрес обложки из галереи
    tick: 0,
  };

  /* ---------- Значки ---------- */

  /* Рисуются здесь, а не берутся из набора: восемь штук на весь
     эмулятор, все в один штрих 1.5, и ни один не должен выглядеть
     узнаваемой иконкой из чужой библиотеки. */
  const ICON = {
    search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4 4"/>',
    refresh: '<path d="M20 12a8 8 0 1 1-2.3-5.6"/><path d="M20 4v5h-5"/>',
    gear: '<circle cx="12" cy="12" r="3.2"/><path d="M19.4 13.5a7.7 7.7 0 0 0 0-3l1.8-1.4-1.9-3.2-2.1.9a7.6 7.6 0 0 0-2.6-1.5L14.2 3H9.8l-.4 2.3a7.6 7.6 0 0 0-2.6 1.5l-2.1-.9-1.9 3.2 1.8 1.4a7.7 7.7 0 0 0 0 3l-1.8 1.4 1.9 3.2 2.1-.9a7.6 7.6 0 0 0 2.6 1.5l.4 2.3h4.4l.4-2.3a7.6 7.6 0 0 0 2.6-1.5l2.1.9 1.9-3.2z"/>',
    chevron: '<path d="M6 9l6 6 6-6"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    up: '<path d="M6 15l6-6 6 6"/>',
    down: '<path d="M6 9l6 6 6-6"/>',
    note: '<path d="M9 18V6l10-2v12"/><circle cx="7" cy="18" r="2"/><circle cx="17" cy="16" r="2"/>',
    idea: '<path d="M9 18h6M10 21h4"/><path d="M12 3a6 6 0 0 0-3.5 10.9c.3.3.5.7.5 1.1h6c0-.4.2-.8.5-1.1A6 6 0 0 0 12 3z"/>',
  };

  const svg = (name, size = 16) =>
    `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor"
       stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON[name]}</svg>`;

  const esc = (s) =>
    String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  /* Ссылка с сервера в разметку — только через проверку схемы.
     ------------------------------------------------------------------
     ПОЧЕМУ ОДНОГО esc() МАЛО. Обложка новости уезжает в
     `style="background-image:url('…')"`. Кавычка внутри превращается в
     `&#39;`, но разбор идёт в два шага: сначала HTML раскрывает
     сущности, и только потом CSS видит уже настоящую кавычку. Строка
     вида `x'); background:…` таким образом закрывает url() и
     дописывает свои объявления.

     Управляющие символы вырезаются ДО разбора схемы: браузер по
     спецификации URL удаляет табуляции и переводы строк перед тем, как
     определить схему, поэтому `java&#9;script:` для него — javascript:.
     Правило перенесено из панели 1.0 (`sanitizeUrl`) как есть.
     ------------------------------------------------------------------ */
  const safeUrl = (value) => {
    const v = String(value ?? '')
      .replace(/[\u0000-\u001F\u007F]/g, '')
      .trim();
    if (!v) return '';
    // Скобка и кавычка ломают url() даже в безопасной схеме
    if (/[()'"\\]/.test(v)) return '';
    if (/^[a-z][a-z0-9+.-]*:/i.test(v)) {
      const scheme = v.slice(0, v.indexOf(':')).toLowerCase();
      return scheme === 'http' || scheme === 'https' ? v : '';
    }
    // Относительная ссылка, якорь, протокол-относительная — безопасны
    return v;
  };

  /* Значок игры приходит из каталога (`iconUrl` в GameInfo). Буква на
     плашке — не «дизайн», а честный запасной вариант: у части игр в
     реестре иконки нет, и лаунчер рисует ровно то же самое. */
  function icon(g) {
    return safeUrl(g.iconUrl)
      ? `<img class="emu-ico" src="${esc(safeUrl(g.iconUrl))}" alt="" loading="lazy" decoding="async">`
      : `<span class="emu-ico emu-ico--letter" data-letter="${esc((g.title || '?').slice(0, 1))}"></span>`;
  }

  /* ---------- Подписи ---------- */

  const listSubtitle = (g) => C.listSubtitle(g, state.queue);
  const listTone = (g) => C.listTone(g, state.queue);

  /* ---------- Отрисовка ---------- */

  function render() {
    const root = $('[data-emu]');
    if (!root) return;
    root.classList.toggle('emu--compact', !wide.matches);
    root.innerHTML = wide.matches
      ? `${chrome()}
         ${header()}
         <div class="emu-body">
           ${sidebar()}
           ${panel()}
         </div>
         ${queueDock()}`
      : `${chrome()}
         <div class="emu-body">
           ${strip()}
           ${panel()}
         </div>
         ${queueDock()}`;
    wire();
  }

  /* Узкий вариант списка: игры лентой чипов вместо колонки. Поиск,
     свободное место и «Поделиться идеей» убраны — на телефоне это три
     строки ради того, чего здесь всё равно не сделать. */
  function strip() {
    return `
      <div class="emu-strip" role="tablist" aria-label="Игры">
        ${state.games
          .map(
            (g, i) => `
          <button class="emu-chip${i === state.selected ? ' on' : ''}" type="button" data-emu-select="${i}">
            ${icon(g)}<span>${esc(g.title)}</span>
          </button>`
          )
          .join('')}
      </div>`;
  }

  function chrome() {
    return `
      <div class="emu-title">
        <span class="emu-app">${logo()}<span>Лаунчер Chill Hub</span></span>
        <span class="emu-win" aria-hidden="true"><i class="min"></i><i class="max"></i><i class="cls"></i></span>
      </div>`;
  }

  // Тот же знак, что у настоящего лаунчера: он собирается из
  // scripts/icon/geometry.mjs, поэтому правится в одном месте.
  const logo = () =>
    `<svg class="emu-logo" viewBox="0 0 32 32" aria-hidden="true">` +
    `<rect x="0" y="0" width="32" height="32" rx="5" fill="#d1663c"/>` +
    `<rect x="2" y="2" width="28" height="28" rx="3" fill="#2a1a12"/>` +
    `<rect x="6" y="8" width="20" height="4" fill="#e5825b"/>` +
    `<rect x="6" y="14" width="14" height="4" fill="#e5825b"/>` +
    `<rect x="6" y="20" width="9" height="4" fill="#e5825b"/></svg>`;

  function header() {
    return `
      <div class="emu-head">
        <span class="emu-hello">Добро пожаловать в ${logo()}<b>Chill Hub</b></span>
        <!-- В лаунчере тут построчно печатается караоке по песне. Чужой
             текст на своей странице не воспроизводим: полоса на месте,
             строка нейтральная. -->
        <span class="emu-karaoke">${svg('note', 15)}<span><b>Караоке</b><i>в лаунчере здесь построчно печатается песня</i></span></span>
        <button class="emu-icon emu-icon--framed" type="button" title="Настройки">${svg('gear')}</button>
      </div>`;
  }

  function sidebar() {
    const rows = state.games
      .map((g, i) => {
        return `
        <button class="emu-game${i === state.selected ? ' on' : ''}" type="button" data-emu-select="${i}">
          ${icon(g)}
          <span class="emu-game-text">
            <b>${esc(g.title)}</b>
            <i data-tone="${listTone(g)}">${esc(listSubtitle(g))}</i>
          </span>
        </button>`;
      })
      .join('');

    return `
      <aside class="emu-side">
        <div class="emu-side-head">
          <b>Игры</b>
          <button class="emu-icon" type="button" title="Обновить список" data-emu-refresh>${svg('refresh', 15)}</button>
        </div>
        <label class="emu-search">${svg('search', 14)}<input type="text" placeholder="Поиск" data-emu-search aria-label="Поиск по играм"></label>
        <div class="emu-games">${rows}</div>
        <div class="emu-side-foot">
          <span class="emu-free">Свободно на диске: ${formatSize(state.freeBytes)}</span>
          <button class="emu-btn emu-btn--wide" type="button" title="Поделиться идеей или сообщить о проблеме">Поделиться идеей</button>
        </div>
      </aside>`;
  }

  function panel() {
    const g = state.games[state.selected];
    if (!g) return '<section class="emu-main"></section>';

    const mode = C.effectiveMode(g, state.queue, state.maintenance);
    const look = C.look(mode);

    const launch = C.launchButtons(g, mode);
    const actionVisible = launch.length === 0;

    const meta = C.heroMeta(g);

    const hint = C.spaceHint(mode, g.bytes, state.freeBytes);

    const cover = state.covers[g.gameId];

    return `
      <section class="emu-main">
        <div class="emu-hero${cover ? ' has-cover' : ''}" data-game="${esc(g.gameId)}"
             ${cover ? `style="--cover: url('${esc(cover)}')"` : ''}>
          <span class="emu-badge">${g.installed ? (g.needsUpdate ? 'Обновление' : 'Установлена') : 'Не установлена'}</span>
          <h3>${esc(g.title)}</h3>
          <p class="emu-meta">${meta.map(esc).join(' · ')}</p>
          <div class="emu-actions">
            ${
              actionVisible
                ? `<button class="emu-action ${look.look}" type="button" ${look.on ? '' : 'disabled'} data-emu-action>${esc(look.text)}</button>`
                : launch
                    .map(
                      (b) => `<button class="emu-launch${b.accent ? ' accent' : ''}" type="button" data-emu-launch="${b.target}">
                        <b>${esc(b.title)}</b><i>${esc(b.sub)}</i>
                      </button>`
                    )
                    .join('') +
                  `<button class="emu-chev" type="button" title="Другие варианты запуска" data-emu-menu>${svg('chevron', 16)}</button>`
            }
            <button class="emu-about" type="button">${svg('info', 15)}Об игре</button>
          </div>
          ${hint ? `<p class="emu-hint">${esc(hint)}</p>` : ''}
        </div>

        ${!wide.matches ? '' : `
        <div class="emu-tabs" role="tablist">
          <button class="emu-tab${state.tab === 'game' ? ' on' : ''}" type="button" role="tab" data-emu-tab="game">Новости игры</button>
          <button class="emu-tab${state.tab === 'launcher' ? ' on' : ''}" type="button" role="tab" data-emu-tab="launcher">Новости лаунчера</button>
          <button class="emu-icon" type="button" title="Обновить новости">${svg('refresh', 14)}</button>
        </div>

        <div class="emu-feed">${feed()}</div>`}
      </section>`;
  }

  function feed() {
    if (!state.news.length) {
      return '<div class="emu-empty"><b>Новостей пока нет</b><span>Здесь появятся заметки о сборках и обновлениях.</span></div>';
    }
    return state.news
      .map(
        (n) => `
        <article class="emu-post">
          <span class="emu-post-cover"${safeUrl(n.coverUrl) ? ` style="background-image:url('${esc(safeUrl(n.coverUrl))}')"` : ''}></span>
          <span class="emu-post-text">
            <b>${esc(n.title)}</b>
            <i>${esc(dateRu(n.createdAt))}</i>
            <span>${esc(n.summary || '')}</span>
          </span>
        </article>`
      )
      .join('');
  }

  function dateRu(iso) {
    const d = new Date(iso);
    if (Number.isNaN(+d)) return '';
    return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
  }

  /* Core/UI/QueueDockLayout.cs: очередь показывается, только когда в ней
     что-то есть. Пустая панель внизу читается как сломанная. */
  function queueDock() {
    if (!state.queue.length) return '';
    const rows = state.queue
      .map((q, i) => {
        const g = state.games.find((x) => x.gameId === q.gameId);
        if (!g) return '';
        const pct = Math.round((q.done / q.total) * 100);
        const left = q.speed > 0 ? formatEta((q.total - q.done) / q.speed) : '—';
        return `
        <div class="emu-q${q.state === 'run' ? ' run' : ''}">
          ${icon(g)}
          <span class="emu-q-text">
            <b>${esc(g.title)}${q.state === 'run' ? ` <em>${pct}%</em>` : ''}</b>
            <i>${esc(C.queueLabel(q, i))}</i>
          </span>
          ${
            q.state === 'run'
              ? `<span class="emu-q-num">
                   <b>${formatSize(q.done)} / ${formatSize(q.total)}</b>
                   <i>${formatSize(q.speed)}/с · осталось ${left}</i>
                 </span>`
              : `<span class="emu-q-move">
                   <button class="emu-icon" type="button" title="Выше в очереди" data-emu-up="${q.gameId}">${svg('up', 15)}</button>
                   <button class="emu-icon" type="button" title="Ниже в очереди" data-emu-down="${q.gameId}">${svg('down', 15)}</button>
                 </span>`
          }
          <button class="emu-icon" type="button" title="Убрать из очереди" data-emu-drop="${q.gameId}">${svg('close', 15)}</button>
          ${q.state === 'run' ? `<span class="emu-q-bar"><i style="width:${pct}%"></i></span>` : ''}
        </div>`;
      })
      .join('');

    return `
      <div class="emu-dock">
        <div class="emu-dock-head">
          <b>${esc(C.dockTitle(state.queue))}</b>
        </div>
        ${rows}
        ${state.queue.length > 1 ? '<button class="emu-collapse" type="button">Свернуть очередь</button>' : ''}
      </div>`;
  }

  /* ---------- Поведение ---------- */

  function wire() {
    $$('[data-emu-select]').forEach((b) =>
      b.addEventListener('click', () => {
        state.selected = Number(b.dataset.emuSelect);
        render();
        loadCover();
      })
    );

    const search = $('[data-emu-search]');
    if (search) {
      search.addEventListener('input', () => {
        const q = search.value.trim().toLowerCase();
        $$('.emu-game').forEach((row, i) => {
          row.hidden = q ? !state.games[i].title.toLowerCase().includes(q) : false;
        });
      });
    }

    const act = $('[data-emu-action]');
    if (act) act.addEventListener('click', onAction);

    $$('[data-emu-launch]').forEach((b) => b.addEventListener('click', () => onLaunch(b.dataset.emuLaunch)));
    $$('[data-emu-drop]').forEach((b) => b.addEventListener('click', () => dequeue(b.dataset.emuDrop)));
    $$('[data-emu-up]').forEach((b) => b.addEventListener('click', () => move(b.dataset.emuUp, -1)));
    $$('[data-emu-down]').forEach((b) => b.addEventListener('click', () => move(b.dataset.emuDown, 1)));

    $$('[data-emu-tab]').forEach((b) =>
      b.addEventListener('click', () => {
        state.tab = b.dataset.emuTab;
        render();
      })
    );

    const refresh = $('[data-emu-refresh]');
    if (refresh) refresh.addEventListener('click', () => boot(true));
  }

  function onAction() {
    const g = state.games[state.selected];
    const q = state.queue.find((x) => x.gameId === g.gameId);
    if (q) {
      dequeue(g.gameId);
      return;
    }
    const mode = C.decideMode(g);
    if (mode === 'Play') {
      note(`${g.title} запускается…`);
      return;
    }
    if (mode === 'SteamOnly') return;
    enqueue(g);
  }

  function onLaunch(target) {
    const g = state.games[state.selected];
    const where = target === 'SteamModded' ? 'копию из Steam' : 'локальную сборку';
    note(`${g.title}: запускаем ${where} с модами`);
  }

  /* Операции над очередью — в ядре: там они без DOM и проверены тестом
     (качается одна, эстафета переходит, качающегося не подвинуть). */
  function enqueue(g) {
    state.queue = C.enqueue(state.queue, g);
    render();
  }

  function dequeue(gameId) {
    state.queue = C.dequeue(state.queue, gameId);
    render();
  }

  function move(gameId, dir) {
    state.queue = C.move(state.queue, gameId, dir);
    render();
  }

  function note(text) {
    const host = $('[data-emu-note]');
    if (!host) return;
    host.textContent = text;
    clearTimeout(note.t);
    note.t = setTimeout(() => (host.textContent = ''), 3000);
  }

  /* Галерея — отдельный запрос, и её у части игр нет. Пока обложка не
     пришла (или её нет вовсе), витрина остаётся с градиентом: у неё
     должен быть план Б, а не дыра на месте картинки. */
  async function loadCover() {
    const g = state.games[state.selected];
    if (!g || state.covers[g.gameId] !== undefined) return;

    state.covers[g.gameId] = null; // чтобы не запрашивать второй раз
    const items = await window.CHILLHUB_API.gallery(g.gameId);
    const cover = items.find((i) => i.isCover) || items[0];
    if (!cover) return;

    state.covers[g.gameId] = cover.url;
    if (state.games[state.selected]?.gameId === g.gameId) render();
  }

  /* ---------- Ход времени ---------- */

  /* setInterval, а не requestAnimationFrame: свёрнутая вкладка гасит rAF,
     и очередь застыла бы на половине с живым процентом на экране. */
  function pump() {
    const run = state.queue.find((q) => q.state === 'run');
    if (!run) return;

    // Скорость гуляет вокруг 10 МБ/с — ровный график выглядит поддельным.
    run.speed = (9 + Math.sin(state.tick / 4) * 2.2 + Math.random() * 0.6) * MB;
    run.done = Math.min(run.total, run.done + run.speed * 0.4);
    state.tick++;

    if (run.done >= run.total) {
      const g = state.games.find((x) => x.gameId === run.gameId);
      if (g) {
        g.installed = true;
        g.needsUpdate = false;
      }
      state.queue = state.queue.filter((q) => q !== run);
      if (state.queue.length) state.queue[0].state = 'run';
      render();
      if (g) note(`${g.title}: готово, лишние файлы удалены`);
      return;
    }

    paint();
  }

  /* Пока идёт загрузка, перерисовывается только то, что меняется: полная
     перерисовка каждые 400 мс сбрасывала бы фокус и текст в поиске. */
  function paint() {
    const run = state.queue.find((q) => q.state === 'run');
    if (!run) return;
    const dock = $('.emu-dock .emu-q.run');
    if (!dock) {
      render();
      return;
    }
    const pct = Math.round((run.done / run.total) * 100);
    const g = state.games.find((x) => x.gameId === run.gameId);
    $('b em', dock) && ($('b em', dock).textContent = `${pct}%`);
    const num = $('.emu-q-num', dock);
    if (num) {
      $('b', num).textContent = `${formatSize(run.done)} / ${formatSize(run.total)}`;
      $('i', num).textContent = `${formatSize(run.speed)}/с · осталось ${formatEta((run.total - run.done) / run.speed)}`;
    }
    const bar = $('.emu-q-bar i', dock);
    if (bar) bar.style.width = pct + '%';
    void g;
  }

  /* ---------- Запуск ---------- */

  async function boot(again = false) {
    const data = await window.CHILLHUB_API.load();

    state.games = data.games.map((g) => {
      const seed = SEED[g.gameId] || FALLBACK;
      return {
        gameId: g.gameId,
        title: g.title || g.gameId,
        // Сервер отдаёт путь от корня («/manifests/lethal/icon.png»);
        // лаунчер достраивает его базой API, браузеру достраивать нечего.
        iconUrl: g.iconUrl || '',
        latestVersion: g.latestVersion || '',
        hasServerBuild: g.hasLatest !== false,
        mods: g.mods || null,
        installed: seed.installed,
        needsUpdate: seed.needsUpdate,
        unfinished: false,
        error: false,
        bytes: seed.bytes,
        playtimeMin: seed.playtimeMin,
      };
    });

    state.news = data.news.slice(0, 4);
    state.maintenance = data.maintenance || { enabled: false, blocks: {} };
    if (state.selected >= state.games.length) state.selected = 0;

    render();
    loadCover();
    if (again) note('Список игр перечитан');
  }

  const host = $('[data-emu]');
  if (host) {
    boot();
    setInterval(pump, 400);
    // Порог пересекают поворотом телефона и изменением окна — иначе на
    // широком экране осталась бы узкая раскладка и наоборот.
    wide.addEventListener('change', render);
  }
})();
