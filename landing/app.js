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
        bg: cs.getPropertyValue('--page').trim() || '#0e1114',
        line: cs.getPropertyValue('--line').trim() || '#262e36',
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

  /* В реестре загрузчик записан строчными, а называется он BepInEx.
     Печатать «bepinex» в витрине — то же, что писать «steam». */
  const LOADERS = { bepinex: 'BepInEx', melonloader: 'MelonLoader' };
  const loaderName = (v) => LOADERS[String(v).toLowerCase()] || v;

  function gameCard(g) {
    const mods = g.mods;
    const pack = mods && mods.displayName
      ? `${mods.displayName}${mods.displayVersion ? ' ' + mods.displayVersion : ''}`
      : '';
    const url = packUrl(mods);

    const stats = [
      g.latestVersion ? `сборка <b>${esc(g.latestVersion)}</b>` : 'сборки пока нет',
      mods && mods.loader ? `загрузчик <b>${esc(loaderName(mods.loader))}</b>` : '',
    ].filter(Boolean);

    const title = g.title || g.gameId;

    return `
      <article class="game">
        <div class="game-inner">
          <div class="game-top">
            ${
              g.iconUrl
                ? `<img class="game-ico" src="${esc(safeUrl(g.iconUrl))}" alt="" width="40" height="40" loading="lazy" decoding="async" data-letter="${esc(title.slice(0, 1))}">`
                : `<span class="game-ico game-ico--letter" aria-hidden="true">${esc(title.slice(0, 1))}</span>`
            }
            <h3>${esc(title)}</h3>
          </div>
          ${
            pack
              ? `<p>Модпак ${url ? `<a href="${esc(url)}" rel="noopener noreferrer" target="_blank">${esc(pack)}</a>` : `<b>${esc(pack)}</b>`}. Он один на всех: у вас и у друзей встанет ровно эта версия.</p>`
              : '<p>Модпака для неё пока нет. Лаунчер всё равно поможет: запустит вашу копию игры и будет следить за обновлениями.</p>'
          }
          <div class="tags">
            ${mods && mods.steamAppId ? '<span>Своя копия из Steam</span>' : ''}
            ${mods && mods.hasLatest ? '<span>С модами и без</span>' : ''}
            ${g.hasLatest ? '<span>Сборка с сервера</span>' : '<span>Только своя копия</span>'}
          </div>
          <div class="game-stats">${stats.map((s) => `<span>${s}</span>`).join('')}</div>
        </div>
      </article>`;
  }

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
        ? data.games.map(gameCard).join('')
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

  const REELS = [
    ['Выживание', 'Рогалик', 'Шутер', 'Песочница', 'Пати-игра', 'Стратегия', 'Симулятор', 'Хоррор'],
    ['вчетвером', 'вшестером', 'всей компанией', 'на двоих', 'со случайными людьми'],
    ['с крафтом', 'с голосовым чатом', 'со Steam Workshop', 'с разрушаемым миром', 'со случайными картами', 'с постоянным прогрессом'],
  ];

  const ITEM_H = 44;
  const REPS = 4;

  function slots() {
    const root = $('[data-slots]');
    if (!root) return;

    const tracks = $$('.reel-track', root);
    const out = $('[data-slots-out]', root);
    const spin = $('[data-slots-spin]', root);
    let busy = false;

    tracks.forEach((tr, i) => {
      for (let r = 0; r < REPS; r++) {
        REELS[i].forEach((s) => {
          const d = document.createElement('div');
          d.textContent = s;
          tr.append(d);
        });
      }
      place(tr, 0);
    });

    function place(tr, p) {
      tr.style.transform = `translateY(${ITEM_H - p}px)`;
    }

    function mark() {
      $$('.reel-track div', root).forEach((d) => d.classList.remove('hit'));
      tracks.forEach((tr) => {
        const value = parseFloat(tr.style.transform.match(/-?[\d.]+/));
        tr.children[Math.round((ITEM_H - value) / ITEM_H)]?.classList.add('hit');
      });
    }

    function animate(tr, total, dur, cb) {
      if (!dur) {
        place(tr, total);
        cb();
        return;
      }
      const t0 = performance.now();
      const step = (now) => {
        const k = Math.min(1, (now - t0) / dur);
        place(tr, total * (1 - Math.pow(1 - k, 4)));
        if (k < 1) requestAnimationFrame(step);
        else cb();
      };
      requestAnimationFrame(step);
    }

    function go() {
      if (busy) return;
      busy = true;
      root.classList.remove('done');
      root.classList.add('busy');
      out.textContent = '';

      const picks = REELS.map((items) => Math.floor(Math.random() * items.length));
      const totals = tracks.map((_, i) => (REELS[i].length * (REPS - 1) + picks[i]) * ITEM_H);
      let done = 0;
      let last = 0;

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
        const dur = calm.matches ? 0 : 900 + i * 350;
        last = Math.max(last, dur);
        animate(tr, totals[i], dur, () => {
          if (done < 0) return;
          done++;
          if (done === tracks.length) settle();
        });
      });

      // Сторож: requestAnimationFrame замирает, когда вкладку сворачивают,
      // и без него уход на соседнюю вкладку посреди прокрутки оставлял
      // кнопку заблокированной навсегда.
      const guard = setTimeout(settle, last + 700);
    }

    function finish(picks) {
      busy = false;
      root.classList.remove('busy');
      root.classList.add('done');
      mark();
      const phrase = picks.map((p, i) => REELS[i][p]).join(' ').toLowerCase();
      out.textContent = `Выпало: ${phrase}. Есть такая на примете?`;
      const ta = $('#wish-text');
      if (ta && !ta.value.trim()) ta.value = `Автомат выдал: ${phrase}. Предлагаю добавить: `;
    }

    spin.addEventListener('click', go);
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

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const comment = $('#wish-text', form).value.trim();
      if (!comment) return;

      btn.disabled = true;
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
        form.reset();
        note.textContent = 'Отправлено. Спасибо — прочитаю.';
      } catch {
        // Молчаливый провал тут хуже всего: человек уверен, что написал.
        note.textContent = 'Не ушло. Напишите на tr0llex.rus@gmail.com — так точно дойдёт.';
      } finally {
        btn.disabled = false;
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
