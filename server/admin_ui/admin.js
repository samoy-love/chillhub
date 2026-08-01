// --- Admin API path compatibility shim ---
// Normalize all client-side requests so that any URL starting with '/admin/'
// (except '/admin/ui/' which is static assets) is automatically rewritten to
// '/admin/api/...'. This prevents conflicts with the static '/admin/' route
// in nginx and keeps a single API prefix in production.
(() => {
  const ADMIN_PREFIX = '/admin/';
  const ADMIN_API_PREFIX = '/admin/api/';
  const CSRF_COOKIE = 'csrf_token=';

  function getCsrf() {
    try {
      const parts = document.cookie.split(';');
      for (const p of parts) {
        const s = p.trim();
        if (s.startsWith(CSRF_COOKIE)) return decodeURIComponent(s.slice(CSRF_COOKIE.length));
      }
    } catch { /* no-op */ }
    return '';
  }
  function rewrite(u) {
    try {
      if (typeof u === 'string') {
        if (u.startsWith(ADMIN_PREFIX) && !u.startsWith('/admin/ui/') && !u.startsWith(ADMIN_API_PREFIX)) {
          return ADMIN_API_PREFIX + u.slice(ADMIN_PREFIX.length);
        }
        return u;
      }
      if (u && typeof u.url === 'string') {
        const nu = rewrite(u.url);
        if (nu !== u.url) {
          // Rebuild Request preserving init
          return new Request(nu, u);
        }
        return u;
      }
    } catch { /* no-op */ }
    return u;
  }
  // CSRF-токен — это секрет сессии админки. Отправлять его можно только на свой
  // origin: запрос на чужой хост утащил бы токен туда в заголовке.
  const UNSAFE_METHODS = new Set(['POST','PUT','PATCH','DELETE']);
  function isSameOrigin(u){
    try{
      if(u == null) return false;
      const s = (typeof u === 'string') ? u : (u && typeof u.url === 'string' ? u.url : String(u));
      return new URL(s, window.location.href).origin === window.location.origin;
    }catch{ return false; }
  }
  function needsCsrf(method, url){
    return UNSAFE_METHODS.has(String(method||'GET').toUpperCase()) && isSameOrigin(url);
  }
  // Low-level fetch wrapper: rewrite URL, attach CSRF for unsafe, auto-refresh on 401 once
  try {
    const origFetch = window.fetch;
    async function doFetchOnce(input, init){
      const r = rewrite(input);
      const opts = init ? { ...init } : {};
      // Метод может быть задан и в init, и в самом Request — учитываем оба.
      const method = (opts.method || (r && typeof r === 'object' && r.method) || 'GET');
      if (needsCsrf(method, r)) {
        opts.headers = new Headers(opts.headers || (r && typeof r === 'object' ? r.headers : undefined) || {});
        if (!opts.headers.has('X-CSRF-Token')) {
          const csrf = getCsrf(); if (csrf) opts.headers.set('X-CSRF-Token', csrf);
        }
      }
      return origFetch.call(this, r, opts);
    }
    window.fetch = async function(input, init){
      const res = await doFetchOnce(input, init);
      if (res && res.status === 401) {
        // try refresh once, then retry original
        try { await origFetch.call(this, '/admin/api/auth/refresh', { method: 'POST' }); } catch {}
        return doFetchOnce(input, init);
      }
      return res;
    };
  } catch { /* ignore */ }
  // Patch XHR.open
  try {
    const origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(method, url, ...rest) {
      try {
        if (typeof url === 'string' && url.startsWith(ADMIN_PREFIX) && !url.startsWith('/admin/ui/') && !url.startsWith(ADMIN_API_PREFIX)) {
          url = ADMIN_API_PREFIX + url.slice(ADMIN_PREFIX.length);
        }
      } catch { /* ignore */ }
      try { this._method = method; this._url = url; } catch {}
      return origOpen.call(this, method, url, ...rest);
    };
    // also patch send to attach CSRF for unsafe methods
    const origSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.send = function(body){
      try{
        // Раньше метод вычислялся, но не использовался, и токен уходил вообще
        // с каждым XHR — включая GET и запросы на чужой origin.
        if (needsCsrf(this._method, this._url)) {
          const csrf = getCsrf();
          if (csrf) this.setRequestHeader('X-CSRF-Token', csrf);
        }
      }catch{}
      return origSend.call(this, body);
    };
  } catch { /* ignore */ }
})();

// Suppress a noisy Chrome extension promise error in console (does not affect functionality)
try{
  window.addEventListener('unhandledrejection', (e)=>{
    const msg = String((e && e.reason) || '');
    if(/A listener indicated an asynchronous response/.test(msg)){
      e.preventDefault();
    }
  });
}catch{}

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

// ==== Tabs switching (Launcher / Games / News / Inbox) ====
document.addEventListener('DOMContentLoaded', ()=>{
  const tabs = [
    { btn: 'tabLauncher', sec: 'secLauncher' },
    { btn: 'tabManifests', sec: 'secManifests' },
    { btn: 'tabNews', sec: 'secNews' },
    { btn: 'tabInbox', sec: 'secInbox' },
    { btn: 'tabMaint', sec: 'secMaint' },
    { btn: 'tabMetrics', sec: 'secMetrics' },
  ];
  const activate = (id)=>{
    for(const t of tabs){
      const b = document.getElementById(t.btn);
      const s = document.getElementById(t.sec);
      if(!b||!s) continue;
      const on = (t.btn===id);
      b.classList.toggle('active', on);
      s.classList.toggle('hidden', !on);
      if(on){ try{ localStorage.setItem('admin_tab', t.sec); }catch{ /* no-op */ } }
    }
    if(id==='tabInbox') { try{ fbReload(true); }catch{} }
    if(id==='tabMaint') { try{ mtLoad(); }catch{ /* no-op */ } }
    if(id==='tabMetrics') { try{ mxOnTabOpen(); }catch{ /* no-op */ } }
  };
  for(const t of tabs){
    const b = document.getElementById(t.btn);
    if(!b) continue;
    b.addEventListener('click', (e)=>{ e.preventDefault(); activate(t.btn); });
  }
});

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
    const actBtn = isLatest ? '<span class="badge text-bg-success">latest</span>' : ('<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-primary ln-activate">Сделать активной</button>');
    const delBtn = '<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-danger ms-2 ln-delete">Удалить</button>';
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

// Humanize bytes per second to "+/с"
function formatSpeed(bytesPerSec){
  const v = Number(bytesPerSec||0);
  if(!Number.isFinite(v) || v <= 0) return '';
  return formatBytes(v) + '/с';
}

// Format ETA seconds to H:MM:SS or M:SS
function formatEta(sec){
  const s = Math.max(0, Math.floor(Number(sec||0)));
  const pad = (n)=> (n<10?'0':'')+n;
  const h = Math.floor(s/3600);
  const m = Math.floor((s%3600)/60);
  const ss = s%60;
  if(h>0) return h+':'+pad(m)+':'+pad(ss);
  return m+':'+pad(ss);
}

// ==== System free space indicator ====
let __sysFreeTimer = null; // reserved; not used after change
let __sysFreeReq = 0;
async function sysFreeRefresh(){
  const badge = document.getElementById('sys_free');
  if(!badge) return;
  const myReq = ++__sysFreeReq;
  try{
    // Use /admin/system/free which is auto-rewritten to /admin/api/system/free by the fetch shim
    const r = await fetch('/admin/system/free');
    if(!r.ok){ throw new Error('HTTP '+r.status); }
    const j = await r.json();
    if(myReq !== __sysFreeReq) return; // stale
    const bytes = Number(j && j.bytes);
    const total = Number(j && j.total);
    const freeStr = Number.isFinite(bytes) ? formatBytes(bytes) : '—';
    const totalStr = Number.isFinite(total) && total > 0 ? formatBytes(total) : '';
    badge.textContent = totalStr ? (freeStr + ' / ' + totalStr) : freeStr;
    // Helpful tooltip with exact values and percent
    let title = '';
    if(Number.isFinite(bytes)){
      const pct = (Number.isFinite(total) && total>0) ? Math.round(bytes*100/total) : null;
      title = 'Свободно: '+bytes+' байт' + (pct!==null ? ' ('+pct+'%)' : '');
      if(Number.isFinite(total) && total>0){ title += '\nВсего: '+total+' байт'; }
    }
    if(title) badge.title = title; else badge.removeAttribute('title');
    badge.classList.remove('text-bg-secondary','text-bg-success','badge-critical');
    if(Number.isFinite(bytes) && bytes < 10*1024*1024*1024){
      // < 10 GB -> critical: bright red and blinking
      badge.classList.add('badge-critical');
    } else if (Number.isFinite(bytes)) {
      // ok -> green
      badge.classList.add('text-bg-success');
    } else {
      badge.classList.add('text-bg-secondary');
    }
  }catch(e){
    if(myReq !== __sysFreeReq) return;
    badge.textContent = '—';
    badge.classList.remove('text-bg-success','badge-critical');
    badge.classList.add('text-bg-secondary');
  }
}
// No debounce-based auto-refresh anymore; refresh only by clicking the button
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

// Also reflect file selection in launcher upload area
document.addEventListener('DOMContentLoaded', ()=>{
  const upZip = document.getElementById('up_zip');
  if(upZip){
    upZip.addEventListener('change', (ev)=>{
      const file = ev.currentTarget.files && ev.currentTarget.files[0];
      if(file){
        window.__upDroppedFile = file;
        const txt=document.getElementById('up_prog_text'); const wrap=document.getElementById('up_prog_wrap'); const bar=document.getElementById('up_pb');
        if(wrap) wrap.style.display='block';
        if(bar) bar.style.width='0%';
        if(txt) txt.textContent = 'Выбран файл: '+file.name+' ('+file.size+' байт)';
      }
    });
  }
});

// Init system free space UI (only manual refresh by button)
document.addEventListener('DOMContentLoaded', ()=>{
  const btn = document.getElementById('sys_free_refresh');
  if(btn){ btn.addEventListener('click', (e)=>{ e.preventDefault(); e.stopPropagation(); sysFreeRefresh(); }); }
  // Refresh immediately on admin UI load
  try{ sysFreeRefresh(); }catch{}
});

// ==== Feedback Inbox ====
let __fbItems = [];
let __fbSel = '';
let __fbPollTimer = null;

function fbQueryParams(){
  const type = document.getElementById('fb_type')?.value||'';
  const status = document.getElementById('fb_status')?.value||'';
  const important = document.getElementById('fb_important')?.value||'';
  const q = document.getElementById('fb_q')?.value||'';
  const fromRaw = document.getElementById('fb_from')?.value||'';
  const toRaw = document.getElementById('fb_to')?.value||'';
  // Normalize human-friendly dates to RFC3339 Z
  const from = normalizeHumanDate(fromRaw, /*endOfDay*/false);
  const to = normalizeHumanDate(toRaw, /*endOfDay*/true);
  const p = new URLSearchParams();
  if(type){
    if(type === 'bug_auto'){
      p.set('type','bug');
      p.set('auto','1');
    } else {
      p.set('type', type);
    }
  }
  if(status) p.set('status', status);
  if(important) p.set('important', important);
  if(q) p.set('q', q);
  if(from) p.set('from', from);
  if(to) p.set('to', to);
  return p.toString();
}

// Convert human-friendly date strings to RFC3339 (UTC, Z)
// Accepts:
//  - YYYY-MM-DD
//  - DD.MM.YYYY
//  - YYYY-MM-DD HH:MM[:SS]
//  - DD.MM.YYYY HH:MM[:SS]
// Also passes through valid ISO-like strings if Date parses them.
function normalizeHumanDate(str, endOfDay){
  const s = String(str||'').trim();
  if(!s) return '';
  // If looks like ISO already and parses, use it (ensure Z)
  if(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(s)){
    const d = new Date(s);
    if(!isNaN(d.getTime())) return toRfc3339(d);
  }
  // Patterns
  const ymd = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$/;
  const dmy = /^(\d{2})\.(\d{2})\.(\d{4})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$/;
  let m;
  if((m = ymd.exec(s))){
    const Y = +m[1], M = +m[2], D = +m[3];
    const hh = m[4]!==undefined ? +m[4] : (endOfDay?23:0);
    const mm = m[5]!==undefined ? +m[5] : (endOfDay?59:0);
    const ss = m[6]!==undefined ? +m[6] : (endOfDay?59:0);
    const dt = new Date(Y, M-1, D, hh, mm, ss, endOfDay?999:0);
    if(!isNaN(dt.getTime())) return toRfc3339(dt);
  }
  if((m = dmy.exec(s))){
    const D = +m[1], M = +m[2], Y = +m[3];
    const hh = m[4]!==undefined ? +m[4] : (endOfDay?23:0);
    const mm = m[5]!==undefined ? +m[5] : (endOfDay?59:0);
    const ss = m[6]!==undefined ? +m[6] : (endOfDay?59:0);
    const dt = new Date(Y, M-1, D, hh, mm, ss, endOfDay?999:0);
    if(!isNaN(dt.getTime())) return toRfc3339(dt);
  }
  // Fallback: try native Date.parse on s
  const d = new Date(s);
  if(!isNaN(d.getTime())) return toRfc3339(d);
  return '';
}

function toRfc3339(date){
  const pad = (n)=> (n<10?'0':'')+n;
  const Y = date.getUTCFullYear();
  const M = pad(date.getUTCMonth()+1);
  const D = pad(date.getUTCDate());
  const h = pad(date.getUTCHours());
  const m = pad(date.getUTCMinutes());
  const s = pad(date.getUTCSeconds());
  return `${Y}-${M}-${D}T${h}:${m}:${s}Z`;
}

function fbRenderList(){
  const root = document.getElementById('fb_list'); if(!root) return;
  const cnt = document.getElementById('fb_count'); if(cnt) cnt.textContent = String(__fbItems.length||0);
  if(__fbItems.length===0){ root.innerHTML = '<div class="text-body-secondary">Пусто</div>'; return; }
  const html = __fbItems.map(it=>{
    const imp = it.important ? '<span class="badge text-bg-warning ms-2">важное</span>' : '';
    const st = (it.status==='read') ? '<span class="badge text-bg-secondary ms-2">проч.</span>' : '';
    const isAuto = !!(it && it.system && (it.system.auto==='1' || String(it.system.auto).toLowerCase()==='true'));
    const tlabel = (it && it.type==='bug' && isAuto) ? 'Баг (авто)' : (it?.type||'');
    const type = tlabel ? '<span class="badge text-bg-info ms-2">'+escapeHtml(tlabel)+'</span>' : '';
    const name = escapeHtml(it.name||'—');
    const contact = escapeHtml(it.contact||'');
    const cmt = escapeHtml((it.comment||'').slice(0,160));
    const dt = escapeHtml((it.createdAt||'').replace('T',' ').replace('Z',''));
    const active = (it.id===__fbSel) ? ' active' : '';
    return '<a href="#" class="list-group-item list-group-item-action'+active+'" data-id="'+it.id+'">'
         +   '<div class="d-flex w-100 justify-content-between"><strong>'+name+'</strong><small class="text-body-secondary">'+dt+type+imp+st+'</small></div>'
         +   '<div class="small text-body-secondary">'+contact+'</div>'
         +   '<div class="mt-1">'+cmt+'</div>'
         + '</a>';
  }).join('');
  root.innerHTML = html;
  root.querySelectorAll('a.list-group-item').forEach(a=>{
    a.addEventListener('click', (ev)=>{ ev.preventDefault(); const id = a.getAttribute('data-id'); fbSelect(id); });
  });
}

async function fbReload(immediate){
  const qs = fbQueryParams();
  let res; try{ res = await fetch('/admin/feedback/list'+(qs?'?'+qs:'')); }catch(e){ return; }
  if(!res.ok) return;
  let j; try{ j = await res.json(); }catch{ return; }
  __fbItems = Array.isArray(j.items)? j.items : [];
  fbRenderList();
  if(__fbSel){
    const exists = __fbItems.some(x=> x.id===__fbSel);
    if(!exists){ __fbSel = ''; document.getElementById('fb_view')?.replaceChildren(); }
  }
  if(immediate===true) return;
}

async function fbSelect(id){
  __fbSel = id||'';
  const view = document.getElementById('fb_view'); if(!view) return;
  if(!id){ view.textContent=''; return; }
  let res; try{ res = await fetch('/admin/feedback/get?id='+encodeURIComponent(id)); }catch(e){ return; }
  if(!res.ok){ return; }
  let it; try{ it = await res.json(); }catch{ return; }
  const sys = it.system||{};
  const hasSys = Object.keys(sys).length > 0;
  const sysBlock = hasSys ? '<pre class="bg-body-tertiary p-2 border rounded" style="max-height:240px;overflow:auto">'+escapeHtml(JSON.stringify(sys,null,2))+'</pre>' : '';
  const hasLogs = !!(it.attachLogs && it.logs);
  const logsBlock = hasLogs ? '<pre class="bg-body-tertiary p-2 border rounded" style="max-height:240px;overflow:auto">'+escapeHtml(String(it.logs))+'</pre>' : '';
  const debugBlock = (hasLogs || hasSys)
    ? '<details class="mt-3"><summary>Дебаг-информация</summary>' + logsBlock + sysBlock + '</details>'
    : '';
  const isAuto = !!(sys && (sys.auto==='1' || String(sys.auto).toLowerCase()==='true'));
  const tlabel = (it && it.type==='bug' && isAuto) ? 'Баг (авто)' : (it?.type||'');
  view.innerHTML = ''+
    '<div class="d-flex align-items-center justify-content-between">'
    +  '<div><strong>'+escapeHtml(it.name||'—')+'</strong> <span class="text-body-secondary">'+escapeHtml(it.contact||'')+'</span></div>'
    +  '<div class="small text-body-secondary">'+escapeHtml((it.createdAt||'').replace('T',' ').replace('Z',''))+'</div>'
    +'</div>'
    +'<div class="mt-2"><span class="badge text-bg-info">'+escapeHtml(tlabel)+'</span>'+(it.important?'<span class="badge text-bg-warning ms-2">важное</span>':'')+(it.status==='read'?'<span class="badge text-bg-secondary ms-2">проч.</span>':'')+'</div>'
    +'<div class="mt-3 preserve-ws">'+escapeHtml(it.comment||'')+'</div>'
    + debugBlock;
  fbRenderList();
  // Auto-mark as read on open
  try{ await fetch('/admin/feedback/markRead?id='+encodeURIComponent(id), {method:'POST'}); }catch{}
  try{ await window.fbUnreadUpdateBadge(); }catch{}
  try{ await fbReload(true); }catch{}
}

async function fbAction(url){
  const id = __fbSel; if(!id) return;
  let res; try{ res = await fetch(url+'?id='+encodeURIComponent(id), { method:'POST' }); }catch(e){ return; }
  if(!res.ok) return;
  await fbReload(true);
  if(url.includes('delete')){
    // move to next item
    const idx = __fbItems.findIndex(x=> x.id===id);
    const next = (idx>=0 && idx+1<__fbItems.length) ? __fbItems[idx+1].id : '';
    fbSelect(next);
  } else {
    fbSelect(id);
  }
}

document.addEventListener('DOMContentLoaded', ()=>{
  const bind = (id, fn)=>{ const el=document.getElementById(id); if(el) el.addEventListener('click', (e)=>{ e.preventDefault(); fn(); }); };
  bind('fb_refresh', ()=> fbReload(true));
  bind('fb_clear', async ()=>{ if(!confirm('Очистить все обращения?')) return; let r; try{ r=await fetch('/admin/feedback/clear',{method:'POST'});}catch{}; fbReload(true); __fbSel=''; document.getElementById('fb_view')?.replaceChildren(); });
  bind('fb_mark_read', ()=> fbAction('/admin/feedback/markRead'));
  bind('fb_mark_unread', ()=> fbAction('/admin/feedback/markUnread'));
  bind('fb_toggle_imp', ()=> fbAction('/admin/feedback/toggleImportant'));
  bind('fb_delete', ()=>{ if(!__fbSel) return; if(!confirm('Удалить обращение?')) return; fbAction('/admin/feedback/delete'); });
  // Close view button clears selection
  const closeBtn = document.getElementById('fb_close_view');
  if(closeBtn){ closeBtn.addEventListener('click', (e)=>{ e.preventDefault(); __fbSel=''; document.getElementById('fb_view')?.replaceChildren(); fbRenderList(); }); }
  // Filters live change
  ['fb_type','fb_status','fb_important','fb_q','fb_from','fb_to'].forEach(id=>{
    const el = document.getElementById(id); if(!el) return;
    el.addEventListener('change', ()=> fbReload(true));
    if(id==='fb_q') el.addEventListener('input', ()=> fbReload(true));
  });
  // Delete hotkey
  document.addEventListener('keydown', (e)=>{
    const sec = document.getElementById('secInbox');
    const visible = sec && !sec.classList.contains('hidden');
    if(!visible) return;
    if(e.key==='Delete'){
      e.preventDefault();
      const id = __fbSel; if(!id) return;
      fbAction('/admin/feedback/delete');
    }
  });
  // Polling every 12s when Inbox is active
  const poll = async ()=>{
    const sec = document.getElementById('secInbox');
    const visible = sec && !sec.classList.contains('hidden');
    if(visible){ await fbReload(); }
    __fbPollTimer = setTimeout(poll, 12000);
  };
  poll();

  // Global 1-min polling when page visible: unread count and free space
  async function fbUnreadUpdateBadge(){
    try{
      let res = await fetch('/admin/feedback/list?status=new');
      if(!res.ok) return;
      let j = await res.json();
      const n = Array.isArray(j.items) ? j.items.length : 0;
      const b = document.getElementById('fb_unread_badge');
      if(!b) return;
      if(n>0){ b.style.display='inline-block'; b.textContent = String(n); }
      else { b.style.display='none'; b.textContent = '0'; }
    }catch{}
  }
  window.fbUnreadUpdateBadge = fbUnreadUpdateBadge;

  async function periodicVisibleTick(){
    if(document.visibilityState === 'visible'){
      try{ await fbUnreadUpdateBadge(); }catch{}
      try{ await sysFreeRefresh(); }catch{}
    }
  }
  setInterval(periodicVisibleTick, 60000);
  document.addEventListener('visibilitychange', periodicVisibleTick);
  window.addEventListener('focus', periodicVisibleTick);
  // initial badge and free space on load
  periodicVisibleTick();
});


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
    const actBtn = isLatest ? '<span class="badge text-bg-success">latest</span>' : ('<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-primary man-activate">Сделать активной</button>');
    const delBtn = '<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-danger ms-2 man-delete">Удалить</button>';
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
  const file = (window.__manDroppedFile) || document.getElementById('man_zip')?.files?.[0];
  if(!file){ notify('Выберите ZIP-файл'); return; }
  const wrap=document.getElementById('man_prog_wrap'); const bar=document.getElementById('man_pb');
  const pctEl=document.getElementById('man_prog_pct'); const bytesEl=document.getElementById('man_prog_bytes');
  const speedEl=document.getElementById('man_prog_speed'); const medianEl=document.getElementById('man_prog_median'); const peakEl=document.getElementById('man_prog_peak'); const etaEl=document.getElementById('man_prog_eta');
  const txt = document.getElementById('man_prog_text');
  if(wrap) wrap.style.display='block';
  if(bar) bar.style.width='0%';
  if(pctEl) pctEl.textContent='Подготовка к загрузке...';
  if(txt) txt.textContent = '';

  // UI controls: chunk size and concurrency
  const chunkSel = document.getElementById('man_chunk_size');
  let desiredChunk = Number(chunkSel?.value||0)|0; if(desiredChunk<=0) desiredChunk = 8*1024*1024;
  const concSlider = document.getElementById('man_conc');
  const concVal = document.getElementById('man_conc_val');
  const activeNowEl = document.getElementById('man_active_now');
  const activeCapEl = document.getElementById('man_active_cap');
  let userPar = Number(concSlider?.value||6)|0; if(userPar<1) userPar=1; if(userPar>100) userPar=100;
  if(concVal) concVal.textContent = String(userPar);
  if(activeCapEl) activeCapEl.textContent = String(userPar);
  if(activeNowEl) activeNowEl.textContent = '0';
  const speedWrap = document.getElementById('man_speed_wrap'); const speedCanvas = document.getElementById('man_speed');
  if(speedWrap) speedWrap.style.display='block';
  let speedPoints = []; // [{t, bps}]
  let peakBps = 0;
  const HORIZON_MS = 120000; // 2 minutes window
  // Initialize uPlot chart if available
  let speedPlot = null; let speedPlotData = [[], []]; // [timeSec[], bps[]]
  try{
    if (speedWrap && window.uPlot) {
      // Hide old canvas if present
      try{ if(speedCanvas) speedCanvas.style.display = 'none'; }catch{}
      // Remove previous plot if exists (repeat upload)
      try{ const prevPlot = document.getElementById('man_speed_plot'); if(prevPlot) prevPlot.remove(); }catch{}
      const plotHost = document.createElement('div');
      plotHost.id = 'man_speed_plot';
      plotHost.style.width = '100%';
      plotHost.style.height = '180px';
      plotHost.style.background = '#e7eef8';
      plotHost.style.borderRadius = '6px';
      plotHost.style.padding = '4px';
      speedWrap.appendChild(plotHost);
      const fmtBps = (v)=> formatSpeed(v) || '0';
      const wrapW = speedWrap.clientWidth || plotHost.clientWidth || 600;
      const HEIGHT = 180;
      const opts = {
        width: wrapW,
        height: HEIGHT,
        cursor: { drag: { x: false, y: false } },
        scales: { 
          x: { time: false }, 
          y: { auto: false, range: (u, min, max) => [0, Math.max(1, peakBps)] }
        },
        legend: { show: false },
        padding: [8, 8, 8, 12],
        axes: [
          { 
            grid: { show: true, stroke: '#e5e7eb', width: 1 },
            ticks: { stroke: '#9aa1a9', width: 1 },
            stroke: '#000000',
            values: (u, vals)=> vals.map(v=> (v>=0? (Math.round((speedPlotData[0].length>0? (speedPlotData[0][speedPlotData[0].length-1] - v):0))+'s') : ''))
          },
          { 
            grid: { show: true, stroke: '#e5e7eb', width: 1 },
            ticks: { stroke: '#9aa1a9', width: 1 },
            stroke: '#000000',
            values: (u, vals)=> vals.map(fmtBps),
            size: 96 
          }
        ],
        series: [ {}, { label: 'Скорость', stroke: '#0d6efd', width: 2.25 } ],
      };
      speedPlot = new window.uPlot(opts, speedPlotData, plotHost);
      // Resize on wrapper changes
      const ro = new window.ResizeObserver(()=>{
        try{ speedPlot.setSize({ width: speedWrap.clientWidth || plotHost.clientWidth || 600, height: HEIGHT }); }catch{}
      });
      ro.observe(speedWrap);
    }
  }catch{}

  // INIT
  let initRes; try{
    console.group('Upload ZIP');
    console.time('upload_total');
    console.log('[init] request', { gid, ver, file: { name: file.name, size: file.size }, chunkSize: desiredChunk, userPar });
    initRes = await fetch('/admin/api/upload/init', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({
      kind:'game', gameId: gid, version: ver, zipName: file.name, totalSize: file.size, chunkSize: desiredChunk
    }) });
  }catch(e){ notify('Ошибка init: '+e); return; }
  if(!initRes.ok){ notify('HTTP '+initRes.status+' init'); return; }
  const init = await initRes.json();
  console.log('[init] response', init);
  const uploadId = init.uploadId; const chunkSize = init.chunkSize || desiredChunk || (8*1024*1024); const totalChunks = init.totalChunks||Math.ceil(file.size/chunkSize);
  // RESUME: запросить уже полученные чанки
  let received = new Set();
  try{
    const st = await fetch('/admin/api/upload/status?uploadId='+encodeURIComponent(uploadId));
    if(st.ok){ const j = await st.json(); (j.received||[]).forEach(i=> received.add(Number(i)|0)); }
  }catch{}

  // PARALLEL UPLOAD (fixed, user-controlled)
  const allIdx = [];
  for(let i=0;i<totalChunks;i++){ if(!received.has(i)) allIdx.push(i); }
  const totalBytes = file.size; let uploadedBytes = received.size * chunkSize; if(uploadedBytes>totalBytes) uploadedBytes = totalBytes;
  let ptr = 0; let failed = false; let errors = 0; const failedChunks = [];
  // Concurrency is strictly user-controlled (1:1 with slider), clamped to [1..100]
  const maxCap = 100;
  let curPar = Math.max(1, Math.min(100, userPar));
  console.log('[resume] already have', received.size, 'chunks; scheduling', allIdx.length, 'chunks; chunkSize=', chunkSize, 'startPar=', curPar);

  const t0 = performance.now(); let lastT = t0; let lastLoaded = uploadedBytes; let avgSpeed = 0; const alpha = 0.2; const UI_INTERVAL=500; let lastUiTs=0, uiScheduled=false;
  function updateUI(now){
    const pct = Math.floor((uploadedBytes*100)/totalBytes);
    if(bar) bar.style.width = pct+'%';
    const dt = (now - lastT)/1000; let inst=0; if(dt>0) inst = (uploadedBytes - lastLoaded)/dt; if(inst>0) avgSpeed = avgSpeed? (alpha*inst+(1-alpha)*avgSpeed):inst; lastT=now; lastLoaded=uploadedBytes;
    const remain = Math.max(0, totalBytes - uploadedBytes); const eta = (avgSpeed>0)? (remain/avgSpeed):0;
    // Update peak and median (window 8s)
    if(inst>0){ peakBps = Math.max(peakBps, inst); }
    const horizon = HORIZON_MS; const windowPts = speedPoints.filter(p=> now-p.t <= horizon);
    let medianBps = 0; if(windowPts.length>0){ const arr = windowPts.map(p=> p.bps).sort((a,b)=>a-b); const mid = Math.floor(arr.length/2); medianBps = arr.length%2 ? arr[mid] : ((arr[mid-1]+arr[mid])/2); }
    if(pctEl) pctEl.textContent = 'Загружено '+pct+'%';
    if(bytesEl) bytesEl.textContent = '('+formatBytes(uploadedBytes)+' / '+formatBytes(totalBytes)+')';
    if(speedEl) speedEl.textContent = avgSpeed>0 ? formatSpeed(avgSpeed) : '';
    if(medianEl) medianEl.textContent = medianBps>0 ? ('мед '+formatSpeed(medianBps)) : '';
    if(peakEl) peakEl.textContent = peakBps>0 ? ('пик '+formatSpeed(peakBps)) : '';
    if(etaEl) etaEl.textContent = eta>0 ? ('ETA '+formatEta(eta)) : '';
    if(inst>0){
      // Keep raw points for median calc
      speedPoints.push({t: now, bps: inst});
      const horizon = HORIZON_MS; // 120s window
      while(speedPoints.length>0 && (now - speedPoints[0].t) > horizon){ speedPoints.shift(); }
      // Update uPlot if available
      if (typeof uPlot !== 'undefined' && speedPlot) {
        const nowSec = now/1000;
        // append point
        speedPlotData[0].push(nowSec);
        speedPlotData[1].push(inst);
        // trim by time horizon
        const cutoffSec = nowSec - (horizon/1000);
        let startIdx = 0;
        while(startIdx < speedPlotData[0].length && speedPlotData[0][startIdx] < cutoffSec){ startIdx++; }
        if(startIdx>0){
          speedPlotData[0] = speedPlotData[0].slice(startIdx);
          speedPlotData[1] = speedPlotData[1].slice(startIdx);
        }
        // Limit max points to avoid memory bloat
        const MAX_PTS = 500;
        if(speedPlotData[0].length > MAX_PTS){
          const extra = speedPlotData[0].length - MAX_PTS;
          speedPlotData[0].splice(0, extra);
          speedPlotData[1].splice(0, extra);
        }
        try{ speedPlot.setData(speedPlotData); }catch{}
      }
    }
  }
  function scheduleUI(){ const now=performance.now(); if(now-lastUiTs<UI_INTERVAL) return; lastUiTs=now; if(uiScheduled) return; uiScheduled=true; requestAnimationFrame(()=>{ uiScheduled=false; updateUI(performance.now()); }); }

  let active = 0;
  const win = []; // recent writeMs per chunk
  const WIN_MAX = 50;

  async function runNext(){
    if (ptr >= allIdx.length) return;
    const i = allIdx[ptr++];
    active++;
    if(activeNowEl) activeNowEl.textContent = String(active);
    const start = i*chunkSize; const end = Math.min(start+chunkSize, file.size);
    const blob = file.slice(start, end);
    let ok=false, attempts=0; const MAX_ATTEMPTS=5; while(!ok && attempts<MAX_ATTEMPTS){ attempts++;
      try{
        const r = await fetch('/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+i, { method:'PUT', body: blob });
        if(r.ok){
          let j = null; try{ j = await r.json(); }catch(_){}
          const wms = Number(j && j.writeMs || 0)|0; const b = (end-start);
          if(wms>0){ win.push(wms); if(win.length>WIN_MAX) win.shift(); }
          if(attempts>1){ console.log('[chunk ok after retry]', { index:i, attempts, bytes:b, writeMs:wms }); } else { console.log('[chunk ok]', { index:i, bytes:b, writeMs:wms, par:curPar, active, left: allIdx.length - ptr }); }
          ok = true; uploadedBytes += b; scheduleUI();
        } else if(r.status===409){ ok=true; console.log('[chunk skip:exists]', { index:i }); }
        else {
          errors++; console.warn('[chunk http]', { index:i, status:r.status, attempt:attempts }); await new Promise(res=> setTimeout(res, 400*attempts));
        }
      }catch(e){ errors++; console.warn('[chunk fetch]', { index:i, error:String(e), attempt:attempts }); await new Promise(res=> setTimeout(res, 400*attempts)); }
    }
    if(!ok){ failedChunks.push(i); }
    active--;
    if(activeNowEl) activeNowEl.textContent = String(active);
    // Keep pipeline full up to curPar
    while(active < curPar && ptr < allIdx.length){ runNext(); }
  }

  // Handle live concurrency changes
  if(concSlider){ concSlider.addEventListener('input', ()=>{ userPar = Number(concSlider.value|0); if(userPar<1) userPar=1; if(userPar>100) userPar=100; if(concVal) concVal.textContent=String(userPar); if(activeCapEl) activeCapEl.textContent = String(userPar);
    const prev = curPar; curPar = Math.max(1, Math.min(100, userPar)); if(curPar!==prev){ while(active < curPar && ptr < allIdx.length){ runNext(); } }
  }); }

  // Start initial workers
  console.log('[upload] start', { curPar, maxCap, totalChunks, pending: allIdx.length });
  for(let j=0; j<Math.min(curPar, allIdx.length); j++){ runNext(); }
  // No adaptive timer: concurrency is fixed by user settings

  // Wait until all scheduled chunks are processed
  while(ptr < allIdx.length || active > 0){
    await new Promise(res=> setTimeout(res, 200));
    if(failed) break;
  }
  
  // Retry pass for failed chunks (if any)
  if(!failed && failedChunks.length>0){
    console.group('[retry pass] re-upload failed chunks');
    console.log('failedChunks count', failedChunks.length);
    let missPtr = 0; let missActive = 0; let missFailed = false;
    async function runFailed(){
      if(missPtr >= failedChunks.length) return;
      const idx = failedChunks[missPtr++];
      missActive++; if(activeNowEl) activeNowEl.textContent = String(missActive);
      const s = idx*chunkSize; const e = Math.min(s+chunkSize, file.size);
      const bl = file.slice(s, e);
      let ok=false, attempts=0; const MAX=5; while(!ok && attempts<MAX){ attempts++;
        try{
          const r = await fetch('/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+idx, { method:'PUT', body: bl });
          if(r.ok){ ok=true; uploadedBytes += (e-s); scheduleUI(); if(attempts>1){ console.log('[retry ok]', { index:idx, attempts }); } }
          else if(r.status===409){ ok=true; }
          else { console.warn('[retry http]', { index:idx, status:r.status, attempt:attempts }); await new Promise(res=> setTimeout(res, 500*attempts)); }
        }catch(err){ console.warn('[retry fetch]', { index:idx, error:String(err), attempt:attempts }); await new Promise(res=> setTimeout(res, 500*attempts)); }
      }
      if(!ok){ missFailed = true; }
      missActive--; if(activeNowEl) activeNowEl.textContent = String(missActive);
      while(missActive < curPar && missPtr < failedChunks.length){ runFailed(); }
    }
    for(let j=0;j<Math.min(curPar, failedChunks.length); j++){ runFailed(); }
    while(missPtr < failedChunks.length || missActive > 0){ await new Promise(res=> setTimeout(res, 150)); if(missFailed) break; }
    console.groupEnd();
    if(missFailed){ console.timeEnd('upload_total'); console.groupEnd(); notify('Повторная загрузка неудачных чанков завершилась с ошибкой'); return; }
  }
  if(failed){ console.timeEnd('upload_total'); console.groupEnd(); notify('Загрузка чанков завершилась с ошибкой'); return; }

  // COMPLETE
  async function uploadMissingAndRetryComplete(maxRounds=3){
    for(let round=1; round<=maxRounds; round++){
      let comp; try{ comp = await fetch('/admin/api/upload/complete?uploadId='+encodeURIComponent(uploadId), { method:'POST' }); }catch(e){ notify('Ошибка complete: '+e); return false; }
      if(comp.ok){ return true; }
      const code = comp.status|0; console.warn('[complete] http', code, 'round', round);
      // Try to discover missing chunks via status
      let st; try{ st = await fetch('/admin/api/upload/status?uploadId='+encodeURIComponent(uploadId)); }catch{}
      if(!st || !st.ok){ if(code===400||code===409){ console.warn('[complete] retry without status'); } else { return false; } }
      let sjson=null; try{ sjson = st? await st.json(): null; }catch{}
      if(!sjson || !Array.isArray(sjson.received)){ if(code!==400 && code!==409) return false; continue; }
      const have = new Set(sjson.received.map(x=> Number(x)|0));
      const missing = [];
      for(let i=0;i<totalChunks;i++){ if(!have.has(i)) missing.push(i); }
      if(missing.length===0){ // nothing missing but complete failed: stop
        console.warn('[complete] no missing chunks reported, aborting');
        return false;
      }
      console.log('[complete] will re-upload missing', missing.length);
      // Re-upload missing with current concurrency
      let missPtr = 0; let missActive = 0; let missFailed = false;
      async function runMissing(){
        if(missPtr >= missing.length) return;
        const i = missing[missPtr++];
        missActive++; if(activeNowEl) activeNowEl.textContent = String(missActive);
        const start = i*chunkSize; const end = Math.min(start+chunkSize, file.size);
        const blob = file.slice(start, end);
        let ok=false, attempts=0; while(!ok && attempts<3){ attempts++;
          try{
            const r = await fetch('/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+i, { method:'PUT', body: blob });
            if(r.ok){ ok=true; uploadedBytes += (end-start); scheduleUI(); }
            else if(r.status===409){ ok=true; }
            else { await new Promise(res=> setTimeout(res, 300*attempts)); }
          }catch{ await new Promise(res=> setTimeout(res, 300*attempts)); }
        }
        if(!ok){ missFailed = true; }
        missActive--; if(activeNowEl) activeNowEl.textContent = String(missActive);
        while(missActive < curPar && missPtr < missing.length){ runMissing(); }
      }
      for(let j=0;j<Math.min(curPar, missing.length); j++){ runMissing(); }
      while(missPtr < missing.length || missActive > 0){ await new Promise(res=> setTimeout(res, 150)); if(missFailed) break; }
      if(missFailed){ notify('Повторная загрузка пропущенных чанков завершилась с ошибкой'); return false; }
      // try complete next round
    }
    return false;
  }

  const okComplete = await uploadMissingAndRetryComplete(3);
  if(!okComplete){ console.timeEnd('upload_total'); console.groupEnd(); notify('Ошибка завершения загрузки (complete)'); return; }
  if(txt) txt.textContent = 'Сервера проверяет sha256 и готовит распаковку...';

  // PROCESS (NDJSON)
  try{
    console.log('[process] start');
    const url = '/admin/api/upload/process?uploadId='+encodeURIComponent(uploadId);
    const res = await fetch(url, { headers: { 'Accept':'application/x-ndjson', 'Cache-Control':'no-store' } });
    if(!res.ok){ notify('HTTP '+res.status+' process'); return; }
    const dec = new window.TextDecoder();
    let gotAny = false;
    if(res.body && typeof res.body.getReader === 'function'){
      const reader = res.body.getReader(); let buf='';
      while(true){
        const {done, value} = await reader.read(); if(done) break; buf += dec.decode(value, {stream:true});
        const parts = buf.split(/\r?\n/); buf = parts.pop()||'';
        for(const line of parts){ if(!line) continue; gotAny=true; try{ const ev = JSON.parse(line);
          if(ev.type==='start'){ console.log('[process]', ev); if(txt) txt.textContent = 'Старт обработки: '+gid+' '+ver; }
          else if(ev.type==='unzip'){ if(ev.path){ console.log('[unzip]', ev.path); if(txt) txt.textContent = 'Распаковка: '+ev.path; } }
          else if(ev.type==='composeStart'){ console.log('[composeStart]', ev); if(txt) txt.textContent = 'Подготовка манифеста: '+(ev.totalFiles||0)+' файлов'; }
          else if(ev.type==='file'){ if((ev.idx||0)%100===0) console.log('[file]', ev.idx, ev.path); if(txt) txt.textContent = 'Манифест: '+(ev.idx||0)+' файлов, '+formatBytes(ev.bytesDone||0); }
          else if(ev.type==='done'){ console.log('[done]', ev.outPath); if(txt) txt.textContent = 'Готово. Манифест записан'; }
          else if(ev.type==='error'){ console.warn('[process error]', ev.message); notify('Ошибка: '+(ev.message||'unknown')); }
        }catch{} }
      }
    } else {
      // Fallback: non-streaming response (proxy buffering)
      const text = await res.text();
      const lines = text.split(/\r?\n/);
      for(const line of lines){ if(!line) continue; gotAny=true; try{ const ev = JSON.parse(line);
        if(ev.type==='start'){ if(txt) txt.textContent = 'Старт обработки: '+gid+' '+ver; }
        else if(ev.type==='unzip'){ if(ev.path && txt) txt.textContent = 'Распаковка: '+ev.path; }
        else if(ev.type==='composeStart'){ if(txt) txt.textContent = 'Подготовка манифеста: '+(ev.totalFiles||0)+' файлов'; }
        else if(ev.type==='file'){ if(txt) txt.textContent = 'Манифест: '+(ev.idx||0)+' файлов, '+formatBytes(ev.bytesDone||0); }
        else if(ev.type==='done'){ if(txt) txt.textContent = 'Готово. Манифест записан'; }
        else if(ev.type==='error'){ notify('Ошибка: '+(ev.message||'unknown')); }
      }catch{} }
    }
    if(!gotAny){ console.warn('[process] no NDJSON received (maybe buffering)'); }
  }catch(e){ notify('Ошибка process: '+e); }

  try{ manifestsReload(); }catch(_){ }
  try{ gmPrevEnsureVersionsAndRender(gid); }catch(_){ }
  // Optionally set this version as latest
  try{
    const latestFlag = document.getElementById('man_latest')?.checked;
    if(latestFlag){
      const act = await fetch('/admin/activate?gameId='+encodeURIComponent(gid)+'&version='+encodeURIComponent(ver), { method:'POST' });
      if(!act.ok){ notify('HTTP '+act.status+' activate'); }
    }
  }catch(e){ notify('Ошибка activate: '+e); }
  console.timeEnd('upload_total');
  console.groupEnd();
  window.__manDroppedFile = null;
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
  const tb = document.querySelector('#mgm-table tbody'); if(!tb) return;
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
  // Значения приходят с сервера (/admin/games и /admin/games/scan — по сути
  // имена каталогов на диске), поэтому в атрибут их можно класть только
  // экранированными.
  const gidVal = escapeHtml(it.gameId||'');
  const titleVal = escapeHtml(it.title||'');
  const exeVal = escapeHtml(it.exeRelativePath||'');
  const iconVal = escapeHtml(it.iconUrl||'');
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
    document.querySelectorAll('#mgm-table tbody tr.mgm-selected').forEach(el=> el.classList.remove('mgm-selected'));
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
  const tb = document.querySelector('#mgm-table tbody'); if(!tb) return;
  mgmAppendRow(tb, {gameId:'', title:'', exeRelativePath:'', iconUrl:''});
}

async function mgmSave(){
  const rows = Array.from(document.querySelectorAll('#mgm-table tbody tr'));
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
  const tb = document.querySelector('#mgm-table tbody'); if(!tb){ notify('Таблица игр не найдена'); return; }
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

  // Ensure we are authenticated before showing heavy UI; if not, try refresh then redirect to login
  (async ()=>{
    try {
      const me = await fetch('/admin/api/auth/me');
      if (me.status === 401) {
        try { await fetch('/admin/api/auth/refresh', { method: 'POST' }); } catch {}
        const me2 = await fetch('/admin/api/auth/me');
        if (me2.status === 401) { location.href = '/admin/ui/login.html'; return; }
      }
    } catch { /* ignore */ }
  })();
  // Initial load for Launcher tab: populate latest badge and manifest tree
  try{ lnRefresh(); }catch(_){}
  // Initial load for launcher versions selector and list
  try{ lnPrevEnsureVersionsAndRender(); }catch(_){}
  try{ ensureLauncherVersionsCard(); }catch(_){}
  try{ lnManifestsReload(); }catch(_){}
  // Launcher tab controls are bound later in guarded wiring section

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
    const rows = Array.from(document.querySelectorAll('#mgm-table tbody tr'));
    rows.forEach(r=> r.classList.remove('mgm-selected'));
    for(const r of rows){
      const idCell = r.querySelectorAll('td')[0];
      if(!idCell) continue;
      const idInput = idCell.querySelector('input');
      const id = (idInput?.value||'').trim().toLowerCase();
      if(id===chosen.toLowerCase()){ r.classList.add('mgm-selected'); r.scrollIntoView({block:'nearest'}); break; }
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
    const list = exeFiles.map(p=> '<li class="list-group-item list-group-item-action" data-p="'+escapeHtml(p)+'"><code>'+escapeHtml(p)+'</code></li>').join('') || '<li class="list-group-item">.exe не найдены</li>';
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
  el.innerHTML = '\n<div class="modal-dialog modal-lg"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Загрузка по URL</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="url_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n      <div class="input-group input-group-sm ms-auto" style="max-width:520px">\n        <span class="input-group-text">URL</span>\n        <input id="url_input" class="form-control" placeholder="https://..."/>\n      </div>\n    </div>\n    <div class="d-flex align-items-center gap-2 mt-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="url_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-2">\n      <div class="col-12 col-md-7">\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="url_path" placeholder="относительно assets" value="'+escapeHtml(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="url_name" value="image"/>\n        </div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button> <button id="url_ok" type="button" class="btn btn-primary">Сохранить</button></div>\n</div></div>';
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
  el.innerHTML = '\n<div class="modal-dialog modal-xl"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Загрузка изображения</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="pick_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n      <div class="ms-auto small">Файл: <input id="pick_file" type="file" accept="image/*" /></div>\n    </div>\n    <div class="d-flex align-items-center gap-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="pick_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-3">\n      <div class="col-lg-6">\n        <div style="position:sticky; top:8px">\n          <div id="pick_prev_wrap" class="border rounded d-flex align-items-center justify-content-center" style="min-height:240px;">\n            <div class="text-body-secondary">Выберите файл</div>\n          </div>\n        </div>\n      </div>\n      <div class="col-lg-6">\n        <div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">\n          <nav id="pick_breadcrumbs" class="small text-body-secondary"></nav>\n          <div class="btn-group btn-group-sm">\n            <button id="pick_mkdir" type="button" class="btn btn-outline-success">Новая папка</button>\n          </div>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="pick_path" placeholder="относительно /news/assets" value="'+escapeHtml(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="pick_name" value="image"/>\n        </div>\n        <div id="pick_grid" class="row g-2"></div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button><button type="button" class="btn btn-primary" id="pick_ok" disabled>Загрузить</button></div>\n</div></div>';
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
    segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+escapeHtml(acc)+'" class="text-decoration-none">'+escapeHtml(s)+'</a>'); });
    bc.innerHTML = parts.join('');
    bc.querySelectorAll('a').forEach(a=> a.addEventListener('click', (e)=>{ e.preventDefault(); const p=e.currentTarget.getAttribute('data-p'); pickPath=p||''; pathInput.value=pickPath; fetchPickList(); }));
  }
  async function fetchPickList(){
    renderPickBreadcrumbs();
    grid.innerHTML = '<div class="text-body-secondary">Загрузка...</div>';
    let url = '/admin/news/assets?path=' + encodeURIComponent(pickPath);
    let res; try{ res = await fetch(url); }catch(e){ grid.innerHTML = '<div class="text-danger">Ошибка загрузки</div>'; return; }
    if(!res.ok){ grid.innerHTML = '<div class="text-danger">HTTP '+escapeHtml(res.status+' '+(res.statusText||''))+'</div>'; return; }
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
  el.innerHTML = '\n<div class="modal-dialog modal-xl"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Вставка изображения</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="paste_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n    </div>\n    <div class="d-flex align-items-center gap-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="paste_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-3">\n      <div class="col-lg-6">\n        <div style="position:sticky; top:8px">\n          <img src="'+escapeHtml(url)+'" alt="preview" class="img-fluid border rounded"/>\n        </div>\n      </div>\n      <div class="col-lg-6">\n        <div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">\n          <nav id="paste_breadcrumbs" class="small text-body-secondary"></nav>\n          <div class="btn-group btn-group-sm">\n            <button id="paste_mkdir" type="button" class="btn btn-outline-success">Новая папка</button>\n          </div>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="paste_path" placeholder="относительно /news/assets" value="'+escapeHtml(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="paste_name" value="'+escapeHtml((file.name||'image').replace(/\.[^.]+$/, ''))+'"/>\n        </div>\n        <div id="paste_grid" class="row g-2"></div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button><button type="button" class="btn btn-primary" id="paste_ok">Загрузить</button></div>\n</div></div>';
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
    segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+escapeHtml(acc)+'" class="text-decoration-none">'+escapeHtml(s)+'</a>'); });
    bc.innerHTML = parts.join('');
    bc.querySelectorAll('a').forEach(a=> a.addEventListener('click', (e)=>{ e.preventDefault(); const p=e.currentTarget.getAttribute('data-p'); pastePath=p||''; pathInput.value=pastePath; fetchPasteList(); }));
  }
  async function fetchPasteList(){
    renderPasteBreadcrumbs();
    grid.innerHTML = '<div class="text-body-secondary">Загрузка...</div>';
    let url = '/admin/news/assets?path=' + encodeURIComponent(pastePath);
    let res; try{ res = await fetch(url); }catch(e){ grid.innerHTML = '<div class="text-danger">Ошибка загрузки</div>'; return; }
    if(!res.ok){ grid.innerHTML = '<div class="text-danger">HTTP '+escapeHtml(res.status+' '+(res.statusText||''))+'</div>'; return; }
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
    // url приходит из ответа /admin/news/get (поле coverUrl) либо из markdown.
    // Собираем узел через DOM, а не конкатенацией в innerHTML: тогда кавычка
    // в URL не может закрыть атрибут и дописать onerror=/<script src>.
    box.replaceChildren();
    const img = document.createElement('img');
    img.src = url;
    img.alt = 'cover';
    img.style.cssText = 'width:100%;height:100%;object-fit:contain;object-position:center center;display:block';
    box.appendChild(img);
  } else {
    box.innerHTML = '<div class="text-body-secondary small d-flex w-100 h-100 align-items-center justify-content-center">Не задано</div>';
  }
  // Also render a tiny card text preview (title + excerpt) if present
  try{
    const small = document.getElementById('ns_preview_small');
    if(small){
      const title = titleFromMarkdown(md) || 'Без заголовка';
      const excerpt = excerptFromMarkdown(md);
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
  const sections = ['secLauncher','secManifests','secNews','secInbox','secMaint','secMetrics'];
  sections.forEach(s=>{ const el = document.getElementById(s); if(el){ if(s===id) el.classList.remove('hidden'); else el.classList.add('hidden'); } });
  // nav active state
  const tabs = ['tabLauncher','tabManifests','tabNews','tabInbox','tabMaint','tabMetrics'];
  tabs.forEach(i=>{ const el=document.getElementById(i); if(el) el.classList.remove('active'); });
  const map = { 'secLauncher':'tabLauncher', 'secManifests':'tabManifests', 'secNews':'tabNews', 'secInbox':'tabInbox', 'secMaint':'tabMaint', 'secMetrics':'tabMetrics' };
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
  if(id==='secMaint'){ try{ mtLoad(); }catch(_){ /* no-op */ } }
  if(id==='secMetrics'){ try{ mxOnTabOpen(); }catch(_){ /* no-op */ } }
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
    // Путь пишем сразу канонический, с /api/: в nginx описан только
    // `location = /admin/api/uploadStream`, а `location /` отсутствует, поэтому
    // «/admin/uploadStream» уходит в статику и отдаёт 404. Полагаться на shim
    // XMLHttpRequest.open в начале файла нельзя — это подстраховка, а не контракт.
    const xhr = new XMLHttpRequest(); xhr.open('POST','/admin/api/uploadStream');
    xhr.setRequestHeader('Accept','application/x-ndjson');
    // Upload progress (throttled, with lightweight smoothing)
    const t0 = performance.now();
    let lastT = t0;
    let lastLoaded = 0;
    let avgSpeed = 0; // EMA
    const alpha = 0.2;
    const UI_INTERVAL = 250; // ms
    let lastUiTs = 0;
    let uiScheduled = false;
    const uiState = { pct:0, loaded:0, total: file.size, speed:0, eta:0 };
    function applyUI(){
      if (bar) bar.style.width = uiState.pct + '%';
      if (txt) {
        const speedStr = uiState.speed > 0 ? (' \u2022 ' + formatSpeed(uiState.speed)) : '';
        const etaStr = uiState.eta > 0 ? (' \u2022 ETA ' + formatEta(uiState.eta)) : '';
        txt.textContent = 'Загружено ' + uiState.pct + '% (' + formatBytes(uiState.loaded) + ' / ' + formatBytes(uiState.total) + ')' + speedStr + etaStr;
      }
    }
    function scheduleUI(nowMs){
      if (uiScheduled) return;
      if (nowMs - lastUiTs < UI_INTERVAL) return;
      uiScheduled = true;
      lastUiTs = nowMs;
      requestAnimationFrame(()=>{ uiScheduled = false; applyUI(); });
    }
    xhr.upload.onprogress = (e)=>{
      if(!e.lengthComputable) return;
      const now = performance.now();
      const dt = (now - lastT)/1000;
      let inst = 0;
      if (dt > 0) inst = (e.loaded - lastLoaded)/dt;
      if (inst > 0) avgSpeed = avgSpeed ? (alpha*inst + (1-alpha)*avgSpeed) : inst;
      lastT = now; lastLoaded = e.loaded;
      const remain = Math.max(0, e.total - e.loaded);
      const eta = (avgSpeed > 0) ? (remain/avgSpeed) : 0;
      uiState.pct = Math.floor(e.loaded*100/e.total);
      uiState.loaded = e.loaded;
      uiState.speed = avgSpeed;
      uiState.eta = eta;
      scheduleUI(now);
    };

    // Streaming NDJSON parsing from response (throttled & capped)
    let lastLen = 0;
    let lastRespTs = 0;
    const RESP_INTERVAL = 250; // ms
    const MAX_RESP_LINES_PER_TICK = 200;
    xhr.onprogress = ()=>{
      const now = performance.now();
      if (now - lastRespTs < RESP_INTERVAL) return;
      lastRespTs = now;
      const resp = xhr.responseText || '';
      const chunk = resp.substring(lastLen);
      lastLen = resp.length;
      const lines = chunk.split(/\r?\n/).filter(Boolean);
      const toProcess = lines.length > MAX_RESP_LINES_PER_TICK ? lines.slice(lines.length - MAX_RESP_LINES_PER_TICK) : lines;
      for(const line of toProcess){
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
// Show live value for concurrency slider
(()=>{ const s = document.getElementById('man_conc'); const v = document.getElementById('man_conc_val'); if(s&&v){ v.textContent = String(s.value||'6'); s.addEventListener('input', ()=>{ v.textContent = String(s.value||'6'); }); }})();
// Cleanup button
(()=>{ const btn = document.getElementById('man_cleanup'); if(!btn) return; btn.addEventListener('click', async ()=>{
  if(!confirm('Очистить старые/битые временные загрузки?')) return;
  try{ const r = await fetch('/admin/api/upload/cleanup', { method:'POST' }); if(!r.ok){ notify('HTTP '+r.status+' cleanup'); return; } const j = await r.json(); notify('Удалено: '+(j.removed||0)); }catch(e){ notify('Ошибка cleanup: '+e); }
}); })();
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
  // Те же серверные данные, что и в mgmAppendRow: экранируем перед вставкой в value.
  tr.innerHTML = '<td><input class="form-control form-control-sm" value="'+escapeHtml(it.gameId||'')+'"/></td>'+
                 '<td><input class="form-control form-control-sm" value="'+escapeHtml(it.title||'')+'"/></td>'+
                 '<td><input class="form-control form-control-sm" value="'+escapeHtml(it.exeRelativePath||'')+'"/></td>'+
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
    if(!gidEl){ document.getElementById('ns-list').textContent='Элемент выбора игры не найден'; return; }
    if(!gid){ await loadGamesInto(gidEl); gid = gidEl.value; }
    if(!gid){ document.getElementById('ns-list').textContent='Выберите игру'; return; }
  }
  let url='/admin/news/list?scope='+encodeURIComponent(scope);
  if(scope==='game') url += '&gameId='+encodeURIComponent(gid);
  let res; try{ res=await fetch(url); }catch(e){ document.getElementById('ns-list').textContent='Ошибка запроса: '+e; return; }
  if(!res.ok){
    // попытка авто-пересборки индекса при 404
    if(res.status===404){
      let rb; try{ rb = await fetch('/admin/news/rebuild?scope='+encodeURIComponent(scope)+(scope==='game'?'&gameId='+encodeURIComponent(gid):''), {method:'POST'}); }catch(e){}
      if(rb && rb.ok){
        res = await fetch(url);
      }
    }
  }
  if(!res.ok){ document.getElementById('ns-list').textContent='HTTP '+res.status+' '+res.statusText; return; }
  const j = await res.json();
  const root = document.getElementById('ns-list'); root.innerHTML='';
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

// ===== Санитайзер HTML предпросмотра новостей =====
// Серверный конвертер markdown экранирует, что должен, но предпросмотр — это
// чужой HTML в странице с активной сессией администратора и с CSP, которая
// разрешает script-src с jsdelivr/unpkg. Полагаться на одну только серверную
// сторону нельзя, поэтому разбираем разметку отдельным парсером и оставляем
// только то, что реально нужно для markdown.
const SANITIZE_ALLOWED_TAGS = new Set([
  'p','br','hr','h1','h2','h3','h4','h5','h6',
  'strong','b','em','i','u','s','del','ins','mark','small','sub','sup',
  'code','pre','kbd','samp','var','blockquote','q','cite',
  'ul','ol','li','dl','dt','dd',
  'a','img','figure','figcaption',
  'table','thead','tbody','tfoot','tr','th','td','caption','colgroup','col',
  'span','div','section','article','details','summary'
]);
// Теги, которые вырезаем целиком вместе с содержимым: их текст не является
// текстом статьи (script/style) либо сам по себе является вектором.
const SANITIZE_DROP_TAGS = new Set([
  'script','style','iframe','frame','frameset','object','embed','applet',
  'link','meta','base','form','input','button','select','textarea','option',
  'svg','math','template','noscript','portal'
]);
const SANITIZE_ATTRS = {
  a: ['href','title'],
  img: ['src','alt','title','width','height'],
  ol: ['start','type'],
  td: ['colspan','rowspan','align'],
  th: ['colspan','rowspan','align','scope'],
  col: ['span'],
  colgroup: ['span'],
  details: ['open'],
};
// Общие атрибуты, безопасные для любого разрешённого тега.
const SANITIZE_GLOBAL_ATTRS = ['class','title','id','lang','dir'];

// Пропускаем только схемы, которые не умеют выполнять код.
// javascript: и данные вида data:text/html отсекаются.
function sanitizeUrl(value, allowDataImage){
  const v = String(value||'').trim();
  if(!v) return '';
  // Ссылка без схемы (относительная, якорь, протокол-относительная) безопасна.
  if(/^[a-z][a-z0-9+.-]*:/i.test(v)){
    const scheme = v.slice(0, v.indexOf(':')).toLowerCase();
    if(scheme === 'http' || scheme === 'https' || scheme === 'mailto') return v;
    if(allowDataImage && scheme === 'data' && /^data:image\/(png|jpeg|gif|webp|avif);/i.test(v)) return v;
    return '';
  }
  return v;
}

// Возвращает DocumentFragment с очищенной копией разметки.
function sanitizeHtmlFragment(html){
  const out = document.createDocumentFragment();
  const doc = new DOMParser().parseFromString(String(html||''), 'text/html');
  // Парсер DOMParser не исполняет скрипты и не загружает ресурсы,
  // поэтому уже на этом шаге разметка «мертва».
  const convert = (node, parent)=>{
    if(node.nodeType === 3){ parent.appendChild(document.createTextNode(node.nodeValue)); return; }
    if(node.nodeType !== 1) return; // комментарии и прочее — за борт
    const tag = node.tagName.toLowerCase();
    if(SANITIZE_DROP_TAGS.has(tag)) return;
    if(!SANITIZE_ALLOWED_TAGS.has(tag)){
      // Неизвестный, но и не опасный тег: сохраняем его содержимое.
      for(const child of Array.from(node.childNodes)) convert(child, parent);
      return;
    }
    const el = document.createElement(tag);
    const allowed = SANITIZE_GLOBAL_ATTRS.concat(SANITIZE_ATTRS[tag] || []);
    for(const attr of Array.from(node.attributes)){
      const name = attr.name.toLowerCase();
      if(name.startsWith('on')) continue; // обработчики событий — никогда
      if(!allowed.includes(name)) continue;
      let val = attr.value;
      if(name === 'href') val = sanitizeUrl(val, false);
      if(name === 'src') val = sanitizeUrl(val, true);
      if(val === '' && (name === 'href' || name === 'src')) continue;
      el.setAttribute(name, val);
    }
    if(tag === 'a' && el.getAttribute('href')){
      // Предпросмотр открывается в админке; внешняя ссылка не должна получать
      // доступ к window.opener.
      el.setAttribute('target', '_blank');
      el.setAttribute('rel', 'noopener noreferrer nofollow');
    }
    for(const child of Array.from(node.childNodes)) convert(child, el);
    parent.appendChild(el);
  };
  for(const child of Array.from(doc.body.childNodes)) convert(child, out);
  return out;
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
  let rb; try{ rb = await fetch('/admin/news/rebuild?scope='+encodeURIComponent(scope)+(scope==='game'?'&gameId='+encodeURIComponent(gid):''), {method:'POST'}); }catch(e){ notify('Ошибка: '+e); return; }
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
  // contentHtml приходит из серверного конвертера markdown. Даже когда сервер
  // экранирует всё правильно, вставлять его в innerHTML вслепую нельзя:
  // прогоняем через собственный санитайзер (см. sanitizeHtmlFragment).
  if(b) b.replaceChildren(sanitizeHtmlFragment(j.contentHtml || ''));
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
  segs.forEach((s,i)=>{ acc += (i?'/':'')+s; parts.push(' / <a href="#" data-p="'+escapeHtml(acc)+'" class="text-decoration-none">'+escapeHtml(s)+'</a>'); });
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
  // msg собирается из res.statusText и String(e) — это данные сервера и текст
  // исключения, а не наш литерал, поэтому экранируем.
  grid.innerHTML = '<div class="text-danger">'+escapeHtml(msg||'Ошибка')+'</div>';
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
  autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty = true; ta.dispatchEvent(new Event('input'));
  // close modal
  const el = document.getElementById('ns_gallery');
  if(window.bootstrap && el){ const modal = window.bootstrap.Modal.getInstance(el) || new window.bootstrap.Modal(el); modal.hide(); }
}

// ==== Технические работы (maintenance mode) ====
// Все обработчики вешаются через addEventListener: на проде для админки включён
// enforcing CSP без 'unsafe-inline' в script-src, поэтому инлайновые onclick=
// молча перестают работать.

const MT_REASON_MAX_BYTES = 500;
let __mtLast = null;   // последний AdminView с сервера
let __mtDirty = false;

function mtEl(id){ return document.getElementById(id); }

// 'YYYY-MM-DDTHH:mm' в местной зоне -> RFC3339 в UTC.
// Возвращает '' для пустого поля и null, если строку не удалось разобрать.
function mtLocalToUtc(v){
  const s = String(v||'').trim();
  if(!s) return '';
  const d = new Date(s); // форма без смещения трактуется как местное время
  if(isNaN(d.getTime())) return null;
  return toRfc3339(d);
}

// RFC3339 (обычно UTC) -> значение для <input type="datetime-local"> в местной зоне.
function mtUtcToLocalInput(v){
  const s = String(v||'').trim();
  if(!s) return '';
  const d = new Date(s);
  if(isNaN(d.getTime())) return '';
  const pad = (n)=> (n<10?'0':'')+n;
  return d.getFullYear()+'-'+pad(d.getMonth()+1)+'-'+pad(d.getDate())+'T'+pad(d.getHours())+':'+pad(d.getMinutes());
}

// Человекочитаемая метка местного времени для RFC3339-значения.
function mtFmtLocal(v){
  const s = String(v||'').trim();
  if(!s) return '';
  const d = new Date(s);
  if(isNaN(d.getTime())) return s;
  return d.toLocaleString('ru-RU');
}

function mtByteLen(s){
  try{ return new window.TextEncoder().encode(String(s||'')).length; }
  catch{ return String(s||'').length; }
}

function mtBlocksLabel(b){
  const on = [];
  if(b && b.install) on.push('установку');
  if(b && b.update) on.push('обновление');
  if(b && b.launch) on.push('запуск игр');
  return on.length ? on.join(', ') : 'ничего (только баннер)';
}

async function mtErrText(res){
  try{
    const t = (await res.text()||'').trim();
    return t ? ('HTTP '+res.status+': '+t) : ('HTTP '+res.status);
  }catch{ return 'HTTP '+res.status; }
}

function mtSetDirty(v){
  __mtDirty = !!v;
  const b = mtEl('mt_dirty');
  if(b) b.style.display = __mtDirty ? '' : 'none';
}

function mtUpdateReasonCounter(){
  const ta = mtEl('mt_reason'); const out = mtEl('mt_reason_left');
  if(!ta || !out) return;
  const left = MT_REASON_MAX_BYTES - mtByteLen(ta.value);
  out.textContent = String(left);
  out.classList.toggle('text-danger', left < 0);
}

function mtUpdateUtcHint(){
  const hint = mtEl('mt_utc_hint'); if(!hint) return;
  const s = mtLocalToUtc(mtEl('mt_starts')?.value || '');
  const e = mtLocalToUtc(mtEl('mt_ends')?.value || '');
  if(s === null || e === null){ hint.textContent = 'не удалось разобрать дату'; return; }
  if(!s && !e){ hint.textContent = 'окно не задано (с этого момента и до выключения)'; return; }
  hint.textContent = (s || '—') + ' … ' + (e || '—');
}

// Заполняет форму значениями сохранённого состояния.
function mtFillForm(state){
  const st = state || {};
  const b = st.blocks || {};
  const set = (id, val)=>{ const el = mtEl(id); if(el) el.value = val; };
  const chk = (id, val)=>{ const el = mtEl(id); if(el) el.checked = !!val; };
  chk('mt_enabled', st.enabled);
  set('mt_reason', st.reason || '');
  set('mt_starts', mtUtcToLocalInput(st.startsAt));
  set('mt_ends', mtUtcToLocalInput(st.endsAt));
  chk('mt_block_install', b.install);
  chk('mt_block_update', b.update);
  chk('mt_block_launch', b.launch);
  mtUpdateReasonCounter();
  mtUpdateUtcHint();
  mtSetDirty(false);
}

function mtRenderStatus(view){
  const root = mtEl('mt_status'); if(!root) return;
  const st = (view && view.state) || {};
  const eff = (view && view.effective) || {};

  const pathEl = mtEl('mt_path');
  if(pathEl) pathEl.textContent = (view && view.path) || '—';

  const navBadge = mtEl('mt_state_badge');
  if(navBadge){
    navBadge.textContent = eff.enabled ? 'вкл' : (st.enabled ? 'ждёт' : 'выкл');
    navBadge.className = 'badge ms-2 ' + (eff.enabled ? 'text-bg-warning' : (st.enabled ? 'text-bg-info' : 'text-bg-secondary'));
  }

  if(!st.enabled && !st.reason && !st.startsAt && !st.endsAt && !st.updatedAt){
    root.innerHTML = '<div class="alert alert-secondary mb-0">'
      + '<strong>Режим выключен.</strong> Файла состояния нет — это нормальное «выключено».'
      + ' Лаунчер работает без ограничений.'
      + '</div>';
    return;
  }

  const rows = [];
  rows.push(['Сохранённый флаг', st.enabled
    ? '<span class="badge text-bg-warning">включён</span>'
    : '<span class="badge text-bg-secondary">выключен</span>']);
  rows.push(['Действует сейчас', eff.enabled
    ? '<span class="badge text-bg-danger">да</span>'
    : '<span class="badge text-bg-success">нет</span>']);
  rows.push(['Причина', st.reason ? escapeHtml(st.reason) : '<span class="text-body-secondary">не задана</span>']);
  rows.push(['Начало', st.startsAt
    ? (escapeHtml(mtFmtLocal(st.startsAt)) + ' <span class="text-body-secondary">(' + escapeHtml(st.startsAt) + ')</span>')
    : '<span class="text-body-secondary">сразу</span>']);
  rows.push(['Окончание', st.endsAt
    ? (escapeHtml(mtFmtLocal(st.endsAt)) + ' <span class="text-body-secondary">(' + escapeHtml(st.endsAt) + ')</span>')
    : '<span class="text-body-secondary">до ручного выключения</span>']);
  rows.push(['Блокируется', escapeHtml(mtBlocksLabel(st.blocks))]);
  if(st.updatedAt){
    rows.push(['Изменено', escapeHtml(mtFmtLocal(st.updatedAt))
      + (st.updatedBy ? (' <span class="text-body-secondary">' + escapeHtml(st.updatedBy) + '</span>') : '')]);
  }
  if(eff.serverTime){
    rows.push(['Время сервера', escapeHtml(mtFmtLocal(eff.serverTime))
      + ' <span class="text-body-secondary">(' + escapeHtml(eff.serverTime) + ')</span>']);
  }

  let note = '';
  if(st.enabled && !eff.enabled){
    const now = Date.now();
    const s = st.startsAt ? Date.parse(st.startsAt) : NaN;
    const e = st.endsAt ? Date.parse(st.endsAt) : NaN;
    if(!isNaN(s) && now < s){
      note = '<div class="alert alert-info mt-3 mb-0">Режим <strong>запланирован</strong>, но ещё не начался: клиенты пока ничего не видят.</div>';
    } else if(!isNaN(e) && now >= e){
      note = '<div class="alert alert-success mt-3 mb-0">Окно <strong>истекло</strong> — сервер снял режим автоматически, действий не требуется.</div>';
    } else {
      note = '<div class="alert alert-secondary mt-3 mb-0">Флаг в файле включён, но сейчас режим не действует.</div>';
    }
  }

  root.innerHTML = '<table class="table table-sm align-middle mb-0"><tbody>'
    + rows.map(r=> '<tr><th class="text-body-secondary fw-normal" style="width:180px">'+r[0]+'</th><td>'+r[1]+'</td></tr>').join('')
    + '</tbody></table>' + note;
}

function mtRenderPreview(view){
  const root = mtEl('mt_preview'); if(!root) return;
  const eff = (view && view.effective) || {};
  if(!eff.enabled){
    root.innerHTML = '<div class="alert alert-secondary mb-0">Баннер не показывается: режим сейчас не активен.</div>';
    return;
  }
  const reason = eff.reason ? escapeHtml(eff.reason) : 'Идут технические работы.';
  const until = eff.endsAt
    ? ('<div class="small mt-1">Ориентировочно до <strong>'+escapeHtml(mtFmtLocal(eff.endsAt))+'</strong> (в часовом поясе смотрящего).</div>')
    : '<div class="small mt-1">Срок окончания не объявлен.</div>';
  const b = eff.blocks || {};
  const blocked = [];
  if(b.install) blocked.push('установка игр');
  if(b.update) blocked.push('обновление игр');
  if(b.launch) blocked.push('запуск игр');
  const blockLine = blocked.length
    ? '<div class="small mt-1">Недоступно: <strong>'+escapeHtml(blocked.join(', '))+'</strong>.</div>'
    : '<div class="small mt-1">Ограничений нет — только информационное сообщение.</div>';
  root.innerHTML = '<div class="alert alert-warning mb-0">'
    + '<div class="fw-semibold">Технические работы</div>'
    + '<div class="mt-1 preserve-ws">'+reason+'</div>'
    + until + blockLine
    + '</div>';
}

function mtShowError(msg){
  notify(msg);
  const root = mtEl('mt_status');
  if(root) root.innerHTML = '<div class="alert alert-danger mb-0">'+escapeHtml(msg)+'</div>';
}

async function mtLoad(){
  let res;
  try{ res = await fetch('/admin/api/maintenance/get'); }
  catch(e){ mtShowError('Не удалось получить состояние режима: '+e); return; }
  if(!res.ok){ mtShowError('Не удалось получить состояние режима — '+(await mtErrText(res))); return; }
  let view;
  try{ view = await res.json(); }
  catch(e){ mtShowError('Сервер вернул не JSON: '+e); return; }
  __mtLast = view;
  mtRenderStatus(view);
  mtRenderPreview(view);
  if(!__mtDirty) mtFillForm(view.state);
}

async function mtSave(){
  const reason = mtEl('mt_reason')?.value || '';
  if(mtByteLen(reason) > MT_REASON_MAX_BYTES){
    notify('Причина длиннее '+MT_REASON_MAX_BYTES+' байт — сервер её обрежет. Сократите текст.');
    return;
  }
  const startsAt = mtLocalToUtc(mtEl('mt_starts')?.value || '');
  const endsAt = mtLocalToUtc(mtEl('mt_ends')?.value || '');
  if(startsAt === null){ notify('Не удалось разобрать дату начала.'); return; }
  if(endsAt === null){ notify('Не удалось разобрать дату окончания.'); return; }
  if(startsAt && endsAt && Date.parse(endsAt) <= Date.parse(startsAt)){
    notify('Окончание должно быть позже начала.');
    return;
  }
  const payload = {
    enabled: !!mtEl('mt_enabled')?.checked,
    reason: reason.trim(),
    startsAt: startsAt,
    endsAt: endsAt,
    blocks: {
      install: !!mtEl('mt_block_install')?.checked,
      update: !!mtEl('mt_block_update')?.checked,
      launch: !!mtEl('mt_block_launch')?.checked,
    },
  };
  let res;
  try{
    res = await fetch('/admin/api/maintenance/set', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
  }catch(e){ notify('Ошибка сети при сохранении режима: '+e); return; }
  if(!res.ok){ notify('Не удалось сохранить режим — '+(await mtErrText(res))); return; }
  let view = null;
  try{ view = await res.json(); }catch{ /* тело не обязательно */ }
  mtSetDirty(false);
  if(view && view.state){
    __mtLast = view;
    mtRenderStatus(view);
    mtRenderPreview(view);
    mtFillForm(view.state);
  } else {
    await mtLoad();
  }
  notify(payload.enabled ? 'Режим технических работ сохранён и включён.' : 'Состояние сохранено, режим выключен.');
}

async function mtClear(){
  if(!confirm('Снять режим технических работ и удалить файл состояния?')) return;
  let res;
  try{ res = await fetch('/admin/api/maintenance/clear', { method: 'POST' }); }
  catch(e){ notify('Ошибка сети при снятии режима: '+e); return; }
  if(!res.ok){ notify('Не удалось снять режим — '+(await mtErrText(res))); return; }
  mtSetDirty(false);
  await mtLoad();
  mtFillForm({});
  notify('Режим технических работ снят.');
}

document.addEventListener('DOMContentLoaded', ()=>{
  if(!mtEl('secMaint')) return;
  const onClick = (id, fn)=>{ const el = mtEl(id); if(el) el.addEventListener('click', (e)=>{ e.preventDefault(); fn(); }); };
  onClick('mt_refresh', ()=> mtLoad());
  onClick('mt_save', ()=> mtSave());
  onClick('mt_clear', ()=> mtClear());
  onClick('mt_reset_form', ()=> mtFillForm(__mtLast && __mtLast.state));

  const reason = mtEl('mt_reason');
  if(reason) reason.addEventListener('input', ()=>{ mtUpdateReasonCounter(); mtSetDirty(true); });
  ['mt_starts','mt_ends'].forEach(id=>{
    const el = mtEl(id);
    if(el) el.addEventListener('change', ()=>{ mtUpdateUtcHint(); mtSetDirty(true); });
  });
  ['mt_enabled','mt_block_install','mt_block_update','mt_block_launch'].forEach(id=>{
    const el = mtEl(id);
    if(el) el.addEventListener('change', ()=> mtSetDirty(true));
  });
  // Бейдж в навигации должен быть актуален независимо от того, открыта ли вкладка.
  mtLoad();
});

// ==== Метрики ====
// Здесь тоже только addEventListener и никаких инлайновых скриптов: CSP админки
// не содержит 'unsafe-inline' в script-src.

let __mxPlot = null;
let __mxRO = null;
let __mxGamesLoaded = false;
let __mxInited = false;

function mxEl(id){ return document.getElementById(id); }

function mxNum(n){
  const v = Number(n||0);
  if(!isFinite(v)) return '0';
  return v.toLocaleString('ru-RU');
}

// Миллисекунды -> человеческая длительность. 0 означает «нечего усреднять».
function mxFmtMs(ms){
  const v = Number(ms||0);
  if(!(v > 0)) return '—';
  if(v < 1000) return Math.round(v)+' мс';
  if(v < 60000) return (v/1000).toFixed(1)+' с';
  const total = Math.round(v/1000);
  const m = Math.floor(total/60);
  const s = total%60;
  return m+' мин '+(s<10?'0':'')+s+' с';
}

function mxPct(part, total){
  const t = Number(total||0);
  if(!(t > 0)) return '—';
  return ((Number(part||0)*100)/t).toFixed(1).replace('.', ',')+' %';
}

// Значение для <input type="datetime-local"> по отступу в днях назад от текущего момента.
function mxLocalInputAt(msOffsetBack){
  const d = new Date(Date.now() - (msOffsetBack||0));
  const pad = (n)=> (n<10?'0':'')+n;
  return d.getFullYear()+'-'+pad(d.getMonth()+1)+'-'+pad(d.getDate())+'T'+pad(d.getHours())+':'+pad(d.getMinutes());
}

// Верхняя граница периода: datetime-local даёт только минуты, поэтому «по 19:17»
// без этого отбрасывало бы события с 19:17:01 до 19:17:59 — включая свежие,
// из-за чего «за последние 7 дней» показывало ноль сразу после отправки события.
function mxLocalToUtcEnd(v){
  const s = String(v||'').trim();
  if(!s) return '';
  const d = new Date(s);
  if(isNaN(d.getTime())) return null;
  d.setSeconds(59, 999);
  return toRfc3339(d);
}

function mxEmptyRow(cols, text){
  return '<tr><td colspan="'+cols+'" class="text-body-secondary">'+escapeHtml(text)+'</td></tr>';
}

function mxRenderTotals(t){
  const root = mxEl('mx_totals'); if(!root) return;
  const tiles = [
    ['Событий всего', mxNum(t.events), ''],
    ['Запусков лаунчера', mxNum(t.launcherStarts), ''],
    ['Уникальных установок', mxNum(t.uniqueInstalls), 'по installId, не по людям'],
    ['Запусков игр', mxNum(t.gameLaunches), ''],
    ['Установок', mxNum(t.installs), 'успешно '+mxNum(t.installOk)+', с ошибкой '+mxNum(t.installFail)],
    ['Обновлений', mxNum(t.updates), 'успешно '+mxNum(t.updateOk)+', с ошибкой '+mxNum(t.updateFail)],
    ['Ошибок', mxNum(t.errors), 'события вида error'],
    ['Скачано', formatBytes(Number(t.bytesDownloaded||0)), 'сумма поля bytes'],
    ['Среднее время установки', mxFmtMs(t.avgInstallMs), 'только успешные'],
    ['Среднее время обновления', mxFmtMs(t.avgUpdateMs), 'только успешные'],
  ];
  root.innerHTML = tiles.map(x=>
    '<div class="col-6 col-md-4 col-xl-3">'
    + '<div class="border rounded p-2 bg-body-tertiary h-100">'
    + '<div class="small text-body-secondary">'+escapeHtml(x[0])+'</div>'
    + '<div class="fs-5">'+escapeHtml(String(x[1]))+'</div>'
    + (x[2] ? '<div class="small text-body-secondary">'+escapeHtml(x[2])+'</div>' : '')
    + '</div></div>'
  ).join('');
}

function mxRenderDaysTable(byDay){
  const tb = mxEl('mx_days_body'); if(!tb) return;
  if(!byDay || byDay.length===0){ tb.innerHTML = mxEmptyRow(6, 'Нет данных за период.'); return; }
  tb.innerHTML = byDay.map(d=>
    '<tr><td>'+escapeHtml(d.date||'')+'</td>'
    + '<td class="text-end">'+mxNum(d.launcherStarts)+'</td>'
    + '<td class="text-end">'+mxNum(d.installs)+'</td>'
    + '<td class="text-end">'+mxNum(d.updates)+'</td>'
    + '<td class="text-end">'+mxNum(d.gameLaunches)+'</td>'
    + '<td class="text-end">'+mxNum(d.errors)+'</td></tr>'
  ).join('');
}

// График по дням на том же uPlot, что и график скорости загрузки.
// По оси X — индекс дня, подписи берутся из byDay: так не приходится
// пересчитывать UTC-сутки в местные и объяснять сдвиг на границе дня.
function mxRenderChart(byDay){
  const host = mxEl('mx_chart_host'); if(!host) return;
  const note = mxEl('mx_chart_note');
  if(__mxRO){ try{ __mxRO.disconnect(); }catch{ /* no-op */ } __mxRO = null; }
  if(__mxPlot){ try{ __mxPlot.destroy(); }catch{ /* no-op */ } __mxPlot = null; }
  host.replaceChildren();

  if(!byDay || byDay.length===0){
    if(note) note.textContent = '';
    host.innerHTML = '<div class="text-body-secondary">Событий за период нет — рисовать нечего.</div>';
    return;
  }
  if(!window.uPlot){
    if(note) note.textContent = 'библиотека uPlot не загрузилась';
    host.innerHTML = '<div class="alert alert-warning mb-0">График недоступен: uPlot не загрузился. Числа — в таблице ниже.</div>';
    const det = mxEl('mx_days_details'); if(det) det.open = true;
    return;
  }
  if(note) note.textContent = byDay.length+' дн.';

  const xs = byDay.map((_, i)=> i);
  const data = [
    xs,
    byDay.map(d=> Number(d.launcherStarts||0)),
    byDay.map(d=> Number(d.installs||0)),
    byDay.map(d=> Number(d.updates||0)),
    byDay.map(d=> Number(d.gameLaunches||0)),
    byDay.map(d=> Number(d.errors||0)),
  ];
  const label = (v)=>{
    const i = Math.round(v);
    const d = byDay[i];
    return d ? String(d.date||'').slice(5) : '';
  };
  const HEIGHT = 280;
  const opts = {
    width: host.clientWidth || 800,
    height: HEIGHT,
    cursor: { drag: { x: false, y: false } },
    scales: { x: { time: false } },
    legend: { show: true },
    padding: [8, 12, 8, 8],
    axes: [
      { grid: { show: true, stroke: 'rgba(255,255,255,.12)', width: 1 },
        ticks: { stroke: 'rgba(255,255,255,.25)', width: 1 },
        stroke: '#adb5bd',
        values: (u, vals)=> vals.map(label) },
      { grid: { show: true, stroke: 'rgba(255,255,255,.12)', width: 1 },
        ticks: { stroke: 'rgba(255,255,255,.25)', width: 1 },
        stroke: '#adb5bd',
        values: (u, vals)=> vals.map(v=> mxNum(v)) },
    ],
    series: [
      { label: 'Дата (UTC)', value: (u, v)=> label(v) },
      { label: 'Запуски лаунчера', stroke: '#0d6efd', width: 2 },
      { label: 'Установки', stroke: '#198754', width: 2 },
      { label: 'Обновления', stroke: '#0dcaf0', width: 2 },
      { label: 'Запуски игр', stroke: '#ffc107', width: 2 },
      { label: 'Ошибки', stroke: '#dc3545', width: 2 },
    ],
  };
  try{
    __mxPlot = new window.uPlot(opts, data, host);
    __mxRO = new window.ResizeObserver(()=>{
      try{ __mxPlot.setSize({ width: host.clientWidth || 800, height: HEIGHT }); }catch{ /* no-op */ }
    });
    __mxRO.observe(host);
  }catch(e){
    __mxPlot = null;
    host.innerHTML = '<div class="alert alert-warning mb-0">Не удалось построить график: '+escapeHtml(String(e))+'. Числа — в таблице ниже.</div>';
    const det = mxEl('mx_days_details'); if(det) det.open = true;
  }
}

function mxRenderGames(byGame){
  const tb = mxEl('mx_games_body'); if(!tb) return;
  if(!byGame || byGame.length===0){ tb.innerHTML = mxEmptyRow(5, 'Событий, привязанных к играм, нет.'); return; }
  tb.innerHTML = byGame.map(g=>
    '<tr><td><code>'+escapeHtml(g.gameId||'—')+'</code></td>'
    + '<td class="text-end">'+mxNum(g.installs)+'</td>'
    + '<td class="text-end">'+mxNum(g.updates)+'</td>'
    + '<td class="text-end">'+mxNum(g.errors)+'</td>'
    + '<td class="text-end">'+escapeHtml(formatBytes(Number(g.bytes||0)))+'</td></tr>'
  ).join('');
}

function mxRenderCounts(bodyId, items, emptyText, withShare){
  const tb = mxEl(bodyId); if(!tb) return;
  const list = Array.isArray(items) ? items : [];
  const cols = withShare ? 3 : 2;
  if(list.length===0){ tb.innerHTML = mxEmptyRow(cols, emptyText); return; }
  const total = list.reduce((a, x)=> a + Number(x.count||0), 0);
  tb.innerHTML = list.map(x=>
    '<tr><td>'+escapeHtml(x.key||'—')+'</td>'
    + '<td class="text-end">'+mxNum(x.count)+'</td>'
    + (withShare ? '<td class="text-end text-body-secondary">'+escapeHtml(mxPct(x.count, total))+'</td>' : '')
    + '</tr>'
  ).join('');
}

function mxRender(sum){
  const totals = sum.totals || {};
  const byDay = Array.isArray(sum.byDay) ? sum.byDay : [];

  const label = mxEl('mx_range_label');
  if(label){
    label.textContent = 'Период: ' + (mtFmtLocal(sum.from) || '—') + ' — ' + (mtFmtLocal(sum.to) || '—')
      + (mxEl('mx_game')?.value ? (' · игра: ' + mxEl('mx_game').value) : ' · все игры');
  }
  const empty = mxEl('mx_empty');
  if(empty) empty.style.display = Number(totals.events||0) === 0 ? '' : 'none';

  mxRenderTotals(totals);
  mxRenderDaysTable(byDay);
  mxRenderChart(byDay);
  mxRenderGames(sum.byGame);
  mxRenderCounts('mx_errors_body', sum.topErrors, 'Ошибок за период не было.', false);
  mxRenderCounts('mx_versions_body', sum.appVersions, 'Версии не сообщались.', true);
  mxRenderCounts('mx_os_body', sum.os, 'ОС не сообщались.', true);
}

async function mxLoad(){
  const from = mtLocalToUtc(mxEl('mx_from')?.value || '');
  const to = mxLocalToUtcEnd(mxEl('mx_to')?.value || '');
  if(from === null){ notify('Не удалось разобрать дату «с».'); return; }
  if(to === null){ notify('Не удалось разобрать дату «по».'); return; }
  if(from && to && Date.parse(to) < Date.parse(from)){
    notify('Дата «по» раньше даты «с» — сводка будет пустой. Поправьте период.');
    return;
  }
  const p = new URLSearchParams();
  if(from) p.set('from', from);
  if(to) p.set('to', to);
  const gid = mxEl('mx_game')?.value || '';
  if(gid) p.set('gameId', gid);
  const qs = p.toString();

  let res;
  try{ res = await fetch('/admin/api/metrics/summary'+(qs?('?'+qs):'')); }
  catch(e){ notify('Ошибка сети при запросе метрик: '+e); return; }
  if(!res.ok){ notify('Не удалось получить метрики — '+(await mtErrText(res))); return; }
  let sum;
  try{ sum = await res.json(); }
  catch(e){ notify('Сервер вернул не JSON: '+e); return; }
  mxRender(sum);
  notify('Метрики обновлены: событий '+mxNum((sum.totals||{}).events)+'.');
}

async function mxLoadGames(){
  const sel = mxEl('mx_game'); if(!sel) return;
  let res;
  try{ res = await fetch('/admin/games'); }
  catch{ return; } // список игр — удобство, без него фильтр просто пустой
  if(!res.ok) return;
  let j;
  try{ j = await res.json(); }catch{ return; }
  const keep = sel.value;
  sel.innerHTML = '';
  const all = document.createElement('option');
  all.value = ''; all.textContent = 'Все игры';
  sel.appendChild(all);
  (j.items||[]).forEach(it=>{
    const opt = document.createElement('option');
    opt.value = it.gameId;
    opt.textContent = (it.title ? (it.title+' ('+it.gameId+')') : it.gameId);
    sel.appendChild(opt);
  });
  sel.value = keep;
  __mxGamesLoaded = true;
}

function mxSetPreset(days){
  const f = mxEl('mx_from'); const t = mxEl('mx_to');
  if(f) f.value = mxLocalInputAt(days*24*3600*1000);
  if(t) t.value = mxLocalInputAt(0);
}

async function mxClear(){
  if(!confirm('Удалить все накопленные метрики? Действие необратимо.')) return;
  let res;
  try{ res = await fetch('/admin/api/metrics/clear', { method: 'POST' }); }
  catch(e){ notify('Ошибка сети при очистке метрик: '+e); return; }
  if(!res.ok){ notify('Не удалось очистить метрики — '+(await mtErrText(res))); return; }
  notify('Метрики удалены.');
  await mxLoad();
}

function mxOnTabOpen(){
  if(!mxEl('secMetrics')) return;
  if(!__mxInited){
    __mxInited = true;
    if(!mxEl('mx_from')?.value && !mxEl('mx_to')?.value) mxSetPreset(30);
  }
  if(!__mxGamesLoaded){ mxLoadGames().then(()=> mxLoad()); return; }
  mxLoad();
}

document.addEventListener('DOMContentLoaded', ()=>{
  if(!mxEl('secMetrics')) return;
  const onClick = (id, fn)=>{ const el = mxEl(id); if(el) el.addEventListener('click', (e)=>{ e.preventDefault(); fn(); }); };
  onClick('mx_refresh', ()=> mxLoad());
  onClick('mx_clear', ()=> mxClear());
  onClick('mx_last7', ()=>{ mxSetPreset(7); mxLoad(); });
  onClick('mx_last30', ()=>{ mxSetPreset(30); mxLoad(); });
  onClick('mx_last90', ()=>{ mxSetPreset(90); mxLoad(); });
  const sel = mxEl('mx_game');
  if(sel) sel.addEventListener('change', ()=> mxLoad());
  // Восстановление вкладки из localStorage происходит на этапе разбора файла,
  // раньше объявлений let ниже, поэтому тот вызов mxOnTabOpen() гарантированно
  // падает в TDZ и гасится try/catch. Догружаем здесь, если вкладка уже открыта.
  if(!mxEl('secMetrics').classList.contains('hidden')) mxOnTabOpen();
});
