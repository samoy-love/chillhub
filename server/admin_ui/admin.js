// Drag-n-drop wiring for ZIP upload (Launcher tab)
(function(){
  const dz = document.getElementById('up_drop'); if(!dz) return;
  ['dragenter','dragover'].forEach(ev=> dz.addEventListener(ev, (e)=>{ e.preventDefault(); e.stopPropagation(); dz.classList.add('border-primary'); }));
  ['dragleave','drop'].forEach(ev=> dz.addEventListener(ev, (e)=>{ e.preventDefault(); e.stopPropagation(); dz.classList.remove('border-primary'); }));
  dz.addEventListener('drop', (e)=>{
    const files = e.dataTransfer && e.dataTransfer.files ? e.dataTransfer.files : null;
    if(!files || files.length===0){ return; }

    const f = files[0];
    if(!/\.zip$/i.test(f.name)){ notify('Ожидается ZIP-файл'); return; }
    window.__upDroppedFile = f;
    const txt=document.getElementById('up_prog_text'); const wrap=document.getElementById('up_prog_wrap'); const bar=document.getElementById('up_pb');
    if(wrap){ wrap.style.display='block'; }
    if(bar){ bar.style.width='0%'; }
    if(txt){ txt.textContent = 'Выбран файл: '+f.name+' ('+f.size+' байт)'; }
  });
})();

// ==== Launcher preview: ensure versions list and render selected ====
let __lnPrevSeq = 0;
async function lnPrevEnsureVersionsAndRender(){
  try{
    const sel = document.getElementById('ln_prev_ver'); const tree = document.getElementById('ln_tree');
    if(!sel || !tree) return;
    const seq = ++__lnPrevSeq;
    let res = await fetch('/admin/list?gameId=launcher');
    if(!res.ok){ tree.textContent = 'HTTP '+res.status; return; }
    const j = await res.json();
    const latest = j.latest || '';
    const items = Array.isArray(j.items)? j.items: [];
    // If a newer call started while we were awaiting, abort applying this result
    if(seq !== __lnPrevSeq) return;
    // Replace options atomically to avoid flicker and duplicates on concurrent calls
    sel.innerHTML = '';
    const frag = document.createDocumentFragment();
    for(const it of items){
      const opt = document.createElement('option');
      opt.value = it.version; opt.textContent = it.version;
      frag.appendChild(opt);
    }
    sel.appendChild(frag);
    if(latest) sel.value = latest;
    await lnPrevRender(sel.value||'');
  }catch(e){ const tree=document.getElementById('ln_tree'); if(tree) tree.textContent='Ошибка: '+e; }
}

async function lnPrevRender(version){
  const tree = document.getElementById('ln_tree'); if(!tree) return;
  if(!version){ tree.textContent = 'Выберите версию лаунчера'; return; }
  tree.innerHTML = '<span class="text-body-secondary">Загрузка манифеста...</span>';
  try{
    const r = await fetch('/manifests/launcher/'+encodeURIComponent(version)+'.json');
    if(!r.ok){ tree.textContent = 'HTTP '+r.status; return; }
    const manifest = await r.json();
    lnRenderTree(tree, manifest);
  }catch(e){ tree.textContent = 'Ошибка: '+e; }
}

// ==== Launcher versions list (right column on Launcher tab) ====
async function lnManifestsReload(){
  const root = document.getElementById('ln_ver_list'); if(!root) return;
  let res; try{ res = await fetch('/admin/list?gameId=launcher'); }catch(e){ root.textContent = 'Ошибка: '+e; return; }
  if(!res.ok){ root.textContent = 'HTTP '+res.status+' '+res.statusText; return; }
  let j; try{ j = await res.json(); }catch(e){ root.textContent = 'Ошибка парсинга JSON'; return; }
  const latest = j.latest||'';
  const items = Array.isArray(j.items)? j.items: [];
  const rows = items.map(it=>{
    const ver = it.version || '';
    const isLatest = latest && ver === latest;
    const actBtn = isLatest ? '<span class="badge text-bg-success">latest</span>' : ('<button data-ver="'+ver+'" class="btn btn-sm btn-outline-primary ln-activate">Сделать активной</button>');
    const delBtn = '<button data-ver="'+ver+'" class="btn btn-sm btn-outline-danger ms-2 ln-delete">Удалить</button>';
    return '<tr><td class="text-monospace">'+escapeHtml(ver)+'</td><td>'+(isLatest?'<span class="text-success">Активна</span>':'<span class="text-body-secondary">—</span>')+'</td><td class="text-end">'+actBtn+delBtn+'</td></tr>';
  }).join('');
  root.innerHTML = '<div class="table-responsive"><table class="table table-dark table-striped align-middle"><thead><tr><th>Версия</th><th>Статус</th><th class="text-end"></th></tr></thead><tbody>'+rows+'</tbody></table></div>';
  // bind activate buttons
  root.querySelectorAll('.ln-activate').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      if(!confirm('Сделать версию '+ver+' активной?')) return;
      let r; try{ r = await fetch('/admin/activate?gameId=launcher&version='+encodeURIComponent(ver), {method:'POST'}); }catch(e){ notify('Ошибка: '+e); return; }
      if(!r.ok){ notify('HTTP '+r.status+' '+r.statusText); return; }
      try{ await lnManifestsReload(); }catch(_){ }
      try{ await lnRefresh(); }catch(_){ }
    });
  });
  // bind delete buttons
  root.querySelectorAll('.ln-delete').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      if(!confirm('Удалить версию '+ver+'?\nБудут удалены манифест и файлы сборки.')) return;
      let r; try{ r = await fetch('/admin/deleteVersion?gameId=launcher&version='+encodeURIComponent(ver), {method:'POST'}); }catch(e){ notify('Ошибка: '+e); return; }
      if(!r.ok){ notify('HTTP '+r.status+' '+r.statusText); return; }
      notify('Версия '+ver+' удалена');
      try{ await lnManifestsReload(); }catch(_){ }
      try{ await lnRefresh(); }catch(_){ }
    });
  });
}

// ==== Launcher manifest viewer ====
function bumpSemverPatch(v){
  v = String(v||'').trim();
  const m = /^\s*(\d+)\.(\d+)\.(\d+)\s*$/.exec(v);
  if(!m){
    // fallback: if only major.minor given
    const m2 = /^\s*(\d+)\.(\d+)\s*$/.exec(v); if(m2){ return m2[1]+'.'+m2[2]+'.1'; }
    // default first version
    return '1.0.1';
  }
  const a = parseInt(m[1],10), b=parseInt(m[2],10), c=parseInt(m[3],10);
  return a+'.'+b+'.'+(c+1);
}

// Format bytes into human-readable units: Б, КБ, МБ, ГБ, ТБ
function formatBytes(n){
  const size = Number(n);
  if(!Number.isFinite(size) || size < 0) return '0 Б';
  const units = ['Б','КБ','МБ','ГБ','ТБ'];
  if(size < 1024) return size + ' Б';
  let val = size;
  let i = 0;
  while(val >= 1024 && i < units.length-1){ val /= 1024; i++; }
  const str = (i === 0) ? String(Math.round(val)) : (val >= 100 ? Math.round(val).toString() : val.toFixed(1));
  return str + ' ' + units[i];
}
async function lnRefresh(){
  const treeEl = document.getElementById('ln_tree'); if(!treeEl) return;
  treeEl.innerHTML = '<span class="text-body-secondary">Загрузка latest.json...</span>';
  // fetch latest.json
  const bust = Date.now();
  let latest; try{ const r = await fetch('/manifests/launcher/latest.json?t='+bust); if(!r.ok){ treeEl.textContent = 'HTTP '+r.status+' '+r.statusText; return; } latest = await r.json(); }catch(e){ treeEl.textContent = 'Ошибка запроса: '+e; return; }
  const ver = (latest && latest.version) ? latest.version : '';
  // Update latest badge with current version
  try{
    const badge = document.getElementById('ln_latest_badge');
    if(badge){ badge.textContent = ver || '—'; }
  }catch(_){ }
  if(!ver){ treeEl.textContent = 'Не найден latest.json'; return; }
  // show latest and prefill upload version with next patch
  const upVer = document.getElementById('up_ver'); if(upVer){ upVer.value = bumpSemverPatch(ver); }
  treeEl.innerHTML = '<div class="small text-body-secondary mb-1">Текущая версия лаунчера: <code>'+escapeHtml(ver)+'</code></div>'+
                     '<span class="text-body-secondary">Загрузка манифеста...</span>';
  let manifest; try{ const r2 = await fetch('/manifests/launcher/'+encodeURIComponent(ver)+'.json?t='+bust); if(!r2.ok){ treeEl.textContent = 'HTTP '+r2.status+' '+r2.statusText; return; } manifest = await r2.json(); }catch(e){ treeEl.textContent = 'Ошибка загрузки манифеста: '+e; return; }
  lnRenderTree(treeEl, manifest);
}

function lnRenderTree(rootEl, manifest){
  const files = Array.isArray(manifest?.files) ? manifest.files : [];
  const emptyDirs = new Set(Array.isArray(manifest?.emptyDirs)? manifest.emptyDirs : []);
  // build a tree structure
  const node = ()=>({children:new Map(), files:[]});
  const root = node();
  for(const f of files){
    const p = String(f.path||'').replace(/^\/+/, '');
    const parts = p.split('/').filter(Boolean);
    let cur = root;
    for(let i=0;i<parts.length-1;i++){
      const k = parts[i]; if(!cur.children.has(k)) cur.children.set(k, node()); cur = cur.children.get(k);
    }
    const fname = parts[parts.length-1] || '';
    const sz = Number(f.size);
    cur.files.push({name: fname, size: Number.isFinite(sz) ? sz : 0});
  }
  for(const d of emptyDirs){
    const parts = String(d||'').split('/').filter(Boolean);
    let cur = root;
    for(let i=0;i<parts.length;i++){
      const k = parts[i]; if(!cur.children.has(k)) cur.children.set(k, node()); cur = cur.children.get(k);
    }
  }
  // render
  const renderNode = (name, n, depth)=>{
    // Visual indent for folder rows: no indent for root-level folders (depth===1)
    // For deeper folders, indent by 16px per additional level beyond root
    const folderIndent = (depth>1) ? 16*(depth-1) : 0;
    // Files don't have a twisty; add a base spacer equal to the twisty width (~20px)
    const twistyPad = 20;
    let html = '';
    if(name!==null){
      // Folder block as <details> collapsed by default, with SVG twisty and counts
      const dirCount = n.children.size;
      const fileCount = n.files.length;
      html += '<details class="tree-dir" style="margin-left:'+folderIndent+'px">'
           +  '<summary class="d-flex align-items-center tree-summary">'
           +    '<svg class="twisty me-2" width="12" height="12" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5l8 7-8 7V5z" fill="currentColor"/></svg>'
           +    '<span class="me-2">📁</span><strong>'+escapeHtml(name)+'</strong>'
           +    '<span class="ms-2 small text-body-secondary">('+dirCount+' папок, '+fileCount+' файлов)</span>'
           +  '</summary>';
    }
    // folders first
    const keys = Array.from(n.children.keys()).sort((a,b)=> a.localeCompare(b));
    for(const k of keys){ html += renderNode(k, n.children.get(k), depth+1); }
    // files
    for(const f of n.files.sort((a,b)=> a.name.localeCompare(b.name))){
      // For files not in root, indent one extra step for clearer hierarchy
      const filePad = (depth>0 ? 16*depth : 0) + twistyPad;
      html += '<div class="d-flex align-items-center" style="padding-left:'+filePad+'px">'
           + '<span class="me-2">📄</span><span>'+escapeHtml(f.name)+'</span>'
           + '<span class="ms-auto small text-body-secondary" title="'+(Number.isFinite(f.size)?f.size:0)+' байт">'+formatBytes(f.size)+'</span>'
           + '</div>';
    }
    if(name!==null){ html += '</details>'; }
    return html;
  };
  rootEl.innerHTML = '<div class="small text-body-secondary mb-1">Всего файлов: '+files.length+'</div>' + renderNode(null, root, 0);
}

// ==== Manifests page: upload/list/activate ====
async function manifestsReload(){
  const gid = (document.getElementById('gid')?.value||'').trim();
  if(!gid){ notify('Укажите идентификатор игры'); return; }
  let res; try{ res = await fetch('/admin/list?gameId='+encodeURIComponent(gid)); }catch(e){ notify('Ошибка: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  let j; try{ j = await res.json(); }catch(e){ notify('Ошибка парсинга'); return; }
  const root = document.getElementById('ver_list'); if(!root) return;
  const latest = j.latest||'';
  const items = Array.isArray(j.items)? j.items: [];
  // render table
  const rows = items.map(it=>{
    const ver = it.version || '';
    const isLatest = latest && ver === latest;
    const actBtn = isLatest ? '<span class="badge text-bg-success">latest</span>' : ('<button data-ver="'+ver+'" class="btn btn-sm btn-outline-primary man-activate">Сделать активной</button>');
    const delBtn = '<button data-ver="'+ver+'" class="btn btn-sm btn-outline-danger ms-2 man-delete">Удалить</button>';
    return '<tr><td class="text-monospace">'+escapeHtml(ver)+'</td><td>'+(isLatest?'<span class="text-success">Активна</span>':'<span class="text-body-secondary">—</span>')+'</td><td class="text-end">'+actBtn+delBtn+'</td></tr>';
  }).join('');
  root.innerHTML = '<div class="table-responsive"><table class="table table-dark table-striped align-middle"><thead><tr><th>Версия</th><th>Статус</th><th class="text-end"></th></tr></thead><tbody>'+rows+'</tbody></table></div>';
  // bind activate buttons
  root.querySelectorAll('.man-activate').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      if(!confirm('Сделать версию '+ver+' активной?')) return;
      let r; try{ r = await fetch('/admin/activate?gameId='+encodeURIComponent(gid)+'&version='+encodeURIComponent(ver), {method:'POST'}); }catch(e){ notify('Ошибка: '+e); return; }
      if(!r.ok){ notify('HTTP '+r.status+' '+r.statusText); return; }
      manifestsReload();
    });
  });
  // bind delete buttons
  root.querySelectorAll('.man-delete').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      if(!confirm('Удалить версию '+ver+'?\nБудут удалены манифест и файлы сборки.')) return;
      let r; try{ r = await fetch('/admin/deleteVersion?gameId='+encodeURIComponent(gid)+'&version='+encodeURIComponent(ver), {method:'POST'}); }catch(e){ notify('Ошибка: '+e); return; }
      if(!r.ok){ notify('HTTP '+r.status+' '+r.statusText); return; }
      notify('Версия '+ver+' удалена');
      manifestsReload();
      // refresh preview version list if this game is selected
      const curGid = (document.getElementById('gid')?.value||'').trim();
      if(curGid){ gmPrevEnsureVersionsAndRender(curGid); }
    });
  });
}

async function manifestsUpload(){
  const gid = (document.getElementById('gid')?.value||'').trim();
  const ver = (document.getElementById('ver')?.value||'').trim();
  if(!gid){ notify('Укажите идентификатор игры'); return; }
  if(!ver){ notify('Укажите версию'); return; }
  const file = (window.__manDroppedFile)||document.getElementById('man_zip')?.files?.[0];
  if(!file){ notify('Выберите ZIP-файл'); return; }
  const latest = (document.getElementById('man_latest')?.checked) ? '1':'0';
  const fd = new FormData();
  fd.append('kind','game'); fd.append('gameId', gid); fd.append('version', ver); fd.append('zip', file); fd.append('updateLatest', latest);
  const wrap=document.getElementById('man_prog_wrap'); const bar=document.getElementById('man_pb'); const txt=document.getElementById('man_prog_text');
  if(wrap) wrap.style.display='block'; if(bar) bar.style.width='0%'; if(txt) txt.textContent='Подготовка к загрузке...';

  await new Promise((resolve)=>{
    const xhr = new XMLHttpRequest(); xhr.open('POST','/admin/uploadStream');
    xhr.setRequestHeader('Accept','application/x-ndjson');
    // Upload progress
    xhr.upload.onprogress = (e)=>{
      if(e.lengthComputable){
        const pct = Math.floor(e.loaded*100/e.total);
        if(bar) bar.style.width=pct+'%';
        if(txt) txt.textContent='Загружено '+pct+'% ('+e.loaded+' / '+e.total+' байт)';
      }
    };
    // Streaming NDJSON parsing from response
    let lastLen = 0;
    xhr.onprogress = ()=>{
      const resp = xhr.responseText || '';
      const chunk = resp.substring(lastLen);
      lastLen = resp.length;
      const lines = chunk.split(/\r?\n/).filter(Boolean);
      for(const line of lines){
        try{
          const ev = JSON.parse(line);
          if(ev.type === 'start'){
            if(txt) txt.textContent = 'Старт обработки: '+(ev.gameId||gid)+' '+(ev.version||ver);
          } else if(ev.type === 'zipSaved'){
            if(txt) txt.textContent = 'Загрузка завершена, обработка ZIP ('+formatBytes(ev.bytes||0)+')...';
            if(bar) bar.style.width='100%';
          } else if(ev.type === 'unzip'){
            if(txt) txt.textContent = 'Распаковка: '+ev.path;
          } else if(ev.type === 'composeStart'){
            if(txt) txt.textContent = 'Подготовка манифеста: 0/'+(ev.totalFiles||0)+' файлов';
          } else if(ev.type === 'file'){
            if(txt) txt.textContent = 'Манифест: '+(ev.idx||0)+' файлов, '+formatBytes(ev.bytesDone||0);
          } else if(ev.type === 'done'){
            if(txt) txt.textContent = 'Готово. Манифест записан';
            // refresh versions list and preview immediately
            try{ manifestsReload(); }catch(_){}
            try{ gmPrevEnsureVersionsAndRender(gid); }catch(_){}
          } else if(ev.type === 'error'){
            notify('Ошибка: '+(ev.message||'unknown'));
          }
        }catch(_){ /* ignore JSON parse errors for partial lines */ }
      }
    };
    xhr.onreadystatechange = ()=>{
      if(xhr.readyState===4){
        if(xhr.status>=200 && xhr.status<300){
          try{ lnRefresh(); }catch(_){ }
          try{ manifestsReload(); }catch(_){ }
          try{ gmPrevEnsureVersionsAndRender(gid); }catch(_){ }
        } else {
          notify('HTTP '+xhr.status+' '+xhr.statusText+' '+(xhr.responseText||''));
        }
        window.__manDroppedFile=null; resolve();
      }
    };
    xhr.onerror = ()=>{ notify('Ошибка загрузки'); window.__manDroppedFile=null; resolve(); };
    xhr.send(fd);
  });
}

// Drag-n-drop for manifests ZIP
(function(){
  const dz = document.getElementById('man_drop'); if(!dz) return;
  ['dragenter','dragover'].forEach(ev=> dz.addEventListener(ev, (e)=>{ e.preventDefault(); e.stopPropagation(); dz.classList.add('border-primary'); }));
  ['dragleave','drop'].forEach(ev=> dz.addEventListener(ev, (e)=>{ e.preventDefault(); e.stopPropagation(); dz.classList.remove('border-primary'); }));
  dz.addEventListener('drop', (e)=>{
    const files = e.dataTransfer && e.dataTransfer.files ? e.dataTransfer.files : null; if(!files||files.length===0) return;
    const f = files[0]; if(!/\.zip$/i.test(f.name)){ notify('Ожидается ZIP-файл'); return; }
    window.__manDroppedFile = f;
    const wrap=document.getElementById('man_prog_wrap'); const bar=document.getElementById('man_pb'); const txt=document.getElementById('man_prog_text');
    if(wrap) wrap.style.display='block'; if(bar) bar.style.width='0%'; if(txt) txt.textContent = 'Выбран файл: '+f.name+' ('+f.size+' байт)';
  });
})();

// ==== Manifests page: editable games list (mgm_*) ====
async function mgmReload(){
  let res; try{ res = await fetch('/admin/games'); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  let j; try{ j = await res.json(); }catch(e){ notify('Ошибка парсинга'); return; }
  const tb = document.querySelector('#mgm_table tbody'); if(!tb) return;
  tb.innerHTML = '';
  (j.items||[]).forEach(it=> mgmAppendRow(tb, it));
  // restore selection according to current gid input
  const curGid = (document.getElementById('gid')?.value||'').trim().toLowerCase();
  if(curGid){
    const rows = Array.from(tb.querySelectorAll('tr'));
    for(const r of rows){
      const id = r.querySelectorAll('td')[0].querySelector('input').value.trim().toLowerCase();
      if(id===curGid){ r.classList.add('mgm-selected'); break; }
    }
  }
}

function mgmAppendRow(tb, it){
  const tr = document.createElement('tr');
  const gidVal = (it.gameId||'');
  const titleVal = (it.title||'');
  const exeVal = (it.exeRelativePath||'');
  const iconVal = (it.iconUrl||'');
  tr.innerHTML = ''+
    '<td><input class="form-control form-control-sm" value="'+gidVal+'"/></td>'+
    '<td><input class="form-control form-control-sm" value="'+titleVal+'"/></td>'+
    '<td>'+
      '<div class="input-group input-group-sm">'+
        '<input class="form-control mgm-icon" value="'+iconVal+'" placeholder="/manifests/<gameId>/icon.png"/>'+
        '<button type="button" class="btn btn-outline-secondary mgm-icon-upload" title="Загрузить иконку">Загрузить...</button>'+
        '<button type="button" class="btn btn-outline-secondary mgm-icon-default" title="Установить путь по умолчанию">По умолчанию</button>'+
      '</div>'+
    '</td>'+
    '<td>'+
      '<div class="input-group input-group-sm">'+
        '<input class="form-control mgm-exe" value="'+exeVal+'" placeholder="relative\\path\\to\\game.exe" />'+
        '<button type="button" class="btn btn-outline-secondary mgm-pick">Выбрать...</button>'+
      '</div>'+
    '</td>'+
    '<td class="text-end">'+
      '<div class="btn-group btn-group-sm me-2" role="group">'+
        '<button type="button" class="btn btn-outline-secondary mgm-up" title="Вверх">▲</button>'+
        '<button type="button" class="btn btn-outline-secondary mgm-down" title="Вниз">▼</button>'+
      '</div>'+
      '<button type="button" class="btn btn-sm btn-outline-danger mgm-del" title="Удалить игру">Удалить</button>'+
    '</td>';
  // clicking row selects game in the upload panel
  tr.addEventListener('click', (ev)=>{
    const id = tr.querySelectorAll('td')[0].querySelector('input').value.trim();
    const gid = document.getElementById('gid'); if(gid){ gid.value = id; }
    // sync left selector and label
    const gmSel = document.getElementById('gm_select'); if(gmSel){ gmSel.value = id; }
    const lab = document.getElementById('gm_current_id'); if(lab){ lab.textContent = id || '—'; }
    // toggle visual selection
    document.querySelectorAll('#mgm_table tbody tr.mgm-selected').forEach(el=> el.classList.remove('mgm-selected'));
    tr.classList.add('mgm-selected');
    // refresh preview for this game
    gmPrevEnsureVersionsAndRender(id);
    // also refresh versions list to avoid stale list from previous game
    manifestsReload();
  });
  // bind picker button
  const btnPick = tr.querySelector('button.mgm-pick');
  const exeInput = tr.querySelector('input.mgm-exe');
  btnPick?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const gid = tr.querySelectorAll('td')[0].querySelector('input').value.trim();
    if(!gid){ notify('Укажите идентификатор игры в строке'); return; }
    openExePicker(gid, exeInput);
  });
  // bind icon default
  const iconInput = tr.querySelector('input.mgm-icon');
  const btnIconDefault = tr.querySelector('button.mgm-icon-default');
  btnIconDefault?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const gid = tr.querySelectorAll('td')[0].querySelector('input').value.trim();
    if(!gid){ notify('Сначала заполните gameId'); return; }
    if(iconInput){ iconInput.value = '/manifests/' + gid + '/icon.png'; }
  });
  // bind icon upload
  const btnIconUpload = tr.querySelector('button.mgm-icon-upload');
  btnIconUpload?.addEventListener('click', async (ev)=>{
    ev.stopPropagation();
    const gid = tr.querySelectorAll('td')[0].querySelector('input').value.trim();
    if(!gid){ notify('Сначала заполните gameId'); return; }
    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = 'image/*';
    fileInput.onchange = async ()=>{
      const f = fileInput.files && fileInput.files[0];
      if(!f){ return; }
      const fd = new FormData(); fd.append('gameId', gid); fd.append('file', f);
      let res;
      try{ res = await fetch('/admin/games/icon/upload', { method:'POST', body: fd }); }
      catch(e){ notify('Ошибка загрузки: '+e); return; }
      if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
      let j; try{ j = await res.json(); }catch(e){ notify('Плохой JSON'); return; }
      if(j && j.url && iconInput){ iconInput.value = j.url; notify('Иконка обновлена: '+j.url); }
    };
    fileInput.click();
  });
  // bind delete button
  const delBtn = tr.querySelector('button.mgm-del');
  delBtn?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const id = tr.querySelectorAll('td')[0].querySelector('input').value.trim();
    if(!id){
      // empty row: remove silently
      tr.remove();
      return;
    }
    if(confirm('Удалить игру «'+id+'» из списка?\nФайлы манифестов НЕ удаляются. Изменения применятся после нажатия «Сохранить».')){
      tr.remove();
      notify('Игра '+id+' помечена на удаление. Нажмите «Сохранить» для применения.');
    }
  });
  tb.appendChild(tr);

  // bind reorder buttons
  const upBtn = tr.querySelector('button.mgm-up');
  const downBtn = tr.querySelector('button.mgm-down');
  upBtn?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const row = tr;
    const prev = row.previousElementSibling;
    if(prev){ row.parentNode.insertBefore(row, prev); }
  });
  downBtn?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const row = tr;
    const next = row.nextElementSibling;
    if(next){ row.parentNode.insertBefore(next, row); }
  });
}

function mgmAddRow(){
  const tb = document.querySelector('#mgm_table tbody'); if(!tb) return;
  mgmAppendRow(tb, {gameId:'', title:'', exeRelativePath:'', iconUrl:''});
}

async function mgmSave(){
  const rows = Array.from(document.querySelectorAll('#mgm_table tbody tr'));
  const items = rows.map(tr=>{
    const tds = tr.querySelectorAll('td');
    return {
      gameId: tds[0].querySelector('input').value.trim(),
      title: tds[1].querySelector('input').value.trim(),
      iconUrl: tds[2].querySelector('input').value.trim(),
      exeRelativePath: tds[3].querySelector('input').value.trim()
    };
  }).filter(it=>it.gameId);
  // basic validation
  const ids = new Set();
  for(const it of items){ if(!it.gameId){ notify('Пустой gameId'); return; } if(ids.has(it.gameId)){ notify('Дубликат gameId: '+it.gameId); return; } ids.add(it.gameId); }
  let res; try{ res = await fetch('/admin/games/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({items}) }); }catch(e){ notify('Ошибка сохранения: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  notify(await res.text());
  mgmReload();
}

async function mgmScanMissing(){
  notify('Сканирование директорий манифестов...');
  let res; try{ res = await fetch('/admin/games/scan'); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  const j = await res.json();
  const tb = document.querySelector('#mgm_table tbody'); if(!tb){ notify('Таблица игр не найдена'); return; }
  const before = Array.from(tb.querySelectorAll('tr')).length;
  const existing = new Set(Array.from(tb.querySelectorAll('tr')).map(tr=> tr.querySelectorAll('td')[0].querySelector('input').value.trim()));
  (j.items||[]).forEach(it=>{ if(existing.has(it.gameId)) return; mgmAppendRow(tb, it); });
  const after = Array.from(tb.querySelectorAll('tr')).length;
  notify('Сканирование завершено. Добавлено: '+(after-before));
  // Перечитываем список с сервера, чтобы отобразить каноническое состояние реестра
  await mgmReload();
}

// Combined resync: scan -> save -> reload from server
async function mgmResync(){
  notify('Обновление списка игр: добавление недостающих...');
  // fetch current registry
  let curRes; try{ curRes = await fetch('/admin/games'); }catch(e){ notify('Ошибка запроса текущего реестра: '+e); return; }
  if(!curRes.ok){ notify('HTTP '+curRes.status+' '+curRes.statusText); return; }
  const cur = await curRes.json();
  const curItems = Array.isArray(cur?.items) ? cur.items : [];
  const existing = new Set(curItems.map(it=> (it.gameId||'').toLowerCase()).filter(Boolean));
  // fetch scanned games (server already excludes служебные папки)
  let scanRes; try{ scanRes = await fetch('/admin/games/scan'); }catch(e){ notify('Ошибка сканирования: '+e); return; }
  if(!scanRes.ok){ notify('HTTP '+scanRes.status+' '+scanRes.statusText); return; }
  const scan = await scanRes.json();
  const additions = [];
  (scan.items||[]).forEach(it=>{
    const id = String(it.gameId||'').toLowerCase();
    if(!id || existing.has(id)) return;
    additions.push(it);
  });
  if(additions.length===0){ notify('Новых игр не найдено. Текущий список без изменений.'); await mgmReload(); return; }
  const merged = curItems.concat(additions);
  // save merged registry
  let saveRes; try{ saveRes = await fetch('/admin/games/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({items: merged}) }); }catch(e){ notify('Ошибка сохранения: '+e); return; }
  if(!saveRes.ok){ notify('HTTP '+saveRes.status+' '+saveRes.statusText); return; }
  notify(await saveRes.text());
  await mgmReload();
}

window.addEventListener('error', function(e){ var o=document.getElementById('out'); o.textContent += ('\n[JS] Ошибка: '+e.message); });

// Current cover URL (tracked separately; not shown as a comment inside editor)
let currentCoverUrl = '';
// Current published state kept in memory when checkbox is absent
let currentPublished = false;

function notify(msg){ var o=document.getElementById('out'); if(o) o.textContent = msg; }

// ===== Games: Preview and EXE picker (global) =====
async function gmPrevEnsureVersionsAndRender(gameId){
  try{
    const sel = document.getElementById('gm_prev_ver'); const tree = document.getElementById('gm_prev_tree');
    if(!sel || !tree) return;
    sel.innerHTML = '';
    let res = await fetch('/admin/list?gameId=' + encodeURIComponent(gameId));
    if(!res.ok){ tree.textContent = 'HTTP '+res.status; return; }
    const j = await res.json();
    const latest = j.latest || '';
    const items = Array.isArray(j.items)? j.items: [];
    items.forEach(it=>{
      const opt = document.createElement('option'); opt.value = it.version; opt.textContent = it.version; sel.appendChild(opt);
    });
    if(latest){ sel.value = latest; }
    await gmPrevRender(gameId, sel.value||'');
  }catch(e){ const tree=document.getElementById('gm_prev_tree'); if(tree) tree.textContent='Ошибка: '+e; }
}

async function gmPrevRender(gameId, version){
  const tree = document.getElementById('gm_prev_tree'); if(!tree) return;
  if(!gameId || !version){ tree.textContent = 'Выберите игру и версию'; return; }
  tree.innerHTML = '<span class="text-body-secondary">Загрузка манифеста...</span>';
  try{
    const r = await fetch('/manifests/'+encodeURIComponent(gameId)+'/'+encodeURIComponent(version)+'.json');
    if(!r.ok){ tree.textContent = 'HTTP '+r.status; return; }
    const manifest = await r.json();
    lnRenderTree(tree, manifest);
  }catch(e){ tree.textContent = 'Ошибка: '+e; }
}

// (stub removed; lnManifestsReload is defined earlier in the file)

// Bind refresh and change for preview widgets
document.addEventListener('DOMContentLoaded', function(){
  // Initial load for Launcher tab: populate latest badge and manifest tree
  try{ lnRefresh(); }catch(_){}
  // Initial load for launcher versions selector and list
  try{ lnPrevEnsureVersionsAndRender(); }catch(_){}
  try{ ensureLauncherVersionsCard(); }catch(_){}
  try{ lnManifestsReload(); }catch(_){}
  const btn = document.getElementById('gm_prev_refresh');
  if(btn){ btn.addEventListener('click', ()=>{ const gid=(document.getElementById('gid')?.value||'').trim(); if(!gid){ notify('Укажите игру'); return; } gmPrevEnsureVersionsAndRender(gid); }); }
  const sel = document.getElementById('gm_prev_ver');
  if(sel){ sel.addEventListener('change', ()=>{ const gid=(document.getElementById('gid')?.value||'').trim(); const ver = document.getElementById('gm_prev_ver').value; if(!gid||!ver) return; gmPrevRender(gid, ver); }); }
  // refresh versions and preview when game id changed manually
  const gidInput = document.getElementById('gid');
  if(gidInput){
    const onChange = ()=>{ const gid=(gidInput.value||'').trim(); if(!gid) return; manifestsReload(); gmPrevEnsureVersionsAndRender(gid); };
    gidInput.addEventListener('change', onChange);
    gidInput.addEventListener('blur', onChange);
  }

  // === Game selector (gm_select) ===
  async function gmSelectReload(preserve){
    const sel = document.getElementById('gm_select'); if(!sel) return;
    sel.innerHTML = '';
    // fetch games from admin registry
    let res; try{ res = await fetch('/admin/games'); }catch(e){ notify('Ошибка загрузки игр: '+e); return; }
    if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
    let j; try{ j = await res.json(); }catch(e){ notify('Ошибка JSON игр'); return; }
    const items = Array.isArray(j.items)? j.items: [];
    // sort by title (fallback gameId)
    items.sort((a,b)=> (String(a.title||a.gameId||'').localeCompare(String(b.title||b.gameId||''))));
    // fill options
    const curGid = preserve ? (document.getElementById('gid')?.value||'').trim() : '';
    for(const it of items){
      const id = String(it.gameId||'').trim(); if(!id) continue;
      const title = String(it.title||id);
      const opt = document.createElement('option');
      opt.value = id; opt.textContent = title + ' ('+id+')';
      sel.appendChild(opt);
    }
    // select current if exists, else first
    if(curGid && Array.from(sel.options).some(o=> o.value===curGid)) sel.value = curGid;
    if(!sel.value && sel.options.length>0) sel.value = sel.options[0].value;
    // mirror to hidden #gid and label
    const chosen = sel.value||'';
    const gidEl = document.getElementById('gid'); if(gidEl) gidEl.value = chosen;
    const lab = document.getElementById('gm_current_id'); if(lab) lab.textContent = chosen || '—';
    if(chosen){ manifestsReload(); gmPrevEnsureVersionsAndRender(chosen); }
  }
  // initial load
  gmSelectReload(true);
  // bind refresh button
  const selBtn = document.getElementById('gm_select_refresh');
  if(selBtn){ selBtn.addEventListener('click', ()=> gmSelectReload(true)); }
  // change handler
  const gmSelect = document.getElementById('gm_select');
  if(gmSelect){ gmSelect.addEventListener('change', ()=>{
    const chosen = gmSelect.value||'';
    const gidEl = document.getElementById('gid'); if(gidEl) gidEl.value = chosen;
    const lab = document.getElementById('gm_current_id'); if(lab) lab.textContent = chosen || '—';
    if(chosen){ manifestsReload(); gmPrevEnsureVersionsAndRender(chosen); }
    // highlight corresponding row in mgm_table
    const rows = Array.from(document.querySelectorAll('#mgm_table tbody tr'));
    rows.forEach(r=> r.classList.remove('mgm-selected'));
    for(const r of rows){
      const idCell = r.querySelectorAll('td')[0];
      const idVal = idCell?.querySelector('input')?.value?.trim() || '';
      if(idVal && idVal.toLowerCase() === chosen.toLowerCase()){ r.classList.add('mgm-selected'); r.scrollIntoView({block:'nearest'}); break; }
    }
  }); }
});

async function openExePicker(gameId, targetInput){
  try{
    let res = await fetch('/admin/list?gameId='+encodeURIComponent(gameId)); if(!res.ok){ notify('HTTP '+res.status); return; }
    const j = await res.json(); const latest = j.latest || ((j.items||[])[0]?.version||'');
    if(!latest){ notify('Нет доступных версий для '+gameId); return; }
    let manRes = await fetch('/manifests/'+encodeURIComponent(gameId)+'/'+encodeURIComponent(latest)+'.json');
    if(!manRes.ok){ notify('HTTP '+manRes.status); return; }
    const manifest = await manRes.json();
    const files = Array.isArray(manifest?.files)? manifest.files: [];
    const exeFiles = files.map(f=> String(f.path||'')).filter(p=> /\.exe$/i.test(p));
    const el = document.createElement('div'); el.className='modal fade'; el.tabIndex=-1;
    const list = exeFiles.map(p=> '<li class="list-group-item list-group-item-action" data-p="'+p+'"><code>'+escapeHtml(p)+'</code></li>').join('') || '<li class="list-group-item">.exe не найдены</li>';
    el.innerHTML = '\n<div class="modal-dialog"><div class="modal-content">\n  <div class="modal-header"><h5 class="modal-title">Выбор исполняемого файла для '+escapeHtml(gameId)+'</h5><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>\n  <div class="modal-body"><ul class="list-group">'+list+'</ul></div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Закрыть</button></div>\n</div></div>';
    document.body.appendChild(el);
    const modal = window.bootstrap ? new window.bootstrap.Modal(el) : null; if(modal) modal.show();
    el.querySelectorAll('li[data-p]').forEach(li=> li.addEventListener('click', ()=>{ const p = li.getAttribute('data-p'); if(p && targetInput){ targetInput.value = p; } if(modal) modal.hide(); }));
    el.addEventListener('hidden.bs.modal', ()=>{ el.remove(); });
  }catch(e){ notify('Ошибка: '+e); }
}

// ===== Upload helpers (extensions, conflict resolution) =====
function guessOutExtFromUrl(url){
  try{
    const u = new URL(url, window.location.origin);
    const p = u.pathname || '';
    const ext = (p.split('/').pop()||'').split('?')[0].split('#')[0].match(/\.[a-zA-Z0-9]+$/);
    const e = ext ? ext[0].toLowerCase() : '';
    if(['.jpg','.jpeg','.png','.gif','.webp'].includes(e)){
      // normalize jpeg to .jpg
      return (e==='.jpeg')? '.jpg' : e;
    }
  }catch(e){ /* ignore */ }
  return '.jpg';
}

function guessOutExtFromFile(name){
  const m = /\.[a-zA-Z0-9]+$/.exec(name||'');
  const e = m ? m[0].toLowerCase() : '';
  if(e==='.jpeg') return '.jpg';
  return e || '.jpg';
}

async function resolveNameWithMode(path, base, ext, mode){
  // sanitize base (no extension)
  base = (base||'image').replace(/\.[^.]+$/, '').trim() || 'image';
  ext = (ext||'').trim(); if(!/^\.[a-z0-9]+$/i.test(ext)) ext = '.jpg';
  const desired = base + ext;
  if((mode||'rename') === 'overwrite') return desired;
  // fetch directory listing to check conflicts
  let res; try{ res = await fetch('/admin/news/assets?path=' + encodeURIComponent(path||'')); }catch(e){ return desired; }
  if(!res.ok){ return desired; }
  let j; try{ j = await res.json(); }catch(e){ return desired; }
  const existing = new Set(((j.items)||[]).filter(it=>!it.isDir).map(it=> String(it.name||'').toLowerCase()));
  if(!existing.has(desired.toLowerCase())) return desired;
  // find available suffix -2, -3, ...
  for(let i=2;i<10000;i++){
    const cand = base + '-' + i + ext;
    if(!existing.has(cand.toLowerCase())) return cand;
  }
  return Date.now().toString() + ext; // last resort
}

// ===== Upload by URL dialog =====
function openUrlUploadDialog(mode){
  const el = document.createElement('div');
  el.className = 'modal fade'; el.tabIndex = -1;
  el.innerHTML = '\n<div class="modal-dialog modal-lg"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Загрузка по URL</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="url_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n      <div class="input-group input-group-sm ms-auto" style="max-width:520px">\n        <span class="input-group-text">URL</span>\n        <input id="url_input" class="form-control" placeholder="https://..."/>\n      </div>\n    </div>\n    <div class="d-flex align-items-center gap-2 mt-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="url_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-2">\n      <div class="col-12 col-md-7">\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="url_path" placeholder="относительно assets" value="'+(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="url_name" value="image"/>\n        </div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button> <button id="url_ok" type="button" class="btn btn-primary">Сохранить</button></div>\n</div></div>';
  document.body.appendChild(el);
  const modal = window.bootstrap ? new window.bootstrap.Modal(el) : null; if(modal) modal.show();
  const sel = el.querySelector('#url_target'); if(sel){ sel.value = (mode==='cover') ? 'cover' : 'inline'; }
  el.querySelector('#url_ok').addEventListener('click', async ()=>{
    const url = (el.querySelector('#url_input').value||'').trim(); if(!url){ alert('Укажите URL'); return; }
    const path = (el.querySelector('#url_path').value||'').replace(/^\/+|\/+$/g,'');
    const name = el.querySelector('#url_name').value || 'image';
    const modeSel = el.querySelector('#url_overwrite')?.value || 'rename';
    const ext = guessOutExtFromUrl(url);
    const finalName = await resolveNameWithMode(path, name, ext, modeSel);
    const fd = new URLSearchParams(); fd.set('path', path); fd.set('filename', finalName); fd.set('url', url);
    let res; try{ res = await fetch('/admin/news/assets/uploadByUrl', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString()}); }catch(e){ alert('Ошибка: '+e); return; }
    if(!res.ok){ alert('HTTP '+res.status); return; }
    const j = await res.json(); if(!j || !j.url){ alert('Не удалось сохранить'); return; }
    const target = sel.value || 'inline';
    if(target==='inline'){
      const ta = document.getElementById('ns_md'); insertAtCursor(ta, '![image]('+j.url+')'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
    } else {
      setCoverInMarkdown(j.url); const ta=document.getElementById('ns_md'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
    }
    if(modal) modal.hide(); setTimeout(()=>{ el.remove(); }, 300);
  });
  el.addEventListener('hidden.bs.modal', ()=>{ el.remove(); });
}

// ===== File-pick dialog (like paste, but lets you choose a local file) =====
function openPickUploadDialog(mode){
  const el = document.createElement('div');
  el.className = 'modal fade'; el.tabIndex = -1;
  el.innerHTML = '\n<div class="modal-dialog modal-xl"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Загрузка изображения</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="pick_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n      <div class="ms-auto small">Файл: <input id="pick_file" type="file" accept="image/*" /></div>\n    </div>\n    <div class="d-flex align-items-center gap-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="pick_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-3">\n      <div class="col-lg-6">\n        <div style="position:sticky; top:8px">\n          <div id="pick_prev_wrap" class="border rounded d-flex align-items-center justify-content-center" style="min-height:240px;">\n            <div class="text-body-secondary">Выберите файл</div>\n          </div>\n        </div>\n      </div>\n      <div class="col-lg-6">\n        <div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">\n          <nav id="pick_breadcrumbs" class="small text-body-secondary"></nav>\n          <div class="btn-group btn-group-sm">\n            <button id="pick_mkdir" type="button" class="btn btn-outline-success">Новая папка</button>\n          </div>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="pick_path" placeholder="относительно /news/assets" value="'+(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="pick_name" value="image"/>\n        </div>\n        <div id="pick_grid" class="row g-2"></div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button><button type="button" class="btn btn-primary" id="pick_ok" disabled>Загрузить</button></div>\n</div></div>';
  document.body.appendChild(el);
  const modal = window.bootstrap ? new window.bootstrap.Modal(el) : null;
  if(modal) modal.show();
  // defaults
  const sel = el.querySelector('#pick_target'); if(sel){ sel.value = (mode==='cover') ? 'cover' : 'inline'; }
  const fileInput = el.querySelector('#pick_file');
  const prevWrap = el.querySelector('#pick_prev_wrap');
  const okBtn = el.querySelector('#pick_ok');
  const nameInput = el.querySelector('#pick_name');
  // preview update
  let chosenFile = null; let nameTouched = false;
  nameInput.addEventListener('input', ()=>{ nameTouched = true; });
  fileInput.addEventListener('change', ()=>{
    const f = fileInput.files && fileInput.files[0]; chosenFile = f||null;
    prevWrap.innerHTML=''; if(f){ const u=URL.createObjectURL(f); const img=document.createElement('img'); img.src=u; img.className='img-fluid'; prevWrap.appendChild(img); okBtn.disabled=false; if(!nameTouched){ nameInput.value = (f.name||'image').replace(/\.[^.]+$/, ''); } }
    else { prevWrap.innerHTML='<div class="text-body-secondary">Выберите файл</div>'; okBtn.disabled=true; }
  });
  // mini gallery state
  let pickPath = (galleryPath||'') || '';
  const bc = el.querySelector('#pick_breadcrumbs');
  const grid = el.querySelector('#pick_grid');
  const pathInput = el.querySelector('#pick_path');
  function renderPickBreadcrumbs(){
    const segs = pickPath? pickPath.split('/') : [];
    const parts = ['<a href="#" data-p="" class="text-decoration-none">assets</a>'];
    let acc = '';
    segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+acc+'" class="text-decoration-none">'+s+'</a>'); });
    bc.innerHTML = parts.join('');
    bc.querySelectorAll('a').forEach(a=> a.addEventListener('click', (e)=>{ e.preventDefault(); const p=e.currentTarget.getAttribute('data-p'); pickPath=p||''; pathInput.value=pickPath; fetchPickList(); }));
  }
  async function fetchPickList(){
    renderPickBreadcrumbs();
    grid.innerHTML = '<div class="text-body-secondary">Загрузка...</div>';
    let url = '/admin/news/assets?path=' + encodeURIComponent(pickPath);
    let res; try{ res = await fetch(url); }catch(e){ grid.innerHTML = '<div class="text-danger">Ошибка загрузки</div>'; return; }
    if(!res.ok){ grid.innerHTML = '<div class="text-danger">HTTP '+res.status+'</div>'; return; }
    let j; try{ j = await res.json(); }catch(e){ grid.innerHTML = '<div class="text-danger">Плохой JSON</div>'; return; }
    pickPath = j.path || pickPath; pathInput.value = pickPath;
    renderPickGrid(j.items||[]);
  }
  function renderPickGrid(items){
    if(!items || items.length===0){ grid.innerHTML = '<div class="text-body-secondary">Пусто</div>'; return; }
    grid.innerHTML = '';
    items.forEach(it=>{
      const col = document.createElement('div'); col.className='col-6 col-sm-4';
      const card = document.createElement('div'); card.className='card h-100 d-flex flex-column'; card.style.cursor='pointer';
      if(it.isDir){
        // Folder thumbnail like preview (dark bg + white SVG, larger)
        const thumb = document.createElement('div'); thumb.className='card-img-top d-flex align-items-center justify-content-center'; thumb.style.height='140px'; thumb.style.background='#212529'; thumb.innerHTML='\
<svg width="84" height="84" viewBox="0 0 24 24" fill="#fff" xmlns="http://www.w3.org/2000/svg">\
  <path d="M9.5 4H4a2 2 0 0 0-2 2v12c0 1.1.9 2 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8.5l-2-2z"/>\
</svg>';
        card.appendChild(thumb);
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate fw-semibold'; cap.textContent = it.name; cap.style.cursor='pointer';
        cap.addEventListener('click', ()=>{ pickPath = pickPath? (pickPath+'/'+it.name): it.name; pathInput.value=pickPath; fetchPickList(); });
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
        rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pickPath||'', from: it.name, to: nn}).toString()}); fetchPickList(); };
        const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
        del.onclick=async()=>{ if(!confirm('Удалить папку '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pickPath||'', name: it.name}).toString()}); fetchPickList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
        // Make the whole folder card clickable (except action buttons)
        card.addEventListener('click', (e)=>{ if(e.target.closest && e.target.closest('button')) return; pickPath = pickPath? (pickPath+'/'+it.name): it.name; pathInput.value=pickPath; fetchPickList(); });
      } else {
        if(it.url){ const img=document.createElement('img'); img.className='card-img-top'; img.src=it.url; img.alt=it.name; img.style.height='100px'; img.style.objectFit='cover'; card.appendChild(img); }
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name;
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
        rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pickPath||'', from: it.name, to: nn}).toString()}); fetchPickList(); };
        const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
        del.onclick=async()=>{ if(!confirm('Удалить файл '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pickPath||'', name: it.name}).toString()}); fetchPickList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      }
      col.appendChild(card); grid.appendChild(col);
    });
  }
  // mkdir
  el.querySelector('#pick_mkdir').addEventListener('click', async ()=>{
    const name = prompt('Имя новой папки:'); if(!name) return;
    const fd = new URLSearchParams(); fd.set('path', pickPath||''); fd.set('name', name);
    let res; try{ res = await fetch('/admin/news/assets/mkdir', { method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString() }); }catch(e){ return; }
    if(res && res.ok){ fetchPickList(); }
  });
  // sync manual edits
  pathInput.addEventListener('change', ()=>{ pickPath = (pathInput.value||'').replace(/^\/+|\/+$/g,''); fetchPickList(); });
  // initial load
  fetchPickList();
  // upload action
  el.querySelector('#pick_ok').addEventListener('click', async ()=>{
    if(!chosenFile){ alert('Выберите файл'); return; }
    const path = (el.querySelector('#pick_path').value || '').replace(/^\/+|\/+$/g,'');
    const name = el.querySelector('#pick_name').value || 'image';
    const target = el.querySelector('#pick_target')?.value || 'inline';
    const mode = el.querySelector('#pick_overwrite')?.value || 'rename';
    const finalName = await resolveNameWithMode(path, name, guessOutExtFromFile(chosenFile?.name||''), mode);
    const j = await uploadAssetFile(chosenFile, {path, filename:finalName});
    if(j && j.url){
      if(target==='inline'){
        const ta = document.getElementById('ns_md'); insertAtCursor(ta, '![image]('+j.url+')'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; ta.dispatchEvent(new Event('input'));
      } else {
        setCoverInMarkdown(j.url); const ta = document.getElementById('ns_md'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
      }
    }
    if(modal) modal.hide(); setTimeout(()=>{ el.remove(); }, 300);
  });
  el.addEventListener('hidden.bs.modal', ()=>{ el.remove(); });
}

// ===== Insert cover image from file (new flow via assets upload) =====
async function newsInsertCoverFromFile(){
  const f = document.getElementById('ns_cover')?.files?.[0];
  if(!f){ alert('Выберите изображение'); return; }
  const inName = f.name || 'cover';
  const suggested = inName.replace(/\.[^.]+$/, '');
  const dest = await chooseAssetDestination(galleryPath||'', suggested);
  if(!dest) return;
  const j = await uploadAssetFile(f, dest); if(!j){ return; }
  const url = j.url || '';
  if(!url){ notify('Не удалось получить URL изображения'); return; }
  setCoverInMarkdown(url);
  const ta = document.getElementById('ns_md'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty = true; if(ta) ta.dispatchEvent(new Event('input'));
}

// ===== Paste handler with preview modal for inline/cover =====
document.addEventListener('paste', function(e){
  if(!e.clipboardData) return;
  const items = e.clipboardData.items || [];
  for(const it of items){
    if(it.kind==='file'){
      const file = it.getAsFile(); if(!file) continue;
      const mode = (document.activeElement && document.activeElement.id==='ns_md') ? 'inline' : 'cover';
      e.preventDefault();
      openPasteUploadDialog(file, mode);
      break;
    }
  }
});

function openPasteUploadDialog(file, mode){
  const url = URL.createObjectURL(file);
  const el = document.createElement('div');
  el.className = 'modal fade'; el.tabIndex = -1;
  el.innerHTML = '\n<div class="modal-dialog modal-xl"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Вставка изображения</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="paste_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n    </div>\n    <div class="d-flex align-items-center gap-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="paste_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-3">\n      <div class="col-lg-6">\n        <div style="position:sticky; top:8px">\n          <img src="'+url+'" alt="preview" class="img-fluid border rounded"/>\n        </div>\n      </div>\n      <div class="col-lg-6">\n        <div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">\n          <nav id="paste_breadcrumbs" class="small text-body-secondary"></nav>\n          <div class="btn-group btn-group-sm">\n            <button id="paste_mkdir" type="button" class="btn btn-outline-success">Новая папка</button>\n          </div>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="paste_path" placeholder="относительно /news/assets" value="'+(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="paste_name" value="'+(file.name||'image').replace(/\.[^.]+$/, '')+'"/>\n        </div>\n        <div id="paste_grid" class="row g-2"></div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button><button type="button" class="btn btn-primary" id="paste_ok">Загрузить</button></div>\n</div></div>';
  document.body.appendChild(el);
  const modal = window.bootstrap ? new window.bootstrap.Modal(el) : null;
  if(modal) modal.show();
  // default dropdown based on detected mode
  const sel = el.querySelector('#paste_target'); if(sel){ sel.value = (mode==='cover') ? 'cover' : 'inline'; }
  // mini-gallery state & functions
  let pastePath = (galleryPath||'') || '';
  const bc = el.querySelector('#paste_breadcrumbs');
  const grid = el.querySelector('#paste_grid');
  const pathInput = el.querySelector('#paste_path');
  function renderPasteBreadcrumbs(){
    const segs = pastePath? pastePath.split('/') : [];
    const parts = ['<a href="#" data-p="" class="text-decoration-none">assets</a>'];
    let acc = '';
    segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+acc+'" class="text-decoration-none">'+s+'</a>'); });
    bc.innerHTML = parts.join('');
    bc.querySelectorAll('a').forEach(a=> a.addEventListener('click', (e)=>{ e.preventDefault(); const p=e.currentTarget.getAttribute('data-p'); pastePath=p||''; pathInput.value=pastePath; fetchPasteList(); }));
  }
  async function fetchPasteList(){
    renderPasteBreadcrumbs();
    grid.innerHTML = '<div class="text-body-secondary">Загрузка...</div>';
    let url = '/admin/news/assets?path=' + encodeURIComponent(pastePath);
    let res; try{ res = await fetch(url); }catch(e){ grid.innerHTML = '<div class="text-danger">Ошибка загрузки</div>'; return; }
    if(!res.ok){ grid.innerHTML = '<div class="text-danger">HTTP '+res.status+'</div>'; return; }
    let j; try{ j = await res.json(); }catch(e){ grid.innerHTML = '<div class="text-danger">Плохой JSON</div>'; return; }
    pastePath = j.path || pastePath; pathInput.value = pastePath;
    renderPasteGrid(j.items||[]);
  }
  function renderPasteGrid(items){
    if(!items || items.length===0){ grid.innerHTML = '<div class="text-body-secondary">Пусто</div>'; return; }
    grid.innerHTML = '';
    items.forEach(it=>{
      const col = document.createElement('div'); col.className='col-6 col-sm-4';
      const card = document.createElement('div'); card.className='card h-100 d-flex flex-column'; card.style.cursor='pointer';
      if(it.isDir){
        const thumb = document.createElement('div'); thumb.className='card-img-top d-flex align-items-center justify-content-center'; thumb.style.height='140px'; thumb.style.background='#212529'; thumb.innerHTML='\
<svg width="84" height="84" viewBox="0 0 24 24" fill="#fff" xmlns="http://www.w3.org/2000/svg">\
  <path d="M10 4H4c-1.1 0-2 .9-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z"/>\
</svg>';
        card.appendChild(thumb);
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate fw-semibold'; cap.textContent = it.name; cap.style.cursor='pointer';
        cap.addEventListener('click', ()=>{ pastePath = pastePath? (pastePath+'/'+it.name): it.name; pathInput.value=pastePath; fetchPasteList(); });
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
        rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pastePath||'', from: it.name, to: nn}).toString()}); fetchPasteList(); };
        const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
        del.onclick=async()=>{ if(!confirm('Удалить папку '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pastePath||'', name: it.name}).toString()}); fetchPasteList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
        card.addEventListener('click', (e)=>{ if(e.target!==rn && e.target!==del){ pastePath = pastePath? (pastePath+'/'+it.name): it.name; pathInput.value=pastePath; fetchPasteList(); } });
      } else {
        if(it.url){ const img=document.createElement('img'); img.className='card-img-top'; img.src=it.url; img.alt=it.name; img.style.height='100px'; img.style.objectFit='cover'; card.appendChild(img); }
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name;
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
        rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pastePath||'', from: it.name, to: nn}).toString()}); fetchPasteList(); };
        const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
        del.onclick=async()=>{ if(!confirm('Удалить файл '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: pastePath||'', name: it.name}).toString()}); fetchPasteList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      }
      col.appendChild(card); grid.appendChild(col);
    });
  }
  // mkdir
  el.querySelector('#paste_mkdir').addEventListener('click', async ()=>{
    const name = prompt('Имя новой папки:'); if(!name) return;
    const fd = new URLSearchParams(); fd.set('path', pastePath||''); fd.set('name', name);
    let res; try{ res = await fetch('/admin/news/assets/mkdir', { method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString() }); }catch(e){ return; }
    if(res && res.ok){ fetchPasteList(); }
  });
  // sync manual edits
  pathInput.addEventListener('change', ()=>{ pastePath = (pathInput.value||'').replace(/^\/+|\/+$/g,''); fetchPasteList(); });
  // initial load
  fetchPasteList();
  el.querySelector('#paste_ok').addEventListener('click', async ()=>{
    const path = (el.querySelector('#paste_path').value || '').replace(/^\/+|\/+$/g,'');
    const name = el.querySelector('#paste_name').value || 'image';
    const target = el.querySelector('#paste_target')?.value || 'inline';
    const mode = el.querySelector('#paste_overwrite')?.value || 'rename';
    const finalName = await resolveNameWithMode(path, name, guessOutExtFromFile(file?.name||''), mode);
    const j = await uploadAssetFile(file, {path, filename:finalName});
    if(j && j.url){
      if(target==='inline'){
        const ta = document.getElementById('ns_md'); insertAtCursor(ta, '![image]('+j.url+')'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; ta.dispatchEvent(new Event('input'));
      } else {
        setCoverInMarkdown(j.url); const ta = document.getElementById('ns_md'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
      }
    }
    if(modal) modal.hide(); setTimeout(()=>{ el.remove(); URL.revokeObjectURL(url); }, 300);
  });
  el.addEventListener('hidden.bs.modal', ()=>{ el.remove(); URL.revokeObjectURL(url); });
}

async function galleryMkdir(){
  const name = prompt('Имя новой папки:');
  if(!name) return;
  const fd = new URLSearchParams();
  fd.set('path', galleryPath||'');
  fd.set('name', name);
  let res; try{ res = await fetch('/admin/news/assets/mkdir', { method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString() }); }catch(e){ renderGalleryError('Ошибка создания папки: '+e); return; }
  if(!res.ok){ renderGalleryError('HTTP '+res.status+' '+res.statusText); return; }
  galleryFetchAndRender();
}

function debounce(fn, ms){ let t; return function(){ clearTimeout(t); const self=this, args=arguments; t=setTimeout(()=>fn.apply(self,args), ms||250); } }

function autosizeTextArea(el){ if(!el) return; el.style.height='auto'; el.style.overflow='hidden'; el.style.height = (el.scrollHeight + 48) + 'px'; }

function insertAtCursor(ta, text){
  if(!ta) return;
  const start = ta.selectionStart ?? ta.value.length;
  const end = ta.selectionEnd ?? ta.value.length;
  const before = ta.value.substring(0, start);
  const after  = ta.value.substring(end);
  ta.value = before + text + after;
  const caret = start + text.length;
  ta.selectionStart = ta.selectionEnd = caret;
  ta.focus();
}

// Helpers for cover detection and URL normalization in preview
function normalizeUrlForPreview(u){
  u = (u||'').trim(); if(!u) return '';
  if(u.startsWith('http://') || u.startsWith('https://')) return u;
  if(u.startsWith('./')) u = u.slice(2);
  if(u.startsWith('/')) return u;
  if(u.startsWith('assets/')) return '/assets/' + u.replace(/^assets\//,'');
  return '/assets/' + u;
}

function extractCoverFromMarkdown(md){
  // first image ![alt](url)
  const i1 = (md||'').indexOf('![');
  if(i1>=0){
    const rest = md.slice(i1);
    const j = rest.indexOf(']('); if(j>=0){
      const after = rest.slice(j+2);
      const k = after.indexOf(')'); if(k>=0){
        const url = after.slice(0,k).trim();
        return normalizeUrlForPreview(url);
      }
    }
  }
  return '';
}

function updateCoverPreview(){
  const box = document.getElementById('ns_cover_prev'); if(!box) return;
  const md = document.getElementById('ns_md')?.value || '';
  // Prefer state value; fallback to comment/image in md
  const url = currentCoverUrl || extractCoverFromMarkdown(md);
  currentCoverUrl = url || currentCoverUrl;
  // small top image area: fit inside and center
  box.style.display = 'block';
  if(url){
    box.innerHTML = '<img src="'+url+'" alt="cover" style="width:100%;height:100%;object-fit:contain;object-position:center center;display:block"/>';
  } else {
    box.innerHTML = '<div class="text-body-secondary small d-flex w-100 h-100 align-items-center justify-content-center">Не задано</div>';
  }
  // Also render a tiny card text preview (title + excerpt) if present
  try{
    const small = document.getElementById('ns_preview_small');
    if(small){
      const title = titleFromMarkdown(stripCoverComment(md)) || 'Без заголовка';
      const excerpt = excerptFromMarkdown(stripCoverComment(md));
      small.innerHTML = '<div class="small"><div class="fw-semibold text-truncate">'+escapeHtml(title)+'</div><div class="text-body-secondary text-truncate">'+escapeHtml(excerpt)+'</div></div>';
    }
  }catch(e){ /* no-op */ }
}

// Unsaved changes warning 
let editorDirty = false;
window.addEventListener('beforeunload', function(e){
  if(editorDirty){ e.preventDefault(); e.returnValue = 'Есть несохранённые изменения.'; }
});

// ===== News editor helpers =====
function clearNewsEditorAndPreviews(){
  const ta = document.getElementById('ns_md'); if(ta){ ta.value=''; autosizeTextArea(ta); }
  // reset cover state and preview blocks
  currentCoverUrl = '';
  const cover = document.getElementById('ns_cover_prev'); if(cover){ cover.innerHTML = '<div class="text-body-secondary small d-flex w-100 h-100 align-items-center justify-content-center">Не задано</div>'; }
  const a = document.getElementById('ns_preview_list'); if(a){ a.innerHTML=''; }
  const b = document.getElementById('ns_preview_content'); if(b){ b.innerHTML=''; }
  editorDirty = false;
  // drafts removed
}

// drafts removed: new just clears editor (handler wired below)

// Tabs
function showSection(id){
  // sections (guarded: check element exists before toggling)
  const sections = ['secLauncher','secManifests','secNews'];
  sections.forEach(s=>{ const el = document.getElementById(s); if(el){ if(s===id) el.classList.remove('hidden'); else el.classList.add('hidden'); } });
  // nav active state
  const tabs = ['tabLauncher','tabManifests','tabNews'];
  tabs.forEach(i=>{ const el=document.getElementById(i); if(el) el.classList.remove('active'); });
  const map = { 'secLauncher':'tabLauncher', 'secManifests':'tabManifests', 'secNews':'tabNews' };
  const btn = document.getElementById(map[id]); if(btn) btn.classList.add('active');
  // auto actions per section
  if(id==='secNews') {
    try{ newsList(); }catch(_){}
    // If no slug is set, ensure editor and previews are empty
    const slugEl = document.getElementById('ns_slug');
    if(!slugEl || !slugEl.value){ clearNewsEditorAndPreviews(); }
  }
  if(id==='secLauncher'){
    try{ lnRefresh(); }catch(_){ }
    try{ lnPrevEnsureVersionsAndRender(); }catch(_){ }
    try{ lnManifestsReload(); }catch(_){ }
  }
  try{ localStorage.setItem('admin_tab', id); }catch(e){}
}
// Guarded wiring to avoid null errors
if (document.getElementById('tabLauncher')) document.getElementById('tabLauncher').addEventListener('click', ()=>{ showSection('secLauncher'); lnRefresh(); });
if (document.getElementById('tabManifests')) document.getElementById('tabManifests').addEventListener('click', ()=>showSection('secManifests'));
if (document.getElementById('tabNews')) document.getElementById('tabNews').addEventListener('click', ()=>{ showSection('secNews'); try{ newsList(); }catch(e){} });
if (document.getElementById('tabManifests')) document.getElementById('tabManifests').addEventListener('click', ()=>{ showSection('secManifests'); manifestsReload(); mgmReload(); });
// Ensure initial active state reflects saved section
try{
  const saved = localStorage.getItem('admin_tab');
  if(saved){
    showSection(saved);
    if(saved==='secGames'){ /* removed */ }
    if(saved==='secManifests'){ manifestsReload(); mgmReload(); }
    if(saved==='secLauncher'){ lnRefresh(); }
  } else {
    showSection('secLauncher');
    try{ lnRefresh(); }catch(e){}
  }
}catch(e){ showSection('secLauncher'); }

// Guarded wiring for editor actions (drafts removed)
(function(){
  const ta = document.getElementById('ns_md');
  if(ta){
    ta.addEventListener('input', ()=>{ editorDirty = true; autosizeTextArea(ta); updateCoverPreview(); newsPreview(); });
  }
  const btnNew = document.getElementById('ns_btnNew'); if(btnNew) btnNew.addEventListener('click', ()=>{ const slugEl=document.getElementById('ns_slug'); if(slugEl) slugEl.value=''; clearNewsEditorAndPreviews(); const ta=document.getElementById('ns_md'); if(ta){ ta.value = '# Заголовок\n\nКраткое описание...\n\nТекст новости...'; autosizeTextArea(ta); editorDirty = true; } });
})();

async function upload(){
  console.log('upload clicked');
  // Launcher-only upload
  var kind='launcher';
  var ver=document.getElementById('up_ver').value;
  // allow dropped file fallback
  var file=(window.__upDroppedFile)||document.getElementById('up_zip').files[0];
  var latest=document.getElementById('up_latest').checked? '1':'0';
  if(!file){ notify('Выберите ZIP-файл'); return; }
  if(!ver){ notify('Укажите версию'); return; }
  const fd = new FormData();
  fd.append('kind', kind);
  // launcher uploads use fixed gameId
  fd.append('gameId', 'launcher');
  fd.append('version', ver);
  fd.append('zip', file);
  fd.append('updateLatest', latest);
  // show progress UI
  const wrap=document.getElementById('up_prog_wrap'); const bar=document.getElementById('up_pb'); const txt=document.getElementById('up_prog_text');
  if(wrap) wrap.style.display='block'; if(bar) bar.style.width='0%'; if(txt) txt.textContent='Подготовка к загрузке...';

  // use XHR streaming (NDJSON) to mirror game upload UX
  await new Promise((resolve)=>{
    const xhr = new XMLHttpRequest(); xhr.open('POST','/admin/uploadStream');
    xhr.setRequestHeader('Accept','application/x-ndjson');
    // Upload progress
    xhr.upload.onprogress = (e)=>{
      if(e.lengthComputable){
        const pct = Math.floor(e.loaded*100/e.total);
        if(bar) bar.style.width=pct+'%';
        if(txt) txt.textContent='Загружено '+pct+'% ('+e.loaded+' / '+e.total+' байт)';
      }
    };
    // Streaming NDJSON parsing from response
    let lastLen = 0;
    xhr.onprogress = ()=>{
      const resp = xhr.responseText || '';
      const chunk = resp.substring(lastLen);
      lastLen = resp.length;
      const lines = chunk.split(/\r?\n/).filter(Boolean);
      for(const line of lines){
        try{
          const ev = JSON.parse(line);
          if(ev.type === 'start'){
            if(txt) txt.textContent = 'Старт обработки: launcher '+(ev.version||ver);
          } else if(ev.type === 'zipSaved'){
            if(txt) txt.textContent = 'Загрузка завершена, обработка ZIP ('+formatBytes(ev.bytes||0)+')...';
            if(bar) bar.style.width='100%';
          } else if(ev.type === 'unzip'){
            if(txt) txt.textContent = 'Распаковка: '+ev.path;
          } else if(ev.type === 'composeStart'){
            if(txt) txt.textContent = 'Подготовка манифеста: 0/'+(ev.totalFiles||0)+' файлов';
          } else if(ev.type === 'file'){
            if(txt) txt.textContent = 'Манифест: '+(ev.idx||0)+' файлов, '+formatBytes(ev.bytesDone||0);
          } else if(ev.type === 'done'){
            if(txt) txt.textContent = 'Готово. Манифест лаунчера записан';
            try{ lnRefresh(); }catch(_){ }
          } else if(ev.type === 'error'){
            notify('Ошибка: '+(ev.message||'unknown'));
          }
        }catch(_){ /* ignore JSON parse errors for partial lines */ }
      }
    };
    xhr.onreadystatechange = ()=>{
      if(xhr.readyState===4){
        if(!(xhr.status>=200 && xhr.status<300)){
          notify('HTTP '+xhr.status+' '+xhr.statusText+' '+(xhr.responseText||''));
        } else {
          // ensure UI reflects new latest
          try{ lnRefresh(); }catch(_){ }
        }
        window.__upDroppedFile=null; resolve();
      }
    };
    xhr.onerror = ()=>{ notify('Ошибка загрузки'); window.__upDroppedFile=null; resolve(); };
    xhr.send(fd);
  });
}


// Wire buttons (guarded)
if (document.getElementById('btnUpload')) document.getElementById('btnUpload').addEventListener('click', upload);
// Manifests wiring
if (document.getElementById('man_upload')) document.getElementById('man_upload').addEventListener('click', manifestsUpload);
if (document.getElementById('btnList')) document.getElementById('btnList').addEventListener('click', manifestsReload);
// Launcher versions list refresh
if (document.getElementById('ln_list_btn')) document.getElementById('ln_list_btn').addEventListener('click', lnManifestsReload);
// Launcher preview selector wiring
if (document.getElementById('ln_prev_refresh')) document.getElementById('ln_prev_refresh').addEventListener('click', lnPrevEnsureVersionsAndRender);
if (document.getElementById('ln_prev_ver')) document.getElementById('ln_prev_ver').addEventListener('change', ()=>{ const sel=document.getElementById('ln_prev_ver'); if(!sel) return; lnPrevRender(sel.value||''); });

// Fallback: dynamically inject 'Версии лаунчера' card if admin.html is older
function ensureLauncherVersionsCard(){
  if(document.getElementById('ln_ver_list')) return;
  const sec = document.getElementById('secLauncher'); if(!sec) return;
  // find right column (the one with upload card)
  const cols = sec.querySelectorAll('.row .col-lg-6');
  const rightCol = cols.length>=2 ? cols[1] : null;
  if(!rightCol) return;
  const wrap = document.createElement('div');
  wrap.className = 'card mt-3';
  wrap.innerHTML = '<div class="card-header d-flex align-items-center justify-content-between">\
    <span>Версии лаунчера</span>\
    <button type="button" id="ln_list_btn" class="btn btn-sm btn-outline-secondary">Обновить список</button>\
  </div>\
  <div class="card-body">\
    <div id="ln_ver_list" class="mt-1"></div>\
  </div>';
  rightCol.appendChild(wrap);
  // bind refresh after injection
  const b = document.getElementById('ln_list_btn'); if(b){ b.addEventListener('click', lnManifestsReload); }
}
// Manifests page: Games editor buttons
if (document.getElementById('mgm_add')) document.getElementById('mgm_add').addEventListener('click', mgmAddRow);
if (document.getElementById('mgm_save')) document.getElementById('mgm_save').addEventListener('click', mgmSave);
if (document.getElementById('mgm_resync')) document.getElementById('mgm_resync').addEventListener('click', mgmResync);
// Launcher page buttons
if (document.getElementById('ln_refresh')) document.getElementById('ln_refresh').addEventListener('click', lnRefresh);

// News wiring (guarded)
if (document.getElementById('ns_btnList')) document.getElementById('ns_btnList').addEventListener('click', newsList);
if (document.getElementById('ns_btnNew')) document.getElementById('ns_btnNew').addEventListener('click', ()=>{ if(document.getElementById('ns_slug')) document.getElementById('ns_slug').value=''; if(document.getElementById('ns_md')){ const ta=document.getElementById('ns_md'); ta.value=''; autosizeTextArea(ta);} if(document.getElementById('ns_preview')) document.getElementById('ns_preview').innerHTML=''; });
if (document.getElementById('ns_btnSave')) document.getElementById('ns_btnSave').addEventListener('click', newsSave);
if (document.getElementById('ns_btnDelete')) document.getElementById('ns_btnDelete').addEventListener('click', newsDelete);
if (document.getElementById('ns_btnPreview')) document.getElementById('ns_btnPreview').addEventListener('click', newsPreview);
if (document.getElementById('ns_btnCover')) document.getElementById('ns_btnCover').addEventListener('click', ()=>openPickUploadDialog('cover'));
// Image insert wiring
// New toolbar buttons
if (document.getElementById('ns_btnUploadDisk')) document.getElementById('ns_btnUploadDisk').addEventListener('click', ()=>openPickUploadDialog('inline'));
if (document.getElementById('ns_btnUploadUrl')) document.getElementById('ns_btnUploadUrl').addEventListener('click', ()=>openUrlUploadDialog('inline'));
// Gallery wiring
if (document.getElementById('ns_gallery_btn')) document.getElementById('ns_gallery_btn').addEventListener('click', openGalleryModal);
// Fallback event delegation in case elements are re-rendered
document.addEventListener('click', function(e){
  const r = e.target && (e.target.id==='ns_gallery_refresh' || e.target.closest && e.target.closest('#ns_gallery_refresh'));
  if(r){ e.preventDefault(); try{ galleryFetchAndRender(); }catch(err){ console.error(err); } }
  const mk = e.target && (e.target.id==='ns_gallery_mkdir' || e.target.closest && e.target.closest('#ns_gallery_mkdir'));
  if(mk){ e.preventDefault(); try{ galleryMkdir(); }catch(err){ console.error(err); } }
  const us = e.target && (e.target.id==='ns_gallery_url_save' || e.target.closest && e.target.closest('#ns_gallery_url_save'));
  if(us){ e.preventDefault(); (async()=>{
    const urlEl = document.getElementById('ns_gallery_url'); const nameEl=document.getElementById('ns_gallery_url_name'); const modeEl=document.getElementById('ns_gallery_overwrite');
    const url = (urlEl?.value||'').trim(); if(!url){ alert('Укажите URL'); return; }
    const base = (nameEl?.value||'image'); const mode = (modeEl?.value||'rename');
    const finalName = await resolveNameWithMode(galleryPath||'', base, guessOutExtFromUrl(url), mode);
    const fd = new URLSearchParams(); fd.set('path', galleryPath||''); fd.set('filename', finalName); fd.set('url', url);
    let res; try{ res = await fetch('/admin/news/assets/uploadByUrl', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString()}); }catch(err){ renderGalleryError('Ошибка: '+err); return; }
    if(!res.ok){ renderGalleryError('HTTP '+res.status+' '+res.statusText); return; }
    galleryFetchAndRender();
  })(); }
});
document.addEventListener('input', function(e){
  const s = e.target && (e.target.id==='ns_gallery_search');
  if(s){ try{ galleryFetchAndRender(); }catch(err){ console.error(err); } }
});
if (document.getElementById('ns_scope')) document.getElementById('ns_scope').addEventListener('change', ()=>{ try{ localStorage.setItem('news_scope', document.getElementById('ns_scope').value);}catch(e){}; onScopeChanged(); newsList(); });
if (document.getElementById('ns_scope')) { try{ const sv=localStorage.getItem('news_scope'); if(sv){ document.getElementById('ns_scope').value=sv; } }catch(e){}; onScopeChanged(); }
if (document.getElementById('ns_gid')) document.getElementById('ns_gid').addEventListener('change', ()=>{ const s=document.getElementById('ns_slug'); if(s) s.value=''; newsList(); });
if (document.getElementById('ns_btnRebuild')) document.getElementById('ns_btnRebuild').addEventListener('click', newsRebuildAndList);
// Auto preview wiring
if (document.getElementById('ns_md')){
  const ta = document.getElementById('ns_md');
  ta.addEventListener('input', debounce(newsPreview, 250));
  ta.addEventListener('input', ()=>{ autosizeTextArea(ta); updateCoverPreview(); editorDirty = true; });
  // initial
  setTimeout(()=>{ autosizeTextArea(ta); updateCoverPreview(); newsPreview(); }, 0);
}

// Games wiring (legacy removed in favor of Manifests page editor)

async function onScopeChanged(){
  const scope = document.getElementById('ns_scope').value;
  const gd = document.getElementById('ns_gid');
  const wrap = document.getElementById('ns_gid_wrap');
  if(wrap){ wrap.style.display = (scope==='game') ? '' : 'none'; }
  if(scope==='game'){
    await loadGamesInto(gd);
  }
  // clear editor
  const ta = document.getElementById('ns_md');
  if (ta){ ta.value = ''; autosizeTextArea(ta); }
}

async function loadGamesInto(sel){
  let res; try{ res = await fetch('/admin/games'); }catch(e){ return; }
  if(!res.ok){ return; }
  let j = await res.json();
  sel.innerHTML = '';
  (j.items||[]).forEach(it=>{
    const opt = document.createElement('option');
    opt.value = it.gameId; opt.textContent = it.title || it.gameId; sel.appendChild(opt);
  });
  // auto-select first item
  if (sel.options.length > 0) sel.selectedIndex = 0;
}

async function gamesReload(){
  let res; try{ res = await fetch('/admin/games'); }catch(e){ return; }
  if(!res.ok){ return; }
  let j = await res.json();
  const items = j.items||[];
  // update table
  const tb = document.querySelector('#gm_table tbody');
  if (tb){
    tb.innerHTML = '';
    items.forEach(it=> gamesAppendRow(tb, it));
  }
  // update News dropdown if scope is game (without extra fetch)
  const sel = document.getElementById('ns_gid');
  const scopeEl = document.getElementById('ns_scope');
  if (sel && scopeEl && scopeEl.value==='game'){
    sel.innerHTML = '';
    const opt0 = document.createElement('option'); opt0.value=''; opt0.textContent='— Выбрать игру —'; sel.appendChild(opt0);
    items.forEach(it=>{
      const o = document.createElement('option'); o.value = it.gameId || it.gameid || ''; o.textContent = it.title || it.gameId || ''; sel.appendChild(o);
    });
    if (sel.options.length > 0) sel.selectedIndex = 0;
  }
}

function gamesAppendRow(tb, it){
  const tr = document.createElement('tr');
  tr.innerHTML = '<td><input class="form-control form-control-sm" value="'+(it.gameId||'')+'"/></td>'+
                 '<td><input class="form-control form-control-sm" value="'+(it.title||'')+'"/></td>'+
                 '<td><input class="form-control form-control-sm" value="'+(it.exeRelativePath||'')+'"/></td>'+
                 '<td><button class="btn btn-sm btn-outline-danger">Del</button></td>';
  tr.querySelector('button').addEventListener('click', ()=> tr.remove());
  tb.appendChild(tr);
}

function gamesAddRow(){
  const tb = document.querySelector('#gm_table tbody');
  gamesAppendRow(tb, {gameId:'', title:'', exeRelativePath:''});
}

async function gamesSave(){
  const rows = Array.from(document.querySelectorAll('#gm_table tbody tr'));
  const items = rows.map(tr=>{
    const tds = tr.querySelectorAll('td');
    return { gameId: tds[0].querySelector('input').value.trim(), title: tds[1].querySelector('input').value.trim(), exeRelativePath: tds[2].querySelector('input').value.trim() };
  }).filter(it=>it.gameId);
  // validate
  const ids = new Set();
  for(const it of items){ if(!it.gameId){ notify('Пустой gameId'); return; } if(ids.has(it.gameId)){ notify('Дубликат gameId: '+it.gameId); return; } ids.add(it.gameId); }
  let res; try{ res = await fetch('/admin/games/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({items}) }); }catch(e){ document.getElementById('out').textContent='Save error: '+e; return; }
  if(!res.ok){ document.getElementById('out').textContent='HTTP '+res.status+' '+res.statusText; return; }
  document.getElementById('out').textContent = await res.text();
  // refresh table and dropdown (single fetch handled inside gamesReload)
  await gamesReload();
}

// add missing games from server scan (directories)
async function gamesScanMissing(){
  let res; try{ res = await fetch('/admin/games/scan'); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  const j = await res.json();
  const tb = document.querySelector('#gm_table tbody');
  const existing = new Set(Array.from(tb.querySelectorAll('tr')).map(tr=> tr.querySelectorAll('td')[0].querySelector('input').value.trim()));
  (j.items||[]).forEach(it=>{
    if(existing.has(it.gameId)) return;
    gamesAppendRow(tb, it);
  });
}

async function newsList(){
  const scope=document.getElementById('ns_scope').value; let gidEl=document.getElementById('ns_gid'); let gid= gidEl? gidEl.value: '';
  if(scope==='game'){
    // ensure game is selected; if not, load and pick first
    if(!gidEl){ document.getElementById('ns_list').textContent='Элемент выбора игры не найден'; return; }
    if(!gid){ await loadGamesInto(gidEl); gid = gidEl.value; }
    if(!gid){ document.getElementById('ns_list').textContent='Выберите игру'; return; }
  }
  let url='/admin/news/list?scope='+encodeURIComponent(scope);
  if(scope==='game') url += '&gameId='+encodeURIComponent(gid);
  let res; try{ res=await fetch(url); }catch(e){ document.getElementById('ns_list').textContent='Ошибка запроса: '+e; return; }
  if(!res.ok){
    // попытка авто-пересборки индекса при 404
    if(res.status===404){
      let rb; try{ rb = await fetch('/admin/news/rebuild?scope='+encodeURIComponent(scope)+(scope==='game'?'&gameId='+encodeURIComponent(gid):'')); }catch(e){}
      if(rb && rb.ok){
        res = await fetch(url);
      }
    }
    if(!res.ok){ document.getElementById('ns_list').textContent='HTTP '+res.status+' '+res.statusText; return; }
  }
  const j = await res.json();
  const root = document.getElementById('ns_list'); root.innerHTML='';
  if(!j.items || j.items.length===0){ const p=document.createElement('div'); p.className='text-body-secondary'; p.textContent='Нет записей'; root.appendChild(p); return; }
  const items = (j.items||[]);
  (items).forEach(it=>{
    const card = document.createElement('div'); card.className='card card-news'; card.style.cursor='pointer';
    if (it.coverUrl){ const img=document.createElement('img'); img.className='card-img-top'; img.src=it.coverUrl; img.alt=it.title||''; card.appendChild(img); }
    const body = document.createElement('div'); body.className='card-body';
    const h = document.createElement('h5'); h.className='card-title'; h.textContent=it.title||it.slug; body.appendChild(h);
    const s = document.createElement('p'); s.className='card-text'; s.textContent=it.summary||''; body.appendChild(s);
    const btns = document.createElement('div');
    const bEdit = document.createElement('button'); bEdit.className='btn btn-sm btn-outline-primary me-1'; bEdit.textContent='Редактировать'; bEdit.onclick=()=>{ document.getElementById('ns_slug').value=it.slug; newsLoad(); };
    const bDel = document.createElement('button'); bDel.className='btn btn-sm btn-outline-danger'; bDel.textContent='Удалить'; bDel.onclick=()=>{ document.getElementById('ns_slug').value=it.slug; newsDelete(); };
    btns.appendChild(bEdit); btns.appendChild(bDel); body.appendChild(btns);
    // Publish toggle (switch) under buttons
    const sw = document.createElement('div'); sw.className = 'form-check form-switch mt-2';
    const cb = document.createElement('input'); cb.className = 'form-check-input'; cb.type = 'checkbox'; cb.id = 'pub_'+(it.slug||''); cb.checked = !!it.published;
    const lb = document.createElement('label'); lb.className = 'form-check-label small'; lb.setAttribute('for', cb.id); lb.textContent = 'Опубликовано';
    cb.addEventListener('change', async ()=>{
      const scope=document.getElementById('ns_scope').value; const gidEl=document.getElementById('ns_gid'); const gid= gidEl? gidEl.value: '';
      const fd = new URLSearchParams(); fd.set('scope', scope); if(scope==='game') fd.set('gameId', gid); fd.set('slug', it.slug); fd.set('published', cb.checked ? 'true' : 'false');
      let res; try{ res = await fetch('/admin/news/publish', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString()}); }catch(e){ notify('Ошибка: '+e); cb.checked = !cb.checked; return; }
      if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); cb.checked = !cb.checked; return; }
      // optional: update without full reload; for simplicity refresh list to reflect state/order
      newsList();
    });
    sw.appendChild(cb); sw.appendChild(lb); body.appendChild(sw);
    card.appendChild(body);
    card.addEventListener('click', (ev)=>{
      if(ev.target && typeof ev.target.closest==='function' && ev.target.closest('button')){ return; }
      const slugEl = document.getElementById('ns_slug'); if(slugEl) slugEl.value=it.slug; newsLoad();
    });
    root.appendChild(card);
  });
}

async function newsLoad(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value;
  if(!slug){ alert('Укажите идентификатор новости'); return; }
  let url='/admin/news/get?scope='+encodeURIComponent(scope)+'&slug='+encodeURIComponent(slug);
  if(scope==='game') url += '&gameId='+encodeURIComponent(gid);
  let res; try{ res=await fetch(url); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  const j = await res.json();
  const ta = document.getElementById('ns_md');
  const serverMd = j.markdown||'';
  // Take cover and published directly from server meta (not from markdown)
  currentCoverUrl = (j.coverUrl||'');
  currentPublished = !!j.published;
  const pubEl = document.getElementById('ns_published'); if(pubEl){ pubEl.checked = currentPublished; }
  // keep server text by default until user restores; strip legacy comment directives
  const serverMdClean = (serverMd||'')
    .replace(/<!--\s*published\s*:[^>]*-->\s*\n?/ig, '')
    .replace(/<!--\s*cover\s*:[^>]*-->\s*\n?/ig, '');
  ta.value = serverMdClean;
  autosizeTextArea(document.getElementById('ns_md'));
  updateCoverPreview();
  newsPreview();
  editorDirty = false;
}

// ===== Shared helpers for assets upload =====
async function chooseAssetDestination(defaultPath, suggestedBase){
  const path = prompt('Папка (относительно /news/assets):', (defaultPath===undefined?'':(defaultPath||'')));
  if(path===null) return null;
  const filename = prompt('Имя файла (без расширения):', suggestedBase||'image');
  if(filename===null) return null;
  return { path: path||'', filename: filename||suggestedBase||'image' };
}

async function uploadAssetFile(file, dest){
  const fd = new FormData();
  fd.append('path', dest.path||'');
  fd.append('filename', dest.filename||'image');
  fd.append('file', file);
  let res; try{ res=await fetch('/admin/news/assets/upload', {method:'POST', body: fd}); }catch(e){ notify('Ошибка загрузки: '+e); return null; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return null; }
  const j = await res.json();
  return j && j.url ? j : null;
}

function setCoverInMarkdown(url){
  const u = normalizeUrlForPreview(url || '');
  currentCoverUrl = u;
  updateCoverPreview();
}

// ===== Insert image into article body from file =====
async function newsInsertImageFromFile(){
  const f = document.getElementById('ns_img')?.files?.[0];
  if(!f){ alert('Выберите файл изображения'); return; }
  const inName = f.name || 'image';
  const suggested = inName.replace(/\.[^.]+$/, '');
  const dest = await chooseAssetDestination(galleryPath||'', suggested);
  if(!dest) return;
  const j = await uploadAssetFile(f, dest); if(!j){ return; }
  const url = j.url || '';
  if(!url){ notify('Не удалось получить URL изображения'); return; }
  const ta = document.getElementById('ns_md');
  insertAtCursor(ta, '![image](' + url + ')');
  if(document.getElementById('ns_img')) document.getElementById('ns_img').value='';
  autosizeTextArea(ta);
  updateCoverPreview();
  newsPreview();
  editorDirty = true; ta.dispatchEvent(new Event('input'));
}

function newsInsertImageByUrl(){
  const url = prompt('URL изображения (относительный или абсолютный):','/assets/sample.png');
  if(!url) return;
  const ta = document.getElementById('ns_md');
  insertAtCursor(ta, '![image](' + url + ')');
  autosizeTextArea(ta);
  updateCoverPreview();
  newsPreview();
  editorDirty = true; ta.dispatchEvent(new Event('input'));
}

async function newsSave(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value; const md=document.getElementById('ns_md').value;
  if(!slug){ alert('slug required'); return; }
  const pub = !!(document.getElementById('ns_published') && document.getElementById('ns_published').checked);
  currentPublished = pub;
  const fd = new FormData();
  fd.append('scope', scope);
  if(scope==='game') fd.append('gameId', gid);
  fd.append('slug', slug);
  fd.append('markdown', md);
  // send meta fields explicitly
  fd.append('published', pub ? 'true' : 'false');
  fd.append('coverUrl', currentCoverUrl || '');
  let res; try{ res=await fetch('/admin/news/save', {method:'POST', body: fd}); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  notify(await res.text());
  newsList();
  newsPreview();
  editorDirty = false;
}

function slugify(s){
  return (s||'').toLowerCase().replace(/[^a-z0-9а-яё\-\s_]/g,'').replace(/[\s_]+/g,'-').replace(/-+/g,'-').replace(/^-|-$/g,'');
}

// no-op: kept for compatibility with earlier calls
function stripCoverImageFromMarkdown(md, url){
  if(!md||!url) return md||'';
  const u = normalizeUrlForPreview(url);
  // remove leading image line if URL matches cover
  const re = new RegExp('^!\\[[^\n]*?\\]\\((?:'+escapeRegExp(u)+')\\)\\s*\\n?', 'm');
  return (md||'').replace(re, '');
}

function excerptFromMarkdown(md){
  md = md || '';
  const lines = (md||'').split(/\r?\n/);
  for(const ln of lines){
    const s = (ln||'').trim();
    if(!s) continue;
    if(s.startsWith('#')) continue; // skip headings
    if(/^!\[[^\]]*\]\([^)]*\)/.test(s)) continue; // skip images
    return s;
  }
  return '';
}

function escapeHtml(s){
  return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}

function escapeRegExp(s){
  return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
function titleFromMarkdown(md){
  const m = /^#\s+(.+)$/m.exec(md||'');
  return m? m[1].trim(): '';
}

async function newsCreateOpen(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value;
  let md = document.getElementById('ns_md').value;
  if(!md){ md = '# Новость\n\nКраткое описание...\n\nТекст...'; document.getElementById('ns_md').value = md; autosizeTextArea(document.getElementById('ns_md')); }
  let slug = document.getElementById('ns_slug').value.trim();
  if(!slug){ slug = slugify(titleFromMarkdown(md)) || ('news-'+Date.now()); document.getElementById('ns_slug').value = slug; }
  const fd = new FormData();
  fd.append('scope', scope);
  if(scope==='game') fd.append('gameId', gid);
  fd.append('slug', slug);
  fd.append('markdown', md);
  // send meta on create
  const pubEl = document.getElementById('ns_published');
  const pub = !!(pubEl && pubEl.checked);
  currentPublished = pub;
  fd.append('published', pub ? 'true' : 'false');
  fd.append('coverUrl', currentCoverUrl || '');
  let res; try{ res=await fetch('/admin/news/save', {method:'POST', body: fd}); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  notify('Создано');
  await newsLoad();
  newsList();
  newsPreview();
  editorDirty = false;
}

async function newsDelete(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value;
  if(!slug){ alert('Укажите идентификатор новости'); return; }
  if(!confirm('Удалить новость «'+slug+'»?')) return;
  let res; try{ res=await fetch('/admin/news/delete?scope='+encodeURIComponent(scope)+'&slug='+encodeURIComponent(slug)+(scope==='game'?'&gameId='+encodeURIComponent(gid):''), {method:'POST'}); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  notify(await res.text());
  newsList();
}

async function newsRebuildAndList(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value;
  notify('Пересобираем индекс...');
  let rb; try{ rb = await fetch('/admin/news/rebuild?scope='+encodeURIComponent(scope)+(scope==='game'?'&gameId='+encodeURIComponent(gid):'')); }catch(e){ notify('Ошибка: '+e); return; }
  if(!rb.ok){ notify('HTTP '+rb.status+' '+rb.statusText); return; }
  notify('Индекс пересобран');
  newsList();
}

async function newsPreview(){
  // Prepare markdown for preview as-is; server extracts first image/title/summary
  let md=document.getElementById('ns_md').value;
  const fd=new FormData(); fd.append('markdown', md); fd.append('scope', document.getElementById('ns_scope').value); const gidEl=document.getElementById('ns_gid'); if(gidEl) fd.append('gameId', gidEl.value||'');
  let res; try{ res=await fetch('/admin/news/preview', {method:'POST', body: fd}); }catch(e){ const a=document.getElementById('ns_preview_list'); const b=document.getElementById('ns_preview_content'); if(a) a.textContent='Ошибка предпросмотра: '+e; if(b) b.textContent=''; return; }
  if(!res.ok){ const a=document.getElementById('ns_preview_list'); const b=document.getElementById('ns_preview_content'); if(a) a.textContent='HTTP '+res.status+' '+res.statusText; if(b) b.textContent=''; return; }
  let j; try{ j = await res.json(); }catch(e){ const a=document.getElementById('ns_preview_list'); const b=document.getElementById('ns_preview_content'); if(a) a.textContent='Bad JSON'; if(b) b.textContent=''; return; }
  const a=document.getElementById('ns_preview_list'); const b=document.getElementById('ns_preview_content');
  // Build list preview to match launcher design
  if(a){ a.innerHTML = renderListPreviewFromMarkdown(md); }
  if(b) b.innerHTML = j.contentHtml || '';
}

function renderListPreviewFromMarkdown(md){
  try{
    const title = (typeof titleFromMarkdown==='function') ? (titleFromMarkdown(md)||'Без заголовка') : 'Без заголовка';
    const summary = (typeof excerptFromMarkdown==='function') ? (excerptFromMarkdown(md)||'') : '';
    const cover = (currentCoverUrl && currentCoverUrl.trim()) ? currentCoverUrl : (typeof extractCoverFromMarkdown==='function' ? (extractCoverFromMarkdown(md)||'') : '');
    const hasCover = !!cover;
    const coverHtml = hasCover ? (
      '<img src="'+escapeHtml(cover)+'" alt="cover" style="height:120px;width:100%;object-fit:cover;display:block;border-radius:6px"/>'
    ) : '';
    // Date is not known in editor; show placeholder muted date
    const dateHtml = '<div style="color:#666; margin:2px 0 6px 0; font-size: 0.95em">Черновик</div>';
    // Summary up to 3 lines @ 18px, ellipsis
    const summaryHtml = '<div style="word-wrap:break-word; overflow:hidden; line-height:18px; max-height:54px; display:-webkit-box; -webkit-line-clamp:3; -webkit-box-orient:vertical;">'+escapeHtml(summary)+'</div>';
    const cardHtml = [
      '<div class="card" style="background:rgba(255,255,255,0.04); border-radius:8px;">',
      '  <div class="card-body" style="padding:16px;">',
      '    <div class="row g-0 align-items-start">',
      '      <div class="col-auto" style="width:180px">',
               (hasCover ? coverHtml : ''),
      '      </div>',
      '      <div class="col" style="padding-left:12px">',
      '        <div style="font-size:14px; font-weight:600;">'+escapeHtml(title)+'</div>',
               dateHtml,
               summaryHtml,
      '      </div>',
      '    </div>',
      '  </div>',
      '</div>'
    ].join('\n');
    return cardHtml;
  }catch(e){ return '<div class="text-body-secondary">(предпросмотр недоступен)</div>'; }
}

async function newsUploadCover(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value; const f=document.getElementById('ns_cover').files[0];
  if(!f){ alert('Выберите изображение'); return; }
  const fd = new FormData(); fd.append('scope', scope); if(scope==='game') fd.append('gameId', gid); if(slug) fd.append('slug', slug); fd.append('file', f);
  let res; try{ res=await fetch('/admin/news/uploadCover', {method:'POST', body: fd}); }catch(e){ notify('Ошибка загрузки: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  const j = await res.json();
  notify('Обложка загружена: '+(j.coverUrl||''));
  setCoverInMarkdown(j.coverUrl||'');
  const ta = document.getElementById('ns_md'); autosizeTextArea(ta);
  newsPreview();
  editorDirty = true; if(ta) ta.dispatchEvent(new Event('input'));
}
// ===== Gallery UI =====
function openGalleryModal(){
  try{ gallerySetPath(''); galleryFetchAndRender(); }catch(e){}
  const el = document.getElementById('ns_gallery');
  if(!el) return;
  const modal = window.bootstrap ? new window.bootstrap.Modal(el) : null;
  if(modal) modal.show();
}

let galleryPath = '';
function gallerySetPath(p){ galleryPath = (p||'').replace(/^\/+|\/+$/g,''); renderGalleryBreadcrumbs(); }
function renderGalleryBreadcrumbs(){
  const bc = document.getElementById('ns_gallery_breadcrumbs'); if(!bc) return;
  const segs = galleryPath? galleryPath.split('/') : [];
  const parts = ['<a href="#" data-p="" class="text-decoration-none">assets</a>'];
  let acc = '';
  segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+acc+'" class="text-decoration-none">'+s+'</a>'); });
  bc.innerHTML = parts.join('');
  bc.querySelectorAll('a').forEach(a=> a.addEventListener('click', (e)=>{ e.preventDefault(); const p=e.currentTarget.getAttribute('data-p'); gallerySetPath(p); galleryFetchAndRender(); }));
}

async function galleryFetchAndRender(){
  const q = document.getElementById('ns_gallery_search')?.value || '';
  let url = '/admin/news/assets?path=' + encodeURIComponent(galleryPath);
  if(q && q.trim()!==''){ url += ('&q='+encodeURIComponent(q.trim())); }
  else { /* keep */ }
  let res; try{ res = await fetch(url); }catch(e){ renderGalleryError('Ошибка загрузки: '+e); return; }
  if(!res.ok){ renderGalleryError('HTTP '+res.status+' '+res.statusText); return; }
  let j; try{ j = await res.json(); }catch(e){ renderGalleryError('Bad JSON'); return; }
  // ensure path reflects server
  gallerySetPath(j.path||galleryPath);
  renderGalleryGrid(j.items||[]);
}

function renderGalleryError(msg){
  const grid = document.getElementById('ns_gallery_grid'); if(!grid) return;
  grid.innerHTML = '<div class="text-danger">'+(msg||'Ошибка')+'</div>';
}

function renderGalleryGrid(items){
  const grid = document.getElementById('ns_gallery_grid'); if(!grid) return;
  if(!items || items.length===0){ grid.innerHTML = '<div class="text-body-secondary">Пусто</div>'; return; }
  grid.innerHTML = '';
  items.forEach(it=>{
    const col = document.createElement('div'); col.className='col-6 col-sm-4 col-md-3';
    const card = document.createElement('div'); card.className='card h-100 d-flex flex-column'; card.style.cursor='pointer';
    if(it.isDir){
      const thumb = document.createElement('div'); thumb.className='card-img-top d-flex align-items-center justify-content-center'; thumb.style.height='140px'; thumb.style.background='#212529'; thumb.innerHTML='\
<svg width="84" height="84" viewBox="0 0 24 24" fill="#fff" xmlns="http://www.w3.org/2000/svg">\
  <path d="M9.5 4H4a2 2 0 0 0-2 2v12c0 1.1.9 2 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8.5l-2-2z"/>\
</svg>';
      card.appendChild(thumb);
      const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
      const cap = document.createElement('div'); cap.className='small text-truncate fw-semibold'; cap.textContent = it.name; cap.style.cursor='pointer';
      cap.addEventListener('click', ()=>{ gallerySetPath(galleryPath? (galleryPath+'/'+it.name): it.name); galleryFetchAndRender(); });
      const actions = document.createElement('div'); actions.className='mt-1';
      const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
      rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: galleryPath||'', from: it.name, to: nn}).toString()}); galleryFetchAndRender(); };
      const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" fill="#fff" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
      del.onclick=async()=>{ if(!confirm('Удалить папку '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: galleryPath||'', name: it.name}).toString()}); galleryFetchAndRender(); };
      actions.appendChild(rn); actions.appendChild(del);
      body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      // Make the whole folder card clickable (except action buttons)
      card.addEventListener('click', (ev)=>{ if(ev.target.closest && ev.target.closest('button')) return; gallerySetPath(galleryPath? (galleryPath+'/'+it.name): it.name); galleryFetchAndRender(); });
    } else {
      const img = document.createElement('img'); img.className='card-img-top'; img.src = it.url; img.alt = it.name||''; img.loading='lazy'; img.style.height='120px'; img.style.objectFit='cover';
      const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
      const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name||'';
      const actions = document.createElement('div'); actions.className='mt-1';
      const rn = document.createElement('button'); rn.className='btn btn-sm btn-dark'; rn.title='Переименовать'; rn.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">\
  <path fill="#fff" d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>\
</svg>';
      rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; await fetch('/admin/news/assets/rename', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: galleryPath||'', from: it.name, to: nn}).toString()}); galleryFetchAndRender(); };
      const del = document.createElement('button'); del.className='btn btn-sm btn-dark ms-1'; del.title='Удалить'; del.innerHTML='\
<svg width="18" height="18" viewBox="0 0 24 24" fill="#fff" xmlns="http://www.w3.org/2000/svg">\
  <path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>\
  <path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>\
</svg>';
      del.onclick=async()=>{ if(!confirm('Удалить файл '+it.name+'?')) return; await fetch('/admin/news/assets/delete', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: new URLSearchParams({path: galleryPath||'', name: it.name}).toString()}); galleryFetchAndRender(); };
      actions.appendChild(rn); actions.appendChild(del);
      body.appendChild(cap); body.appendChild(actions);
      card.appendChild(img); card.appendChild(body);
      card.addEventListener('click', (ev)=>{ if(ev.target.closest && ev.target.closest('button')) return; const tgt = document.getElementById('ns_gallery_target')?.value || 'inline'; if(tgt==='cover'){ setCoverInMarkdown(it.url); updateCoverPreview(); newsPreview(); } else { insertImageFromGallery(it.url); } const el = document.getElementById('ns_gallery'); if(window.bootstrap && el){ const m = window.bootstrap.Modal.getInstance(el) || new window.bootstrap.Modal(el); m.hide(); } });
    }
    col.appendChild(card);
    grid.appendChild(col);
  });
}

function insertImageFromGallery(url){
  const ta = document.getElementById('ns_md'); if(!ta) return;
  insertAtCursor(ta, '![image](' + url + ')');
  autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty = true; saveDraftDebounced(); ta.dispatchEvent(new Event('input'));
  // close modal
  const el = document.getElementById('ns_gallery');
  if(window.bootstrap && el){ const modal = window.bootstrap.Modal.getInstance(el) || new window.bootstrap.Modal(el); modal.hide(); }
}