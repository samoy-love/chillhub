/* Chill Hub — лендинг 2.0
   ------------------------------------------------------------------
   Страница собирается из данных, а не из свёрстанного текста: игры,
   версии сборок, названия модпаков, режим технических работ и версия
   лаунчера приходят из публичного API (см. api.js). Добавили игру в
   админке — она появилась и здесь.

   Здесь остались: фон, шаблонизация разделов, автомат заявок, сама
   заявка и мелочи. Всё анимированное молчит при `prefers-reduced-motion`
   и замирает, когда вкладка уходит в фон.
   ------------------------------------------------------------------ */

(() => {
  'use strict';

  const calm = window.matchMedia('(prefers-reduced-motion: reduce)');
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

  const esc = (s) =>
    String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  /* Ссылка с сервера в разметку — только через проверку схемы.
     ------------------------------------------------------------------
     ПОЧЕМУ ОДНОГО esc() МАЛО. Адрес с сервера уезжает в атрибут, а
     оттуда однажды и в `style="background-image:url('…')"`. Кавычка
     внутри превращается в `&#39;`, но разбор идёт в два шага: сначала
     HTML раскрывает сущности, и только потом CSS видит уже настоящую
     кавычку. Строка вида `x'); background:…` таким образом закрывает
     url() и дописывает свои объявления.

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

  /* ---------- Фон: волновое поле ---------- */

  /* Горизонтальные линии, идущие волной. Контраст намеренно на грани
     различимости: фон должен читаться боковым зрением как фактура и ни
     разу не перетянуть внимание с текста поверх. */
  function backdrop() {
    const host = $('.backdrop');
    const cv = host && $('canvas', host);
    if (!cv) return;

    const ctx = cv.getContext('2d', { alpha: false });
    let w = 0;
    let h = 0;
    let raf = 0;
    let t = 0;
    const ROWS = 26;

    const readColors = () => {
      const cs = window.getComputedStyle(document.documentElement);
      return {
        bg: cs.getPropertyValue('--page').trim() || '#0c0c10',
        line: cs.getPropertyValue('--line').trim() || '#24242f',
      };
    };

    let colors = readColors();

    function resize() {
      // Плотность режется двойкой: на 3x-экране поле стоит втрое дороже,
      // а разницы в линиях толщиной в пиксель не видно.
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      w = host.clientWidth;
      h = host.clientHeight;
      cv.width = Math.max(1, Math.round(w * dpr));
      cv.height = Math.max(1, Math.round(h * dpr));
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      colors = readColors();
      draw();
    }

    function draw() {
      ctx.fillStyle = colors.bg;
      ctx.fillRect(0, 0, w, h);
      ctx.strokeStyle = colors.line;
      ctx.lineWidth = 1;
      const step = h / (ROWS - 1);

      for (let r = 0; r < ROWS; r++) {
        const y0 = r * step;
        // Волна тем сильнее, чем ниже строка: сверху, где заголовок,
        // поле почти прямое и не мешает читать.
        const amp = 3 + (r / ROWS) * 16;
        const k = 0.0016 + r * 0.00012;
        ctx.globalAlpha = 0.25 + (r / ROWS) * 0.45;
        ctx.beginPath();
        for (let x = 0; x <= w; x += 12) {
          const y = y0 + Math.sin(x * k + t + r * 0.35) * amp;
          if (x === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.stroke();
      }
      ctx.globalAlpha = 1;
    }

    const tick = () => {
      t += 0.006;
      draw();
      raf = requestAnimationFrame(tick);
    };
    const start = () => {
      if (raf || calm.matches || document.hidden) return;
      raf = requestAnimationFrame(tick);
    };
    const stop = () => {
      cancelAnimationFrame(raf);
      raf = 0;
    };

    window.addEventListener('resize', resize, { passive: true });
    document.addEventListener('visibilitychange', () => (document.hidden ? stop() : start()));
    calm.addEventListener('change', () => (calm.matches ? (stop(), draw()) : start()));
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', resize);

    resize();
    host.classList.add('on');
    start();
  }

  /* ---------- Содержимое из API ---------- */

  /* Ссылка на страницу модпака строится из community, а не из нашего
     gameId: у Thunderstore «risk-of-rain-2» там, где у нас «ror2», и
     угаданная ссылка хуже её отсутствия. */
  const packUrl = (mods) =>
    mods && mods.community ? `https://thunderstore.io/c/${encodeURIComponent(mods.community)}/` : '';

  /**
   * Плитка игры в каталоге.
   *
   * КАТАЛОГ ОТВЕЧАЕТ НА ОДИН ВОПРОС: «есть ли тут игра, в которую мы
   * играем». Всё остальное посетитель узнает, когда найдёт свою.
   *
   * Поэтому в плитке осталось только различающее: значок, название и
   * модпак. Ушли номер сборки и имя загрузчика — цифра «1.0.0» и слово
   * «BepInEx» ничего не значат тому, кто ещё ничего не установил; ушли
   * три метки, которые стояли у всех игр одинаково и потому не
   * различали ничего; ушли два абзаца, повторявшиеся дословно в каждой
   * карточке, — они переехали в подпись раздела и сказаны один раз.
   *
   * @param {object} g Игра из /api/games.
   * @returns {string} Разметка плитки.
   */
  function gameCard(g) {
    const mods = g.mods;
    const url = packUrl(mods);
    const title = g.title || g.gameId;

    /* Номер версии модпака не показываем: он меняется на каждой сборке,
       а посетителю важно, ЧТО за модпак, а не какой он сейчас. */
    const pack = (mods && mods.displayName) || '';

    return `
      <article class="game">
        ${
          g.iconUrl
            ? `<img class="game-ico" src="${esc(safeUrl(g.iconUrl))}" alt="" width="40" height="40" loading="lazy" decoding="async" data-letter="${esc(title.slice(0, 1))}">`
            : `<span class="game-ico game-ico--letter" aria-hidden="true">${esc(title.slice(0, 1))}</span>`
        }
        <div class="game-body">
          <h3>${esc(title)}</h3>
          ${
            pack
              ? `<p class="game-pack">Модпак ${url ? `<a href="${esc(url)}" rel="noopener noreferrer" target="_blank">${esc(pack)}</a>` : esc(pack)}</p>`
              : '<p class="game-pack faint">Пока без модпака</p>'
          }
        </div>
      </article>`;
  }

  /* Каталог кончался тупиком: своей игры человек не нашёл, и дальше ему
     некуда. Плитка в конце ведёт туда, где её просят добавить. */
  const askTile = `
      <a class="game game--ask" href="#wish">
        <span class="game-ico game-ico--letter" aria-hidden="true">+</span>
        <div class="game-body">
          <h3>Нет вашей игры?</h3>
          <p class="game-pack">Предложите — добавим</p>
        </div>
      </a>`;

  /* Размер, дата сборки и SHA-256 установщика. Показывается только то,
     что релиз действительно записал в /downloads/setup.json: свёрстанные
     числа устаревают на следующей же сборке, а свёрстанный хеш — это
     опубликованная рядом с кнопкой скачивания ложь. Нет значения —
     нет и строки. */
  const KB = 1024;
  const MB = KB * 1024;

  function humanSize(bytes) {
    const n = Number(bytes);
    if (!Number.isFinite(n) || n <= 0) return '';
    return n >= MB ? `${(n / MB).toFixed(0)} МБ` : `${(n / KB).toFixed(0)} КБ`;
  }

  function humanDate(v) {
    const d = new Date(v);
    if (Number.isNaN(+d)) return '';
    return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
  }

  function setupFacts(setup) {
    const value = {
      size: humanSize(setup.size ?? setup.bytes),
      builtAt: humanDate(setup.builtAt ?? setup.date),
      sha256: String(setup.sha256 || '').trim(),
    };

    $$('[data-setup]').forEach((el) => {
      const v = value[el.dataset.setup];
      if (!v) return;
      const slot = $('span', el);
      if (slot) slot.textContent = v;
      const btn = $('.copy-hash', el);
      if (btn) btn.dataset.hash = v;
      el.hidden = false;
    });
  }

  async function content() {
    // Правовые страницы подключают только app.js: там нечего наполнять,
    // и обращение к отсутствующему модулю уронило бы им весь скрипт —
    // вместе с годом в подвале.
    if (!window.CHILLHUB_API || !$('[data-games]')) return;
    const data = await window.CHILLHUB_API.load();

    const games = $('[data-games]');
    if (games) {
      games.innerHTML = data.games.length
        ? data.games.map(gameCard).join('') + askTile
        : '<p class="dim">Каталог сейчас пуст. Загляните позже: игры добавляются через админку и появляются здесь сами.</p>';
    }

    imageFallback(games);

    if (data.launcherVersion) {
      $$('[data-launcher-version]').forEach((el) => (el.textContent = data.launcherVersion));
    }

    setupFacts(data.setup || {});

    // Технические работы: если сборки сейчас не отдаются, узнавать об
    // этом после установки — худший момент из возможных.
    const m = data.maintenance;
    const banner = $('[data-maint]');
    if (banner && m && m.enabled) {
      $('[data-maint-text]').textContent = maintText(m);
      banner.hidden = false;
    }

    if (!data.live) {
      // Молча подсунуть моки нельзя: посетитель решит, что видит настоящий
      // каталог, а он видит снимок.
      const note = document.createElement('p');
      note.className = 'faint';
      note.style.gridColumn = '1 / -1';
      note.textContent = 'Каталог сейчас недоступен — показан сохранённый снимок.';
      games?.append(note);
    }
  }

  /* ---------- Автомат заявок ---------- */

  /* Три барабана читаются подряд одной фразой: что за игра, с кем в неё
     играть и в чём подвох. Слова подобраны под тех, кто сюда приходит, —
     компанию, которая собирается вечером в голосовом чате; часть строк
     про них самих, а не про жанры. Абсурдные сочетания не ошибка, а
     повод написать заявку. */
  const REELS = [
    [
      'Хоррор',
      'Выживание',
      'Рогалик',
      'Шутер',
      'Песочница',
      'Пати-игра',
      'Стратегия',
      'Головоломка',
      'Гонки',
      'Рыбалка',
      'Симулятор смены',
      'Игра с предателем',
    ],
    [
      'вчетвером',
      'вшестером',
      'на двоих',
      'всей компанией',
      'со случайными людьми',
      'с тем, кто вечно опаздывает',
      'с тем, кто всех подставляет',
      'в пятницу до утра',
      'когда все уже спят',
      'с микрофоном на всю квартиру',
    ],
    [
      'с крафтом',
      'с голосовым чатом',
      'со Steam Workshop',
      'с разрушаемым миром',
      'со случайными картами',
      'с постоянным прогрессом',
      'с физикой, которая всё ломает',
      'где слышно только соседей',
      'где смерть — навсегда',
      'с модами на всё подряд',
      'где никому нельзя верить',
      'с базой, которую надо строить',
    ],
  ];

  // Высота строки задана в стилях (--item-h) и на телефоне другая;
  // 44 — запасное значение там, где стили не разобраны.
  const ITEM_H = 44;
  // Повторов больше, чем нужно для пробега: барабан останавливается на
  // предпоследнем повторе, чтобы и над строкой, и под ней что-то было.
  const REPS = 6;

  function slots() {
    const root = $('[data-slots]');
    if (!root) return;

    const tracks = $$('.reel-track', root);
    const reels = $$('.reel', root);
    const out = $('[data-slots-out]', root);
    const spin = $('[data-slots-spin]', root);
    const lever = $('[data-lever]', root);
    const machine = $('[data-machine]', root);
    const sparks = $('[data-sparks]', root);
    const combo = $('[data-slots-combo]', root);
    const total = $('[data-slots-total]', root);
    let busy = false;

    if (total) total.textContent = String(REELS.reduce((n, r) => n * r.length, 1));

    const itemH = () => parseFloat(window.getComputedStyle(root).getPropertyValue('--item-h')) || ITEM_H;

    tracks.forEach((tr, i) => {
      for (let r = 0; r < REPS; r++) {
        REELS[i].forEach((s) => {
          const d = document.createElement('div');
          d.textContent = s;
          tr.append(d);
        });
      }
      // Стартуем со второго повтора: над окошком тоже должны быть строки,
      // иначе до первой прокрутки верхняя треть барабана пустая.
      place(tr, REELS[i].length * itemH());
    });

    function place(tr, p) {
      tr.style.transform = `translateY(${itemH() - p}px)`;
    }

    function mark() {
      $$('.reel-track div', root).forEach((d) => d.classList.remove('hit'));
      const h = itemH();
      tracks.forEach((tr) => {
        const value = parseFloat(tr.style.transform.match(/-?[\d.]+/));
        tr.children[Math.round((h - value) / h)]?.classList.add('hit');
      });
    }

    // Барабан проскакивает нужную строку на несколько пикселей и
    // возвращается: так останавливается настоящий, с храповиком.
    const OVERSHOOT = 60;

    function animate(tr, totalPx, dur, cb) {
      if (!dur) {
        place(tr, totalPx);
        cb();
        return;
      }
      const t0 = performance.now();
      const step = (now) => {
        const k = Math.min(1, (now - t0) / dur);
        const main = totalPx * (1 - Math.pow(1 - k, 4));
        const bounce = OVERSHOOT * Math.pow(k, 8) * Math.sin(Math.PI * k);
        place(tr, main + bounce);
        if (k < 1) requestAnimationFrame(step);
        else cb();
      };
      requestAnimationFrame(step);
    }

    // Искры из-под линии выигрыша. Вектор у каждой свой; сами убираются,
    // когда догорят.
    function burst() {
      if (!sparks || !machine || calm.matches) return;
      const w = machine.clientWidth;
      const h = machine.clientHeight;
      const frag = document.createDocumentFragment();
      for (let i = 0; i < 28; i++) {
        const el = document.createElement('i');
        const a = Math.random() * Math.PI * 2;
        const r = 60 + Math.random() * 140;
        el.style.setProperty('--x', `${w * (0.15 + Math.random() * 0.7)}px`);
        el.style.setProperty('--y', `${h * 0.45}px`);
        el.style.setProperty('--dx', `${Math.cos(a) * r}px`);
        el.style.setProperty('--dy', `${Math.sin(a) * r - 40}px`);
        el.style.setProperty('--s', `${2 + Math.random() * 4}px`);
        el.style.setProperty('--d', `${0.6 + Math.random() * 0.7}s`);
        el.addEventListener('animationend', () => el.remove(), { once: true });
        frag.append(el);
      }
      sparks.append(frag);
    }

    function go() {
      if (busy) return;
      busy = true;
      root.classList.remove('done', 'thud');
      root.classList.add('busy');
      reels.forEach((r) => r.classList.remove('stopped'));
      out.textContent = '';
      if (combo) combo.textContent = '···';

      const h = itemH();
      const picks = REELS.map((items) => Math.floor(Math.random() * items.length));
      const totals = tracks.map((_, i) => (REELS[i].length * (REPS - 2) + picks[i]) * h);
      let done = 0;
      let last = 0;

      /* Сторож объявлен ДО прокрутки, а не после неё.
         ------------------------------------------------------------
         При выключенном движении барабаны встают мгновенно, прямо в
         цикле ниже: `animate` зовёт продолжение сразу, `settle` доходит
         до `clearTimeout(guard)` — а `guard` в этот момент ещё не
         объявлен, и получается ReferenceError.

         Для человека это означало, что автомат не работал ВООБЩЕ: у
         того, кто выключил анимации в системе, первое же нажатие
         роняло обработчик, кнопка оставалась заблокированной, и
         страница не говорила ничего. */
      let guard = 0;

      const settle = () => {
        if (done < 0) return;
        done = -1;
        clearTimeout(guard);
        tracks.forEach((tr, i) => place(tr, totals[i]));
        finish(picks);
      };

      tracks.forEach((tr, i) => {
        // Барабаны останавливаются по очереди — три одновременные
        // остановки читаются как один рывок.
        const dur = calm.matches ? 0 : 1100 + i * 420;
        last = Math.max(last, dur);
        animate(tr, totals[i], dur, () => {
          if (done < 0) return;
          reels[i].classList.add('stopped');
          done++;
          if (done === tracks.length) settle();
        });
      });

      // Сторож: requestAnimationFrame замирает, когда вкладку сворачивают,
      // и без него уход на соседнюю вкладку посреди прокрутки оставлял
      // кнопку заблокированной навсегда. Если барабаны уже встали,
      // сторожить нечего.
      if (done >= 0) guard = setTimeout(settle, last + 700);
    }

    function finish(picks) {
      busy = false;
      root.classList.remove('busy');
      root.classList.add('done', 'thud');
      mark();
      burst();

      // Номер комбинации — порядковый в декартовом произведении барабанов.
      let idx = 0;
      picks.forEach((p, i) => (idx = idx * REELS[i].length + p));
      if (combo) combo.textContent = String(idx + 1).padStart(4, '0');

      /* Регистр не трогается: жанр остаётся с прописной, а «Steam
         Workshop» — собой. Прежде вся строка приводилась к нижнему
         регистру, и имя собственное портилось. */
      const phrase = picks.map((p, i) => REELS[i][p]).join(' ');
      out.textContent = `Выпало: ${phrase}. Есть такая на примете?`;
      const ta = $('#wish-text');
      if (ta && !ta.value.trim()) typeInto(ta, `Автомат выдал: ${phrase}. Предлагаю добавить: `);
    }

    // Автомат сам печатает комбинацию в заявку, по букве. Если человек
    // начал писать раньше, чем допечаталось, — уступает ему.
    let typer = 0;

    function typeInto(ta, text) {
      clearInterval(typer);
      const form = ta.closest('form');
      if (calm.matches) {
        ta.value = text;
        return;
      }
      let i = 0;
      ta.value = '';
      form?.classList.add('typing');
      const stop = () => {
        clearInterval(typer);
        form?.classList.remove('typing');
        ta.removeEventListener('input', stop);
        form?.removeEventListener('submit', flush, true);
      };
      // Нажали «Отправить», не дождавшись конца набора, — уходит вся
      // фраза, а не её половина. Ловится на захвате, чтобы сработать
      // раньше обработчика отправки.
      const flush = () => {
        ta.value = text;
        stop();
      };
      ta.addEventListener('input', stop);
      form?.addEventListener('submit', flush, true);
      typer = setInterval(() => {
        i++;
        ta.value = text.slice(0, i);
        if (i >= text.length) stop();
      }, 22);
    }

    spin.addEventListener('click', go);

    if (lever) {
      lever.addEventListener('click', () => {
        if (busy) return;
        lever.classList.remove('pulled');
        void lever.offsetWidth;
        lever.classList.add('pulled');
        go();
      });
    }

    if (machine) {
      machine.addEventListener('animationend', (e) => {
        if (e.animationName === 'thud') root.classList.remove('thud');
      });
      // Блик на стекле идёт за курсором.
      machine.addEventListener('pointermove', (e) => {
        const b = machine.getBoundingClientRect();
        machine.style.setProperty('--mx', `${((e.clientX - b.left) / b.width) * 100}%`);
        machine.style.setProperty('--my', `${((e.clientY - b.top) / b.height) * 100}%`);
      });
    }
  }

  /* Текст баннера технических работ.

     Прежде здесь стояла одна выдуманная фраза: «Сборки временно не
     отдаются, уже установленные игры запускаются как обычно». Первая
     половина неверна, когда закрыто только обновление, вторая — когда
     закрыт запуск, а закрывается он отдельным флагом. Обещание «играть
     можно» человек проверяет сразу и запоминает надолго.

     Поэтому говорим ровно то, что сказал сервер: причину, что именно
     закрыто, и срок. Слова те же, что у баннера в лаунчере
     (Core/Shell/MaintenanceBannerView.cs) — это одно сообщение, увиденное
     в двух местах. */
  function maintText(m, now) {
    const parts = [];

    const reason = String(m.reason || '').trim();
    parts.push(reason ? (/[.!?…]$/.test(reason) ? reason : reason + '.') : 'На сервере идут технические работы.');

    const b = m.blocks || {};
    const closed = [];
    if (b.install) closed.push('установка новых игр');
    if (b.update) closed.push('обновление уже установленных');
    if (b.launch) closed.push('запуск');
    if (closed.length) parts.push('Сейчас недоступно: ' + closed.join(', ') + '.');
    else parts.push('Скачивать и играть при этом можно как обычно.');

    parts.push(maintEta(m, now));
    return parts.filter(Boolean).join(' ');
  }

  /* Срок считаем по часам СЕРВЕРА: у посетителя они бывают сбиты, и
     тогда ещё не наступивший срок выглядел бы истёкшим. Показываем
     местное время — оно и есть то, на которое человек посмотрит. */
  function maintEta(m, now) {
    const until = new Date(m.endsAt || '');
    if (!m.endsAt || Number.isNaN(until.getTime())) return '';

    const server = new Date(m.serverTime || now || Date.now());
    const from = Number.isNaN(server.getTime()) ? new Date() : server;
    if (until <= from) return 'Работы затянулись, ждём сообщения от сервера.';

    const hhmm = until.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    const sameDay = until.toDateString() === new Date().toDateString();
    const when = sameDay ? 'сегодня в ' + hhmm : until.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' }) + ' в ' + hhmm;
    return 'Ожидаемое окончание — ' + when + '.';
  }

  /* ---------- Заявка ---------- */

  /* Уходит в тот же публичный эндпоинт, что и «Поделиться идеей» в
     лаунчере, и попадает в те же «Обращения». Почтовая ссылка на её
     месте перекладывала работу на посетителя и терялась у всех, у кого
     почтовый клиент не настроен. */
  function wish() {
    const form = $('[data-wish]');
    if (!form) return;
    const note = $('[data-wish-note]', form);
    const btn = $('button[type="submit"]', form);
    const no = $('[data-wish-no]', form);

    // Номер билета — декорация: загорается, как только в заявке
    // появляется текст, и не имеет ничего общего с номером обращения.
    const ta = $('#wish-text', form);
    if (no && ta) {
      ta.addEventListener('input', () => {
        const has = ta.value.trim().length > 0;
        if (has && !no.classList.contains('lit')) {
          no.textContent = '№ ' + String(Math.floor(1000 + Math.random() * 9000));
          no.classList.add('lit');
        } else if (!has) {
          no.textContent = '№ ····';
          no.classList.remove('lit');
        }
      });
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const comment = $('#wish-text', form).value.trim();
      if (!comment) return;

      btn.disabled = true;
      form.classList.add('sending');
      form.classList.remove('sent');
      note.textContent = 'отправляем…';

      try {
        const r = await fetch('/feedback/submit', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            type: 'idea',
            comment,
            contact: $('#wish-contact', form).value.trim(),
            name: '',
            attachLogs: false,
          }),
        });
        if (!r.ok) throw new Error(String(r.status));
        form.classList.add('sent');
        note.textContent = 'Отправлено. Спасибо — прочитаю.';
        // Штамп держится, пока человек не начнёт следующую заявку.
        setTimeout(() => {
          form.reset();
          form.classList.remove('sent');
          no?.classList.remove('lit');
          if (no) no.textContent = '№ ····';
        }, 2600);
      } catch {
        // Молчаливый провал тут хуже всего: человек уверен, что написал.
        note.textContent = 'Не ушло. Напишите на tr0llex.rus@gmail.com — так точно дойдёт.';
      } finally {
        btn.disabled = false;
        form.classList.remove('sending');
      }
    });
  }

  /* ---------- Мелочи ---------- */

  function skeletons() {
    $$('img.skeleton').forEach((img) => {
      const done = () => img.classList.add('loaded');
      if (img.complete && img.naturalWidth) done();
      else img.addEventListener('load', done, { once: true });
      img.addEventListener('error', done, { once: true });
    });
  }

  /* Картинка, которая не загрузилась, оставляет пустой прямоугольник —
     страница выглядит недоделанной, а не «без иллюстрации». Значок игры
     подменяется той же буквой на плашке, что и при отсутствии адреса;
     остальные картинки просто убираются из потока. */
  function imageFallback(root) {
    $$('img', root || document).forEach((img) => {
      if (img.dataset.fallbackDone) return;
      img.dataset.fallbackDone = '1';
      img.addEventListener(
        'error',
        () => {
          const letter = img.dataset.letter;
          if (letter) {
            const span = document.createElement('span');
            span.className = img.className.replace('game-ico', 'game-ico game-ico--letter');
            span.setAttribute('aria-hidden', 'true');
            span.textContent = letter;
            img.replaceWith(span);
            return;
          }
          img.hidden = true;
        },
        { once: true }
      );
    });
  }

  function copyHash() {
    const btn = $('.copy-hash');
    if (!btn) return;
    // Обработчик вешается один раз, а значение приезжает позже — из
    // setupFacts. До него у кнопки нет data-hash, и она скрыта.

    btn.addEventListener('click', async () => {
      const was = btn.textContent;
      try {
        await navigator.clipboard.writeText(btn.dataset.hash);
        btn.textContent = 'хеш скопирован';
      } catch {
        btn.textContent = btn.dataset.hash.slice(0, 16) + '…';
      }
      setTimeout(() => (btn.textContent = was), 2000);
    });
  }

  function year() {
    const el = $('[data-year]');
    if (el) el.textContent = String(new Date().getFullYear());
  }

  backdrop();
  content();
  slots();
  wish();
  skeletons();
  imageFallback();
  copyHash();
  year();
})();
