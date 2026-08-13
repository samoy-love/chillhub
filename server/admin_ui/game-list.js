// Список игр — вкладка «Игры» (трек I).
//
// Реальные данные и вся логика сохранения по-прежнему живут в скрытой
// #mgm-table (см. admin.js: mgmReload/mgmAppendRow/mgmSave/mgmResync) — этот
// файл только рисует поверх неё searchable-список (#gm_list) с
// drag-reorder (order) и пином (pinned), и карточку «Обзор» выбранной игры
// (#gm_ov_*), которая читает/пишет значения той же скрытой строки.
//
// Контракт order/pinned — PLAN.md, раздел 1: сохраняются через тот же
// /admin/games/save, что и раньше (см. mgmSave в admin.js), без нового
// эндпоинта.
(function () {
  'use strict';

  function esc(s) {
    if (window.escapeHtml) return window.escapeHtml(s);
    return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function say(msg) { if (window.notify) window.notify(msg); }

  function getRows() { return Array.from(document.querySelectorAll('#mgm-table tbody tr')); }
  function getSelectedGid() { return (document.getElementById('gid') && document.getElementById('gid').value || '').trim(); }
  function getHiddenRowFor(gid) {
    const target = String(gid || '').trim().toLowerCase();
    return getRows().find(function (tr) {
      const inp = tr.querySelectorAll('td')[0].querySelector('input');
      return inp && inp.value.trim().toLowerCase() === target;
    }) || null;
  }
  function getSelectedRow() { return document.querySelector('#mgm-table tbody tr.mgm-selected'); }

  // dualInput — прокси, которая при записи .value обновляет и скрытый инпут
  // строки (от него зависит mgmSave), и видимый инпут вкладки «Обзор» разом.
  // Используется, чтобы переиспользовать существующие openExePicker и логику
  // загрузки иконки без переписывания: они умеют работать с любым объектом,
  // у которого есть .value.
  function dualInput(hiddenInput, visibleInput) {
    return {
      get value() { return visibleInput ? visibleInput.value : (hiddenInput ? hiddenInput.value : ''); },
      set value(v) {
        if (visibleInput) visibleInput.value = v;
        if (hiddenInput) {
          hiddenInput.value = v;
          hiddenInput.dispatchEvent(new Event('input', { bubbles: true }));
        }
      },
    };
  }

  // ===== Searchable list (#gm_list) =====
  let __gmDragTr = null;

  function gmListRender() {
    const host = document.getElementById('gm_list');
    if (!host) return;
    const q = (document.getElementById('gm_search') && document.getElementById('gm_search').value || '').trim().toLowerCase();
    const curGid = getSelectedGid().toLowerCase();
    host.innerHTML = '';
    const rows = getRows();
    let shown = 0;
    rows.forEach(function (tr) {
      const tds = tr.querySelectorAll('td');
      const gid = tds[0].querySelector('input').value.trim();
      const title = tds[1].querySelector('input').value.trim() || gid;
      if (!gid) return;
      if (q && gid.toLowerCase().indexOf(q) === -1 && title.toLowerCase().indexOf(q) === -1) return;
      shown++;
      const pinned = tr.dataset.pinned === '1';
      const item = document.createElement('div');
      item.className = 'list-group-item d-flex align-items-center gap-2' + (gid.toLowerCase() === curGid ? ' active' : '');
      item.draggable = true;
      item.innerHTML = ''
        + '<span class="text-body-secondary" style="cursor:grab" title="Перетащить">⋮⋮</span>'
        + '<button type="button" class="btn btn-sm ' + (pinned ? 'btn-warning' : 'btn-outline-secondary') + ' gm-pin" title="Закрепить вверху списка">' + (pinned ? '★' : '☆') + '</button>'
        + '<span class="flex-grow-1 text-truncate">' + esc(title) + ' <span class="text-body-secondary small">(' + esc(gid) + ')</span></span>';

      item.addEventListener('click', function (ev) {
        if (ev.target.closest('.gm-pin')) return;
        tr.click(); // reuses the existing row-selection logic in admin.js (mgmAppendRow)
        if (window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(gid);
        gmListRender();
      });

      const pinBtn = item.querySelector('.gm-pin');
      pinBtn.addEventListener('click', function (ev) {
        ev.stopPropagation();
        tr.dataset.pinned = pinned ? '0' : '1';
        if (window.mgmSetDirty) window.mgmSetDirty(true);
        gmListRender();
        // Пин применяется сразу — это переключатель состояния, а не черновик.
        if (window.mgmSave) window.mgmSave();
      });

      item.addEventListener('dragstart', function (ev) {
        __gmDragTr = tr;
        item.classList.add('dragging');
        if (ev.dataTransfer) { ev.dataTransfer.effectAllowed = 'move'; try { ev.dataTransfer.setData('text/plain', gid); } catch (_) { /* Firefox needs a payload */ } }
      });
      item.addEventListener('dragend', function () { item.classList.remove('dragging'); __gmDragTr = null; });
      item.addEventListener('dragover', function (ev) { ev.preventDefault(); });
      item.addEventListener('drop', function (ev) {
        ev.preventDefault();
        if (!__gmDragTr || __gmDragTr === tr) return;
        tr.parentNode.insertBefore(__gmDragTr, tr);
        if (window.mgmSetDirty) window.mgmSetDirty(true);
        gmListRender();
        // Порядок — как пин, применяется сразу тем же /admin/games/save.
        if (window.mgmSave) window.mgmSave();
      });

      host.appendChild(item);
    });
    if (shown === 0) {
      host.innerHTML = '<div class="text-body-secondary small p-2">' + (q ? 'Ничего не найдено' : 'Список пуст') + '</div>';
    }
  }

  // ===== "Обзор" tab: mirrors the selected hidden row =====
  // updateModpacksTabVisibility показывает/прячет вкладку «Модпаки» (трек K)
  // по наличию thunderstoreCommunity у выбранной игры, и обновляет список
  // скачанных модпаков панели (window.createModpacksPanel, modpacks.js), если
  // она уже смонтирована.
  function updateModpacksTabVisibility(community) {
    const item = document.getElementById('gmtab-modpacks-item');
    if (item) item.style.display = community ? '' : 'none';
    if (community && window.__modpacksPanel && window.__modpacksPanel.refresh) window.__modpacksPanel.refresh();
  }

  function gmSyncOverviewFromRow(gid) {
    const idEl = document.getElementById('gm_ov_gid');
    const titleEl = document.getElementById('gm_ov_title');
    const iconEl = document.getElementById('gm_ov_icon');
    const exeEl = document.getElementById('gm_ov_exe');
    const tsEl = document.getElementById('gm_ov_thunderstore');
    if (idEl) idEl.value = gid || '';
    const tr = getHiddenRowFor(gid);
    if (!tr) {
      if (titleEl) titleEl.value = '';
      if (iconEl) iconEl.value = '';
      if (exeEl) exeEl.value = '';
      if (tsEl) tsEl.value = '';
      updateModpacksTabVisibility('');
      return;
    }
    const tds = tr.querySelectorAll('td');
    if (titleEl) titleEl.value = tds[1].querySelector('input').value;
    if (iconEl) iconEl.value = tds[2].querySelector('input').value;
    if (exeEl) exeEl.value = tds[3].querySelector('input').value;
    const tsInput = tds[4] && tds[4].querySelector('input.mgm-thunderstore');
    const community = tsInput ? tsInput.value.trim() : '';
    if (tsEl) tsEl.value = community;
    updateModpacksTabVisibility(community);
  }

  function bindOverviewWriteback() {
    [['gm_ov_title', 1], ['gm_ov_icon', 2], ['gm_ov_exe', 3], ['gm_ov_thunderstore', 4]].forEach(function (pair) {
      const id = pair[0], tdIdx = pair[1];
      const el = document.getElementById(id);
      if (!el) return;
      el.addEventListener('input', function () {
        const tr = getSelectedRow();
        if (!tr) return;
        const inp = tr.querySelectorAll('td')[tdIdx].querySelector('input');
        if (!inp) return;
        inp.value = el.value;
        inp.dispatchEvent(new Event('input', { bubbles: true }));
      });
    });
  }

  function bindOverviewActions() {
    const iconUpload = document.getElementById('gm_ov_icon_upload');
    const iconDefault = document.getElementById('gm_ov_icon_default');
    const exePick = document.getElementById('gm_ov_exe_pick');

    if (iconUpload) iconUpload.addEventListener('click', function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const row = getSelectedRow();
      const target = dualInput(row ? row.querySelector('input.mgm-icon') : null, document.getElementById('gm_ov_icon'));
      const fileInput = document.createElement('input');
      fileInput.type = 'file'; fileInput.accept = 'image/*';
      fileInput.onchange = async function () {
        const f = fileInput.files && fileInput.files[0];
        if (!f) return;
        const fd = new FormData(); fd.append('gameId', gid); fd.append('file', f);
        let res;
        try { res = await fetch('/admin/games/icon/upload', { method: 'POST', body: fd }); } catch (e) { say('Ошибка загрузки: ' + e); return; }
        if (!res.ok) { say('HTTP ' + res.status + ' ' + res.statusText); return; }
        let j; try { j = await res.json(); } catch (e) { say('Плохой JSON'); return; }
        if (j && j.url) { target.value = j.url; say('Иконка обновлена: ' + j.url); }
      };
      fileInput.click();
    });

    if (iconDefault) iconDefault.addEventListener('click', function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const row = getSelectedRow();
      dualInput(row ? row.querySelector('input.mgm-icon') : null, document.getElementById('gm_ov_icon')).value = '/manifests/' + gid + '/icon.png';
    });

    if (exePick) exePick.addEventListener('click', function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const row = getSelectedRow();
      const target = dualInput(row ? row.querySelector('input.mgm-exe') : null, document.getElementById('gm_ov_exe'));
      if (window.openExePicker) window.openExePicker(gid, target);
    });
  }

  // ===== "Опасная зона" tab =====
  async function confirmDialog(opts) {
    if (window.askConfirm) return window.askConfirm(opts);
    return Promise.resolve(window.confirm((opts.title || '') + '\n\n' + (opts.body || '')));
  }

  function bindDangerZone() {
    const unpub = document.getElementById('gm_dz_unpublish');
    const del = document.getElementById('gm_dz_delete');

    if (unpub) unpub.addEventListener('click', async function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const ok = await confirmDialog({
        title: 'Снять «' + gid + '» с публикации?',
        body: 'Игра должна пропасть из лаунчера, а файлы манифестов и сборок — остаться на диске.',
        okText: 'Снять с публикации',
        danger: true,
      });
      if (!ok) return;
      // TODO(Трек H): в реестре (server/internal/adminapi/games/games.go,
      // Entry) нет флага published/unpublished и нет отдельного эндпоинта —
      // пока подтверждение диалога ни к чему не приводит на сервере.
      say('Эндпоинт «снять с публикации» ещё не реализован на сервере (см. TODO(Трек H) в game-list.js).');
    });

    if (del) del.addEventListener('click', async function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const ok = await confirmDialog({
        title: 'Удалить игру «' + gid + '» и все версии?',
        body: 'Запись будет убрана из реестра сразу после подтверждения. Файлы манифестов и сборок на диске НЕ удаляются — массового удаления версий на сервере пока нет.',
        okText: 'Удалить из списка',
        danger: true,
      });
      if (!ok) return;
      const row = getHiddenRowFor(gid);
      if (row) row.remove();
      // TODO(Трек H): нет эндпоинта массового удаления версий/файлов игры —
      // удаляется только запись в реестре через games.Save. В
      // server/internal/adminapi/builds/builds.go есть только DeleteVersion
      // по одной версии (дергается из admin.js по кнопке в списке версий).
      if (window.mgmSave) await window.mgmSave();
      say('Игра «' + gid + '» удалена из реестра. Файлы версий на диске нужно убрать вручную (см. TODO(Трек H) в game-list.js).');
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    const search = document.getElementById('gm_search');
    if (search) search.addEventListener('input', gmListRender);
    bindOverviewWriteback();
    bindOverviewActions();
    bindDangerZone();
    gmListRender();
  });

  window.gmListRender = gmListRender;
  window.gmSyncOverviewFromRow = gmSyncOverviewFromRow;
})();
