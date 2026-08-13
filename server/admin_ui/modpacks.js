// Модпаки с Thunderstore — вкладка «Модпаки» в карточке игры (трек K).
//
// По образцу game-gallery.js: самостоятельный модуль window.createModpacksPanel({root, getGameId}),
// который трек I (когда заведёт вкладки в карточке игры) сможет смонтировать
// в свою разметку, а до тех пор живёт самостоятельной карточкой на вкладке
// «Игры» — см. admin.html.
//
// Эндпоинты — контракт трека K (server/internal/adminapi/thunderstore):
//   GET  /admin/thunderstore/search?community=..&q=.. -> {items:[PackageSummary]}
//   GET  /admin/thunderstore/list?gameId=..            -> {items:[DownloadedModpack]}
//   POST /admin/thunderstore/download {gameId,namespace,name,version} -> NDJSON progress
//        {"type":"progress","message":".."} / {"type":"done"} / {"type":"error","message":".."}
//   POST /admin/thunderstore/delete {gameId,namespace,name} -> {status,removedFiles}
//
// Прогресс скачивания показывается построчно (лог шагов), а не одним общим
// прогресс-баром — так просит PLAN.md, и так честнее: шаги неравномерны
// (резолвинг зависимостей может занять секунды, скачивание крупного мода —
// минуты).
(function () {
  'use strict';

  const EP = {
    search: '/admin/thunderstore/search',
    list: '/admin/thunderstore/list',
    download: '/admin/thunderstore/download',
    delete: '/admin/thunderstore/delete',
  };

  function esc(s) {
    if (window.escapeHtml) return window.escapeHtml(s);
    return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function toast(msg, level) {
    if (window.notifyLevel) { window.notifyLevel(msg, level); return; }
    console[level === 'error' ? 'error' : 'log'](msg);
  }
  function confirmDialog(opts) {
    if (window.askConfirm) return window.askConfirm(opts);
    return Promise.resolve(window.confirm((opts.title || '') + '\n\n' + (opts.body || '')));
  }

  // createModpacksPanel(opts):
  //   opts.root       — селектор или элемент корня панели
  //   opts.getGameId  — () => текущий gameId
  //   opts.getCommunity — (gameId) => thunderstoreCommunity (может быть async);
  //                        пусто/undefined — панель прячет себя.
  function createModpacksPanel(opts) {
    const o = opts || {};
    const root = typeof o.root === 'string' ? document.querySelector(o.root) : o.root;
    if (!root) { console.error('modpacks: root not found', o.root); return null; }
    const getGameId = typeof o.getGameId === 'function' ? o.getGameId : function () { return o.gameId || ''; };
    const getCommunity = typeof o.getCommunity === 'function' ? o.getCommunity : function () { return ''; };

    root.innerHTML = ''+
      '<div data-mp="wrap">' +
        '<div class="input-group input-group-sm mb-2">' +
          '<input data-mp="q" type="text" class="form-control" placeholder="Поиск модов...">' +
          '<button type="button" class="btn btn-outline-secondary" data-mp="search-btn">Найти</button>' +
        '</div>' +
        '<div data-mp="results" class="mp-results mb-3"></div>' +
        '<div class="card-header px-0 bg-transparent border-0"><strong>Скачанные модпаки</strong></div>' +
        '<div data-mp="downloaded" class="mb-3"></div>' +
        '<div data-mp="log" class="small font-monospace mp-log" style="max-height:220px;overflow:auto;white-space:pre-wrap"></div>' +
      '</div>';

    const els = {
      q: root.querySelector('[data-mp="q"]'),
      searchBtn: root.querySelector('[data-mp="search-btn"]'),
      results: root.querySelector('[data-mp="results"]'),
      downloaded: root.querySelector('[data-mp="downloaded"]'),
      log: root.querySelector('[data-mp="log"]'),
    };

    function logLine(msg) {
      if (!els.log) return;
      const t = new Date().toLocaleTimeString();
      els.log.textContent += '[' + t + '] ' + msg + '\n';
      els.log.scrollTop = els.log.scrollHeight;
    }

    async function search() {
      const gameId = getGameId();
      const community = await getCommunity(gameId);
      if (!community) { els.results.innerHTML = '<div class="text-body-secondary">У игры не задано сообщество Thunderstore.</div>'; return; }
      const q = (els.q && els.q.value || '').trim();
      let res;
      try {
        res = await fetch(EP.search + '?community=' + encodeURIComponent(community) + '&q=' + encodeURIComponent(q));
      } catch (e) { els.results.innerHTML = '<div class="text-danger">Ошибка запроса: ' + esc(String(e)) + '</div>'; return; }
      if (!res.ok) {
        const text = await res.text().catch(function () { return ''; });
        els.results.innerHTML = '<div class="text-danger">HTTP ' + res.status + ' ' + esc(res.statusText) + (text ? ': ' + esc(text) : '') + '</div>';
        return;
      }
      let j; try { j = await res.json(); } catch (e) { els.results.innerHTML = '<div class="text-danger">Плохой JSON</div>'; return; }
      renderResults(j.items || []);
    }

    function renderResults(items) {
      if (!items.length) { els.results.innerHTML = '<div class="text-body-secondary">Ничего не найдено</div>'; return; }
      els.results.innerHTML = '';
      const list = document.createElement('div');
      list.className = 'list-group';
      items.forEach(function (it) {
        const row = document.createElement('div');
        row.className = 'list-group-item d-flex align-items-center gap-2 flex-wrap';
        const info = document.createElement('div');
        info.className = 'flex-grow-1';
        info.innerHTML = '<div class="fw-semibold">' + esc(it.fullName || (it.namespace + '/' + it.name)) + '</div>' +
          '<div class="small text-body-secondary">' + esc(it.description || '') + '</div>' +
          '<div class="small">v' + esc(it.latestVersion || '?') + ' · ' + (it.downloads || 0) + ' загрузок · ' +
          '<a href="' + esc(it.thunderstoreUrl || '#') + '" target="_blank" rel="noopener">на Thunderstore</a></div>';
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-primary';
        btn.textContent = 'Скачать';
        btn.addEventListener('click', function () { downloadModpack(it.namespace, it.name, it.latestVersion, btn); });
        row.appendChild(info);
        row.appendChild(btn);
        list.appendChild(row);
      });
      els.results.appendChild(list);
    }

    async function downloadModpack(namespace, name, version, btn) {
      const gameId = getGameId();
      if (!gameId) { toast('Игра не выбрана', 'error'); return; }
      if (!version) { toast('У пакета нет версии для скачивания', 'error'); return; }
      if (btn) { btn.disabled = true; btn.textContent = 'Скачивание...'; }
      logLine('Скачивание ' + namespace + '-' + name + '-' + version + '...');
      let res;
      try {
        res = await fetch(EP.download, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ gameId: gameId, namespace: namespace, name: name, version: version }),
        });
      } catch (e) {
        logLine('Ошибка запроса: ' + e);
        if (btn) { btn.disabled = false; btn.textContent = 'Скачать'; }
        return;
      }
      if (!res.ok) {
        const text = await res.text().catch(function () { return ''; });
        logLine('HTTP ' + res.status + ' ' + res.statusText + (text ? ': ' + text : ''));
        if (btn) { btn.disabled = false; btn.textContent = 'Скачать'; }
        return;
      }
      let ok = true;
      if (res.body && typeof res.body.getReader === 'function') {
        const reader = res.body.getReader();
        const decoder = new window.TextDecoder();
        let buf = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += decoder.decode(value, { stream: true });
          let idx;
          while ((idx = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, idx).trim();
            buf = buf.slice(idx + 1);
            if (!line) continue;
            let ev; try { ev = JSON.parse(line); } catch (e) { continue; }
            if (ev.type === 'progress') logLine(ev.message);
            else if (ev.type === 'error') { logLine('Ошибка: ' + ev.message); ok = false; }
            else if (ev.type === 'done') logLine('Готово.');
          }
        }
      } else {
        // Fallback for environments without a streaming body reader: the
        // whole NDJSON response arrives at once and is parsed line by line.
        const text = await res.text();
        text.split('\n').forEach(function (line) {
          line = line.trim();
          if (!line) return;
          let ev; try { ev = JSON.parse(line); } catch (e) { return; }
          if (ev.type === 'progress') logLine(ev.message);
          else if (ev.type === 'error') { logLine('Ошибка: ' + ev.message); ok = false; }
          else if (ev.type === 'done') logLine('Готово.');
        });
      }
      if (btn) { btn.disabled = false; btn.textContent = 'Скачать'; }
      if (ok) { toast('Модпак ' + namespace + '-' + name + ' скачан', 'success'); }
      else { toast('Скачивание ' + namespace + '-' + name + ' завершилось ошибкой — см. журнал', 'error'); }
      loadDownloaded();
    }

    async function loadDownloaded() {
      const gameId = getGameId();
      if (!gameId) { els.downloaded.innerHTML = ''; return; }
      let res;
      try { res = await fetch(EP.list + '?gameId=' + encodeURIComponent(gameId)); }
      catch (e) { els.downloaded.innerHTML = '<div class="text-danger">Ошибка запроса: ' + esc(String(e)) + '</div>'; return; }
      if (!res.ok) { els.downloaded.innerHTML = '<div class="text-danger">HTTP ' + res.status + ' ' + esc(res.statusText) + '</div>'; return; }
      let j; try { j = await res.json(); } catch (e) { els.downloaded.innerHTML = '<div class="text-danger">Плохой JSON</div>'; return; }
      renderDownloaded(j.items || []);
    }

    function renderDownloaded(items) {
      if (!items.length) { els.downloaded.innerHTML = '<div class="text-body-secondary">Пока ничего не скачано</div>'; return; }
      els.downloaded.innerHTML = '';
      const list = document.createElement('div');
      list.className = 'list-group';
      items.forEach(function (it) {
        const row = document.createElement('div');
        row.className = 'list-group-item d-flex align-items-center gap-2 flex-wrap';
        const info = document.createElement('div');
        info.className = 'flex-grow-1';
        info.innerHTML = '<div class="fw-semibold">' + esc(it.namespace + '-' + it.name) + ' <span class="text-body-secondary">v' + esc(it.rootVersion || '') + '</span></div>' +
          '<div class="small text-body-secondary">' + (it.fileCount || 0) + ' файл(ов) в BepInEx, ' + (it.graph ? it.graph.length : 0) + ' пакет(ов) в графе зависимостей · обновлено ' + esc(it.updatedAt || '') + '</div>' +
          '<div class="small"><a href="' + esc(it.thunderstoreUrl || '#') + '" target="_blank" rel="noopener">на Thunderstore</a></div>';
        const del = document.createElement('button');
        del.type = 'button';
        del.className = 'btn btn-sm btn-outline-danger';
        del.textContent = 'Удалить';
        del.addEventListener('click', async function () {
          const ok = await confirmDialog({
            title: 'Удалить модпак «' + it.namespace + '-' + it.name + '»?',
            body: 'Будут удалены все объединённые файлы BepInEx и запись профиля. Отменить нельзя.',
            okText: 'Удалить',
            danger: true,
          });
          if (!ok) return;
          const gameId = getGameId();
          let res;
          try {
            res = await fetch(EP.delete, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ gameId: gameId, namespace: it.namespace, name: it.name }),
            });
          } catch (e) { toast('Ошибка удаления: ' + e, 'error'); return; }
          if (!res.ok) { toast('HTTP ' + res.status + ' ' + res.statusText, 'error'); return; }
          let j; try { j = await res.json(); } catch (e) { j = {}; }
          logLine('Удалён модпак ' + it.namespace + '-' + it.name + (j.removedFiles ? ': ' + j.removedFiles.join(', ') : ''));
          toast('Модпак удалён', 'success');
          loadDownloaded();
        });
        row.appendChild(info);
        row.appendChild(del);
        list.appendChild(row);
      });
      els.downloaded.appendChild(list);
    }

    if (els.searchBtn) els.searchBtn.addEventListener('click', function (e) { e.preventDefault(); search(); });
    if (els.q) els.q.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); search(); } });

    return { search: search, refresh: loadDownloaded };
  }

  window.createModpacksPanel = createModpacksPanel;
})();
