// Вкладка «Моды»: настройка игры, каталог модпаков Thunderstore, сборка и
// список собранных версий.
//
// Собран как фабрика с поиском элементов по data-атрибутам внутри своего корня
// (по образцу game-gallery.js), а не по глобальным id: панель живёт на своей
// вкладке и не должна конкурировать за имена с остальной админкой.
//
// ВАЖНО ПРО ЗАПРОСЫ. Ни один запрос отсюда не идёт на thunderstore.io напрямую.
// Обёртка window.fetch в шапке admin.js переписывает пути и вешает CSRF-токен,
// а обращение к чужому хосту из браузера упёрлось бы в CORS и заодно утащило бы
// туда трафик админки. Всё ходит через свой сервер: /admin/mods/*.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  function esc(s) {
    if (typeof window !== 'undefined' && window.escapeHtml) return window.escapeHtml(s);
    return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // formatBytes и notify живут в admin.js; в тестах их нет, поэтому обе
  // вызываются через мягкие обёртки.
  function bytes(n) {
    if (typeof window !== 'undefined' && window.formatBytes) return window.formatBytes(n);
    return String(n) + ' B';
  }
  function say(msg, level) {
    if (typeof window !== 'undefined' && window.notifyLevel) return window.notifyLevel(msg, level || 'info');
    if (typeof window !== 'undefined' && window.notify) return window.notify(msg);
  }

  // formatCount делает из 2699026 «2.7M» — в карточке каталога важен порядок
  // величины, а не точное число загрузок.
  function formatCount(n) {
    const v = Number(n) || 0;
    if (v >= 1e6) return (v / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
    if (v >= 1e3) return (v / 1e3).toFixed(1).replace(/\.0$/, '') + 'K';
    return String(v);
  }

  // formatDate показывает «3 дня назад» вместо ISO-строки: для выбора модпака
  // важно, живой он или заброшен, а не точная дата.
  function formatDate(iso, now) {
    if (!iso) return '';
    const then = Date.parse(iso);
    if (!then) return '';
    const days = Math.floor(((now || Date.now()) - then) / 86400000);
    if (days <= 0) return 'сегодня';
    if (days === 1) return 'вчера';
    if (days < 30) return days + ' дн. назад';
    if (days < 365) return Math.floor(days / 30) + ' мес. назад';
    return Math.floor(days / 365) + ' г. назад';
  }

  // catalogCardHtml рисует одну карточку каталога.
  //
  // Размер пакета намеренно НЕ показывается: в листинге Thunderstore это вес
  // архива самого модпака, а он почти пустой — у LethalReloaded 9 МБ против
  // 1.8 ГБ реального дерева. Настоящий вес появляется только после разбора
  // состава, и до тех пор честнее не показывать никакого.
  function catalogCardHtml(entry, now) {
    const full = entry.namespace + '/' + entry.name;
    const badges = [];
    if (entry.is_deprecated) badges.push('<span class="badge text-bg-warning">устарел</span>');
    if (entry.is_nsfw) badges.push('<span class="badge text-bg-danger">NSFW</span>');
    return ''
      + '<div class="col-12 col-md-6 col-xxl-4">'
      + '<div class="card h-100" data-mc-card="' + esc(full) + '">'
      + '<div class="card-body d-flex gap-3">'
      + (entry.icon_url
        ? '<img src="' + esc(entry.icon_url) + '" alt="" width="64" height="64" class="rounded flex-shrink-0" loading="lazy">'
        : '<div class="rounded flex-shrink-0 bg-secondary" style="width:64px;height:64px"></div>')
      + '<div class="flex-grow-1 min-w-0">'
      + '<div class="fw-semibold text-truncate">' + esc(entry.name) + '</div>'
      + '<div class="small text-body-secondary text-truncate">' + esc(entry.namespace) + '</div>'
      + '<div class="small mt-1" style="max-height:3.2em;overflow:hidden">' + esc(entry.description || '') + '</div>'
      + '<div class="small text-body-secondary mt-2 d-flex flex-wrap gap-2 align-items-center">'
      + '<span title="Загрузок">↓ ' + formatCount(entry.download_count) + '</span>'
      + '<span title="Оценок">★ ' + formatCount(entry.rating_count) + '</span>'
      + '<span>' + esc(formatDate(entry.last_updated, now)) + '</span>'
      + badges.join(' ')
      + '</div>'
      + '<div class="mt-2 d-flex gap-2">'
      + '<button type="button" class="btn btn-sm btn-outline-secondary" data-mc-readme="' + esc(full) + '">README</button>'
      + '<button type="button" class="btn btn-sm btn-outline-secondary" data-mc-resolve="' + esc(full) + '">Состав</button>'
      + '<button type="button" class="btn btn-sm btn-primary" data-mc-build="' + esc(full) + '">Собрать</button>'
      + '</div>'
      + '</div></div></div></div>';
  }

  // versionsTableHtml — список собранных версий модпака.
  //
  // Своя таблица, а не versionsTableHtml из admin.js: у версии модпака другие
  // колонки (пакет, ссылка на Thunderstore, число модов, метка «доступно
  // обновление»), и натягивать их на таблицу сборок игры значило бы переписать
  // её так, что она перестала бы описывать сборки.
  function versionsTableHtml(data) {
    const items = (data && data.items) || [];
    if (!items.length) {
      return '<p class="text-body-secondary mb-0">Ни одного модпака ещё не собрано.</p>';
    }
    const updates = {};
    ((data && data.updates) || []).forEach(function (u) { updates[u.version] = u; });

    const rows = items.map(function (it) {
      const upd = updates[it.version];
      const name = it.displayName || it.version;
      const badges = [];
      if (it.active) badges.push('<span class="badge text-bg-success">активен</span>');
      if (it.missing > 0) badges.push('<span class="badge text-bg-warning" title="Столько модов не нашлось на Thunderstore">пропущено ' + it.missing + '</span>');
      if (upd && upd.latest) badges.push('<span class="badge text-bg-info">доступна ' + esc(upd.latest) + '</span>');
      if (upd && upd.deprecated) badges.push('<span class="badge text-bg-warning">автор пометил устаревшим</span>');

      return '<tr>'
        + '<td><div class="fw-semibold">' + esc(name) + '</div>'
        + '<div class="small text-body-secondary">' + esc(it.version) + '</div>'
        + (it.packageUrl ? '<a class="small" href="' + esc(it.packageUrl) + '" target="_blank" rel="noopener">страница на Thunderstore</a>' : '')
        + '</td>'
        + '<td>' + badges.join(' ') + '</td>'
        + '<td class="text-nowrap">' + esc((it.createdAt || '').replace('T', ' ').replace('Z', '')) + '</td>'
        + '<td class="text-end">' + (it.packages || 0) + '</td>'
        + '<td class="text-end">' + (it.files || 0) + '</td>'
        + '<td class="text-end text-nowrap">' + bytes(it.bytes || 0) + '</td>'
        + '<td class="text-end text-nowrap">'
        + (it.active ? '' : '<button type="button" class="btn btn-sm btn-success me-1" data-md-activate="' + esc(it.version) + '">Активировать</button>')
        + (upd && upd.latest ? '<button type="button" class="btn btn-sm btn-outline-primary me-1" data-md-rebuild="' + esc(upd.namespace + '/' + upd.name) + '" data-md-rebuild-version="' + esc(upd.latest) + '">Пересобрать</button>' : '')
        + '<button type="button" class="btn btn-sm btn-outline-secondary me-1" data-md-diff="' + esc(it.version) + '">Дифф</button>'
        + (it.active ? '' : '<button type="button" class="btn btn-sm btn-outline-danger" data-md-delete="' + esc(it.version) + '">Удалить</button>')
        + '</td>'
        + '</tr>';
    }).join('');

    return '<div class="table-responsive"><table class="table table-admin table-striped align-middle">'
      + '<thead><tr><th>Модпак</th><th>Состояние</th><th>Собран</th>'
      + '<th class="text-end">Модов</th><th class="text-end">Файлов</th>'
      + '<th class="text-end">Размер</th><th></th></tr></thead>'
      + '<tbody>' + rows + '</tbody></table></div>';
  }

  // diffHtml — что изменилось в составе между двумя версиями.
  function diffHtml(items) {
    if (!items || !items.length) return '<p class="text-body-secondary mb-0">Состав не изменился.</p>';
    const label = { added: 'добавлен', removed: 'удалён', updated: 'обновлён' };
    const cls = { added: 'text-success', removed: 'text-danger', updated: 'text-info' };
    return '<ul class="list-unstyled mb-0 small">' + items.map(function (d) {
      const ver = d.change === 'updated' ? esc(d.from) + ' → ' + esc(d.to) : esc(d.to || d.from || '');
      return '<li><span class="' + (cls[d.change] || '') + '">' + (label[d.change] || d.change) + '</span> '
        + esc(d.package) + ' <span class="text-body-secondary">' + ver + '</span></li>';
    }).join('') + '</ul>';
  }

  // createModsPanel собирает панель над одним корневым элементом.
  function createModsPanel(opts) {
    const root = typeof document !== 'undefined' ? document.querySelector(opts.root) : null;
    if (!root) return null;

    const el = function (name) { return root.querySelector('[data-md="' + name + '"]'); };
    // games держим в состоянии, чтобы смена выбора не требовала перезапроса
    // списка: перерисовка <select> сбрасывает выделение на первый пункт, и
    // выбранная игра молча подменялась бы первой в списке.
    const state = { gameId: '', page: 1, mods: null, games: [] };

    function gameId() { return state.gameId; }

    function setStatus(text) {
      const status = el('status');
      if (status) status.textContent = text || '';
    }

    function setBusy(busy, text) {
      setStatus(text);
      root.querySelectorAll('button[data-md-busy]').forEach(function (b) { b.disabled = !!busy; });
    }

    // ---- список игр -------------------------------------------------------

    async function loadGames() {
      const sel = el('game');
      if (!sel) return;
      try {
        const res = await fetch('/admin/games');
        const data = await res.json();
        const items = (data && data.items) || [];
        state.games = items;
        sel.innerHTML = items.map(function (g) {
          const flag = g.mods && g.mods.enabled ? ' ✓' : '';
          return '<option value="' + esc(g.gameId) + '">' + esc(g.title || g.gameId) + flag + '</option>';
        }).join('');
        if (!items.length) return;

        // Ранее выбранная игра должна пережить перезапрос: sel.value читать
        // нельзя — после перезаписи innerHTML он показывает первый пункт.
        const keep = items.some(function (g) { return g.gameId === state.gameId; });
        state.gameId = keep ? state.gameId : items[0].gameId;
        sel.value = state.gameId;
        selectGame(state.gameId);
      } catch (e) {
        say('Не удалось получить список игр: ' + e, 'error');
      }
    }

    // selectGame переключает панель на игру из уже загруженного списка, без
    // перезапроса: перерисовка <select> сбросила бы выбор на первый пункт.
    function selectGame(gameId) {
      state.gameId = gameId;
      state.page = 1;
      applyGame(state.games.find(function (g) { return g.gameId === gameId; }));
    }

    // applyGame показывает настройки модов выбранной игры.
    function applyGame(game) {
      state.mods = (game && game.mods) || null;
      const on = !!(state.mods && state.mods.enabled);

      const toggle = el('enabled');
      if (toggle) toggle.checked = on;
      const slug = el('slug');
      if (slug && state.mods) slug.value = state.mods.community || '';

      const info = el('meta');
      if (info) {
        info.innerHTML = on
          ? '<div class="small text-body-secondary">'
            + 'Steam AppID: <code>' + esc(state.mods.steamAppId || '—') + '</code> · '
            + 'папка: <code>' + esc(state.mods.steamFolder || '—') + '</code> · '
            + 'загрузчик: <code>' + esc(state.mods.loader || '—') + '</code>'
            + '</div>'
          : '<div class="small text-body-secondary">Моды для этой игры не настроены.</div>';
      }

      const browse = el('browse');
      if (browse) {
        const community = (state.mods && state.mods.community) || '';
        const uuid = (state.mods && state.mods.sectionUuid) || '';
        // Ссылка на каталог сайта требует UUID раздела, а не слаг: с
        // ?section=modpacks Thunderstore молча не применяет фильтр.
        browse.href = community
          ? 'https://thunderstore.io/c/' + encodeURIComponent(community) + '/?ordering=most-downloaded'
            + (uuid ? '&section=' + encodeURIComponent(uuid) : '')
          : '#';
        browse.classList.toggle('disabled', !community);
      }

      const panels = el('panels');
      if (panels) panels.classList.toggle('hidden', !on);
      if (on) { reloadVersions(); reloadCatalog(); }
    }

    async function pullEcosystem() {
      const slug = (el('slug') || {}).value || '';
      if (!slug.trim()) { say('Укажите слаг игры на Thunderstore', 'error'); return; }
      setBusy(true, 'Читаем схему Thunderstore…');
      try {
        const body = new URLSearchParams({ gameId: gameId(), slug: slug.trim() });
        const res = await fetch('/admin/games/ecosystem', { method: 'POST', body });
        if (!res.ok) { say(await res.text(), 'error'); return; }
        const data = await res.json();
        state.mods = data.mods;
        applyGame({ mods: data.mods });
        say('Метаданные подтянуты из Thunderstore');
      } catch (e) {
        say('Ошибка: ' + e, 'error');
      } finally {
        setBusy(false, '');
      }
    }

    // ---- каталог ----------------------------------------------------------

    async function reloadCatalog() {
      const grid = el('catalog');
      if (!grid) return;
      const q = (el('search') || {}).value || '';
      const ordering = (el('ordering') || {}).value || 'most-downloaded';
      grid.innerHTML = '<div class="col-12 text-body-secondary">Загрузка каталога…</div>';
      try {
        const params = new URLSearchParams({ gameId: gameId(), q: q, ordering: ordering, page: String(state.page) });
        const res = await fetch('/admin/mods/catalog?' + params.toString());
        if (!res.ok) { grid.innerHTML = '<div class="col-12 text-danger">' + esc(await res.text()) + '</div>'; return; }
        const data = await res.json();
        const now = Date.now();
        grid.innerHTML = (data.results || []).map(function (e) { return catalogCardHtml(e, now); }).join('')
          || '<div class="col-12 text-body-secondary">Ничего не найдено.</div>';
        const count = el('count');
        if (count) count.textContent = 'Найдено: ' + (data.count || 0) + ' · страница ' + state.page;
      } catch (e) {
        grid.innerHTML = '<div class="col-12 text-danger">' + esc(String(e)) + '</div>';
      }
    }

    async function showReadme(full) {
      const parts = full.split('/');
      const box = el('readme');
      const body = el('readmeBody');
      if (!box || !body) return;
      box.classList.remove('hidden');
      body.textContent = 'Загрузка README…';
      try {
        const params = new URLSearchParams({ namespace: parts[0], name: parts[1] });
        const res = await fetch('/admin/mods/readme?' + params.toString());
        if (!res.ok) { body.textContent = await res.text(); return; }
        const data = await res.json();
        // Markdown показываем как есть, без рендера: README модпаков полны
        // картинок и сырого HTML с чужого домена, и вставлять это в админку
        // одним innerHTML — приглашение к XSS.
        body.textContent = data.markdown || '(README пуст)';
      } catch (e) {
        body.textContent = String(e);
      }
    }

    async function resolvePack(full, version) {
      const parts = full.split('/');
      setBusy(true, 'Разбираем состав…');
      try {
        const body = new URLSearchParams({ gameId: gameId(), namespace: parts[0], name: parts[1] });
        if (version) body.set('version', version);
        const res = await fetch('/admin/mods/resolve', { method: 'POST', body });
        if (!res.ok) { say(await res.text(), 'error'); return null; }
        const plan = await res.json();
        const note = 'Пакетов: ' + plan.packages
          + ' · скачать ' + bytes(plan.totalBytes || 0)
          + (plan.cachedBytes ? ' (в кеше ' + bytes(plan.cachedBytes) + ')' : '')
          + (plan.missing && plan.missing.length ? ' · НЕДОСТУПНО: ' + plan.missing.length : '')
          + (plan.spaceOk ? '' : ' · МАЛО МЕСТА: ' + plan.spaceNote);
        say(note, plan.spaceOk && !(plan.missing || []).length ? 'info' : 'error');
        // Строку состояния ставим ПОСЛЕ разблокировки кнопок: setBusy(false)
        // без текста затирает её, и итог разбора исчезал в том же тике, в
        // котором появлялся, — вместе с предупреждением о нехватке места.
        setBusy(false);
        setStatus(note);
        return plan;
      } catch (e) {
        say('Ошибка: ' + e, 'error');
        setBusy(false);
        return null;
      }
    }

    // ---- сборка -----------------------------------------------------------

    // buildPack идёт сразу в сборку, БЕЗ предварительного разбора состава.
    //
    // Разбор дерева — не бесплатная проверка: сервер ограничивает себя примерно
    // тремя запросами в секунду к Thunderstore, и обход 151 пакета занимает
    // около 48 секунд. Сборка делает ровно тот же обход внутри себя, поэтому
    // пара «сначала resolve, потом build» стоила полутора минут ожидания до
    // первого скачанного байта, причём вторая половина шла вообще без
    // признаков жизни на экране.
    //
    // Пропавшие моды и нехватку места проверяет сама сборка и отказывается
    // молча их проглотить; сюда это приезжает событием error, и тогда — и
    // только тогда — спрашиваем оператора и повторяем с allowMissing.
    async function buildPack(full, version, allowMissing) {
      const parts = full.split('/');
      const bar = el('progress');
      const status = el('status');
      setBusy(true, 'Сборка…');
      if (bar) bar.style.width = '0%';

      try {
        const body = new URLSearchParams({ gameId: gameId(), namespace: parts[0], name: parts[1] });
        if (version) body.set('version', version);
        if (allowMissing) body.set('allowMissing', '1');
        const res = await fetch('/admin/api/mods/build', {
          method: 'POST',
          body,
          headers: { Accept: 'application/x-ndjson', 'Cache-Control': 'no-store' },
        });
        if (!res.ok) { say(await res.text(), 'error'); return; }

        let failed = false;
        let missing = null;
        const seen = await window.readNdjsonStream(res, function (ev) {
          if (ev.type === 'package' && ev.total) {
            const pct = Math.round((ev.step / ev.total) * 100);
            if (bar) bar.style.width = pct + '%';
            if (status) status.textContent = 'Скачивание ' + ev.step + '/' + ev.total + ': ' + (ev.message || '');
          } else if (ev.type === 'error') {
            failed = true;
            // Сервер перечисляет пропавшие пакеты в тексте ошибки: это
            // единственный случай, который оператор может разрешить сам.
            if (/больше нет на Thunderstore/.test(ev.message || '')) missing = ev.message;
            if (window.setStatusError) window.setStatusError(status, 'Ошибка сборки: ' + (ev.message || ''));
            else if (status) status.textContent = 'Ошибка сборки: ' + (ev.message || '');
            say('Ошибка сборки: ' + (ev.message || ''), 'error');
          } else if (status) {
            status.textContent = ev.message || ev.type;
          }
        });
        if (!seen) {
          say('Сервер не прислал ни одного события — вероятно, ответ буферизуется прокси', 'error');
          return;
        }
        if (missing && !allowMissing) {
          const agreed = typeof confirm === 'function'
            && confirm(missing + '\n\nСобрать модпак без них?');
          if (agreed) {
            setBusy(false);
            return buildPack(full, version, true);
          }
          return;
        }
        if (!failed) {
          if (bar) bar.style.width = '100%';
          say('Модпак собран. Чтобы игроки его получили, нажмите «Активировать».');
          reloadVersions();
        }
      } catch (e) {
        say('Ошибка сборки: ' + e, 'error');
      } finally {
        setBusy(false);
      }
    }

    // ---- собранные версии -------------------------------------------------

    async function reloadVersions() {
      const box = el('versions');
      if (!box) return;
      box.innerHTML = '<p class="text-body-secondary mb-0">Загрузка…</p>';
      try {
        const res = await fetch('/admin/mods/list?gameId=' + encodeURIComponent(gameId()));
        if (!res.ok) { box.innerHTML = '<p class="text-danger mb-0">' + esc(await res.text()) + '</p>'; return; }
        const data = await res.json();
        state.versions = (data.items || []).map(function (i) { return i.version; });
        box.innerHTML = versionsTableHtml(data);
      } catch (e) {
        box.innerHTML = '<p class="text-danger mb-0">' + esc(String(e)) + '</p>';
      }
    }

    async function post(url, params, okMsg) {
      try {
        const res = await fetch(url, { method: 'POST', body: new URLSearchParams(params) });
        if (!res.ok) { say(await res.text(), 'error'); return false; }
        if (okMsg) say(okMsg);
        return true;
      } catch (e) {
        say('Ошибка: ' + e, 'error');
        return false;
      }
    }

    async function showDiff(version) {
      const list = state.versions || [];
      const others = list.filter(function (v) { return v !== version; });
      if (!others.length) { say('Сравнивать не с чем: собрана одна версия', 'error'); return; }
      const from = others[0];
      const box = el('diff');
      if (!box) return;
      box.classList.remove('hidden');
      box.innerHTML = 'Сравнение…';
      try {
        const params = new URLSearchParams({ gameId: gameId(), from: from, to: version });
        const res = await fetch('/admin/mods/diff?' + params.toString());
        if (!res.ok) { box.innerHTML = '<span class="text-danger">' + esc(await res.text()) + '</span>'; return; }
        const data = await res.json();
        box.innerHTML = '<div class="fw-semibold mb-1">' + esc(from) + ' → ' + esc(version) + '</div>' + diffHtml(data.items);
      } catch (e) {
        box.innerHTML = '<span class="text-danger">' + esc(String(e)) + '</span>';
      }
    }

    // ---- события ----------------------------------------------------------

    root.addEventListener('click', function (e) {
      const t = e.target.closest('button, a');
      if (!t) return;

      if (t.dataset.mdPull !== undefined) { e.preventDefault(); pullEcosystem(); }
      else if (t.dataset.mdSearch !== undefined) { e.preventDefault(); state.page = 1; reloadCatalog(); }
      else if (t.dataset.mdPrev !== undefined) { e.preventDefault(); if (state.page > 1) { state.page--; reloadCatalog(); } }
      else if (t.dataset.mdNext !== undefined) { e.preventDefault(); state.page++; reloadCatalog(); }
      else if (t.dataset.mdReadmeClose !== undefined) { e.preventDefault(); const b = el('readme'); if (b) b.classList.add('hidden'); }
      else if (t.dataset.mcReadme) { e.preventDefault(); showReadme(t.dataset.mcReadme); }
      else if (t.dataset.mcResolve) { e.preventDefault(); resolvePack(t.dataset.mcResolve); }
      else if (t.dataset.mcBuild) { e.preventDefault(); buildPack(t.dataset.mcBuild); }
      else if (t.dataset.mdRebuild) { e.preventDefault(); buildPack(t.dataset.mdRebuild, t.dataset.mdRebuildVersion); }
      else if (t.dataset.mdActivate) {
        e.preventDefault();
        post('/admin/mods/activate', { gameId: gameId(), version: t.dataset.mdActivate }, 'Модпак активирован')
          .then(function (ok) { if (ok) reloadVersions(); });
      } else if (t.dataset.mdDelete) {
        e.preventDefault();
        if (typeof confirm === 'function' && !confirm('Удалить версию ' + t.dataset.mdDelete + '?')) return;
        post('/admin/mods/deleteVersion', { gameId: gameId(), version: t.dataset.mdDelete }, 'Версия удалена')
          .then(function (ok) { if (ok) reloadVersions(); });
      } else if (t.dataset.mdDiff) {
        e.preventDefault();
        showDiff(t.dataset.mdDiff);
      } else if (t.dataset.mdCacheSweep !== undefined) {
        e.preventDefault();
        post('/admin/mods/cache', {}, 'Кеш подметён').then(refreshCache);
      } else if (t.dataset.mdCacheClear !== undefined) {
        e.preventDefault();
        if (typeof confirm === 'function' && !confirm('Очистить кеш архивов полностью? Следующая сборка скачает всё заново.')) return;
        post('/admin/mods/cache', { all: '1' }, 'Кеш очищен').then(refreshCache);
      }
    });

    root.addEventListener('change', function (e) {
      if (e.target === el('game')) { selectGame(e.target.value); }
      if (e.target === el('ordering')) { state.page = 1; reloadCatalog(); }
    });

    root.addEventListener('keydown', function (e) {
      if (e.target === el('search') && e.key === 'Enter') { e.preventDefault(); state.page = 1; reloadCatalog(); }
    });

    async function refreshCache() {
      const box = el('cache');
      if (!box) return;
      try {
        const res = await fetch('/admin/mods/cache');
        const data = await res.json();
        box.textContent = 'Кеш архивов: ' + (data.files || 0) + ' файлов, ' + bytes(data.bytes || 0)
          + ' · хранится ' + (data.ttlDays || 30) + ' дней';
      } catch (_) {
        box.textContent = '';
      }
    }

    return {
      reload: function () { loadGames(); refreshCache(); },
      reloadVersions,
      reloadCatalog,
    };
  }

  return { createModsPanel, catalogCardHtml, versionsTableHtml, diffHtml, formatCount, formatDate };
});
