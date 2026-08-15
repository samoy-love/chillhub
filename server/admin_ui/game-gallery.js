// Галерея игры — вкладка «Галерея» в карточке игры.
//
// По образцу модалки #ns_gallery (галерея картинок новостей, см. admin.js:
// openGalleryModal/galleryFetchAndRender/renderGalleryGrid), но:
//   - корень не content/news/assets, а content/<gameId>/gallery/;
//   - у каждой картинки есть подпись (caption), которая правится прямо на
//     плашке и сохраняется по blur/change через SetCaption;
//   - на плашке есть кнопка «Сделать обложкой» (видна по hover), которая
//     проставляет gallery.json.cover через SetCover и подсвечивает текущую
//     обложку бейджем.
//
// Эндпоинты зарегистрированы в server/cmd/admin/routes.go поверх
// server/internal/adminapi/gamegallery:
//
//   GET  /admin/api/games/gallery?gameId=..&path=..&q=..        -> Handlers.List
//        {path, items:[{name,url,size,modTime,isDir}]}
//   POST /admin/api/games/gallery/mkdir        {gameId, path, name}       -> Handlers.Mkdir
//   POST /admin/api/games/gallery/upload       multipart {gameId, path, filename, file} -> Handlers.Upload
//   POST /admin/api/games/gallery/uploadByUrl  {gameId, path, filename, url} -> Handlers.UploadByURL
//   POST /admin/api/games/gallery/delete       {gameId, path, name}       -> Handlers.Delete
//   POST /admin/api/games/gallery/rename       {gameId, path, from, to}   -> Handlers.Rename
//   POST /admin/api/games/gallery/setCaption   {gameId, file, caption}    -> Handlers.SetCaptionHandler
//   POST /admin/api/games/gallery/setCover     {gameId, file}             -> Handlers.SetCoverHandler
//
// Важно: Handlers.List (см. gamegallery.go) отдаёт только name/url/size/
// modTime/isDir — caption и cover в ответе списка НЕТ, они живут отдельно в
// content/<gameId>/gallery/gallery.json, который раздаётся публично под
// /content/<gameId>/gallery/gallery.json (h.contentBase() врастает в
// PathPrefix("/content/")). Поэтому этот файл сам подтягивает gallery.json и
// сводит caption/cover с списком файлов на клиенте — см. fetchGalleryMeta().
(function () {
  'use strict';

  const EP = {
    list: '/admin/api/games/gallery',
    mkdir: '/admin/api/games/gallery/mkdir',
    upload: '/admin/api/games/gallery/upload',
    uploadByUrl: '/admin/api/games/gallery/uploadByUrl',
    delete: '/admin/api/games/gallery/delete',
    rename: '/admin/api/games/gallery/rename',
    setCaption: '/admin/api/games/gallery/setCaption',
    setCover: '/admin/api/games/gallery/setCover',
  };

  // Небольшие копии общих хелперов admin.js: файл должен работать
  // самостоятельно, не полагаясь на порядок загрузки скриптов, а
  // window.escapeHtml/notifyLevel/askConfirm переиспользуются, если уже есть —
  // одинаковое поведение с остальной админкой лучше, чем дублирующий тост.
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
    // Запасной путь без admin.js: последствия (bullets) обязаны доехать и
    // сюда — иначе подтверждать придётся вслепую.
    const bullets = Array.isArray(opts.bullets) ? opts.bullets.filter(Boolean) : [];
    const text = [opts.title || '', opts.body || '', ...bullets.map(function (b) { return '• ' + b; })]
      .filter(Boolean).join('\n\n');
    return Promise.resolve(window.confirm(text));
  }

  const IMAGE_EXT_RE = /\.(png|jpe?g|gif|webp|avif|bmp|svg|ico)(\?|#|$)/i;
  function isImageName(name) { return IMAGE_EXT_RE.test(String(name || '')); }

  // Состояние держим на инстанс, чтобы можно было смонтировать несколько
  // галерей на странице (или ту же — на разных играх) без глобальных коллизий.
  function createGameGallery(opts) {
    const o = opts || {};
    const root = typeof o.root === 'string' ? document.querySelector(o.root) : o.root;
    if (!root) { console.error('game-gallery: root not found', o.root); return null; }
    const getGameId = typeof o.getGameId === 'function' ? o.getGameId : function () { return o.gameId || ''; };

    const els = {
      grid: root.querySelector('[data-gg="grid"]'),
      breadcrumbs: root.querySelector('[data-gg="breadcrumbs"]'),
      search: root.querySelector('[data-gg="search"]'),
      refresh: root.querySelector('[data-gg="refresh"]'),
      mkdir: root.querySelector('[data-gg="mkdir"]'),
      url: root.querySelector('[data-gg="url"]'),
      urlName: root.querySelector('[data-gg="url-name"]'),
      overwrite: root.querySelector('[data-gg="overwrite"]'),
      urlSave: root.querySelector('[data-gg="url-save"]'),
      uploadFile: root.querySelector('[data-gg="upload-file"]'),
    };

    let path = '';

    function setPath(p) { path = String(p || '').replace(/^\/+|\/+$/g, ''); renderBreadcrumbs(); }

    function renderBreadcrumbs() {
      if (!els.breadcrumbs) return;
      const segs = path ? path.split('/') : [];
      const parts = ['<a href="#" data-p="" class="text-decoration-none">gallery</a>'];
      let acc = '';
      segs.forEach(function (s, i) {
        acc += (i ? '/' : '') + s;
        parts.push(' / <a href="#" data-p="' + esc(acc) + '" class="text-decoration-none">' + esc(s) + '</a>');
      });
      els.breadcrumbs.innerHTML = parts.join('');
      els.breadcrumbs.querySelectorAll('a').forEach(function (a) {
        a.addEventListener('click', function (e) { e.preventDefault(); setPath(a.getAttribute('data-p')); fetchAndRender(); });
      });
    }

    // httpReason превращает ответ сервера в фразу, которую можно показать человеку.
    // Раньше в панель уходило «HTTP 404» — код, по которому оператору непонятно ни
    // что случилось, ни что делать, и который выглядит как сбой даже там, где всё
    // исправно (у новой игры просто ещё нет папки галереи).
    function httpReason(res, fallback) {
      switch (res.status) {
        case 400: return 'Панель отправила неверный запрос. Обновите страницу и попробуйте снова.';
        case 401:
        case 403: return 'Сессия истекла. Войдите заново.';
        case 404: return 'Папка галереи не найдена — возможно, её удалили. Нажмите «Обновить».';
        case 413: return 'Файл слишком большой. Ограничение — 32 МБ.';
        case 415: return 'Такой формат картинки не поддерживается.';
        case 500:
        case 502:
        case 503: return 'Сервер не смог выполнить операцию. Попробуйте ещё раз через минуту.';
        default: return (fallback || 'Не удалось выполнить операцию') + ' (код ' + res.status + ').';
      }
    }

    function renderError(msg) {
      if (!els.grid) return;
      els.grid.innerHTML = '<div class="text-danger">' + esc(msg || 'Ошибка') + '</div>';
    }

    // fetchGalleryMeta загружает gallery.json (cover + подписи) напрямую с
    // публичного /content/, потому что Handlers.List его не отдаёт. Файла
    // может не быть (свежая игра без обложки) — это не ошибка, а пустая
    // галерея: readGalleryFile на сервере тоже трактует ENOENT так же.
    async function fetchGalleryMeta(gameId) {
      let res;
      try { res = await fetch('/content/' + encodeURIComponent(gameId) + '/gallery/gallery.json', { cache: 'no-store' }); }
      catch (e) { return { cover: '', items: [] }; }
      if (!res.ok) return { cover: '', items: [] };
      try { const j = await res.json(); return { cover: j.cover || '', items: Array.isArray(j.items) ? j.items : [] }; }
      catch (e) { return { cover: '', items: [] }; }
    }

    async function fetchAndRender() {
      const gameId = getGameId();
      if (!gameId) { renderError('Игра не выбрана'); return; }
      const q = (els.search && els.search.value) || '';
      let url = EP.list + '?gameId=' + encodeURIComponent(gameId) + '&path=' + encodeURIComponent(path);
      if (q.trim() !== '') url += '&q=' + encodeURIComponent(q.trim());
      let res;
      try { res = await fetch(url); } catch (e) { renderError('Ошибка загрузки: ' + e); return; }
      if (!res.ok) { renderError(httpReason(res, 'Не удалось открыть галерею')); return; }
      let j;
      try { j = await res.json(); } catch (e) { renderError('Сервер вернул не то, что ожидалось. Обновите страницу.'); return; }
      setPath(j.path || path);
      const items = j.items || [];
      // Подписи и обложка в gallery.json ключуются по голому имени файла
      // (SetCaption/SetCover сохраняют SanitizeFilename без пути), поэтому
      // сводим их со списком только когда мы в корне галереи — как и сам
      // сервер, где gallery.json лежит рядом с картинками, а не в подпапках.
      if (!path) {
        const meta = await fetchGalleryMeta(gameId);
        const capByName = {};
        meta.items.forEach(function (it) { if (it && it.file) capByName[it.file] = it.caption || ''; });
        items.forEach(function (it) {
          if (it.isDir) return;
          it.caption = capByName[it.name] || '';
          it.isCover = !!meta.cover && meta.cover === it.name;
        });
      }
      renderGrid(items);
    }

    async function mutate(url, params) {
      let r;
      try {
        r = await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams(params).toString(),
        });
      } catch (e) { toast('Не удалось выполнить операцию: ' + e, 'error'); return false; }
      if (!r.ok) {
        let detail = '';
        try { detail = (await r.text() || '').trim(); } catch { /* noop */ }
        toast('Не удалось выполнить операцию — HTTP ' + r.status + ' ' + r.statusText + (detail ? ': ' + detail : ''), 'error');
        return false;
      }
      return true;
    }

    function iconBtn(kind, extraClass) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'btn btn-sm btn-dark asset-icon-btn' + (extraClass ? ' ' + extraClass : '');
      const titles = { rename: 'Переименовать', delete: 'Удалить' };
      const icons = {
        rename: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z"/></svg>',
        delete: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M6 7h12l-1 13H7L6 7zm3-3h6l1 2H8l1-2z"/></svg>',
      };
      b.title = titles[kind] || '';
      b.setAttribute('aria-label', b.title);
      b.innerHTML = icons[kind] || '';
      return b;
    }

    function renderGrid(items) {
      if (!els.grid) return;
      if (!items || items.length === 0) {
        // Пустая галерея — обычное состояние новой игры, а не сбой: подсказываем,
        // что делать дальше, вместо односложного «Пусто».
        els.grid.innerHTML = '<div class="text-body-secondary py-3">'
          + (path
            ? 'В этой папке пока пусто.'
            : 'У игры ещё нет ни одной картинки. Загрузите обложку с диска или по ссылке — кнопки выше.')
          + '</div>';
        return;
      }
      els.grid.innerHTML = '';
      const gameId = getGameId();
      items.forEach(function (it) {
        const col = document.createElement('div');
        col.className = 'col-6 col-sm-4 col-md-3';
        const card = document.createElement('div');
        card.className = 'card h-100 d-flex flex-column';

        if (it.isDir) {
          card.style.cursor = 'pointer';
          const thumb = document.createElement('div');
          thumb.className = 'card-img-top d-flex align-items-center justify-content-center';
          thumb.style.height = '140px';
          thumb.style.background = '#212529';
          thumb.innerHTML = '<svg width="84" height="84" viewBox="0 0 24 24" fill="#fff"><path d="M9.5 4H4a2 2 0 0 0-2 2v12c0 1.1.9 2 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8.5l-2-2z"/></svg>';
          card.appendChild(thumb);
          const body = document.createElement('div');
          body.className = 'card-body p-2 mt-auto';
          const cap = document.createElement('div');
          cap.className = 'small text-truncate fw-semibold';
          cap.textContent = it.name;
          const actions = document.createElement('div');
          actions.className = 'mt-1';
          const rn = iconBtn('rename');
          rn.onclick = async function (e) {
            e.stopPropagation();
            const nn = prompt('Новое имя папки', it.name);
            if (!nn || nn === it.name) return;
            if (!await mutate(EP.rename, { gameId: gameId, path: path || '', from: it.name, to: nn })) return;
            fetchAndRender();
          };
          const del = iconBtn('delete', 'ms-1');
          del.onclick = async function (e) {
            e.stopPropagation();
            if (!await confirmDialog({
              title: 'Удалить папку?',
              body: 'Папка «' + it.name + '» и всё её содержимое будут удалены с диска.',
              bullets: [
                'Картинки из неё пропадут везде, где на них уже стоят ссылки — в карточке игры и в новостях.',
                'Отменить нельзя: файлы придётся загружать заново.',
              ],
              okText: 'Удалить папку',
              danger: true,
            })) return;
            if (!await mutate(EP.delete, { gameId: gameId, path: path || '', name: it.name })) return;
            fetchAndRender();
          };
          actions.appendChild(rn); actions.appendChild(del);
          body.appendChild(cap); body.appendChild(actions);
          card.appendChild(body);
          card.addEventListener('click', function (ev) {
            if (ev.target.closest && ev.target.closest('button')) return;
            setPath(path ? (path + '/' + it.name) : it.name);
            fetchAndRender();
          });
        } else {
          card.classList.add('gg-thumb-card');
          card.style.position = 'relative';

          const image = isImageName(it.name) || isImageName(it.url);
          const imgWrap = document.createElement('div');
          imgWrap.style.position = 'relative';
          imgWrap.style.height = '120px';
          imgWrap.style.overflow = 'hidden';

          if (image) {
            const img = document.createElement('img');
            img.className = 'card-img-top';
            img.src = it.url; img.alt = it.name || ''; img.loading = 'lazy';
            img.style.height = '120px'; img.style.width = '100%'; img.style.objectFit = 'cover';
            imgWrap.appendChild(img);
          } else {
            const ext = (String(it.name || '').match(/\.([^.]+)$/) || [])[1] || 'file';
            const ph = document.createElement('div');
            ph.className = 'd-flex flex-column align-items-center justify-content-center text-body-secondary';
            ph.style.height = '120px'; ph.style.background = '#212529';
            ph.innerHTML = '<div style="font-size:28px">📄</div><div class="small text-uppercase">' + esc(ext.slice(0, 6)) + '</div>';
            imgWrap.appendChild(ph);
          }

          if (it.isCover) {
            const badge = document.createElement('span');
            badge.className = 'badge text-bg-success';
            badge.style.position = 'absolute'; badge.style.top = '4px'; badge.style.left = '4px';
            badge.textContent = 'Обложка';
            imgWrap.appendChild(badge);
          }

          if (image) {
            const coverBtn = document.createElement('button');
            coverBtn.type = 'button';
            coverBtn.className = 'btn btn-sm btn-outline-light gg-cover-btn';
            coverBtn.textContent = 'Сделать обложкой';
            coverBtn.style.position = 'absolute'; coverBtn.style.right = '4px'; coverBtn.style.bottom = '4px';
            coverBtn.style.opacity = '0'; coverBtn.style.transition = 'opacity .15s';
            coverBtn.disabled = !!it.isCover;
            if (it.isCover) coverBtn.textContent = 'Текущая обложка';
            coverBtn.onclick = async function (e) {
              e.stopPropagation();
              const rel = path ? (path + '/' + it.name) : it.name;
              if (!await mutate(EP.setCover, { gameId: gameId, file: rel })) return;
              toast('Обложка обновлена', 'success');
              fetchAndRender();
            };
            imgWrap.appendChild(coverBtn);
            imgWrap.addEventListener('mouseenter', function () { coverBtn.style.opacity = '1'; });
            imgWrap.addEventListener('mouseleave', function () { coverBtn.style.opacity = '0'; });
          }

          const body = document.createElement('div');
          body.className = 'card-body p-2 mt-auto';

          const capInput = document.createElement('input');
          capInput.type = 'text';
          capInput.className = 'form-control form-control-sm mb-1';
          capInput.placeholder = 'Подпись...';
          capInput.value = it.caption || '';
          capInput.dataset.orig = it.caption || '';
          if (!image) capInput.disabled = true;
          const saveCaption = async function () {
            if (capInput.value === capInput.dataset.orig) return;
            const rel = path ? (path + '/' + it.name) : it.name;
            if (!await mutate(EP.setCaption, { gameId: gameId, file: rel, caption: capInput.value })) return;
            capInput.dataset.orig = capInput.value;
            toast('Подпись сохранена', 'success');
          };
          capInput.addEventListener('blur', saveCaption);
          capInput.addEventListener('change', saveCaption);
          capInput.addEventListener('click', function (e) { e.stopPropagation(); });

          const nameRow = document.createElement('div');
          nameRow.className = 'small text-truncate';
          nameRow.textContent = it.name || '';

          const actions = document.createElement('div');
          actions.className = 'mt-1';
          const rn = iconBtn('rename');
          rn.onclick = async function (e) {
            e.stopPropagation();
            const nn = prompt('Новое имя файла', it.name);
            if (!nn || nn === it.name) return;
            if (!await mutate(EP.rename, { gameId: gameId, path: path || '', from: it.name, to: nn })) return;
            fetchAndRender();
          };
          const del = iconBtn('delete', 'ms-1');
          del.onclick = async function (e) {
            e.stopPropagation();
            if (!await confirmDialog({
              title: 'Удалить файл?',
              body: 'Файл «' + it.name + '» будет удалён с диска.',
              bullets: [
                'Если он уже вставлен в карточку игры или в новость, картинка там пропадёт.',
                'Отменить нельзя: файл придётся загружать заново.',
              ],
              okText: 'Удалить файл',
              danger: true,
            })) return;
            if (!await mutate(EP.delete, { gameId: gameId, path: path || '', name: it.name })) return;
            fetchAndRender();
          };
          actions.appendChild(rn); actions.appendChild(del);

          body.appendChild(capInput);
          body.appendChild(nameRow);
          body.appendChild(actions);
          card.appendChild(imgWrap);
          card.appendChild(body);
        }

        col.appendChild(card);
        els.grid.appendChild(col);
      });
    }

    async function uploadFile(file) {
      const gameId = getGameId();
      if (!gameId) { toast('Игра не выбрана', 'error'); return; }
      const fd = new FormData();
      fd.append('gameId', gameId);
      fd.append('path', path || '');
      fd.append('filename', file.name || 'image');
      fd.append('file', file);
      let res;
      try { res = await fetch(EP.upload, { method: 'POST', body: fd }); } catch (e) { toast('Ошибка загрузки: ' + e, 'error'); return; }
      if (!res.ok) { toast(httpReason(res, 'Не удалось загрузить картинку'), 'error'); return; }
      fetchAndRender();
    }

    if (els.refresh) els.refresh.addEventListener('click', function (e) { e.preventDefault(); fetchAndRender(); });
    if (els.search) els.search.addEventListener('input', function () { fetchAndRender(); });
    if (els.mkdir) els.mkdir.addEventListener('click', async function (e) {
      e.preventDefault();
      const gameId = getGameId();
      if (!gameId) { toast('Игра не выбрана', 'error'); return; }
      const name = prompt('Имя новой папки:');
      if (!name) return;
      if (!await mutate(EP.mkdir, { gameId: gameId, path: path || '', name: name })) return;
      fetchAndRender();
    });
    if (els.urlSave) els.urlSave.addEventListener('click', async function (e) {
      e.preventDefault();
      const gameId = getGameId();
      if (!gameId) { toast('Игра не выбрана', 'error'); return; }
      const url = (els.url && els.url.value || '').trim();
      if (!url) { toast('Укажите URL', 'error'); return; }
      const name = (els.urlName && els.urlName.value) || 'image';
      const fd = new URLSearchParams();
      fd.set('gameId', gameId); fd.set('path', path || ''); fd.set('filename', name); fd.set('url', url);
      let res;
      try { res = await fetch(EP.uploadByUrl, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: fd.toString() }); } catch (e2) { renderError('Ошибка: ' + e2); return; }
      if (!res.ok) { toast(httpReason(res, 'Не удалось сохранить картинку'), 'error'); return; }
      fetchAndRender();
    });
    if (els.uploadFile) els.uploadFile.addEventListener('change', function () {
      const f = els.uploadFile.files && els.uploadFile.files[0];
      if (f) uploadFile(f);
      els.uploadFile.value = '';
    });

    return { setPath: setPath, fetchAndRender: fetchAndRender, refresh: fetchAndRender };
  }

  window.createGameGallery = createGameGallery;
})();
