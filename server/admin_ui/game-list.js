// Список игр — вкладка «Игры».
//
// Реальные данные и вся логика сохранения по-прежнему живут в скрытой
// #mgm-table (см. admin.js: mgmReload/mgmAppendRow/mgmSave/mgmResync) — этот
// файл только рисует поверх неё searchable-список (#gm_list) с
// drag-reorder (order) и пином (pinned), и карточку «Обзор» выбранной игры
// (#gm_ov_*), которая читает/пишет значения той же скрытой строки.
//
// order/pinned/unpublished сохраняются тем же /admin/games/save, что и остальные
// поля реестра (см. mgmSave в admin.js), без отдельных эндпоинтов.
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
        + '<span class="flex-grow-1 text-truncate">' + esc(title) + ' <span class="text-body-secondary small">(' + esc(gid) + ')</span></span>'
        // Снятую с публикации игру видно прямо в списке: иначе единственный
        // признак того, что её нет в лаунчере, лежит на вкладке «Публикация и удаление».
        + (tr.dataset.unpublished === '1' ? '<span class="badge text-bg-secondary" title="Не публикуется в лаунчере">скрыта</span>' : '');

      item.addEventListener('click', function (ev) {
        if (ev.target.closest('.gm-pin')) return;
        tr.click(); // reuses the existing row-selection logic in admin.js (mgmAppendRow)
        if (window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(gid);
        // Галерею надо позвать явно: tr.click() присваивает #gm_select.value
        // напрямую, а программное присваивание не порождает change, на котором
        // висит обновление галереи в admin.js. Без этой строки панель
        // показывала галерею предыдущей игры.
        if (window.__gameGallery && window.__gameGallery.fetchAndRender) window.__gameGallery.fetchAndRender();
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
  function gmSyncOverviewFromRow(gid) {
    const idEl = document.getElementById('gm_ov_gid');
    const titleEl = document.getElementById('gm_ov_title');
    const iconEl = document.getElementById('gm_ov_icon');
    const exeEl = document.getElementById('gm_ov_exe');
    if (idEl) idEl.value = gid || '';
    const tr = getHiddenRowFor(gid);
    if (!tr) {
      if (titleEl) titleEl.value = '';
      if (iconEl) iconEl.value = '';
      if (exeEl) exeEl.value = '';
      updateDangerZone(gid);
      return;
    }
    const tds = tr.querySelectorAll('td');
    if (titleEl) titleEl.value = tds[1].querySelector('input').value;
    if (iconEl) iconEl.value = tds[2].querySelector('input').value;
    if (exeEl) exeEl.value = tds[3].querySelector('input').value;
    updateDangerZone(gid);
  }

  function bindOverviewWriteback() {
    [['gm_ov_title', 1], ['gm_ov_icon', 2], ['gm_ov_exe', 3]].forEach(function (pair) {
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

  // ===== Вкладка «Публикация и удаление» =====
  async function confirmDialog(opts) {
    if (window.askConfirm) return window.askConfirm(opts);
    return Promise.resolve(window.confirm((opts.title || '') + '\n\n' + (opts.body || '')));
  }

  function bindDangerZone() {
    const unpub = document.getElementById('gm_dz_unpublish');
    const del = document.getElementById('gm_dz_delete');

    // Снятие с публикации — переключатель, а не односторонняя дверь: та же
    // кнопка возвращает игру в лаунчер. Состояние живёт в реестре
    // (games.Entry.Unpublished) и уезжает обычным сохранением списка.
    if (unpub) unpub.addEventListener('click', async function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const row = getHiddenRowFor(gid);
      if (!row) { say('Игра «' + gid + '» не найдена в списке'); return; }
      const hidden = row.dataset.unpublished === '1';
      if (!hidden) {
        const ok = await confirmDialog({
          title: 'Снять «' + gid + '» с публикации?',
          body: 'Игра пропадёт из лаунчера. Записи в реестре, манифесты и сборки останутся на месте — вернуть можно этой же кнопкой.',
          okText: 'Снять с публикации',
          danger: true,
        });
        if (!ok) return;
      }
      row.dataset.unpublished = hidden ? '0' : '1';
      if (window.mgmSetDirty) window.mgmSetDirty(true);
      if (window.mgmSave) await window.mgmSave();
      updateDangerZone(gid);
      say(hidden
        ? 'Игра «' + gid + '» снова публикуется в лаунчере.'
        : 'Игра «' + gid + '» снята с публикации.');
    });

    if (del) del.addEventListener('click', async function () {
      const gid = getSelectedGid();
      if (!gid) { say('Сначала выберите игру'); return; }
      const ok = await confirmDialog({
        title: 'Удалить игру «' + gid + '» и все версии?',
        body: 'С диска будут стёрты манифесты и все распакованные сборки этой игры, запись уйдёт из реестра. Отменить нельзя.',
        okText: 'Удалить безвозвратно',
        danger: true,
      });
      if (!ok) return;
      const body = new URLSearchParams({ gameId: gid });
      let res;
      try {
        res = await fetch('/admin/games/purge', {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: body.toString(),
        });
      } catch (e) {
        say('Не удалось удалить игру: ' + e);
        return;
      }
      if (!res.ok) { say('Не удалось удалить игру: ' + res.status + ' ' + (await res.text())); return; }
      // Реестр уже переписан сервером — перечитываем его вместо того, чтобы
      // угадывать новое состояние по локальным строкам таблицы.
      const row = getHiddenRowFor(gid);
      if (row) row.remove();
      if (window.mgmReload) await window.mgmReload();
      say('Игра «' + gid + '» удалена вместе с манифестами и сборками.');
    });
  }

  // updateDangerZone приводит подписи «Опасной зоны» в соответствие с тем, что
  // кнопки сделают сейчас: у снятой с публикации игры та же кнопка возвращает
  // её обратно, и надпись «Снять с публикации» на ней читалась бы как ошибка.
  function updateDangerZone(gid) {
    const unpub = document.getElementById('gm_dz_unpublish');
    if (!unpub) return;
    const row = getHiddenRowFor(gid);
    const hidden = !!row && row.dataset.unpublished === '1';
    unpub.textContent = hidden ? 'Вернуть в лаунчер' : 'Снять с публикации';
    unpub.classList.toggle('btn-outline-danger', !hidden);
    unpub.classList.toggle('btn-outline-success', hidden);
    const state = document.getElementById('gm_dz_state');
    if (state) {
      state.textContent = hidden
        ? 'Игра снята с публикации: в лаунчере её нет, файлы на месте.'
        : 'Игра публикуется в лаунчере.';
    }
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
