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
      if(!u) return false;
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

// Карточки загрузки собираются из общего шаблона (upload-card.js) ДО всего
// остального кода этого файла: ниже идут getElementById('up_conc'),
// ('man_drop') и десятки других, и к моменту их вызова элементы уже должны
// быть в DOM. В разметке на их месте стоят пустые <div data-upload-card>.
try{ mountUploadCards(document); }catch(e){ console.error('upload cards', e); }

// ==== Тосты и журнал ====
//
// ПОЧЕМУ: единственным каналом сообщений был <pre id="out"> в самом низу
// страницы, и notify() затирал в нём предыдущую строку — без времени, без
// уровня, без истории. На вкладке «Лаунчер» с деревом на 478 файлов этот
// блок находится в двух экранах ниже прогресс-бара, за которым следят, так
// что ошибка заливки фактически оставалась незамеченной (тот же случай
// описан в шапке ui-status.js). Тост виден с любой позиции скролла, журнал
// хранит историю с отметками времени, а #out остаётся приёмником последнего
// сообщения — на него смотрит обработчик window.onerror и тесты.
const JOURNAL_LIMIT = 200;
const __journal = [];

function nowHms(){
  const d = new Date();
  const pad = (n)=> (n<10?'0':'')+n;
  return pad(d.getHours())+':'+pad(d.getMinutes())+':'+pad(d.getSeconds());
}

// Уровень сообщения по его тексту: почти весь существующий код зовёт
// notify() одной строкой, и переписывать все вызовы ради второго аргумента
// не нужно — «HTTP 500», «Ошибка ...», «Не удалось ...» распознаются сами.
function guessLevel(msg){
  const s = String(msg||'');
  if(/(ошибк|не удалось|HTTP [45]\d\d|провал|отказ)/i.test(s)) return 'error';
  if(/(внимание|осторожно|битые|устарел)/i.test(s)) return 'warn';
  if(/(готово|успешно|сохранен|обновлен|удалена|удалены|создан|применен|пересобран)/i.test(s)) return 'success';
  return 'info';
}

const TOAST_TTL = { error: 12000, warn: 9000, success: 5000, info: 5000 };

function showToast(msg, level){
  const host = document.getElementById('toast_host'); if(!host) return;
  const lvl = level || guessLevel(msg);
  const el = document.createElement('div');
  el.className = 'admin-toast admin-toast-'+lvl;
  el.setAttribute('role', lvl==='error' ? 'alert' : 'status');
  const time = document.createElement('span');
  time.className = 'admin-toast-time';
  time.textContent = nowHms();
  const body = document.createElement('div');
  body.className = 'admin-toast-body';
  body.textContent = String(msg||'');
  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'btn-close btn-close-white btn-sm';
  close.setAttribute('aria-label', 'Закрыть');
  close.addEventListener('click', ()=> el.remove());
  el.appendChild(time); el.appendChild(body); el.appendChild(close);
  host.appendChild(el);
  // Ошибку не гасим по таймеру слишком быстро: её должны успеть прочитать.
  const ttl = TOAST_TTL[lvl] || 5000;
  setTimeout(()=>{ if(el.isConnected) el.remove(); }, ttl);
  // Больше пяти тостов на экране — уже стена: старые убираем.
  while(host.children.length > 5){ host.removeChild(host.firstChild); }
}

function journalRender(){
  const log = document.getElementById('journal_log');
  const cnt = document.getElementById('journal_count');
  const last = document.getElementById('journal_last');
  if(cnt) cnt.textContent = String(__journal.length);
  if(last){
    const l = __journal[__journal.length-1];
    last.textContent = l ? (l.time+' · '+l.msg.slice(0,80)) : '';
    last.className = 'ms-2 small journal-'+(l ? l.level : 'info');
  }
  if(!log) return;
  // Новые сверху: журнал читают сразу после действия, а не с начала сессии.
  log.innerHTML = __journal.slice().reverse().map(e=>
    '<div class="journal-line"><span class="journal-time">'+escapeHtml(e.time)+'</span>'
    + '<span class="journal-'+e.level+'">'+escapeHtml(e.msg)+'</span></div>'
  ).join('');
}

function journalAdd(msg, level){
  __journal.push({ time: nowHms(), msg: String(msg||''), level: level || guessLevel(msg) });
  while(__journal.length > JOURNAL_LIMIT) __journal.shift();
  journalRender();
}

// notifyLevel — то же, что notify(), но с явным уровнем: для мест, где текст
// сообщения не даёт его угадать («Метрики удалены» — это success, а не info).
function notifyLevel(msg, level){
  const o = document.getElementById('out'); if(o) o.textContent = msg;
  journalAdd(msg, level);
  showToast(msg, level);
}

// notifyQuiet — для рутины, которая случается сама («метрики обновлены» при
// каждом открытии вкладки). Такое место в журнале есть, а всплывать поверх
// экрана ему незачем: тост, который показывают без просьбы, перестают читать.
function notifyQuiet(msg, level){
  const o = document.getElementById('out'); if(o) o.textContent = msg;
  journalAdd(msg, level);
}

// httpErrText разворачивает ответ сервера в читаемое сообщение. Сервер шлёт
// осмысленный текст (http.Error(w, "saved but index rebuild failed", ...)),
// а UI показывал голое «HTTP 500» и выбрасывал тело — половина диагностики
// до пользователя не доезжала.
async function httpErrText(res, prefix){
  const head = (prefix ? prefix+' — ' : '') + 'HTTP ' + res.status + (res.statusText ? ' '+res.statusText : '');
  let body = '';
  try{ body = (await res.text()||'').trim(); }catch{ /* тело может быть уже прочитано */ }
  if(!body) return head;
  // JSON-ошибки вида {"error":"..."} показываем полем, а не всем телом.
  try{
    const j = JSON.parse(body);
    const m = j && (j.error || j.message || j.detail);
    if(m) return head + ': ' + m;
  }catch{ /* обычный текст */ }
  if(body.length > 400) body = body.slice(0, 400)+'…';
  return head + ': ' + body;
}

// notifyHttp — стандартная реакция на неуспешный ответ.
async function notifyHttp(res, prefix){
  notifyLevel(await httpErrText(res, prefix), 'error');
}

// ==== Диалоги подтверждения ====
//
// Нативный confirm() одинаково защищал и «очистить временные файлы», и
// «удалить версию вместе с файлами сборки»: одна и та же кнопка OK под Enter.
// Поэтому у опасного действия здесь своя модалка с красной кнопкой, которая не
// в фокусе.
//
// ВВОД ПОДТВЕРЖДАЮЩЕГО СЛОВА УБРАН.
//
// У трёх самых разрушительных действий (удалить версию, очистить обращения,
// удалить метрики) требовалось напечатать номер версии или слово «удалить».
// Защиты это не давало: администратор здесь один, он же и набирает эту строку,
// причём номер версии прямо перед глазами — переписать его можно не читая
// вообще ничего. А цена была настоящей: у каждого действия свой ритуал (где-то
// «удалить», где-то «удалить метрики», где-то номер), и рутинная операция вроде
// чистки старых версий превращалась в перепечатывание строк по одной.
//
// Осторожность даёт не барьер, а понимание последствий, поэтому вместо поля
// ввода — явный список того, что исчезнет, и можно ли это вернуть. Кнопка
// по-прежнему красная и не в фокусе: случайный Enter ничего не удалит.
//
// bullets — список последствий, по строке на пункт; читается быстрее сплошного
// абзаца, когда пунктов больше одного.
//
// Без bootstrap (тесты в jsdom, недоступный CDN раньше) откатываемся на
// нативный confirm: текст тот же, теряется только оформление.
function askConfirm(opts){
  const o = opts || {};
  const title = o.title || 'Подтверждение';
  const body = o.body || '';
  const bullets = Array.isArray(o.bullets) ? o.bullets.filter(Boolean) : [];
  const okText = o.okText || 'Продолжить';
  const danger = !!o.danger;
  if(!window.bootstrap || !window.bootstrap.Modal){
    const text = [title, body, ...bullets.map(b=>'• '+b)].filter(Boolean).join('\n\n');
    return Promise.resolve(!!window.confirm(text));
  }
  return new Promise((resolve)=>{
    const el = document.createElement('div');
    el.className = 'modal fade';
    el.setAttribute('tabindex','-1');
    el.innerHTML = ''+
      '<div class="modal-dialog modal-dialog-centered"><div class="modal-content">'+
      '  <div class="modal-header"><h5 class="modal-title">'+escapeHtml(title)+'</h5>'+
      '    <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Закрыть"></button></div>'+
      '  <div class="modal-body">'+
      '    <div class="preserve-ws">'+escapeHtml(body)+'</div>'+
      (bullets.length
        ? '    <ul class="mt-2 mb-0 small">'+bullets.map(b=>'<li>'+escapeHtml(b)+'</li>').join('')+'</ul>'
        : '')+
      '  </div>'+
      '  <div class="modal-footer">'+
      '    <button type="button" class="btn btn-secondary" data-bs-dismiss="modal" id="__ask_cancel">Отмена</button>'+
      '    <button type="button" class="btn '+(danger?'btn-danger':'btn-primary')+'" id="__ask_ok">'+escapeHtml(okText)+'</button>'+
      '  </div>'+
      '</div></div>';
    document.body.appendChild(el);
    const modal = new window.bootstrap.Modal(el);
    let answer = false;
    const okBtn = el.querySelector('#__ask_ok');
    // Фокус остаётся на «Отмена»: Enter не должен подтверждать удаление.
    el.addEventListener('shown.bs.modal', ()=> el.querySelector('#__ask_cancel')?.focus());
    okBtn.addEventListener('click', ()=>{ answer = true; modal.hide(); });
    el.addEventListener('hidden.bs.modal', ()=>{ el.remove(); resolve(answer); });
    modal.show();
  });
}

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
    if(txt){ txt.textContent = 'Выбран файл: '+f.name+' ('+formatBytes(f.size)+')'; }
    uploadSpaceCheck('up', f);
  });
})();

// ==== Tabs switching ====
// Единственная система вкладок живёт в showSection() ниже. Раньше здесь был
// второй, независимый набор обработчиков: обе системы вешались на одни и те же
// ссылки, tabManifests был привязан дважды, и один клик по вкладке порождал
// 4-6 лишних HTTP-запросов (списки версий, реестр игр, обращения). См. TAB_MAP.

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
    // Второй селектор — база сравнения. По умолчанию это версия, стоящая в
    // списке прямо перед показываемой: «что изменилось с прошлого релиза» —
    // ровно тот вопрос, ради которого сюда заходят.
    fillDiffSelect('ln_diff_ver', items.map(it=> it.version), sel.value||'');
    await lnPrevRender(sel.value||'');
  }catch(e){ const tree=document.getElementById('ln_tree'); if(tree) tree.textContent='Ошибка: '+e; }
}

// fillDiffSelect наполняет селектор «Сравнить с» всеми версиями, кроме
// показываемой, и выбирает предыдущую по порядку.
function fillDiffSelect(selectId, versions, current){
  const sel = document.getElementById(selectId); if(!sel) return;
  const prevValue = sel.value;
  sel.innerHTML = '';
  const none = document.createElement('option');
  none.value = ''; none.textContent = '— без сравнения —';
  sel.appendChild(none);
  const list = (versions||[]).filter(v=> v && v !== current);
  for(const v of list){
    const o = document.createElement('option'); o.value = v; o.textContent = v; sel.appendChild(o);
  }
  const idx = (versions||[]).indexOf(current);
  const suggested = idx > 0 ? versions[idx-1] : '';
  sel.value = list.includes(prevValue) ? prevValue : (list.includes(suggested) ? suggested : '');
}

// fetchManifest возвращает манифест версии или null (сообщение об ошибке —
// забота вызывающего: у превью игры и лаунчера разные адреса).
async function fetchManifest(url){
  try{
    const r = await fetch(url);
    if(!r.ok) return null;
    return await r.json();
  }catch{ return null; }
}

async function lnPrevRender(version){
  const tree = document.getElementById('ln_tree'); if(!tree) return;
  const sum = document.getElementById('ln_diff_summary');
  if(!version){ tree.textContent = 'Выберите версию лаунчера'; if(sum) sum.innerHTML=''; return; }
  tree.innerHTML = '<span class="text-body-secondary">Загрузка манифеста...</span>';
  try{
    const r = await fetch('/manifests/launcher/'+encodeURIComponent(version)+'.json');
    if(!r.ok){ tree.textContent = await httpErrText(r, 'Манифест '+version); return; }
    const manifest = await r.json();
    const baseVer = document.getElementById('ln_diff_ver')?.value || '';
    const base = baseVer ? await fetchManifest('/manifests/launcher/'+encodeURIComponent(baseVer)+'.json') : null;
    const diff = lnRenderTree(tree, manifest, base);
    if(sum) sum.innerHTML = baseVer && !base
      ? '<span class="text-danger">Манифест '+escapeHtml(baseVer)+' не прочитан</span>'
      : treeDiffSummaryHtml(diff, baseVer);
  }catch(e){ tree.textContent = 'Ошибка: '+e; }
}

// ==== Список версий (общий для лаунчера и игр) ====
//
// Раньше таблица состояла из версии, прочерка в колонке «Статус» и двух
// кнопок. По ней нельзя было ответить ни на один вопрос, который перед ней
// ставят: что это за сборка, когда собрана, сколько занимает, безопасно ли её
// удалять. Данные для этого сервер теперь отдаёт вместе со списком
// (ListVersions -> createdAt/files/bytes).

// Дата сборки в местной зоне; пустое значение — прочерк, а не «Invalid Date».
function fmtDateTime(v){
  const s = String(v||'').trim();
  if(!s) return '—';
  const d = new Date(s);
  if(isNaN(d.getTime())) return s;
  return d.toLocaleString('ru-RU', { year:'numeric', month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit' });
}

// «Активна» против «—» читалось как «нет данных». Версия, не помеченная
// latest, — это архив, и так и называется.
function versionsTableHtml(items, latest, cls){
  const rows = items.map(it=>{
    const ver = it.version || '';
    const isLatest = latest && ver === latest;
    const actBtn = isLatest
      ? '<span class="badge text-bg-success">latest</span>'
      : ('<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-primary '+cls+'-activate">Сделать активной</button>');
    const delBtn = '<button data-ver="'+escapeHtml(ver)+'" class="btn btn-sm btn-outline-danger ms-2 '+cls+'-delete">Удалить</button>';
    const status = isLatest
      ? '<span class="text-success">активна</span>'
      : '<span class="text-body-secondary">архив</span>';
    const files = Number(it.files||0);
    const bytes = Number(it.bytes||0);
    return '<tr>'
      + '<td class="text-monospace">'+escapeHtml(ver)+'</td>'
      + '<td>'+status+'</td>'
      + '<td class="text-body-secondary">'+escapeHtml(fmtDateTime(it.createdAt))+'</td>'
      + '<td class="text-end text-body-secondary">'+(files? escapeHtml(String(files)) : '—')+'</td>'
      + '<td class="text-end text-body-secondary">'+(bytes? escapeHtml(formatBytes(bytes)) : '—')+'</td>'
      + '<td class="text-end text-nowrap">'+actBtn+delBtn+'</td>'
      + '</tr>';
  }).join('');
  const total = items.reduce((a, it)=> a + Number(it.bytes||0), 0);
  const foot = items.length
    ? '<tfoot><tr><td colspan="4" class="text-body-secondary">Версий: '+items.length+'</td>'
      + '<td class="text-end text-body-secondary">'+escapeHtml(formatBytes(total))+'</td><td></td></tr></tfoot>'
    : '';
  return '<div class="table-responsive"><table class="table table-admin table-striped align-middle">'
    + '<thead><tr><th>Версия</th><th>Статус</th><th>Собрана</th>'
    + '<th class="text-end">Файлов</th><th class="text-end">Размер</th><th class="text-end"></th></tr></thead>'
    + '<tbody>'+(rows || '<tr><td colspan="6" class="text-body-secondary">Версий нет</td></tr>')+'</tbody>'
    + foot + '</table></div>';
}

// bindVersionActions вешает подтверждения на кнопки таблицы. Удаление версии
// сносит и манифест, и файлы сборки — это необратимо, поэтому здесь требуется
// ввести номер версии, а не просто нажать Enter на кнопке OK.
function bindVersionActions(root, cls, gameId, afterChange){
  root.querySelectorAll('.'+cls+'-activate').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      const ok = await askConfirm({
        title: 'Сделать версию активной',
        body: 'Версия '+ver+' станет latest: все лаунчеры увидят её как текущую и начнут обновляться на неё.',
        okText: 'Сделать активной',
      });
      if(!ok) return;
      let r;
      try{ r = await fetch('/admin/activate?gameId='+encodeURIComponent(gameId)+'&version='+encodeURIComponent(ver), {method:'POST'}); }
      catch(e){ notifyLevel('Не удалось активировать версию: '+e, 'error'); return; }
      if(!r.ok){ await notifyHttp(r, 'Активация версии '+ver); return; }
      notifyLevel('Версия '+ver+' активна', 'success');
      try{ await afterChange(); }catch(_){ /* обновление вида не критично */ }
    });
  });
  root.querySelectorAll('.'+cls+'-delete').forEach(btn=>{
    btn.addEventListener('click', async (ev)=>{
      const ver = ev.currentTarget.getAttribute('data-ver'); if(!ver) return;
      const ok = await askConfirm({
        title: 'Удалить версию '+ver+'?',
        body: 'Версия '+ver+' исчезнет с сервера.',
        bullets: [
          'Манифест и все файлы сборки удаляются с диска безвозвратно.',
          'Вернуть версию можно только повторной заливкой того же ZIP.',
          'Если версия сейчас активна (latest), обновляться станет не на что, пока вы не назначите активной другую.',
        ],
        okText: 'Удалить версию',
        danger: true,
      });
      if(!ok) return;
      let r;
      try{ r = await fetch('/admin/deleteVersion?gameId='+encodeURIComponent(gameId)+'&version='+encodeURIComponent(ver), {method:'POST'}); }
      catch(e){ notifyLevel('Не удалось удалить версию: '+e, 'error'); return; }
      if(!r.ok){ await notifyHttp(r, 'Удаление версии '+ver); return; }
      notifyLevel('Версия '+ver+' удалена', 'success');
      try{ await afterChange(); }catch(_){ /* обновление вида не критично */ }
    });
  });
}

// ==== Launcher versions list (right column on Launcher tab) ====
async function lnManifestsReload(){
  const root = document.getElementById('ln_ver_list'); if(!root) return;
  let res; try{ res = await fetch('/admin/list?gameId=launcher'); }catch(e){ root.textContent = 'Ошибка: '+e; return; }
  if(!res.ok){ root.textContent = await httpErrText(res, 'Список версий лаунчера'); return; }
  let j; try{ j = await res.json(); }catch(e){ root.textContent = 'Ошибка парсинга JSON'; return; }
  const latest = j.latest||'';
  const items = Array.isArray(j.items)? j.items: [];
  root.innerHTML = versionsTableHtml(items, latest, 'ln');
  bindVersionActions(root, 'ln', 'launcher', async ()=>{
    await lnManifestsReload();
    await lnRefresh();
    await lnPrevEnsureVersionsAndRender();
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
    __sysFreeBytes = Number.isFinite(bytes) ? bytes : null;
    // Пересчитать «влезет ли» для уже выбранных файлов.
    try{
      uploadSpaceCheck('up', window.__upDroppedFile || document.getElementById('up_zip')?.files?.[0]);
      uploadSpaceCheck('man', window.__manDroppedFile || document.getElementById('man_zip')?.files?.[0]);
    }catch(_){ /* карточки могут быть ещё не смонтированы */ }
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
// Последнее известное свободное место: нужно, чтобы прикинуть, влезет ли
// выбранный архив, не дожидаясь ответа сервера на каждый выбор файла.
let __sysFreeBytes = null;

// Заливка 10–15 ГБ, упавшая на «кончилось место», — это полчаса впустую.
// Проверка делается до старта: распакованная сборка занимает примерно вдвое
// больше самого ZIP (архив лежит во временном каталоге, пока разворачивается
// содержимое), поэтому запас считается с коэффициентом.
const UPLOAD_SPACE_FACTOR = 2.2;

function uploadSpaceCheck(prefix, file){
  const out = document.getElementById(prefix+'_fit');
  if(!out) return true;
  if(!file || __sysFreeBytes === null){ out.textContent = ''; out.className = 'small text-body-secondary'; return true; }
  const need = Math.round(file.size * UPLOAD_SPACE_FACTOR);
  const enough = need <= __sysFreeBytes;
  out.textContent = (enough ? '≈' : 'Не хватает места: нужно ≈')
    + formatBytes(need) + ' из ' + formatBytes(__sysFreeBytes) + ' свободных';
  out.className = 'small ' + (enough ? 'text-body-secondary' : 'text-danger');
  return enough;
}

// Валидация версии до отправки: поле принимало «1.39» и «1.2.3 » молча, а
// ошибка всплывала уже на сервере — после того, как ZIP уехал целиком.
function uploadVersionValid(ver){
  return /^\d+\.\d+\.\d+$/.test(String(ver||'').trim());
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
  // show latest and prefill upload version with next patch (пока поле не
  // трогали руками — иначе автоподстановка затирает набранное)
  const upVer = document.getElementById('up_ver'); if(upVer && !upVer.dataset.touched){ upVer.value = bumpSemverPatch(ver); }
  treeEl.innerHTML = '<div class="small text-body-secondary mb-1">Текущая версия лаунчера: <code>'+escapeHtml(ver)+'</code></div>'+
                     '<span class="text-body-secondary">Загрузка манифеста...</span>';
  let manifest; try{ const r2 = await fetch('/manifests/launcher/'+encodeURIComponent(ver)+'.json?t='+bust); if(!r2.ok){ treeEl.textContent = 'HTTP '+r2.status+' '+r2.statusText; return; } manifest = await r2.json(); }catch(e){ treeEl.textContent = 'Ошибка загрузки манифеста: '+e; return; }
  lnRenderTree(treeEl, manifest);
}

// ==== Дерево файлов манифеста ====
//
// Дерево лаунчера — это 478 файлов в пятнадцати языковых папках, и до сих пор
// с ним нельзя было сделать ничего: ни найти файл, ни свернуть всё разом, ни
// узнать, чем сборка отличается от предыдущей. Последнее — главный вопрос
// каждого релиза, и данные для ответа лежали рядом: селектор версии уже был.

// manifestFileMap: путь -> {size, hash} по одному манифесту.
function manifestFileMap(manifest){
  const m = new Map();
  const files = Array.isArray(manifest?.files) ? manifest.files : [];
  for(const f of files){
    const p = String(f.path||'').replace(/^\/+/, '');
    if(!p) continue;
    const sz = Number(f.size);
    m.set(p, { size: Number.isFinite(sz)? sz : 0, hash: String(f.blake3||f.sha256||'') });
  }
  return m;
}

// diffManifests сравнивает две карты файлов и возвращает статус по каждому
// пути текущей версии плюс список пропавших. Файл считается изменённым, если
// разошёлся хеш; при его отсутствии — по размеру.
function diffManifests(cur, base){
  const status = new Map();
  let added = 0, modified = 0;
  for(const [p, f] of cur){
    const b = base.get(p);
    if(!b){ status.set(p, 'add'); added++; continue; }
    const changed = (f.hash && b.hash) ? (f.hash !== b.hash) : (f.size !== b.size);
    if(changed){ status.set(p, 'mod'); modified++; }
    else status.set(p, 'same');
  }
  const removed = [];
  for(const [p] of base){ if(!cur.has(p)) removed.push(p); }
  return { status, added, modified, removed };
}

const TREE_MARK = { add: '+', mod: '~', del: '−' };

// treeRender строит DOM дерева из подготовленных данных. Вынесено отдельно от
// загрузки, потому что фильтр и «развернуть всё» перерисовывают то же самое,
// не ходя в сеть повторно.
function treeRender(rootEl){
  const data = rootEl.__treeData; if(!data) return;
  const { files, emptyDirs, diff, forceOpen } = data;
  const query = String(data.query||'').trim().toLowerCase();
  const onlyChanged = !!data.onlyChanged;

  // Список строк: файлы текущей версии плюс пропавшие из базовой (их надо
  // показать, иначе «удалено 3» не с чем сопоставить).
  const rows = files.map(f=>({
    path: f.path,
    size: f.size,
    state: diff ? (diff.status.get(f.path) || 'same') : 'same',
  }));
  if(diff){
    for(const p of diff.removed) rows.push({ path: p, size: 0, state: 'del' });
  }

  const visible = rows.filter(r=>{
    if(onlyChanged && r.state === 'same') return false;
    if(query && !r.path.toLowerCase().includes(query)) return false;
    return true;
  });

  const node = ()=>({children:new Map(), files:[]});
  const root = node();
  for(const r of visible){
    const parts = r.path.split('/').filter(Boolean);
    let cur = root;
    for(let i=0;i<parts.length-1;i++){
      const k = parts[i]; if(!cur.children.has(k)) cur.children.set(k, node()); cur = cur.children.get(k);
    }
    cur.files.push({ name: parts[parts.length-1] || '', size: r.size, state: r.state, path: r.path });
  }
  // Пустые папки показываем только когда ничего не отфильтровано: иначе они
  // создают ложное впечатление, что поиск что-то нашёл.
  if(!query && !onlyChanged){
    for(const d of emptyDirs){
      const parts = String(d||'').split('/').filter(Boolean);
      let cur = root;
      for(let i=0;i<parts.length;i++){
        const k = parts[i]; if(!cur.children.has(k)) cur.children.set(k, node()); cur = cur.children.get(k);
      }
    }
  }

  // Фильтр бесполезен, если найденное спрятано внутри свёрнутых папок.
  const openAll = forceOpen === true || (forceOpen !== false && (!!query || onlyChanged));

  const renderNode = (name, n, depth)=>{
    const folderIndent = (depth>1) ? 16*(depth-1) : 0;
    const twistyPad = 20;
    let html = '';
    if(name!==null){
      const dirCount = n.children.size;
      const fileCount = n.files.length;
      html += '<details class="tree-dir"'+(openAll?' open':'')+' style="margin-left:'+folderIndent+'px">'
           +  '<summary class="d-flex align-items-center tree-summary">'
           +    '<svg class="twisty me-2" width="12" height="12" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5l8 7-8 7V5z" fill="currentColor"/></svg>'
           +    '<span class="me-2">📁</span><strong>'+escapeHtml(name)+'</strong>'
           +    '<span class="ms-2 small text-body-secondary">('+dirCount+' папок, '+fileCount+' файлов)</span>'
           +  '</summary>';
    }
    const keys = Array.from(n.children.keys()).sort((a,b)=> a.localeCompare(b));
    for(const k of keys){ html += renderNode(k, n.children.get(k), depth+1); }
    for(const f of n.files.sort((a,b)=> a.name.localeCompare(b.name))){
      const filePad = (depth>0 ? 16*depth : 0) + twistyPad;
      const cls = (f.state && f.state!=='same') ? (' tree-'+f.state) : '';
      const mark = TREE_MARK[f.state] ? ('<span class="me-1">'+TREE_MARK[f.state]+'</span>') : '';
      const hit = query ? ' tree-row-hit' : '';
      const size = (f.state === 'del') ? '' : ('<span class="ms-auto small text-body-secondary" title="'+(Number.isFinite(f.size)?f.size:0)+' байт">'+formatBytes(f.size)+'</span>');
      html += '<div class="d-flex align-items-center'+hit+'" style="padding-left:'+filePad+'px" title="'+escapeHtml(f.path)+'">'
           + '<span class="me-2">📄</span><span class="'+cls.trim()+'">'+mark+escapeHtml(f.name)+'</span>'
           + size
           + '</div>';
    }
    if(name!==null){ html += '</details>'; }
    return html;
  };

  const head = query || onlyChanged
    ? 'Показано файлов: '+visible.length+' из '+rows.length
    : 'Всего файлов: '+files.length;
  const body = visible.length
    ? renderNode(null, root, 0)
    : '<div class="text-body-secondary">Ничего не найдено</div>';
  rootEl.innerHTML = '<div class="small text-body-secondary mb-1">'+escapeHtml(head)+'</div>' + body;
}

// lnRenderTree принимает манифест (и, необязательно, базовый для сравнения),
// запоминает разобранные данные на элементе и рисует их.
function lnRenderTree(rootEl, manifest, baseManifest){
  const files = [];
  for(const f of (Array.isArray(manifest?.files) ? manifest.files : [])){
    const p = String(f.path||'').replace(/^\/+/, '');
    const sz = Number(f.size);
    files.push({ path: p, size: Number.isFinite(sz)? sz : 0 });
  }
  const emptyDirs = Array.isArray(manifest?.emptyDirs)? manifest.emptyDirs : [];
  const diff = baseManifest
    ? diffManifests(manifestFileMap(manifest), manifestFileMap(baseManifest))
    : null;
  const prev = rootEl.__treeData || {};
  rootEl.__treeData = {
    files, emptyDirs, diff,
    query: prev.query || '',
    onlyChanged: false,
    forceOpen: undefined,
  };
  treeRender(rootEl);
  return diff;
}

// treeDiffSummaryHtml — строка «+3 / ~12 / −1» под селекторами версий.
function treeDiffSummaryHtml(diff, baseVer){
  if(!diff) return '';
  if(!diff.added && !diff.modified && !diff.removed.length){
    return '<span class="text-body-secondary">Отличий от '+escapeHtml(baseVer)+' нет</span>';
  }
  return 'Относительно <code>'+escapeHtml(baseVer)+'</code>: '
    + '<span class="tree-add">+'+diff.added+'</span> · '
    + '<span class="tree-mod">~'+diff.modified+'</span> · '
    + '<span class="tree-del">−'+diff.removed.length+'</span>';
}

// wireTreeControls связывает поле фильтра и кнопки «развернуть/свернуть всё»
// с деревом. Одинаково для вкладок «Лаунчер» (ln_) и «Игры» (gm_).
function wireTreeControls(prefix, treeId){
  const tree = document.getElementById(treeId); if(!tree) return;
  const q = document.getElementById(prefix+'_tree_q');
  const expand = document.getElementById(prefix+'_tree_expand');
  const collapse = document.getElementById(prefix+'_tree_collapse');
  const apply = (patch)=>{
    if(!tree.__treeData) return;
    Object.assign(tree.__treeData, patch);
    treeRender(tree);
  };
  if(q) q.addEventListener('input', debounce(()=> apply({ query: q.value, forceOpen: undefined }), 150));
  if(expand) expand.addEventListener('click', (e)=>{ e.preventDefault(); apply({ forceOpen: true }); });
  if(collapse) collapse.addEventListener('click', (e)=>{ e.preventDefault(); apply({ forceOpen: false }); });
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
        if(txt) txt.textContent = 'Выбран файл: '+file.name+' ('+formatBytes(file.size)+')';
        uploadSpaceCheck('up', file);
      }
    });
  }
  const manZip = document.getElementById('man_zip');
  if(manZip){
    manZip.addEventListener('change', (ev)=>{
      const file = ev.currentTarget.files && ev.currentTarget.files[0];
      if(file) uploadSpaceCheck('man', file);
    });
  }
  // Ручная правка версии отключает автоподстановку следующего патча: иначе
  // введённое затирается при первом же обновлении списка версий.
  ['ver','up_ver'].forEach(id=>{
    const el = document.getElementById(id); if(!el) return;
    el.addEventListener('input', ()=>{ el.dataset.touched = '1'; });
  });
});

// Кнопка очистки журнала.
document.addEventListener('DOMContentLoaded', ()=>{
  const btn = document.getElementById('journal_clear');
  if(btn) btn.addEventListener('click', (e)=>{ e.preventDefault(); __journal.length = 0; journalRender(); });
  journalRender();
});

// Init system free space UI (only manual refresh by button)
document.addEventListener('DOMContentLoaded', ()=>{
  const btn = document.getElementById('sys_free_refresh');
  if(btn){ btn.addEventListener('click', (e)=>{ e.preventDefault(); e.stopPropagation(); sysFreeRefresh(); }); }
  // Refresh immediately on admin UI load
  try{ sysFreeRefresh(); }catch{}
});

// Выход из панели. Сессию гасит сервер (кука HttpOnly, из JS её не стереть),
// поэтому после ответа просто уходим на форму входа — независимо от того,
// ответил сервер успехом или нет: держать оператора в панели, из которой он
// попросил выйти, хуже, чем лишний раз показать логин.
document.addEventListener('DOMContentLoaded', ()=>{
  const btn = document.getElementById('auth_logout');
  if(!btn) return;
  btn.addEventListener('click', async (e)=>{
    e.preventDefault();
    btn.disabled = true;
    try{ await fetch('/admin/api/auth/logout', { method:'POST' }); }
    catch{ /* сеть отвалилась — всё равно уводим на вход */ }
    // Отдельной страницы логина нет: /admin/ сам отдаёт login.html анониму
    // (см. handleAdminUI в cmd/admin/main.go).
    window.location.href = '/admin/';
  });
});

// ==== Feedback Inbox ====
let __fbItems = [];
let __fbSel = '';
let __fbPollTimer = null;
let __fbSeq = 0;        // порядковый номер запроса списка обращений
let __fbListHtml = '';  // последняя отрисованная разметка списка

function fbQueryParams(){
  const type = document.getElementById('fb_type')?.value||'';
  const status = document.getElementById('fb_status')?.value||'';
  const important = document.getElementById('fb_important')?.value||'';
  const q = document.getElementById('fb_q')?.value||'';
  const fromRaw = document.getElementById('fb_from')?.value||'';
  const toRaw = document.getElementById('fb_to')?.value||'';
  // Поля — <input type="datetime-local">, как на вкладках «Метрики» и
  // «Технические работы»: браузер отдаёт либо пустую строку, либо
  // 'YYYY-MM-DDTHH:mm' в местной зоне, и разбираются они теми же двумя
  // функциями, что и там. Верхняя граница округляется вверх до конца минуты
  // (см. mxLocalToUtcEnd): datetime-local не даёт секунд, и «по 19:17» иначе
  // отбрасывало бы обращение, отправленное в 19:17:30.
  const from = mtLocalToUtc(fromRaw) || '';
  const to = mxLocalToUtcEnd(toRaw) || '';
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

// Обращения хранятся в UTC, а разбирает их человек в Москве: раньше время
// показывалось «как в файле», и обращение, отправленное в 21:35, выглядело
// пришедшим в 18:35 — при разборе инцидента это расходится с логами и памятью
// пользователя. Зона прибита к Europe/Moscow, а не к зоне браузера: админку
// открывают и в дороге, а договариваться о времени надо в одной шкале.
const FB_TZ = 'Europe/Moscow';
function fbFmtTime(v){
  const s = String(v||'').trim();
  if(!s) return '';
  const d = new Date(s);
  if(isNaN(d.getTime())) return s;
  const p = {};
  new Intl.DateTimeFormat('ru-RU', {
    timeZone: FB_TZ,
    year:'numeric', month:'2-digit', day:'2-digit',
    hour:'2-digit', minute:'2-digit', second:'2-digit', hourCycle:'h23',
  }).formatToParts(d).forEach(x=>{ p[x.type] = x.value; });
  return p.year+'-'+p.month+'-'+p.day+' '+p.hour+':'+p.minute+':'+p.second+' МСК';
}

function fbRenderList(){
  const root = document.getElementById('fb_list'); if(!root) return;
  const cnt = document.getElementById('fb_count'); if(cnt) cnt.textContent = String(__fbItems.length||0);
  if(__fbItems.length===0){
    const empty = '<div class="text-body-secondary">Пусто</div>';
    if(empty !== __fbListHtml){ __fbListHtml = empty; root.innerHTML = empty; }
    return;
  }
  const html = __fbItems.map(it=>{
    const imp = it.important ? '<span class="badge text-bg-warning ms-2">важное</span>' : '';
    const st = (it.status==='read') ? '<span class="badge text-bg-secondary ms-2">проч.</span>' : '';
    const isAuto = !!(it && it.system && (it.system.auto==='1' || String(it.system.auto).toLowerCase()==='true'));
    const tlabel = (it && it.type==='bug' && isAuto) ? 'Баг (авто)' : (it?.type||'');
    const type = tlabel ? '<span class="badge text-bg-info ms-2">'+escapeHtml(tlabel)+'</span>' : '';
    const name = escapeHtml(it.name||'—');
    const contact = escapeHtml(it.contact||'');
    const cmt = escapeHtml((it.comment||'').slice(0,160));
    const dt = escapeHtml(fbFmtTime(it.createdAt));
    const active = (it.id===__fbSel) ? ' active' : '';
    return '<a href="#" class="list-group-item list-group-item-action'+active+'" data-id="'+it.id+'">'
         +   '<div class="d-flex w-100 justify-content-between"><strong>'+name+'</strong><small class="text-body-secondary">'+dt+type+imp+st+'</small></div>'
         +   '<div class="small text-body-secondary">'+contact+'</div>'
         +   '<div class="mt-1">'+cmt+'</div>'
         + '</a>';
  }).join('');
  // Поллинг раз в 12 с полностью переписывал innerHTML, из-за чего терялась
  // позиция скролла и выделение. Если разметка не изменилась — не трогаем DOM
  // вообще, а если изменилась — восстанавливаем прокрутку списка.
  if(html === __fbListHtml) return;
  __fbListHtml = html;
  const scrollTop = root.scrollTop;
  root.innerHTML = html;
  root.scrollTop = scrollTop;
  root.querySelectorAll('a.list-group-item').forEach(a=>{
    a.addEventListener('click', (ev)=>{ ev.preventDefault(); const id = a.getAttribute('data-id'); fbSelect(id); });
  });
}

async function fbReload(immediate){
  const qs = fbQueryParams();
  // Ответы приходят не в порядке отправки: без счётчика поиск «мигал» старыми
  // результатами, а фоновый поллинг мог затереть свежую выдачу фильтра.
  const seq = ++__fbSeq;
  let res; try{ res = await fetch('/admin/feedback/list'+(qs?'?'+qs:'')); }catch(e){ notify('Не удалось загрузить обращения: '+e); return; }
  if(seq !== __fbSeq) return;
  if(!res.ok){ notify('Не удалось загрузить обращения — HTTP '+res.status+' '+res.statusText); return; }
  let j; try{ j = await res.json(); }catch(e){ notify('Список обращений: сервер вернул не JSON'); return; }
  if(seq !== __fbSeq) return;
  __fbItems = Array.isArray(j.items)? j.items : [];
  fbRenderList();
  if(__fbSel){
    const exists = __fbItems.some(x=> x.id===__fbSel);
    if(!exists){ __fbSel = ''; document.getElementById('fb_view')?.replaceChildren(); }
  }
  if(immediate===true) return;
}

// Последнее открытое обращение целиком: нужно кнопкам «Ответить» и
// «Копировать дебаг», чтобы не ходить за ним на сервер повторно.
let __fbCur = null;

function fbSyncActions(){
  const has = !!__fbSel;
  ['fb_toggle_imp','fb_reply','fb_copy_debug','fb_close_view','fb_delete'].forEach(id=>{
    const el = document.getElementById(id); if(el) el.disabled = !has;
  });
  const sw = document.getElementById('fb_read_switch');
  if(sw){
    sw.disabled = !has;
    sw.checked = !!(__fbCur && __fbCur.status === 'read');
  }
  // Ответить можно только если человек оставил контакт, похожий на почту.
  const reply = document.getElementById('fb_reply');
  if(reply && has){
    const c = String(__fbCur?.contact||'').trim();
    const mail = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(c);
    reply.disabled = !mail;
    reply.title = mail ? ('Написать на '+c) : 'Контакт не указан или это не адрес почты';
  }
}

async function fbSelect(id){
  __fbSel = id||'';
  const view = document.getElementById('fb_view'); if(!view) return;
  if(!id){ view.textContent=''; __fbCur = null; fbSyncActions(); return; }
  let res; try{ res = await fetch('/admin/feedback/get?id='+encodeURIComponent(id)); }catch(e){ notify('Не удалось открыть обращение: '+e); return; }
  if(!res.ok){ await notifyHttp(res, 'Открытие обращения'); return; }
  let it; try{ it = await res.json(); }catch(e){ notify('Обращение: сервер вернул не JSON'); return; }
  __fbCur = it;
  const sys = it.system||{};
  const hasSys = Object.keys(sys).length > 0;
  // Высоты в vh, а не 240px: на 1440 под этими блоками оставалось пол-экрана
  // пустоты, а сама диагностика читалась в щели на восемь строк.
  const sysBlock = hasSys ? '<pre class="bg-body-tertiary p-2 border rounded panel-scroll panel-scroll-sm">'+escapeHtml(JSON.stringify(sys,null,2))+'</pre>' : '';
  const hasLogs = !!(it.attachLogs && it.logs);
  const logsBlock = hasLogs ? '<pre class="bg-body-tertiary p-2 border rounded panel-scroll panel-scroll-md">'+escapeHtml(String(it.logs))+'</pre>' : '';
  const debugBlock = (hasLogs || hasSys)
    ? '<details class="mt-3" open><summary>Дебаг-информация</summary>' + logsBlock + sysBlock + '</details>'
    : '';
  const isAuto = !!(sys && (sys.auto==='1' || String(sys.auto).toLowerCase()==='true'));
  const tlabel = (it && it.type==='bug' && isAuto) ? 'Баг (авто)' : (it?.type||'');
  view.innerHTML = ''+
    '<div class="d-flex align-items-center justify-content-between">'
    +  '<div><strong>'+escapeHtml(it.name||'—')+'</strong> <span class="text-body-secondary">'+escapeHtml(it.contact||'')+'</span></div>'
    +  '<div class="small text-body-secondary">'+escapeHtml(fbFmtTime(it.createdAt))+'</div>'
    +'</div>'
    +'<div class="mt-2"><span class="badge text-bg-info">'+escapeHtml(tlabel)+'</span>'+(it.important?'<span class="badge text-bg-warning ms-2">важное</span>':'')+(it.status==='read'?'<span class="badge text-bg-secondary ms-2">проч.</span>':'')+'</div>'
    +'<div class="mt-3 preserve-ws">'+escapeHtml(it.comment||'')+'</div>'
    + debugBlock;
  fbRenderList();
  fbSyncActions();
  // Auto-mark as read on open
  try{ await fetch('/admin/feedback/markRead?id='+encodeURIComponent(id), {method:'POST'}); }catch{}
  try{ await window.fbUnreadUpdateBadge(); }catch{}
  try{ await fbReload(true); }catch{}
}

async function fbAction(url){
  const id = __fbSel; if(!id) return;
  let res; try{ res = await fetch(url+'?id='+encodeURIComponent(id), { method:'POST' }); }catch(e){ notifyLevel('Действие не выполнено: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'Действие над обращением'); return; }
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
  bind('fb_reset_dates', ()=>{
    const f = document.getElementById('fb_from'); const t = document.getElementById('fb_to');
    if(f) f.value = ''; if(t) t.value = '';
    fbReload(true);
  });
  bind('fb_clear', async ()=>{
    const n = __fbItems.length;
    const ok = await askConfirm({
      title: 'Удалить все обращения?',
      body: 'Сейчас в выдаче '+n+', но удаляются все — фильтры и период на удаление не влияют.',
      bullets: [
        'Тексты обращений, контакты и приложенная диагностика стираются целиком.',
        'Копии нет: восстановить обращения неоткуда.',
      ],
      okText: 'Удалить всё',
      danger: true,
    });
    if(!ok) return;
    let r;
    try{ r = await fetch('/admin/feedback/clear',{method:'POST'}); }
    catch(e){ notifyLevel('Не удалось очистить обращения: '+e, 'error'); return; }
    if(!r.ok){ await notifyHttp(r, 'Очистка обращений'); return; }
    __fbSel=''; __fbCur = null; document.getElementById('fb_view')?.replaceChildren();
    await fbReload(true);
    notifyLevel('Обращения очищены.', 'success');
  });
  // Один переключатель вместо пары кнопок «Прочитано» / «Пометить непрочитанным»:
  // это одно состояние, а не два действия.
  const readSwitch = document.getElementById('fb_read_switch');
  if(readSwitch){
    readSwitch.addEventListener('change', ()=>{
      if(!__fbSel){ readSwitch.checked = false; return; }
      fbAction(readSwitch.checked ? '/admin/feedback/markRead' : '/admin/feedback/markUnread');
    });
  }
  bind('fb_toggle_imp', ()=> fbAction('/admin/feedback/toggleImportant'));
  bind('fb_delete', async ()=>{
    if(!__fbSel) return;
    const ok = await askConfirm({
      title: 'Удалить обращение?',
      body: 'Запись пропадёт вместе с приложенной диагностикой.',
      okText: 'Удалить',
      danger: true,
    });
    if(ok) fbAction('/admin/feedback/delete');
  });
  // Цикл обратной связи обрывался на просмотре: контакт был, а ответить —
  // нечем. mailto открывает почтовый клиент с уже подставленной темой.
  bind('fb_reply', ()=>{
    const c = String(__fbCur?.contact||'').trim();
    if(!c){ notify('В обращении нет контакта'); return; }
    const subject = 'Chill Hub: ответ на ваше обращение';
    const quoted = String(__fbCur?.comment||'').split(/\r?\n/).map(l=> '> '+l).join('\n');
    const body = '\n\n---\nВаше обращение от '+fbFmtTime(__fbCur?.createdAt)+':\n'+quoted+'\n';
    location.href = 'mailto:'+encodeURIComponent(c)+'?subject='+encodeURIComponent(subject)+'&body='+encodeURIComponent(body);
  });
  bind('fb_copy_debug', async ()=>{
    if(!__fbCur){ notify('Обращение не выбрано'); return; }
    const parts = [];
    if(__fbCur.logs) parts.push(String(__fbCur.logs));
    if(__fbCur.system && Object.keys(__fbCur.system).length) parts.push(JSON.stringify(__fbCur.system, null, 2));
    const text = parts.join('\n\n');
    if(!text){ notify('В обращении нет диагностики'); return; }
    try{ await navigator.clipboard.writeText(text); notifyLevel('Диагностика скопирована в буфер обмена', 'success'); }
    catch(e){ notifyLevel('Не удалось скопировать: '+e, 'error'); }
  });
  // Close view button clears selection
  const closeBtn = document.getElementById('fb_close_view');
  if(closeBtn){ closeBtn.addEventListener('click', (e)=>{ e.preventDefault(); __fbSel=''; __fbCur = null; document.getElementById('fb_view')?.replaceChildren(); fbRenderList(); fbSyncActions(); }); }
  fbSyncActions();
  // Filters live change
  // Поиск раньше слал запрос на каждое нажатие клавиши, хотя debounce в файле
  // уже был. Порядок ответов гарантирует счётчик внутри fbReload.
  const fbSearchReload = debounce(()=> fbReload(true), 300);
  ['fb_type','fb_status','fb_important','fb_q','fb_from','fb_to'].forEach(id=>{
    const el = document.getElementById(id); if(!el) return;
    el.addEventListener('change', ()=> fbReload(true));
    if(id==='fb_q') el.addEventListener('input', fbSearchReload);
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
  let res; try{ res = await fetch('/admin/list?gameId='+encodeURIComponent(gid)); }catch(e){ notifyLevel('Не удалось получить список версий: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'Список версий '+gid); return; }
  let j; try{ j = await res.json(); }catch(e){ notifyLevel('Список версий: сервер вернул не JSON', 'error'); return; }
  const root = document.getElementById('ver_list'); if(!root) return;
  const latest = j.latest||'';
  const items = Array.isArray(j.items)? j.items: [];
  root.innerHTML = versionsTableHtml(items, latest, 'man');
  bindVersionActions(root, 'man', gid, async ()=>{
    await manifestsReload();
    const curGid = (document.getElementById('gid')?.value||'').trim();
    if(curGid) await gmPrevEnsureVersionsAndRender(curGid);
  });
  // Следующая версия подставляется по последней существующей: поле по
  // умолчанию показывало 1.0.0 даже когда на сервере уже лежала 1.1.0.
  const verEl = document.getElementById('ver');
  if(verEl && !verEl.dataset.touched){
    const known = items.map(it=> it.version).filter(Boolean);
    const base = latest || known[known.length-1] || '';
    verEl.value = base ? bumpSemverPatch(base) : '1.0.0';
  }
}

// runUploadBench and applyBenchBest are thin DOM wiring around the testable
// core in upload-bench.js (parseBenchList, benchCombos, benchUploadOnce,
// pickClosestChunkOption) — that file is loaded as a separate <script> before
// this one specifically so it can be covered by tests/web/*.test.js as an
// ordinary required module, see the comment at its top.
// benchInputs собирает и проверяет поля теста. Возвращает null и сообщает
// причину, если вводом пользоваться нельзя.
function benchInputs(quiet){
  const file = document.getElementById('bench_zip')?.files?.[0];
  const probeMB = Number(document.getElementById('bench_probe_mb')?.value||256)||256;
  const probeBytes = Math.max(1, probeMB)*1024*1024;
  const chunkSizesMB = parseBenchList(document.getElementById('bench_chunks_mb')?.value, 1);
  const concs = parseBenchList(document.getElementById('bench_concs')?.value, 1).map(n=> Math.max(1, Math.min(100, Math.round(n))));
  if(!file){ if(!quiet) notify('Выберите файл для теста'); return null; }
  if(!chunkSizesMB.length || !concs.length){
    if(!quiet) notify('Укажите хотя бы один размер чанка и одну параллельность');
    return null;
  }
  return { file, probeBytes, chunkSizesMB, concs };
}

// benchShowPlan пишет под формой, во что обойдётся прогон: сколько ячеек,
// сколько байт уедет на сервер и сколько это займёт при последней измеренной
// скорости. Пересчитывается при каждой правке полей — до нажатия кнопки.
function benchShowPlan(){
  const el = document.getElementById('bench_plan'); if(!el) return;
  const inp = benchInputs(true);
  if(!inp){ el.textContent = ''; return; }
  const plan = benchPlan(inp.chunkSizesMB, inp.concs, inp.probeBytes, inp.file.size);
  const known = Number(window.__benchLastSpeed||0);
  const eta = known > 0 ? (' · при последней измеренной скорости '+formatSpeed(known)+' это ≈ '+formatEta(plan.totalBytes/known)) : '';
  // Больше десяти гигабайт пробы — это уже полноценная заливка сборки,
  // о которой стоит сказать заранее, а не после часа ожидания.
  const heavy = plan.totalBytes > 10*1024*1024*1024;
  el.className = 'small ' + (heavy ? 'text-warning' : 'text-body-secondary');
  el.textContent = 'Прогон: ' + plan.combos.length + ' комбинаций, будет залито и отброшено '
    + formatBytes(plan.totalBytes) + eta
    + (heavy ? ' — это много: уменьшите пробу или число комбинаций.' : '');
}

// Флаг остановки: прогон на два часа обязан прерываться, а не «дождитесь
// конца сетки». Объект, а не булево, чтобы benchUploadOnce видела изменение.
let __benchAbort = { aborted: false };

async function runUploadBench(){
  const inp = benchInputs(); if(!inp) return;
  const { file, probeBytes, chunkSizesMB, concs } = inp;

  const statusEl = document.getElementById('bench_status');
  const table = document.getElementById('bench_table'); const tbody = document.getElementById('bench_tbody');
  const applyWrap = document.getElementById('bench_apply_wrap'); const bestEl = document.getElementById('bench_best');
  const progWrap = document.getElementById('bench_progress');
  const pb = document.getElementById('bench_pb');
  const stepPb = document.getElementById('bench_step_pb');
  const stepEl = document.getElementById('bench_step');
  const stepBytesEl = document.getElementById('bench_step_bytes');
  const speedEl = document.getElementById('bench_speed');
  const elapsedEl = document.getElementById('bench_elapsed');
  const etaEl = document.getElementById('bench_eta');
  const stopBtn = document.getElementById('bench_stop');

  if(table) table.style.display='table';
  if(applyWrap) applyWrap.style.display='none';
  if(tbody) tbody.innerHTML='';
  if(progWrap) progWrap.style.display='';
  if(stopBtn) stopBtn.style.display='';

  const plan = benchPlan(chunkSizesMB, concs, probeBytes, file.size);
  const combos = plan.combos;
  const results = [];
  __benchAbort = { aborted: false };

  // Скорость по скользящему окну — тем же способом, что и на заливке сборки
  // (см. rate-estimator.js). Окно шире, чем там: прогресс приходит целыми
  // чанками, и на 256 МБ это событие раз в несколько секунд.
  const est = makeRateEstimator(30000);
  const t0 = performance.now();
  let doneBytes = 0;        // подтверждено за весь прогон
  let stepDone = 0;         // подтверждено в текущей комбинации
  let stepTotal = 0;
  let stepIdx = 0;

  // Живой скорости верим только после того, как окно наберёт несколько секунд:
  // подтверждения чанков в начале прилетают пачкой (все потоки стартуют разом),
  // и разница «байты / доли секунды» давала на экране сотни МБ/с и «осталось
  // 0:00» при готовности в одну восьмую. До этого показываем среднюю.
  const LIVE_SPEED_MIN_SPAN_MS = 3000;

  const paint = ()=>{
    const elapsedSec = (performance.now() - t0)/1000;
    const live = est.spanMs() >= LIVE_SPEED_MIN_SPAN_MS ? est.rate() : 0;
    const p = benchProgress({ doneBytes, totalBytes: plan.totalBytes, elapsedSec, liveSpeed: live });
    if(pb){
      const w = p.pct.toFixed(1)+'%';
      pb.style.width = w;
      pb.textContent = Math.round(p.pct)+'%';
      pb.setAttribute('aria-valuenow', String(Math.round(p.pct)));
    }
    if(stepPb) stepPb.style.width = (stepTotal>0 ? Math.min(100, stepDone*100/stepTotal) : 0)+'%';
    if(stepBytesEl) stepBytesEl.textContent = stepTotal>0
      ? ('шаг: '+formatBytes(stepDone)+' из '+formatBytes(stepTotal))
      : '';
    if(speedEl){
      speedEl.textContent = live>0
        ? ('скорость '+formatSpeed(live))
        : (p.avgSpeed>0 ? ('средняя '+formatSpeed(p.avgSpeed)) : 'скорость —');
    }
    if(elapsedEl) elapsedEl.textContent = 'прошло '+formatEta(elapsedSec);
    if(etaEl) etaEl.textContent = p.etaSec !== null
      ? ('осталось ≈ '+formatEta(p.etaSec)+' · всего '+formatBytes(plan.totalBytes))
      : ('всего '+formatBytes(plan.totalBytes));
  };

  // Тик раз в секунду: время и остаток обязаны идти даже тогда, когда внутри
  // шага ещё ни один чанк не подтверждён. Именно это и выглядело как
  // «зависло» — строка не менялась минутами.
  paint();
  const ticker = setInterval(paint, 1000);

  try{
    for(let i=0;i<combos.length;i++){
      if(__benchAbort.aborted) break;
      const {cs, c, bytes} = combos[i];
      stepIdx = i+1; stepDone = 0; stepTotal = bytes;
      if(stepEl) stepEl.textContent = 'комбинация '+stepIdx+'/'+combos.length+': чанк '+cs+' МБ, '+c+' потоков';
      if(statusEl) statusEl.textContent = '';
      paint();

      const base = doneBytes;
      const r = await benchUploadOnce(file, cs, c, probeBytes, {
        signal: __benchAbort,
        onProgress: (pr)=>{
          stepDone = pr.uploadedBytes;
          stepTotal = pr.totalSize;
          doneBytes = base + pr.uploadedBytes;
          est.push(performance.now(), doneBytes);
          paint();
        },
      });

      // Байты незавершённого шага в общий зачёт не идут: он будет отброшен.
      if(r.ok){ doneBytes = base + r.bytes; } else { doneBytes = base; }

      const row = document.createElement('tr');
      if(r.ok){
        results.push(r);
        window.__benchLastSpeed = r.speed;
        row.innerHTML = '<td>'+formatBytes(r.chunkSize)+'</td><td>'+r.concurrency+'</td><td>'+formatSpeed(r.speed)+'</td><td>'+formatEta(r.seconds)+'</td>';
      } else if(r.aborted){
        row.innerHTML = '<td>'+cs+' МБ</td><td>'+c+'</td><td colspan="2" class="text-body-secondary">остановлено</td>';
      } else {
        row.innerHTML = '<td>'+cs+' МБ</td><td>'+c+'</td><td colspan="2" class="text-danger">'+escapeHtml(r.error||'ошибка')+'</td>';
      }
      if(tbody) tbody.appendChild(row);
      paint();
    }
  } finally {
    clearInterval(ticker);
    if(stopBtn) stopBtn.style.display='none';
    if(stepEl) stepEl.textContent = '';
    paint();
  }

  const spent = formatEta((performance.now()-t0)/1000);
  if(statusEl){
    statusEl.textContent = __benchAbort.aborted
      ? ('Остановлено на '+stepIdx+'-й комбинации из '+combos.length+'. Успешных замеров: '+results.length+'. Потрачено '+spent+'.')
      : ('Готово за '+spent+'. Проверено комбинаций: '+combos.length+', успешно: '+results.length+'.');
  }
  if(results.length){
    results.sort((a,b)=> b.speed - a.speed);
    const best = results[0];
    window.__benchBest = best;
    if(bestEl) bestEl.textContent = formatBytes(best.chunkSize)+' / '+best.concurrency+' поток(ов) — '+formatSpeed(best.speed);
    if(applyWrap) applyWrap.style.display='flex';
  }
  benchShowPlan();
}

function applyBenchBest(chunkSelId, concSliderId, concValId){
  const best = window.__benchBest; if(!best){ notify('Сначала запустите тест'); return; }
  const sel = document.getElementById(chunkSelId);
  if(sel){
    const values = Array.from(sel.options).map(opt=> Number(opt.value)||0);
    const closest = pickClosestChunkOption(values, best.chunkSize);
    if(closest!==null) sel.value = String(closest);
  }
  const slider = document.getElementById(concSliderId); const val = document.getElementById(concValId);
  if(slider){ slider.value = String(Math.max(1, Math.min(100, best.concurrency))); slider.dispatchEvent(new Event('input')); }
  if(val) val.textContent = slider ? slider.value : String(best.concurrency);
  notify('Параметры применены: '+formatBytes(best.chunkSize)+' / '+best.concurrency+' поток(ов)');
}

// runChunkedUpload drives the resumable init/chunk/complete/process pipeline
// against a ZIP file, updating the progress UI rooted at `${prefix}_*` ids
// (mirroring the man_* markup this was extracted from: prog_wrap/pb/
// prog_pct/..., chunk_size, conc(+_val), active_now/active_cap,
// speed_wrap/speed). Shared by the game upload card (`man`) and the
// launcher upload card (`up`) so both get the same parallel chunked
// pipeline — the launcher card used to POST the whole ZIP as one unchunked
// XHR request, which is exactly the "3 МБ/с и не разгоняется" bottleneck
// that started this whole investigation.
// Returns true once the archive is published (extracted + manifest written).
async function runChunkedUpload(prefix, kind, gameId, version, file){
  const id = (suffix)=> document.getElementById(prefix+'_'+suffix);
  const gid = gameId, ver = version;
  const wrap=id('prog_wrap'); const bar=id('pb');
  const pctEl=id('prog_pct'); const bytesEl=id('prog_bytes');
  const speedEl=id('prog_speed'); const medianEl=id('prog_median'); const peakEl=id('prog_peak'); const etaEl=id('prog_eta');
  const txt = id('prog_text');
  if(wrap) wrap.style.display='block';
  if(bar) bar.style.width='0%';
  if(pctEl) pctEl.textContent='Подготовка к загрузке...';
  clearStatusError(txt, '');

  // UI controls: chunk size and concurrency
  const chunkSel = id('chunk_size');
  let desiredChunk = Number(chunkSel?.value||0)|0; if(desiredChunk<=0) desiredChunk = 8*1024*1024;
  const concSlider = id('conc');
  const concVal = id('conc_val');
  const activeNowEl = id('active_now');
  const activeCapEl = id('active_cap');
  let userPar = Number(concSlider?.value||6)|0; if(userPar<1) userPar=1; if(userPar>100) userPar=100;
  if(concVal) concVal.textContent = String(userPar);
  if(activeCapEl) activeCapEl.textContent = String(userPar);
  if(activeNowEl) activeNowEl.textContent = '0';
  // «активно 0/0» в простое — шум: счётчик показывается только на время
  // заливки, когда он что-то значит.
  const activeWrap = id('active_wrap');
  if(activeWrap) activeWrap.style.display='';
  const speedWrap = id('speed_wrap'); const speedCanvas = id('speed');
  if(speedWrap) speedWrap.style.display='block';
  if(speedCanvas) speedCanvas.style.height='180px';
  let speedPoints = []; // [{t, bps}]
  let peakBps = 0;
  const HORIZON_MS = 120000; // 2 minutes window

  // INIT
  let initRes; try{
    console.group('Upload ZIP');
    console.time('upload_total');
    console.log('[init] request', { kind, gid, ver, file: { name: file.name, size: file.size }, chunkSize: desiredChunk, userPar });
    initRes = await fetch('/admin/api/upload/init', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({
      kind, gameId: gid, version: ver, zipName: file.name, totalSize: file.size, chunkSize: desiredChunk
    }) });
  }catch(e){ setStatusError(txt, 'Ошибка init: '+e); notify('Ошибка init: '+e); console.groupEnd(); return false; }
  if(!initRes.ok){ setStatusError(txt, 'HTTP '+initRes.status+' init'); notify('HTTP '+initRes.status+' init'); console.groupEnd(); return false; }
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
  const failedChunks = [];
  // Concurrency is strictly user-controlled (1:1 with slider), clamped to [1..100]
  const maxCap = 100;
  let curPar = Math.max(1, Math.min(100, userPar));
  console.log('[resume] already have', received.size, 'chunks; scheduling', allIdx.length, 'chunks; chunkSize=', chunkSize, 'startPar=', curPar);

  // inFlight — байты, уже застриманные в ещё НЕ завершённые чанки (ключ —
  // индекс чанка). uploadedBytes считает только целиком подтверждённые
  // чанки; без inFlight прогресс на крупных чанках (десятки-сотни МБ) рос бы
  // скачками раз в десятки секунд — см. комментарий в шапке chunk-upload.js
  // про то, почему это само по себе выглядело как «загрузка не начинается»
  // и «скорость меряется неправильно».
  const inFlight = new Map();

  // 200мс, а не 500 — на чанках размером в десятки МБ пять обновлений в
  // секунду всё ещё дешёвы (перерисовка холста и пара textContent), а разница
  // ощущается: полоса и график иначе кажутся "подвисающими" рывками.
  const UI_INTERVAL=200;
  // Скорость считается не между соседними тиками (200мс), а по скользящему
  // окну — см. комментарий в шапке rate-estimator.js про то, почему рывки
  // буфера сокета иначе дают всплески в сотни МБ/с. 5с — компромисс между
  // тем, чтобы сгладить рывок целиком, и тем, чтобы цифра не казалась
  // заторможенной на быстрой/короткой загрузке.
  //
  // Экспоненциального сглаживания поверх окна намеренно нет: соседние окна
  // перекрываются почти целиком, так что усреднения EMA уже не добавляет —
  // только задержку, из-за которой скорость и ETA отставали от реальности на
  // старте и на финише заливки.
  const rateEstimator = makeRateEstimator(5000);
  let shownSpeed = 0;
  function updateUI(now){
    const displayed = Math.min(totalBytes, uploadedBytes + pendingBytes(inFlight));
    const pct = Math.floor((displayed*100)/totalBytes);
    if(bar) bar.style.width = pct+'%';
    // Пока окно не набрало двух точек (первый тик) скорость неизвестна —
    // показываем прошлую, а не мигаем пустотой.
    const inst = rateEstimator.push(now, displayed);
    if(inst>0) shownSpeed = inst;
    const remain = Math.max(0, totalBytes - displayed); const eta = (shownSpeed>0)? (remain/shownSpeed):0;
    // «Пик» теперь означает максимум пятисекундного среднего, а не максимум
    // мгновенной производной — та показывала рывок буфера сокета и за любую
    // длинную заливку успевала упереться в число, которого канал никогда не
    // выдавал. Медиана считается по точкам графика за HORIZON_MS (120с).
    if(inst>0){ peakBps = Math.max(peakBps, inst); }
    const horizon = HORIZON_MS; const windowPts = speedPoints.filter(p=> now-p.t <= horizon);
    let medianBps = 0; if(windowPts.length>0){ const arr = windowPts.map(p=> p.bps).sort((a,b)=>a-b); const mid = Math.floor(arr.length/2); medianBps = arr.length%2 ? arr[mid] : ((arr[mid-1]+arr[mid])/2); }
    if(pctEl) pctEl.textContent = 'Загружено '+pct+'%';
    if(bytesEl) bytesEl.textContent = '('+formatBytes(displayed)+' / '+formatBytes(totalBytes)+')';
    if(speedEl) speedEl.textContent = shownSpeed>0 ? formatSpeed(shownSpeed) : '';
    if(medianEl) medianEl.textContent = medianBps>0 ? ('мед '+formatSpeed(medianBps)) : '';
    if(peakEl) peakEl.textContent = peakBps>0 ? ('пик '+formatSpeed(peakBps)) : '';
    if(etaEl) etaEl.textContent = eta>0 ? ('ETA '+formatEta(eta)) : '';
    if(inst>0){
      // Keep raw points for median calc and for the chart
      speedPoints.push({t: now, bps: inst});
      const horizon = HORIZON_MS; // 120s window
      while(speedPoints.length>0 && (now - speedPoints[0].t) > horizon){ speedPoints.shift(); }
    }
    if(speedCanvas){
      try{ drawSpeedChart(speedCanvas, speedPoints, { now, horizonMs: HORIZON_MS, peakBps, formatSpeed }); }catch(_){ }
    }
  }
  // See ui-throttle.js for why this goes through setTimeout instead of
  // requestAnimationFrame: rAF stops firing the moment this tab is
  // backgrounded, which used to freeze the percentage, speed and the graph
  // for the rest of a long upload — looking exactly like "the graph doesn't
  // draw at all".
  const uiThrottle = makeUiThrottler(UI_INTERVAL, ()=> updateUI(performance.now()));
  function scheduleUI(){ uiThrottle.schedule(); }

  const win = []; // recent writeMs per chunk
  const WIN_MAX = 50;

  async function uploadOne(i){
    const start = i*chunkSize; const end = Math.min(start+chunkSize, file.size);
    const blob = file.slice(start, end);
    const b = (end-start);
    const r = await uploadChunkWithRetries(uploadId, i, blob, {
      url: '/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+i,
      onProgress: (loaded)=>{ inFlight.set(i, loaded); scheduleUI(); },
      onAttemptFailed: (info)=> console.warn('[chunk fail]', info),
    });
    inFlight.delete(i);
    if(r.ok){
      if(r.writeMs>0){ win.push(r.writeMs); if(win.length>WIN_MAX) win.shift(); }
      // 409 (r.exists) — чанк уже лежит на сервере (гонка с ретраем или
      // устаревший ответ /status). Байты всё равно надо засчитать: чанк на
      // месте, а без прибавки прогресс-бар недосчитывал бы его до самого
      // конца и не доходил до 100%.
      if(r.exists){ console.log('[chunk skip:exists]', { index:i }); } else if(r.attempts>1){ console.log('[chunk ok after retry]', { index:i, attempts:r.attempts, bytes:b, writeMs:r.writeMs }); } else { console.log('[chunk ok]', { index:i, bytes:b, writeMs:r.writeMs, par:curPar }); }
      uploadedBytes += b; scheduleUI();
    }
    return r.ok;
  }

  // Handle live concurrency changes: curPar is read fresh by runWorkerPool
  // before scheduling each next worker, so moving the slider mid-upload just
  // takes effect on the next slot instead of needing a restart.
  if(concSlider){ concSlider.addEventListener('input', ()=>{ userPar = Number(concSlider.value|0); if(userPar<1) userPar=1; if(userPar>100) userPar=100; if(concVal) concVal.textContent=String(userPar); if(activeCapEl) activeCapEl.textContent = String(userPar);
    curPar = Math.max(1, Math.min(100, userPar));
  }); }

  console.log('[upload] start', { curPar, maxCap, totalChunks, pending: allIdx.length });
  const poolFailed = await runWorkerPool(allIdx, ()=> curPar, uploadOne, (active)=>{ if(activeNowEl) activeNowEl.textContent = String(active); });
  failedChunks.push(...poolFailed);
  // Force one final, unthrottled UI sync: on a fast link (or a small build) the
  // whole chunk phase can finish inside a single UI_INTERVAL window, so the
  // scheduled updateUI() from the last chunk may never have run yet. Without
  // this, the bar/graph can sit at their pre-upload state through the entire
  // complete+process phase, which reads the same as the frozen-graph bug above.
  updateUI(performance.now());

  async function uploadOneRetry(idx){
    const s = idx*chunkSize; const e = Math.min(s+chunkSize, file.size);
    const bl = file.slice(s, e);
    const r = await uploadChunkWithRetries(uploadId, idx, bl, {
      url: '/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+idx,
      onProgress: (loaded)=>{ inFlight.set(idx, loaded); scheduleUI(); },
      onAttemptFailed: (info)=> console.warn('[retry chunk fail]', info),
    });
    inFlight.delete(idx);
    if(r.ok){ uploadedBytes += (e-s); scheduleUI(); if(r.attempts>1){ console.log('[retry ok]', { index:idx, attempts:r.attempts }); } }
    return r.ok;
  }

  // Retry pass for failed chunks (if any)
  if(failedChunks.length>0){
    console.group('[retry pass] re-upload failed chunks');
    console.log('failedChunks count', failedChunks.length);
    const stillFailed = await runWorkerPool(failedChunks, ()=> curPar, uploadOneRetry, (active)=>{ if(activeNowEl) activeNowEl.textContent = String(active); });
    console.groupEnd();
    if(stillFailed.length>0){ console.timeEnd('upload_total'); console.groupEnd(); const m='Повторная загрузка неудачных чанков завершилась с ошибкой'; setStatusError(txt, m); notify(m); return false; }
  }

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
      async function runMissing(i){
        const start = i*chunkSize; const end = Math.min(start+chunkSize, file.size);
        const blob = file.slice(start, end);
        const r = await uploadChunkWithRetries(uploadId, i, blob, {
          url: '/admin/api/upload/chunk?uploadId='+encodeURIComponent(uploadId)+'&index='+i,
          maxAttempts: 3, retryDelayMs: 300,
          onProgress: (loaded)=>{ inFlight.set(i, loaded); scheduleUI(); },
        });
        inFlight.delete(i);
        if(r.ok){ uploadedBytes += (end-start); scheduleUI(); }
        return r.ok;
      }
      const missingFailed = await runWorkerPool(missing, ()=> curPar, runMissing, (active)=>{ if(activeNowEl) activeNowEl.textContent = String(active); });
      if(missingFailed.length>0){ notify('Повторная загрузка пропущенных чанков завершилась с ошибкой'); return false; }
      // try complete next round
    }
    return false;
  }

  const okComplete = await uploadMissingAndRetryComplete(3);
  if(!okComplete){ console.timeEnd('upload_total'); console.groupEnd(); const m='Ошибка завершения загрузки (complete)'; setStatusError(txt, m); notify(m); return false; }
  if(txt) txt.textContent = 'Сервера проверяет sha256 и готовит распаковку...';

  // PROCESS (NDJSON)
  let processOk = true;
  try{
    console.log('[process] start');
    const url = '/admin/api/upload/process?uploadId='+encodeURIComponent(uploadId);
    // Метод обязателен: обработчик распаковывает архив, публикует версию и удаляет
    // ZIP, то есть меняет состояние. CSRF-проверка на сервере действует только для
    // POST/PUT/PATCH/DELETE, поэтому GET оставлял бы эту операцию без защиты.
    // Заголовок X-CSRF-Token подставит обёртка fetch в начале файла.
    const res = await fetch(url, { method:'POST', headers: { 'Accept':'application/x-ndjson', 'Cache-Control':'no-store' } });
    if(!res.ok){ setStatusError(txt, 'HTTP '+res.status+' process'); notify('HTTP '+res.status+' process'); console.timeEnd('upload_total'); console.groupEnd(); return false; }
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
          else if(ev.type==='error'){
            console.warn('[process error]', ev.message);
            // notify() пишет в #out — маленький <pre> в самом низу страницы,
            // до которого ещё надо долистать. Без обновления txt (заметная
            // строка статуса прямо под прогрессом) экран так и остаётся на
            // "Старт обработки: ..." навсегда, а ошибка тихо ждёт внизу —
            // ровно то, что выглядит как "ничего не произошло".
            setStatusError(txt, 'Ошибка обработки: '+(ev.message||'unknown'));
            notify('Ошибка: '+(ev.message||'unknown'));
            processOk = false;
          }
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
        else if(ev.type==='error'){
          setStatusError(txt, 'Ошибка обработки: '+(ev.message||'unknown'));
          notify('Ошибка: '+(ev.message||'unknown'));
          processOk = false;
        }
      }catch{} }
    }
    if(!gotAny){ console.warn('[process] no NDJSON received (maybe buffering)'); }
  }catch(e){ setStatusError(txt, 'Ошибка обработки: '+e); notify('Ошибка process: '+e); console.timeEnd('upload_total'); console.groupEnd(); return false; }

  console.timeEnd('upload_total');
  console.groupEnd();
  return processOk;
}

// uploadFinished прибирает индикаторы, которые имеют смысл только во время
// заливки, и обновляет остаток места — он только что изменился на гигабайты.
function uploadFinished(prefix){
  const wrap = document.getElementById(prefix+'_active_wrap');
  if(wrap) wrap.style.display = 'none';
  const fit = document.getElementById(prefix+'_fit');
  if(fit){ fit.textContent = ''; fit.className = 'small text-body-secondary'; }
  try{ sysFreeRefresh(); }catch(_){ /* индикатор места не критичен */ }
}

async function manifestsUpload(){
  const gid = (document.getElementById('gid')?.value||'').trim();
  const ver = (document.getElementById('ver')?.value||'').trim();
  if(!gid){ notify('Укажите идентификатор игры'); return; }
  if(!ver){ notify('Укажите версию'); return; }
  const file = (window.__manDroppedFile) || document.getElementById('man_zip')?.files?.[0];
  if(!file){ notify('Выберите ZIP-файл'); return; }
  if(!uploadVersionValid(ver)){ notifyLevel('Версия должна быть вида 1.2.3 — введено «'+ver+'»', 'error'); return; }
  if(!uploadSpaceCheck('man', file)){
    const go = await askConfirm({
      title: 'Места может не хватить',
      body: 'Архив '+formatBytes(file.size)+', при распаковке потребуется примерно '+formatBytes(Math.round(file.size*UPLOAD_SPACE_FACTOR))+', а свободно '+formatBytes(__sysFreeBytes||0)+'. Заливка, скорее всего, оборвётся на распаковке.',
      okText: 'Всё равно загрузить',
      danger: true,
    });
    if(!go) return;
  }
  const ok = await runChunkedUpload('man', 'game', gid, ver, file);
  uploadFinished('man');
  window.__manDroppedFile = null;
  if(!ok) return;
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
    if(wrap) wrap.style.display='block'; if(bar) bar.style.width='0%'; if(txt) txt.textContent = 'Выбран файл: '+f.name+' ('+formatBytes(f.size)+')';
    uploadSpaceCheck('man', f);
  });
})();

// ==== Manifests page: editable games list (mgm_*) ====
//
// Таблица игр редактируется прямо на месте, а перечитывание с сервера
// (mgmReload) молча затирает несохранённые правки. Раньше это было особенно
// легко сделать кнопкой «Обновить», которая ещё и была жёлтой — то есть
// выглядела опаснее зелёного «Сохранить», хотя предупреждения не давала.
let __mgmDirty = false;

function mgmSetDirty(v){
  __mgmDirty = !!v;
  const b = document.getElementById('mgm_dirty');
  if(b) b.style.display = __mgmDirty ? '' : 'none';
}

// mgmConfirmDiscard спрашивает, можно ли выбросить правки таблицы.
async function mgmConfirmDiscard(what){
  if(!__mgmDirty) return true;
  return askConfirm({
    title: 'Несохранённые изменения',
    body: 'В таблице игр есть правки, которые не сохранены. '+what+' перечитает список с сервера и потеряет их.',
    okText: 'Потерять правки',
    danger: true,
  });
}

async function mgmReload(){
  let res; try{ res = await fetch('/admin/games'); }catch(e){ notify('Ошибка запроса: '+e); return; }
  if(!res.ok){ notify('HTTP '+res.status+' '+res.statusText); return; }
  let j; try{ j = await res.json(); }catch(e){ notify('Ошибка парсинга'); return; }
  const tb = document.querySelector('#mgm-table tbody'); if(!tb) return;
  tb.innerHTML = '';
  (j.items||[]).forEach(it=> mgmAppendRow(tb, it));
  mgmSetDirty(false);
  // restore selection according to current gid input
  const curGid = (document.getElementById('gid')?.value||'').trim().toLowerCase();
  if(curGid){
    const rows = Array.from(tb.querySelectorAll('tr'));
    for(const r of rows){
      const id = r.querySelectorAll('td')[0].querySelector('input').value.trim().toLowerCase();
      if(id===curGid){ r.classList.add('mgm-selected'); break; }
    }
  }
  // game-list.js рисует поверх этой (скрытой) таблицы searchable-список и
  // карточку «Обзор» — оба перечитываются из тех же строк.
  if(window.gmListRender) window.gmListRender();
  if(window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(curGid);
}

function mgmAppendRow(tb, it){
  const tr = document.createElement('tr');
  // pinned хранится на самой строке: до трека H сервер это поле не отдаёт и
  // не сохраняет, но game-list.js уже пишет его в payload /admin/games/save
  // как и остальные поля реестра.
  tr.dataset.pinned = it && it.pinned ? '1' : '0';
  // unpublished — как pinned, состояние строки, а не поле ввода: игра остаётся
  // в реестре со всеми файлами, но публичный /api/games её не отдаёт.
  tr.dataset.unpublished = it && it.unpublished ? '1' : '0';
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
  // Любая правка в строке помечает таблицу грязной: иначе о потере узнаёшь
  // только по тому, что введённое пропало.
  tr.querySelectorAll('input').forEach(inp=> inp.addEventListener('input', ()=> mgmSetDirty(true)));
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
    if(window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(id);
    if(window.gmListRender) window.gmListRender();
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
      mgmSetDirty(true);
      return;
    }
    (async ()=>{
      const ok = await askConfirm({
        title: 'Убрать игру «'+id+'» из списка?',
        body: 'Файлы манифестов и сборки НЕ удаляются — пропадёт только запись в реестре. Изменение применится после нажатия «Сохранить».',
        okText: 'Убрать из списка',
        danger: true,
      });
      if(!ok) return;
      tr.remove();
      mgmSetDirty(true);
      notify('Игра '+id+' помечена на удаление. Нажмите «Сохранить» для применения.');
    })();
  });
  tb.appendChild(tr);

  // bind reorder buttons
  const upBtn = tr.querySelector('button.mgm-up');
  const downBtn = tr.querySelector('button.mgm-down');
  upBtn?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const row = tr;
    const prev = row.previousElementSibling;
    if(prev){ row.parentNode.insertBefore(row, prev); mgmSetDirty(true); }
  });
  downBtn?.addEventListener('click', (ev)=>{
    ev.stopPropagation();
    const row = tr;
    const next = row.nextElementSibling;
    if(next){ row.parentNode.insertBefore(next, row); mgmSetDirty(true); }
  });
}

function mgmAddRow(){
  const tb = document.querySelector('#mgm-table tbody'); if(!tb) return;
  const gameId = (prompt('ID новой игры (латиница, цифры, дефис, подчёркивание):') || '').trim();
  if(!gameId) return;
  const dup = Array.from(tb.querySelectorAll('tr')).some(tr=>{
    const inp = tr.querySelectorAll('td')[0].querySelector('input');
    return inp && inp.value.trim().toLowerCase() === gameId.toLowerCase();
  });
  if(dup){ notify('Игра с ID «'+gameId+'» уже есть в списке'); return; }
  mgmAppendRow(tb, {gameId, title:'', exeRelativePath:'', iconUrl:'', pinned:false});
  mgmSetDirty(true);
  const newRow = tb.querySelector('tr:last-child');
  if(newRow) newRow.click();
}

async function mgmSave(){
  const rows = Array.from(document.querySelectorAll('#mgm-table tbody tr'));
  const items = rows.map((tr, idx)=>{
    const tds = tr.querySelectorAll('td');
    return {
      gameId: tds[0].querySelector('input').value.trim(),
      title: tds[1].querySelector('input').value.trim(),
      iconUrl: tds[2].querySelector('input').value.trim(),
      exeRelativePath: tds[3].querySelector('input').value.trim(),
      // order/pinned: order — позиция в списке
      // (её же меняет drag-reorder в game-list.js), pinned — звёздочка там же.
      order: idx,
      pinned: tr.dataset.pinned === '1',
      unpublished: tr.dataset.unpublished === '1',
    };
  }).filter(it=>it.gameId);
  // basic validation
  const ids = new Set();
  for(const it of items){ if(!it.gameId){ notify('Пустой gameId'); return; } if(ids.has(it.gameId)){ notify('Дубликат gameId: '+it.gameId); return; } ids.add(it.gameId); }
  let res; try{ res = await fetch('/admin/games/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({items}) }); }catch(e){ notifyLevel('Ошибка сохранения: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'Сохранение списка игр'); return; }
  notifyLevel(await res.text(), 'success');
  mgmSetDirty(false);
  mgmReload();
}

// Combined resync: scan -> save -> reload from server
async function mgmResync(){
  // Кнопка перечитывает реестр с сервера — с несохранёнными правками в
  // таблице это молчаливая их потеря.
  if(!await mgmConfirmDiscard('«Найти новые»')) return;
  notifyQuiet('Обновление списка игр: добавление недостающих...', 'info');
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

// Необработанное исключение раньше только дописывалось в невидимый #out.
// Теперь оно ещё и попадает в журнал с отметкой времени и всплывает тостом:
// «панель молча перестала работать» — худший из возможных отчётов об ошибке.
window.addEventListener('error', function(e){
  var o=document.getElementById('out'); if(o) o.textContent += ('\n[JS] Ошибка: '+e.message);
  try{ journalAdd('[JS] '+e.message, 'error'); showToast('Сбой в интерфейсе: '+e.message, 'error'); }catch(_){ /* журнал мог не подняться */ }
});

// Current cover URL (tracked separately; not shown as a comment inside editor)
let currentCoverUrl = '';
// Current published state kept in memory when checkbox is absent
let currentPublished = false;

// notify остаётся точкой входа для всего старого кода: #out по-прежнему
// получает последнее сообщение (на него смотрят тесты и window.onerror), но
// теперь то же самое уходит в журнал с отметкой времени и всплывает тостом.
function notify(msg){
  var o=document.getElementById('out'); if(o) o.textContent = msg;
  try{ journalAdd(msg); showToast(msg); }catch(_){ /* журнал — не критичный путь */ }
}

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
    fillDiffSelect('gm_diff_ver', items.map(it=> it.version), sel.value||'');
    await gmPrevRender(gameId, sel.value||'');
  }catch(e){ const tree=document.getElementById('gm_prev_tree'); if(tree) tree.textContent='Ошибка: '+e; }
}

async function gmPrevRender(gameId, version){
  const tree = document.getElementById('gm_prev_tree'); if(!tree) return;
  const sum = document.getElementById('gm_diff_summary');
  if(!gameId || !version){ tree.textContent = 'Выберите игру и версию'; if(sum) sum.innerHTML=''; return; }
  tree.innerHTML = '<span class="text-body-secondary">Загрузка манифеста...</span>';
  try{
    const base = '/manifests/'+encodeURIComponent(gameId)+'/';
    const r = await fetch(base+encodeURIComponent(version)+'.json');
    if(!r.ok){ tree.textContent = await httpErrText(r, 'Манифест '+version); return; }
    const manifest = await r.json();
    const baseVer = document.getElementById('gm_diff_ver')?.value || '';
    const baseManifest = baseVer ? await fetchManifest(base+encodeURIComponent(baseVer)+'.json') : null;
    const diff = lnRenderTree(tree, manifest, baseManifest);
    if(sum) sum.innerHTML = baseVer && !baseManifest
      ? '<span class="text-danger">Манифест '+escapeHtml(baseVer)+' не прочитан</span>'
      : treeDiffSummaryHtml(diff, baseVer);
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
  // Начальную загрузку вкладки «Лаунчер» уже сделал showSection() при разборе
  // файла — повторять её здесь значит удваивать запросы. Список версий
  // заполняем только если вкладка открыта.
  try{
    const sec = document.getElementById('secLauncher');
    if(sec && !sec.classList.contains('hidden')) lnManifestsReload();
  }catch(_){}
  // Launcher tab controls are bound later in guarded wiring section

  const sel = document.getElementById('gm_prev_ver');
  const gmRerender = ()=>{ const gid=(document.getElementById('gid')?.value||'').trim(); const ver = document.getElementById('gm_prev_ver')?.value; if(!gid||!ver) return; gmPrevRender(gid, ver); };
  if(sel){ sel.addEventListener('change', ()=>{
    // Смена показываемой версии меняет и набор доступных баз сравнения.
    const versions = Array.from(sel.options).map(o=> o.value);
    fillDiffSelect('gm_diff_ver', versions, sel.value||'');
    gmRerender();
  }); }
  const gmDiff = document.getElementById('gm_diff_ver');
  if(gmDiff){ gmDiff.addEventListener('change', gmRerender); }
  wireTreeControls('gm', 'gm_prev_tree');
  wireTreeControls('ln', 'ln_tree');
  const lnDiff = document.getElementById('ln_diff_ver');
  if(lnDiff){ lnDiff.addEventListener('change', ()=>{ const v = document.getElementById('ln_prev_ver')?.value||''; if(v) lnPrevRender(v); }); }
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
    if(__gameGallery) __gameGallery.fetchAndRender();
    if(window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(chosen);
    if(window.gmListRender) window.gmListRender();
  }
  // Галерея выбранной игры: вкладка «Галерея» в карточке игры (#gg_root в
  // admin.html). Разметка есть в DOM с самого начала — Bootstrap только
  // переключает видимость вкладок, поэтому монтировать при показе не нужно.
  let __gameGallery = null;
  if (window.createGameGallery && document.getElementById('gg_root')) {
    // Наружу — чтобы game-list.js мог обновить галерею при выборе игры из
    // левого списка. Там выбор идёт через tr.click(), который присваивает
    // #gm_select.value напрямую, а программное присваивание НЕ порождает
    // событие change — единственное, на котором висело обновление галереи.
    // Из-за этого панель показывала галерею той игры, что была выбрана до
    // перезагрузки, а не выбранной сейчас.
    __gameGallery = window.createGameGallery({
      root: '#gg_root',
      getGameId: () => (document.getElementById('gm_select')?.value || document.getElementById('gid')?.value || '').trim(),
    });
    window.__gameGallery = __gameGallery;
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
    if(__gameGallery) __gameGallery.fetchAndRender();
    if(window.gmSyncOverviewFromRow) window.gmSyncOverviewFromRow(chosen);
    if(window.gmListRender) window.gmListRender();
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

let __dlgSeq = 0; // счётчик для генерации id заголовков диалогов
// Показывает динамически собранный модальный диалог.
// Раньше каждый такой диалог делал `document.body.appendChild(el)` и сразу
// `window.bootstrap ? new Modal(el) : null`. Если bootstrap не загрузился
// (CDN недоступен, блокировщик, обрыв сети), элемент оставался висеть в body
// навсегда — утечка DOM, растущая с каждым вызовом, — а пользователь просто
// не видел диалога и не понимал, почему кнопка «не работает».
// Возвращает экземпляр Modal либо null, если показать диалог нельзя.
function openDynamicModal(el, onDispose){
  const dispose = ()=>{
    try{ el.remove(); }catch{ /* уже удалён */ }
    try{ if(onDispose) onDispose(); }catch{ /* no-op */ }
  };
  if(!window.bootstrap || !window.bootstrap.Modal){
    dispose();
    notifyLevel('Диалог не открылся: не загрузилась библиотека Bootstrap. Обновите страницу.', 'error');
    return null;
  }
  // Доступность: role/aria-modal bootstrap проставляет сам при показе, но
  // связать диалог с его заголовком он не может — делаем это здесь, иначе
  // скринридер объявляет диалог без имени.
  el.setAttribute('role', 'dialog');
  const titleEl = el.querySelector('.modal-title');
  if(titleEl){
    if(!titleEl.id){ titleEl.id = 'dlg-title-' + (++__dlgSeq); }
    el.setAttribute('aria-labelledby', titleEl.id);
  }
  document.body.appendChild(el);
  el.addEventListener('hidden.bs.modal', dispose, { once: true });
  const modal = new window.bootstrap.Modal(el);
  modal.show();
  return modal;
}

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
    const modal = openDynamicModal(el);
    if(!modal) return;
    el.querySelectorAll('li[data-p]').forEach(li=> li.addEventListener('click', ()=>{ const p = li.getAttribute('data-p'); if(p && targetInput){ targetInput.value = p; } modal.hide(); }));
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
  const modal = openDynamicModal(el);
  if(!modal) return;
  const sel = el.querySelector('#url_target'); if(sel){ sel.value = (mode==='cover') ? 'cover' : 'inline'; }
  el.querySelector('#url_ok').addEventListener('click', async ()=>{
    const url = (el.querySelector('#url_input').value||'').trim(); if(!url){ notify('Укажите URL'); return; }
    const path = (el.querySelector('#url_path').value||'').replace(/^\/+|\/+$/g,'');
    const name = el.querySelector('#url_name').value || 'image';
    const modeSel = el.querySelector('#url_overwrite')?.value || 'rename';
    const ext = guessOutExtFromUrl(url);
    const finalName = await resolveNameWithMode(path, name, ext, modeSel);
    const fd = new URLSearchParams(); fd.set('path', path); fd.set('filename', finalName); fd.set('url', url);
    let res; try{ res = await fetch('/admin/news/assets/uploadByUrl', {method:'POST', headers:{'Content-Type':'application/x-www-form-urlencoded'}, body: fd.toString()}); }catch(e){ notifyLevel('Не удалось сохранить по URL: '+e, 'error'); return; }
    if(!res.ok){ await notifyHttp(res, 'Сохранение по URL'); return; }
    const j = await res.json(); if(!j || !j.url){ notifyLevel('Сервер не вернул адрес сохранённого файла', 'error'); return; }
    const target = sel.value || 'inline';
    if(target==='inline'){
      const ta = document.getElementById('ns_md'); insertAtCursor(ta, '![image]('+j.url+')'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
    } else {
      setCoverInMarkdown(j.url); const ta=document.getElementById('ns_md'); autosizeTextArea(ta); updateCoverPreview(); newsPreview(); editorDirty=true; if(ta) ta.dispatchEvent(new Event('input'));
    }
    modal.hide(); // узел удалит обработчик hidden.bs.modal в openDynamicModal
  });
}

// ===== File-pick dialog (like paste, but lets you choose a local file) =====
function openPickUploadDialog(mode){
  const el = document.createElement('div');
  el.className = 'modal fade'; el.tabIndex = -1;
  el.innerHTML = '\n<div class="modal-dialog modal-xl"><div class="modal-content">\n  <div class="modal-header flex-column align-items-stretch">\n    <div class="d-flex w-100 align-items-center justify-content-between">\n      <h5 class="modal-title mb-1">Загрузка изображения</h5>\n      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>\n    </div>\n    <div class="d-flex w-100 align-items-center gap-2">\n      <label class="small text-nowrap">Куда</label>\n      <select id="pick_target" class="form-select form-select-sm" style="max-width:200px">\n        <option value="inline">В текст</option>\n        <option value="cover">Обложка</option>\n      </select>\n      <div class="ms-auto small">Файл: <input id="pick_file" type="file" accept="image/*" /></div>\n    </div>\n    <div class="d-flex align-items-center gap-2">\n      <label class="small text-nowrap">Если имя занято:</label>\n      <select id="pick_overwrite" class="form-select form-select-sm" style="max-width:200px">\n        <option value="rename">Переименовать</option>\n        <option value="overwrite">Перезаписать</option>\n      </select>\n    </div>\n  </div>\n  <div class="modal-body">\n    <div class="row g-3">\n      <div class="col-lg-6">\n        <div style="position:sticky; top:8px">\n          <div id="pick_prev_wrap" class="border rounded d-flex align-items-center justify-content-center" style="min-height:240px;">\n            <div class="text-body-secondary">Выберите файл</div>\n          </div>\n        </div>\n      </div>\n      <div class="col-lg-6">\n        <div class="d-flex align-items-center justify-content-between mb-2 flex-wrap gap-2">\n          <nav id="pick_breadcrumbs" class="small text-body-secondary"></nav>\n          <div class="btn-group btn-group-sm">\n            <button id="pick_mkdir" type="button" class="btn btn-outline-success">Новая папка</button>\n          </div>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Папка</span><input type="text" class="form-control" id="pick_path" placeholder="относительно /news/assets" value="'+escapeHtml(galleryPath||'')+'"/>\n        </div>\n        <div class="input-group input-group-sm mb-2">\n          <span class="input-group-text">Имя</span><input type="text" class="form-control" id="pick_name" value="image"/>\n        </div>\n        <div id="pick_grid" class="row g-2"></div>\n      </div>\n    </div>\n  </div>\n  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Отмена</button><button type="button" class="btn btn-primary" id="pick_ok" disabled>Загрузить</button></div>\n</div></div>';
  const modal = openDynamicModal(el);
  if(!modal) return;
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
        const rn = assetIconBtn('rename');
        rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: pickPath||'', from: it.name, to: nn})) return; fetchPickList(); };
        const del = assetIconBtn('delete', null, 'ms-1');
        del.onclick=async()=>{ if(!await askConfirm({title:'Удалить папку?', body:'Папка «'+it.name+'» и всё её содержимое будут удалены с диска. Ссылки на эти картинки в уже опубликованных новостях перестанут работать.', okText:'Удалить папку', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: pickPath||'', name: it.name})) return; fetchPickList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
        // Make the whole folder card clickable (except action buttons)
        card.addEventListener('click', (e)=>{ if(e.target.closest && e.target.closest('button')) return; pickPath = pickPath? (pickPath+'/'+it.name): it.name; pathInput.value=pickPath; fetchPickList(); });
      } else {
        if(it.url){ const img=document.createElement('img'); img.className='card-img-top'; img.src=it.url; img.alt=it.name; img.style.height='100px'; img.style.objectFit='cover'; card.appendChild(img); }
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name;
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = assetIconBtn('rename');
        rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: pickPath||'', from: it.name, to: nn})) return; fetchPickList(); };
        const del = assetIconBtn('delete', null, 'ms-1');
        del.onclick=async()=>{ if(!await askConfirm({title:'Удалить файл?', body:'Файл «'+it.name+'» будет удалён с диска. Если он вставлен в опубликованную новость, картинка там пропадёт.', okText:'Удалить файл', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: pickPath||'', name: it.name})) return; fetchPickList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      }
      col.appendChild(card); grid.appendChild(col);
    });
  }
  // mkdir
  el.querySelector('#pick_mkdir').addEventListener('click', async ()=>{
    const name = prompt('Имя новой папки:'); if(!name) return;
    if(!await assetsMutate('/admin/news/assets/mkdir', {path: pickPath||'', name: name})) return;
    fetchPickList();
  });
  // sync manual edits
  pathInput.addEventListener('change', ()=>{ pickPath = (pathInput.value||'').replace(/^\/+|\/+$/g,''); fetchPickList(); });
  // initial load
  fetchPickList();
  // upload action
  el.querySelector('#pick_ok').addEventListener('click', async ()=>{
    if(!chosenFile){ notify('Выберите файл'); return; }
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
    modal.hide(); // узел удалит обработчик hidden.bs.modal в openDynamicModal
  });
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
  // Blob-ссылку предпросмотра освобождаем в том же месте, где удаляется узел.
  const modal = openDynamicModal(el, ()=> URL.revokeObjectURL(url));
  if(!modal){ URL.revokeObjectURL(url); return; }
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
        const rn = assetIconBtn('rename');
        rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: pastePath||'', from: it.name, to: nn})) return; fetchPasteList(); };
        const del = assetIconBtn('delete', null, 'ms-1');
        del.onclick=async()=>{ if(!await askConfirm({title:'Удалить папку?', body:'Папка «'+it.name+'» и всё её содержимое будут удалены с диска. Ссылки на эти картинки в уже опубликованных новостях перестанут работать.', okText:'Удалить папку', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: pastePath||'', name: it.name})) return; fetchPasteList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
        card.addEventListener('click', (e)=>{ if(e.target!==rn && e.target!==del){ pastePath = pastePath? (pastePath+'/'+it.name): it.name; pathInput.value=pastePath; fetchPasteList(); } });
      } else {
        if(it.url){ const img=document.createElement('img'); img.className='card-img-top'; img.src=it.url; img.alt=it.name; img.style.height='100px'; img.style.objectFit='cover'; card.appendChild(img); }
        const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
        const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name;
        const actions = document.createElement('div'); actions.className='mt-1';
        const rn = assetIconBtn('rename');
        rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: pastePath||'', from: it.name, to: nn})) return; fetchPasteList(); };
        const del = assetIconBtn('delete', null, 'ms-1');
        del.onclick=async()=>{ if(!await askConfirm({title:'Удалить файл?', body:'Файл «'+it.name+'» будет удалён с диска. Если он вставлен в опубликованную новость, картинка там пропадёт.', okText:'Удалить файл', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: pastePath||'', name: it.name})) return; fetchPasteList(); };
        actions.appendChild(rn); actions.appendChild(del);
        body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      }
      col.appendChild(card); grid.appendChild(col);
    });
  }
  // mkdir
  el.querySelector('#paste_mkdir').addEventListener('click', async ()=>{
    const name = prompt('Имя новой папки:'); if(!name) return;
    if(!await assetsMutate('/admin/news/assets/mkdir', {path: pastePath||'', name: name})) return;
    fetchPasteList();
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
    modal.hide(); // узел удалит и blob освободит обработчик из openDynamicModal
  });
}

// Общая обёртка для операций над ассетами (переименование/удаление/mkdir).
// Раньше эти вызовы делались как `await fetch(...)` без единой проверки: при
// отказе сервера список просто перерисовывался в прежнем виде, пользователь
// считал, что промахнулся по кнопке, и жал ещё раз. Сообщение показываем через
// alert: эти операции запускаются в том числе из модальных диалогов, где панель
// #out закрыта подложкой и её никто не увидит.
async function assetsMutate(url, params){
  let r;
  try{
    r = await fetch(url, {
      method:'POST',
      headers:{'Content-Type':'application/x-www-form-urlencoded'},
      body: new URLSearchParams(params).toString()
    });
  }catch(e){ notifyLevel('Не удалось выполнить операцию: '+e, 'error'); return false; }
  if(!r.ok){
    let detail = '';
    try{ detail = (await r.text()||'').trim(); }catch{ /* тело не обязательно */ }
    notifyLevel('Не удалось выполнить операцию — HTTP '+r.status+' '+r.statusText+(detail? (': '+detail) : ''), 'error');
    return false;
  }
  return true;
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
  if(editorDirty || __mgmDirty){ e.preventDefault(); e.returnValue = 'Есть несохранённые изменения.'; }
});

// ==== Черновики новостей ====
//
// В файле честно написано «drafts removed», и единственной страховкой
// оставался beforeunload. Для поля, в которое пишут руками, этого мало:
// перезагрузка вкладки, обрыв сессии или случайный «Новая» — и текст пропал.
// Черновик живёт в localStorage этого браузера, ключ — раздел+игра+slug,
// и предлагается к восстановлению, только если отличается от серверного.
const NEWS_DRAFT_PREFIX = 'news_draft:';

function newsDraftKey(){
  const scope = document.getElementById('ns_scope')?.value || 'launcher';
  const gid = scope==='game' ? (document.getElementById('ns_gid')?.value || '') : '';
  const slug = document.getElementById('ns_slug')?.value || '';
  return NEWS_DRAFT_PREFIX + scope + ':' + gid + ':' + slug;
}

function newsDraftSave(){
  try{
    const md = document.getElementById('ns_md')?.value || '';
    const key = newsDraftKey();
    if(!md.trim()){ localStorage.removeItem(key); newsDraftUpdateBadge(); return; }
    localStorage.setItem(key, JSON.stringify({ md, cover: currentCoverUrl||'', at: Date.now() }));
    newsDraftUpdateBadge();
  }catch(e){ /* приватный режим/переполнение — черновик просто не сохранится */ }
}

function newsDraftLoad(){
  try{
    const raw = localStorage.getItem(newsDraftKey());
    if(!raw) return null;
    const j = JSON.parse(raw);
    return (j && typeof j.md === 'string') ? j : null;
  }catch(e){ return null; }
}

function newsDraftDrop(){
  try{ localStorage.removeItem(newsDraftKey()); }catch(e){ /* no-op */ }
  newsDraftUpdateBadge();
}

function newsDraftUpdateBadge(){
  const badge = document.getElementById('ns_draft_badge');
  const btn = document.getElementById('ns_btnRestoreDraft');
  const d = newsDraftLoad();
  const ta = document.getElementById('ns_md');
  const differs = !!d && !!ta && d.md !== ta.value;
  if(btn) btn.style.display = differs ? '' : 'none';
  if(badge){
    if(!d){ badge.textContent = ''; return; }
    badge.textContent = 'черновик сохранён ' + new Date(d.at||Date.now()).toLocaleTimeString('ru-RU');
  }
}

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
// Единственный источник правды о вкладках: кнопка -> секция -> что подгрузить.
const TAB_MAP = [
  { btn: 'tabLauncher',  sec: 'secLauncher' },
  { btn: 'tabManifests', sec: 'secManifests' },
  { btn: 'tabNews',      sec: 'secNews' },
  { btn: 'tabInbox',     sec: 'secInbox' },
  { btn: 'tabMaint',     sec: 'secMaint' },
  { btn: 'tabBench',     sec: 'secBench' },
  { btn: 'tabMetrics',   sec: 'secMetrics' },
];

function showSection(id){
  // sections (guarded: check element exists before toggling)
  TAB_MAP.forEach(t=>{ const el = document.getElementById(t.sec); if(el){ el.classList.toggle('hidden', t.sec !== id); } });
  // nav active state. Кроме класса переключаем aria-selected и tabindex:
  // вкладки — это role="tab", и вспомогательные технологии читают состояние
  // именно оттуда, а roving tabindex оставляет в табуляции ровно одну вкладку,
  // как того требует шаблон tablist.
  TAB_MAP.forEach(t=>{
    const el = document.getElementById(t.btn); if(!el) return;
    const active = t.sec === id;
    el.classList.toggle('active', active);
    el.setAttribute('aria-selected', active ? 'true' : 'false');
    if(active) el.removeAttribute('tabindex'); else el.setAttribute('tabindex', '-1');
  });
  // auto actions per section — ровно один раз на переключение
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
  if(id==='secManifests'){
    try{ manifestsReload(); }catch(_){ /* no-op */ }
    // Возврат на вкладку не должен затирать несохранённую правку таблицы:
    // переключение вкладок — это не команда «выбросить введённое».
    try{ if(!__mgmDirty) mgmReload(); }catch(_){ /* no-op */ }
  }
  if(id==='secInbox'){ try{ fbReload(true); }catch(_){ /* no-op */ } }
  if(id==='secMaint'){ try{ mtLoad(); }catch(_){ /* no-op */ } }
  if(id==='secMetrics'){ try{ mxOnTabOpen(); }catch(_){ /* no-op */ } }
  try{ localStorage.setItem('admin_tab', id); }catch(e){}
  // Адресная строка должна показывать открытую вкладку: ссылку на «Метрики»
  // иначе не скопировать. replaceState, а не hash: переключение вкладок не
  // должно засорять историю браузера и ломать «Назад».
  try{
    const hash = Object.keys(HASH_TAB_MAP).find(k=> HASH_TAB_MAP[k] === id);
    if(hash && location.hash.replace(/^#/,'') !== hash){
      history.replaceState(null, '', '#'+hash);
    }
  }catch(e){ /* history может быть недоступна */ }
}
// Guarded wiring to avoid null errors: по одному обработчику на вкладку.
TAB_MAP.forEach((t, i)=>{
  const btn = document.getElementById(t.btn);
  if(!btn) return;
  btn.addEventListener('click', (e)=>{ e.preventDefault(); showSection(t.sec); });
  // Стрелки, Home/End — обязательная часть шаблона tablist: без них вкладки
  // остаются шестью ссылками, по которым можно только табать поочерёдно.
  btn.addEventListener('keydown', (e)=>{
    const last = TAB_MAP.length - 1;
    let next = null;
    if(e.key === 'ArrowRight') next = i >= last ? 0 : i+1;
    else if(e.key === 'ArrowLeft') next = i <= 0 ? last : i-1;
    else if(e.key === 'Home') next = 0;
    else if(e.key === 'End') next = last;
    else return;
    e.preventDefault();
    const target = TAB_MAP[next];
    showSection(target.sec);
    document.getElementById(target.btn)?.focus();
  });
});

// #launcher, #manifests, ... открывают нужную вкладку сразу при загрузке —
// не дожидаясь клика и не завися от того, что этот браузер запомнил в
// прошлый раз. Нужно для ссылок из уведомлений о выкатке: "версия
// опубликована" должно вести прямо на вкладку "Лаунчер", а не на то, что
// было открыто в последний визит.
const HASH_TAB_MAP = { launcher:'secLauncher', manifests:'secManifests', news:'secNews', inbox:'secInbox', maint:'secMaint', bench:'secBench', metrics:'secMetrics' };
function sectionFromHash(){
  const raw = (location.hash || '').replace(/^#/, '').trim().toLowerCase();
  return HASH_TAB_MAP[raw] || null;
}
window.addEventListener('hashchange', ()=>{
  const sec = sectionFromHash();
  if(sec) showSection(sec);
});

// Ensure initial active state reflects the hash, then the saved section.
try{
  const fromHash = sectionFromHash();
  const saved = localStorage.getItem('admin_tab');
  const known = TAB_MAP.some(t=> t.sec === saved);
  showSection(fromHash || (known ? saved : 'secLauncher'));
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
  const ver = document.getElementById('up_ver').value;
  const file = (window.__upDroppedFile) || document.getElementById('up_zip').files[0];
  const latest = document.getElementById('up_latest').checked;
  if(!file){ notify('Выберите ZIP-файл'); return; }
  if(!ver){ notify('Укажите версию'); return; }
  if(!uploadVersionValid(ver)){ notifyLevel('Версия должна быть вида 1.2.3 — введено «'+ver+'»', 'error'); return; }
  if(!uploadSpaceCheck('up', file)){
    const go = await askConfirm({
      title: 'Места может не хватить',
      body: 'Архив '+formatBytes(file.size)+', при распаковке потребуется примерно '+formatBytes(Math.round(file.size*UPLOAD_SPACE_FACTOR))+', а свободно '+formatBytes(__sysFreeBytes||0)+'. Заливка, скорее всего, оборвётся на распаковке.',
      okText: 'Всё равно загрузить',
      danger: true,
    });
    if(!go) return;
  }
  const ok = await runChunkedUpload('up', 'launcher', 'launcher', ver, file);
  uploadFinished('up');
  window.__upDroppedFile = null;
  if(!ok) return;
  try{ lnRefresh(); }catch(_){ }
  if(latest){
    try{
      const act = await fetch('/admin/activate?gameId=launcher&version='+encodeURIComponent(ver), { method:'POST' });
      if(!act.ok){ notify('HTTP '+act.status+' activate'); } else { try{ lnRefresh(); }catch(_){ } }
    }catch(e){ notify('Ошибка activate: '+e); }
  }
}

// Кнопки длительных операций (заливка ZIP, сохранение реестра, сохранение
// новости) должны блокироваться на время работы: без этого повторный клик
// запускал вторую заливку в тот же слот версии, и две операции писали
// одновременно. Состояние возвращаем в finally, чтобы кнопка не осталась
// заблокированной после ошибки.
function bindBusyClick(id, fn, busyText){
  const btn = document.getElementById(id);
  if(!btn) return;
  let running = false;
  btn.addEventListener('click', async (e)=>{
    e.preventDefault();
    if(running) return;
    running = true;
    const prevDisabled = btn.disabled;
    const prevText = btn.textContent;
    btn.disabled = true;
    btn.setAttribute('aria-busy', 'true');
    if(busyText) btn.textContent = busyText;
    try{ await fn(); }
    finally{
      running = false;
      btn.disabled = prevDisabled;
      btn.removeAttribute('aria-busy');
      if(busyText) btn.textContent = prevText;
    }
  });
}

// Wire buttons (guarded)
bindBusyClick('btnUpload', upload, 'Загрузка...');
// Manifests wiring
bindBusyClick('man_upload', manifestsUpload, 'Загрузка...');
bindBusyClick('bench_run', runUploadBench, 'Тестирование...');
(()=>{ const b = document.getElementById('bench_apply_game'); if(b) b.addEventListener('click', ()=> applyBenchBest('man_chunk_size','man_conc','man_conc_val')); })();
// Остановка прогона. Текущая комбинация дольливает уже отправленные чанки и
// отбрасывает пробу — обрывать её посреди PUT незачем, счёт идёт на секунды.
(()=>{
  const stop = document.getElementById('bench_stop'); if(!stop) return;
  stop.addEventListener('click', (e)=>{
    e.preventDefault();
    __benchAbort.aborted = true;
    stop.disabled = true;
    stop.textContent = 'Останавливаю...';
    const st = document.getElementById('bench_status');
    if(st) st.textContent = 'Останавливаю после текущих чанков...';
    setTimeout(()=>{ stop.disabled = false; stop.textContent = 'Остановить'; }, 3000);
  });
})();
// План прогона пересчитывается при правке полей: сколько ячеек и сколько
// гигабайт уедет, видно до нажатия кнопки, а не после часа ожидания.
(()=>{
  ['bench_zip','bench_probe_mb','bench_chunks_mb','bench_concs'].forEach(id=>{
    const el = document.getElementById(id); if(!el) return;
    el.addEventListener('input', benchShowPlan);
    el.addEventListener('change', benchShowPlan);
  });
  benchShowPlan();
})();
// Show live value for concurrency slider
(()=>{ const s = document.getElementById('man_conc'); const v = document.getElementById('man_conc_val'); if(s&&v){ v.textContent = String(s.value||'6'); s.addEventListener('input', ()=>{ v.textContent = String(s.value||'6'); }); }})();
(()=>{ const s = document.getElementById('up_conc'); const v = document.getElementById('up_conc_val'); if(s&&v){ v.textContent = String(s.value||'6'); s.addEventListener('input', ()=>{ v.textContent = String(s.value||'6'); }); }})();
// Очистка временных загрузок — одна и та же операция на обеих вкладках.
async function uploadCleanup(){
  const ok = await askConfirm({
    title: 'Очистить временные загрузки?',
    body: 'Удаляются незавершённые и повреждённые куски заливок. Опубликованные версии и манифесты не трогаются.',
    okText: 'Очистить',
  });
  if(!ok) return;
  try{
    const r = await fetch('/admin/api/upload/cleanup', { method:'POST' });
    if(!r.ok){ await notifyHttp(r, 'Очистка временных загрузок'); return; }
    const j = await r.json();
    notify('Удалено: '+(j.removed||0));
    sysFreeRefresh();
  }catch(e){ notifyLevel('Ошибка cleanup: '+e, 'error'); }
}
['man_cleanup','up_cleanup'].forEach(id=>{
  const btn = document.getElementById(id); if(!btn) return;
  btn.addEventListener('click', uploadCleanup);
});
if (document.getElementById('btnList')) document.getElementById('btnList').addEventListener('click', manifestsReload);
// Launcher versions list refresh
if (document.getElementById('ln_list_btn')) document.getElementById('ln_list_btn').addEventListener('click', lnManifestsReload);
// Launcher preview selector wiring
if (document.getElementById('ln_prev_ver')) document.getElementById('ln_prev_ver').addEventListener('change', ()=>{
  const sel=document.getElementById('ln_prev_ver'); if(!sel) return;
  fillDiffSelect('ln_diff_ver', Array.from(sel.options).map(o=> o.value), sel.value||'');
  lnPrevRender(sel.value||'');
});

// Manifests page: Games editor buttons
if (document.getElementById('mgm_add')) document.getElementById('mgm_add').addEventListener('click', mgmAddRow);
bindBusyClick('mgm_save', mgmSave, 'Сохранение...');
bindBusyClick('mgm_resync', mgmResync, 'Обновление...');
// Launcher page buttons
if (document.getElementById('ln_refresh')) document.getElementById('ln_refresh').addEventListener('click', lnRefresh);

// News wiring (guarded)
if (document.getElementById('ns_btnNew')) document.getElementById('ns_btnNew').addEventListener('click', async ()=>{
  const ta = document.getElementById('ns_md');
  // «Новая» стирала набранный текст без вопросов — при том, что кнопка стоит
  // вплотную к «Сохранить».
  if(editorDirty && ta && ta.value.trim()){
    const ok = await askConfirm({
      title: 'Начать новую новость?',
      body: 'Текущий текст не сохранён на сервере. Он останется в черновике этого браузера, но поле будет очищено.',
      okText: 'Очистить',
    });
    if(!ok) return;
    newsDraftSave();
  }
  if(document.getElementById('ns_slug')) document.getElementById('ns_slug').value='';
  if(ta){ ta.value=''; autosizeTextArea(ta); }
  if(document.getElementById('ns_preview')) document.getElementById('ns_preview').innerHTML='';
  editorDirty = false;
  newsDraftUpdateBadge();
});
if (document.getElementById('ns_btnRestoreDraft')) document.getElementById('ns_btnRestoreDraft').addEventListener('click', ()=>{
  const d = newsDraftLoad(); if(!d) return;
  const ta = document.getElementById('ns_md'); if(!ta) return;
  ta.value = d.md;
  if(d.cover) currentCoverUrl = d.cover;
  autosizeTextArea(ta); updateCoverPreview(); newsPreview();
  editorDirty = true;
  newsDraftUpdateBadge();
  notifyLevel('Черновик восстановлен', 'success');
});
bindBusyClick('ns_btnSave', newsSave, 'Сохранение...');
if (document.getElementById('ns_btnDelete')) document.getElementById('ns_btnDelete').addEventListener('click', newsDelete);
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
    const url = (urlEl?.value||'').trim(); if(!url){ notify('Укажите URL'); return; }
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
  ta.addEventListener('input', debounce(newsDraftSave, 800));
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
      if(!res.ok){ await notifyHttp(res, 'Публикация новости'); cb.checked = !cb.checked; return; }
      // Если переключили ту новость, что открыта в редакторе, состояние надо
      // подтянуть и сюда — иначе следующее «Сохранить» вернёт прежний флаг.
      const openSlug = document.getElementById('ns_slug')?.value || '';
      if(openSlug && openSlug === it.slug) currentPublished = cb.checked;
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
  if(!slug){ notify('Укажите идентификатор новости'); return; }
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
  // keep server text by default until user restores; strip legacy comment directives
  const serverMdClean = (serverMd||'')
    .replace(/<!--\s*published\s*:[^>]*-->\s*\n?/ig, '')
    .replace(/<!--\s*cover\s*:[^>]*-->\s*\n?/ig, '');
  ta.value = serverMdClean;
  autosizeTextArea(document.getElementById('ns_md'));
  updateCoverPreview();
  newsPreview();
  editorDirty = false;
  // Черновик предлагается, только если он расходится с тем, что на сервере.
  newsDraftUpdateBadge();
}

// Кнопки действий над файлом/папкой в сетке ассетов. Иконки карандаша и
// корзины были скопированы инлайном в шесть мест (галерея, диалог загрузки с
// диска, диалог вставки из буфера — по паре в каждом), и копии успели
// разойтись: в галерее у <svg> стоял fill="#fff", а в диалогах его не было, и
// та же самая корзина рисовалась там чёрной на тёмной кнопке. Разметка теперь
// одна, а цвет берётся от кнопки (currentColor), а не прибит числом.
const ASSET_ICONS = {
  rename: '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" xmlns="http://www.w3.org/2000/svg">'
    + '<path d="M3 17.25V21h3.75l11.06-11.06-3.75-3.75L3 17.25zm14.81-9.06c.2-.2.2-.51 0-.71l-2.29-2.29a.5.5 0 0 0-.71 0l-1.83 1.83 3 3 1.83-1.83z"/>'
    + '</svg>',
  delete: '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" xmlns="http://www.w3.org/2000/svg">'
    + '<path d="M6 7h12l-1 13a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 7zm3 3a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1zm6 0a1 1 0 0 0-1 1v7a1 1 0 1 0 2 0v-7a1 1 0 0 0-1-1z"/>'
    + '<path d="M9 3h6l1 1h4a1 1 0 1 1 0 2H4a1 1 0 1 1 0-2h4l1-1z"/>'
    + '</svg>',
};
const ASSET_ICON_TITLES = { rename: 'Переименовать', delete: 'Удалить' };

// assetIconBtn собирает кнопку с иконкой: kind — 'rename' или 'delete'.
function assetIconBtn(kind, onClick, extraClass){
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-sm btn-dark asset-icon-btn' + (extraClass ? ' '+extraClass : '');
  b.title = ASSET_ICON_TITLES[kind] || '';
  b.setAttribute('aria-label', b.title);
  b.innerHTML = ASSET_ICONS[kind] || '';
  if(onClick) b.addEventListener('click', onClick);
  return b;
}

// ===== Shared helpers for assets upload =====
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

async function newsSave(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value; const md=document.getElementById('ns_md').value;
  if(!slug){ notify('Укажите идентификатор новости — без него сохранять некуда'); return; }
  // Флаг публикации берём из состояния, прочитанного с сервера, а не из
  // чекбокса #ns_published: этого элемента в разметке нет с тех пор, как
  // публикацией управляет переключатель в списке новостей. Выражение всегда
  // давало false, а сервер (news.Save) применяет присланное поле — то есть
  // сохранение текста молча снимало новость с публикации.
  const pub = currentPublished;
  const fd = new FormData();
  fd.append('scope', scope);
  if(scope==='game') fd.append('gameId', gid);
  fd.append('slug', slug);
  fd.append('markdown', md);
  // send meta fields explicitly
  fd.append('published', pub ? 'true' : 'false');
  fd.append('coverUrl', currentCoverUrl || '');
  let res; try{ res=await fetch('/admin/news/save', {method:'POST', body: fd}); }catch(e){ notifyLevel('Не удалось сохранить новость: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'Сохранение новости'); return; }
  notifyLevel(await res.text(), 'success');
  newsList();
  newsPreview();
  editorDirty = false;
  // Сохранённое на сервере больше не нуждается в локальной копии.
  newsDraftDrop();
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
  // Управляющие символы вырезаем ДО разбора схемы. Браузер по спецификации URL
  // удаляет табуляции и переводы строк перед тем, как определить схему, поэтому
  // "java&#9;script:alert(1)" для него — javascript:. Наша же регулярка такую строку
  // схемой не признавала (\t не входит в класс символов) и пропускала её как
  // «относительную ссылку» — то есть проверка обходилась одним символом.
  const v = String(value || '').replace(/[\u0000-\u001F\u007F]/g, '').trim();
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
  const doc = new window.DOMParser().parseFromString(String(html||''), 'text/html');
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

function titleFromMarkdown(md){
  const m = /^#\s+(.+)$/m.exec(md||'');
  return m? m[1].trim(): '';
}

async function newsDelete(){
  const scope=document.getElementById('ns_scope').value; const gid=document.getElementById('ns_gid').value; const slug=document.getElementById('ns_slug').value;
  if(!slug){ notify('Укажите идентификатор новости'); return; }
  const ok = await askConfirm({
    title: 'Удалить новость?',
    body: 'Статья «'+slug+'» будет удалена вместе с метаданными и убрана из индекса. Загруженные картинки останутся в галерее.',
    okText: 'Удалить',
    danger: true,
  });
  if(!ok) return;
  let res; try{ res=await fetch('/admin/news/delete?scope='+encodeURIComponent(scope)+'&slug='+encodeURIComponent(slug)+(scope==='game'?'&gameId='+encodeURIComponent(gid):''), {method:'POST'}); }catch(e){ notifyLevel('Не удалось удалить новость: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'Удаление новости'); return; }
  notifyLevel(await res.text(), 'success');
  newsDraftDrop();
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

// ===== Gallery UI =====
function openGalleryModal(){
  try{ gallerySetPath(''); galleryFetchAndRender(); }catch(e){}
  const el = document.getElementById('ns_gallery');
  if(!el) return;
  // Галерея живёт в разметке, утечки тут нет, но без bootstrap диалог просто
  // не открывался бы молча — сообщаем причину.
  if(!window.bootstrap || !window.bootstrap.Modal){
    notify('Галерея не открылась: не загрузилась библиотека Bootstrap (CDN недоступен?). Обновите страницу.');
    return;
  }
  new window.bootstrap.Modal(el).show();
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

// Расширения, которые браузер действительно покажет как картинку.
const IMAGE_EXT_RE = /\.(png|jpe?g|gif|webp|avif|bmp|svg|ico)(\?|#|$)/i;
function isImageName(name){ return IMAGE_EXT_RE.test(String(name||'')); }

// fileThumb — плашка вместо превью для файла, который картинкой не является.
function fileThumb(name){
  const ext = (String(name||'').match(/\.([^.]+)$/)||[])[1] || 'file';
  const thumb = document.createElement('div');
  thumb.className = 'card-img-top d-flex flex-column align-items-center justify-content-center text-body-secondary';
  thumb.style.height = '120px';
  thumb.style.background = '#212529';
  const icon = document.createElement('div');
  icon.style.fontSize = '28px';
  icon.textContent = '📄';
  const label = document.createElement('div');
  label.className = 'small text-uppercase';
  label.textContent = ext.slice(0, 6);
  thumb.appendChild(icon); thumb.appendChild(label);
  return thumb;
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
      const rn = assetIconBtn('rename');
      rn.onclick=async()=>{ const nn=prompt('Новое имя папки', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: galleryPath||'', from: it.name, to: nn})) return; galleryFetchAndRender(); };
      const del = assetIconBtn('delete', null, 'ms-1');
      del.onclick=async()=>{ if(!await askConfirm({title:'Удалить папку?', body:'Папка «'+it.name+'» и всё её содержимое будут удалены с диска. Ссылки на эти картинки в уже опубликованных новостях перестанут работать.', okText:'Удалить папку', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: galleryPath||'', name: it.name})) return; galleryFetchAndRender(); };
      actions.appendChild(rn); actions.appendChild(del);
      body.appendChild(cap); body.appendChild(actions); card.appendChild(body);
      // Make the whole folder card clickable (except action buttons)
      card.addEventListener('click', (ev)=>{ if(ev.target.closest && ev.target.closest('button')) return; gallerySetPath(galleryPath? (galleryPath+'/'+it.name): it.name); galleryFetchAndRender(); });
    } else {
      // В галерее лежат не только картинки (например, ping.txt), и <img> на
      // такой файл давал битую превьюшку с иконкой «сломанное изображение».
      // Не-картинке рисуем понятную иконку файла с расширением.
      const img = isImageName(it.name) || isImageName(it.url)
        ? (()=>{ const i = document.createElement('img'); i.className='card-img-top'; i.src = it.url; i.alt = it.name||''; i.loading='lazy'; i.style.height='120px'; i.style.objectFit='cover'; return i; })()
        : fileThumb(it.name);
      const body = document.createElement('div'); body.className='card-body p-2 mt-auto';
      const cap = document.createElement('div'); cap.className='small text-truncate'; cap.textContent = it.name||'';
      const actions = document.createElement('div'); actions.className='mt-1';
      const rn = assetIconBtn('rename');
      rn.onclick=async()=>{ const nn=prompt('Новое имя файла', it.name); if(!nn||nn===it.name) return; if(!await assetsMutate('/admin/news/assets/rename', {path: galleryPath||'', from: it.name, to: nn})) return; galleryFetchAndRender(); };
      const del = assetIconBtn('delete', null, 'ms-1');
      del.onclick=async()=>{ if(!await askConfirm({title:'Удалить файл?', body:'Файл «'+it.name+'» будет удалён с диска. Если он вставлен в опубликованную новость, картинка там пропадёт.', okText:'Удалить файл', danger:true})) return; if(!await assetsMutate('/admin/news/assets/delete', {path: galleryPath||'', name: it.name})) return; galleryFetchAndRender(); };
      actions.appendChild(rn); actions.appendChild(del);
      body.appendChild(cap); body.appendChild(actions);
      card.appendChild(img); card.appendChild(body);
      card.addEventListener('click', (ev)=>{
        if(ev.target.closest && ev.target.closest('button')) return;
        const tgt = document.getElementById('ns_gallery_target')?.value || 'inline';
        const image = isImageName(it.name) || isImageName(it.url);
        if(tgt==='cover'){
          if(!image){ notify('Обложкой можно сделать только изображение'); return; }
          setCoverInMarkdown(it.url); updateCoverPreview(); newsPreview();
        } else if(image){
          insertImageFromGallery(it.url);
        } else {
          // Не картинка — вставляем ссылку, а не ![image](...): иначе в статье
          // получается заведомо битое изображение.
          insertLinkFromGallery(it.url, it.name||'файл');
        }
        const el = document.getElementById('ns_gallery'); if(window.bootstrap && el){ const m = window.bootstrap.Modal.getInstance(el) || new window.bootstrap.Modal(el); m.hide(); }
      });
    }
    col.appendChild(card);
    grid.appendChild(col);
  });
}

function insertLinkFromGallery(url, name){
  const ta = document.getElementById('ns_md'); if(!ta) return;
  insertAtCursor(ta, '['+name+']('+url+')');
  autosizeTextArea(ta); newsPreview(); editorDirty = true; ta.dispatchEvent(new Event('input'));
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
  const ok = await askConfirm({
    title: 'Снять режим и удалить состояние?',
    body: 'Файл состояния будет удалён целиком: причина, расписание и набор блокировок пропадут, и ввести их придётся заново. Пользователи сразу перестанут видеть баннер.',
    okText: 'Выключить и удалить',
    danger: true,
  });
  if(!ok) return;
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

// Миллисекунды -> «12 ч 34 мин» / «34 мин» для сумм, где mxFmtMs (секунды)
// был бы нечитаем: «741 мин 00 с» вместо «12 ч 21 мин».
function mxFmtHours(ms){
  const v = Number(ms||0);
  if(!(v > 0)) return '—';
  const totalMin = Math.round(v/60000);
  const h = Math.floor(totalMin/60);
  const m = totalMin%60;
  return h > 0 ? (h+' ч '+m+' мин') : (m+' мин');
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

// mxOutcomeNote расписывает исход операций так, чтобы числа сходились.
// Раньше под «Установок 19» стояло «успешно 4, с ошибкой 8», и оставшиеся
// семь событий пользователю приходилось угадывать: это установки, у которых
// результат не сообщён (прерванные или ещё идущие на момент отчёта).
function mxOutcomeNote(total, ok, fail){
  const rest = Math.max(0, Number(total||0) - Number(ok||0) - Number(fail||0));
  const parts = ['успешно '+mxNum(ok), 'с ошибкой '+mxNum(fail)];
  // Отменённые сюда же: лаунчер шлёт им результат cancel, а сводка считает
  // отдельно только ok и fail — намеренно, чтобы брошенная закачка не попадала
  // в долю неудач. Называем остаток тем, чем он в основном и является.
  if(rest > 0) parts.push('отменено или без результата '+mxNum(rest));
  return parts.join(', ');
}

// mxSavedNote — ради этой строки лаунчер и написан. «Скачано 40 МБ» само по себе
// не говорит ничего; смысл появляется рядом с полным весом тех же сборок.
// Пустая строка, когда сравнивать не с чем: события старых лаунчеров полного
// веса не сообщали, и выдавать их за стопроцентную экономию нельзя.
function mxSavedNote(bytes, fullBytes){
  const b = Number(bytes||0);
  const full = Number(fullBytes||0);
  if(full <= 0 || full < b) return 'сумма поля bytes';
  return 'вместо '+formatBytes(full)+' целиком — сэкономлено '+mxPct(full - b, full);
}

// mxIntegrityNote — проверку целостности запускает сам пользователь, и запускает
// её тогда, когда игра уже ведёт себя странно. Голое число проверок ничего не
// стоит: важно, сколько из них нашли расхождение.
function mxIntegrityNote(t){
  const checks = Number(t.integrityChecks||0);
  if(checks === 0) return 'запускает сам пользователь';
  const failed = Number(t.integrityFailed||0);
  const files = Number(t.hashMismatches||0);
  const parts = ['с расхождением '+mxNum(failed)];
  if(files > 0) parts.push('файлов не сошлось '+mxNum(files));
  return parts.join(', ');
}

function mxRenderTotals(t){
  const root = mxEl('mx_totals'); if(!root) return;
  const tiles = [
    ['Событий всего', mxNum(t.events), ''],
    ['Запусков лаунчера', mxNum(t.launcherStarts), ''],
    ['Уникальных установок', mxNum(t.uniqueInstalls), 'по installId, не по людям'],
    ['Запусков игр', mxNum(t.gameLaunches), ''],
    ['Установок', mxNum(t.installs), mxOutcomeNote(t.installs, t.installOk, t.installFail)],
    ['Обновлений', mxNum(t.updates), mxOutcomeNote(t.updates, t.updateOk, t.updateFail)],
    ['Ошибок', mxNum(t.errors), 'события вида error'],
    ['Скачано', formatBytes(Number(t.bytesDownloaded||0)), mxSavedNote(t.bytesDownloaded, t.fullBytes)],
    ['Среднее время установки', mxFmtMs(t.avgInstallMs), 'только успешные'],
    ['Среднее время обновления', mxFmtMs(t.avgUpdateMs), 'только успешные'],
    ['Проверок целостности', mxNum(t.integrityChecks), mxIntegrityNote(t)],
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

// График по дням на том же canvas-модуле (line-chart.js), что и график
// скорости загрузки (speed-chart.js) — оба заменили uPlot, который эта
// страница грузила отдельным <script> с unpkg.com; см. комментарий в шапке
// speed-chart.js про то, почему внешний CDN здесь больше не используется.
// По оси X — индекс дня, подписи берутся из byDay: так не приходится
// пересчитывать UTC-сутки в местные и объяснять сдвиг на границе дня.
function mxRenderChart(byDay){
  const host = mxEl('mx_chart_host'); if(!host) return;
  const note = mxEl('mx_chart_note');
  if(__mxRO){ try{ __mxRO.disconnect(); }catch{ /* no-op */ } __mxRO = null; }
  host.replaceChildren();

  if(!byDay || byDay.length===0){
    if(note) note.textContent = '';
    host.innerHTML = '<div class="text-body-secondary">Событий за период нет — рисовать нечего.</div>';
    return;
  }
  if(note) note.textContent = byDay.length+' дн.';

  const legendHost = document.createElement('div');
  legendHost.className = 'small mb-2';
  const canvas = document.createElement('canvas');
  canvas.style.width = '100%';
  canvas.style.height = '280px';
  host.appendChild(legendHost);
  host.appendChild(canvas);

  const xs = byDay.map((_, i)=> i);
  const series = [
    { label: 'Запуски лаунчера', color: '#0d6efd', values: byDay.map(d=> Number(d.launcherStarts||0)) },
    { label: 'Установки', color: '#198754', values: byDay.map(d=> Number(d.installs||0)) },
    { label: 'Обновления', color: '#0dcaf0', values: byDay.map(d=> Number(d.updates||0)) },
    { label: 'Запуски игр', color: '#ffc107', values: byDay.map(d=> Number(d.gameLaunches||0)) },
    { label: 'Ошибки', color: '#dc3545', values: byDay.map(d=> Number(d.errors||0)) },
  ];
  const xLabelFor = (i)=>{ const d = byDay[i]; return d ? String(d.date||'').slice(5) : ''; };
  const render = ()=> drawMultiLineChart(canvas, xs, series, { xLabelFor, formatY: mxNum, legendHost });
  render();
  __mxRO = new window.ResizeObserver(render);
  __mxRO.observe(host);
}

function mxRenderGames(byGame){
  const tb = mxEl('mx_games_body'); if(!tb) return;
  if(!byGame || byGame.length===0){ tb.innerHTML = mxEmptyRow(11, 'Событий, привязанных к играм, нет.'); return; }
  tb.innerHTML = byGame.map(g=>
    '<tr><td><code>'+escapeHtml(g.gameId||'—')+'</code></td>'
    + '<td class="text-end">'+mxNum(g.installs)+'</td>'
    + '<td class="text-end">'+mxNum(g.updates)+'</td>'
    + '<td class="text-end">'+mxNum(g.errors)+'</td>'
    + '<td class="text-end" title="'+escapeHtml(mxSavedNote(g.bytes, g.fullBytes))+'">'+escapeHtml(formatBytes(Number(g.bytes||0)))+'</td>'
    + '<td class="text-end" title="'+escapeHtml('файлов не сошлось: '+mxNum(g.hashMismatches))+'">'+mxNum(g.integrityChecks)+'</td>'
    + '<td class="text-end">'+mxNum(g.uniquePlayers)+'</td>'
    + '<td class="text-end">'+mxNum(g.sessions)+'</td>'
    + '<td class="text-end">'+escapeHtml(mxFmtHours(Number(g.playtimeMs||0)))+'</td>'
    + '<td class="text-end">'+escapeHtml(mxFmtMs(g.avgSessionMs))+'</td>'
    + '<td class="text-end">'+escapeHtml(mxFmtMs(g.medianSessionMs))+'</td></tr>'
  ).join('');
}

// ==== Время в играх: своя карточка, но те же byDay/totals из /metrics/summary —
// период и фильтр по игре у неё общие с остальными разделами метрик. ====

function mxRenderPtTotals(t){
  const root = mxEl('mx_pt_totals'); if(!root) return;
  const tiles = [
    ['Уникальных игроков', mxNum(t.uniquePlayers), 'по installId, у кого была хоть одна сессия'],
    ['Игровых сессий', mxNum(t.gameSessions), ''],
    ['Время в играх', mxFmtHours(t.playtimeMs), 'сумма длительностей сессий'],
    ['Среднее время сессии', mxFmtMs(t.avgSessionMs), ''],
    ['Медианное время сессии', mxFmtMs(t.medianSessionMs), 'меньше подвержено выбросам, чем среднее'],
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

function mxRenderPtDaysTable(byDay){
  const tb = mxEl('mx_pt_days_body'); if(!tb) return;
  if(!byDay || byDay.length===0){ tb.innerHTML = mxEmptyRow(3, 'Нет данных за период.'); return; }
  tb.innerHTML = byDay.map(d=>
    '<tr><td>'+escapeHtml(d.date||'')+'</td>'
    + '<td class="text-end">'+mxNum(d.sessions)+'</td>'
    + '<td class="text-end">'+escapeHtml(mxFmtHours(Number(d.playtimeMs||0)))+'</td></tr>'
  ).join('');
}

let __mxPtRO = null;

function mxRenderPtChart(byDay){
  const host = mxEl('mx_pt_chart_host'); if(!host) return;
  if(__mxPtRO){ try{ __mxPtRO.disconnect(); }catch{ /* no-op */ } __mxPtRO = null; }
  host.replaceChildren();

  const hasPlaytime = (byDay||[]).some(d=> Number(d.sessions||0) > 0);
  if(!byDay || byDay.length===0 || !hasPlaytime){
    host.innerHTML = '<div class="text-body-secondary">Игровых сессий за период нет — рисовать нечего.</div>';
    return;
  }

  const legendHost = document.createElement('div');
  legendHost.className = 'small mb-2';
  const canvas = document.createElement('canvas');
  canvas.style.width = '100%';
  canvas.style.height = '220px';
  host.appendChild(legendHost);
  host.appendChild(canvas);

  const xs = byDay.map((_, i)=> i);
  // Минуты, а не миллисекунды: та же шкала, что и Сессии, читается вменяемо
  // на одном графике, вместо шестизначных чисел рядом с единицами.
  const series = [
    { label: 'Сессии', color: '#ffc107', values: byDay.map(d=> Number(d.sessions||0)) },
    { label: 'Минут в играх', color: '#0d6efd', values: byDay.map(d=> Math.round(Number(d.playtimeMs||0)/60000)) },
  ];
  const xLabelFor = (i)=>{ const d = byDay[i]; return d ? String(d.date||'').slice(5) : ''; };
  const render = ()=> drawMultiLineChart(canvas, xs, series, { xLabelFor, formatY: mxNum, legendHost });
  render();
  __mxPtRO = new window.ResizeObserver(render);
  __mxPtRO.observe(host);
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

// ==== Топ ошибок: код -> конкретные события ====
//
// «sync_failed — 8» и всё: ни версии, ни игры, ни времени. Дальше этой
// строки расследование не шло, потому что ручки, отдающей сами события, не
// существовало — теперь есть /admin/api/metrics/errors.
function mxRenderErrors(items){
  const tb = mxEl('mx_errors_body'); if(!tb) return;
  const list = Array.isArray(items) ? items : [];
  if(list.length===0){ tb.innerHTML = mxEmptyRow(2, 'Ошибок за период не было.'); return; }
  tb.innerHTML = list.map(x=>
    '<tr><td><a href="#" class="mx-err" data-code="'+escapeHtml(x.key||'')+'">'+escapeHtml(x.key||'—')+'</a></td>'
    + '<td class="text-end">'+mxNum(x.count)+'</td></tr>'
  ).join('');
  tb.querySelectorAll('a.mx-err').forEach(a=>{
    a.addEventListener('click', (e)=>{ e.preventDefault(); mxShowErrorEvents(a.getAttribute('data-code')||''); });
  });
}

async function mxShowErrorEvents(code){
  if(!code) return;
  const p = new URLSearchParams();
  p.set('code', code);
  const from = mtLocalToUtc(mxEl('mx_from')?.value || '');
  const to = mxLocalToUtcEnd(mxEl('mx_to')?.value || '');
  if(from) p.set('from', from);
  if(to) p.set('to', to);
  const gid = mxEl('mx_game')?.value || '';
  if(gid) p.set('gameId', gid);

  let res;
  try{ res = await fetch('/admin/api/metrics/errors?'+p.toString()); }
  catch(e){ notifyLevel('Не удалось получить события ошибки: '+e, 'error'); return; }
  if(!res.ok){ await notifyHttp(res, 'События ошибки '+code); return; }
  let j; try{ j = await res.json(); }catch(e){ notifyLevel('События ошибки: сервер вернул не JSON', 'error'); return; }

  const items = Array.isArray(j.items) ? j.items : [];
  const rows = items.length
    ? items.map(ev=>
        '<tr><td class="text-nowrap">'+escapeHtml(String(ev.ts||'').replace('T',' ').replace('Z',''))+'</td>'
        + '<td><code>'+escapeHtml(ev.gameId||'—')+'</code></td>'
        + '<td>'+escapeHtml(ev.version||'—')+'</td>'
        + '<td>'+escapeHtml(ev.appVersion||'—')+'</td>'
        + '<td>'+escapeHtml(ev.os||'—')+'</td>'
        + '<td class="text-end">'+escapeHtml(ev.installId ? String(ev.installId).slice(0,8) : '—')+'</td></tr>'
      ).join('')
    : '<tr><td colspan="6" class="text-body-secondary">Событий с этим кодом в периоде нет.</td></tr>';

  const capped = j.capped
    ? '<div class="small text-body-secondary mt-2">Показаны последние '+escapeHtml(String(j.limit||items.length))+' событий — в периоде их может быть больше.</div>'
    : '';

  const el = document.createElement('div');
  el.className = 'modal fade';
  el.setAttribute('tabindex','-1');
  el.innerHTML = ''+
    '<div class="modal-dialog modal-xl modal-dialog-scrollable"><div class="modal-content">'+
    '  <div class="modal-header"><h5 class="modal-title">Ошибка <code>'+escapeHtml(code)+'</code> — последние события</h5>'+
    '    <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Закрыть"></button></div>'+
    '  <div class="modal-body">'+
    '    <div class="table-responsive"><table class="table table-sm table-admin table-striped align-middle mb-0">'+
    '      <thead><tr><th>Когда (UTC)</th><th>Игра</th><th>Версия сборки</th><th>Версия лаунчера</th><th>ОС</th><th class="text-end">installId</th></tr></thead>'+
    '      <tbody>'+rows+'</tbody></table></div>'+ capped +
    '  </div>'+
    '  <div class="modal-footer"><button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Закрыть</button></div>'+
    '</div></div>';
  document.body.appendChild(el);
  if(!window.bootstrap || !window.bootstrap.Modal){
    // Без bootstrap показать модалку нечем — уводим данные в журнал, чтобы
    // клик всё-таки чем-то заканчивался.
    el.remove();
    notify('Ошибка '+code+': событий '+items.length+' (диалог недоступен — не загрузился bootstrap)');
    return;
  }
  const modal = new window.bootstrap.Modal(el);
  el.addEventListener('hidden.bs.modal', ()=> el.remove());
  modal.show();
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
  mxRenderPtTotals(totals);
  mxRenderPtDaysTable(byDay);
  mxRenderPtChart(byDay);
  mxRenderGames(sum.byGame);
  mxRenderErrors(sum.topErrors);
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
  notifyQuiet('Метрики обновлены: событий '+mxNum((sum.totals||{}).events)+'.', 'info');
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
  const ok = await askConfirm({
    title: 'Удалить все метрики?',
    body: 'Удаляются обе генерации файла событий — выбранный период на удаление не влияет.',
    bullets: [
      'Пропадёт вся накопленная история запусков, установок и ошибок.',
      'Графики и сводки начнутся с нуля: сравнить «до и после» будет не с чем.',
      'Копии нет: восстановить историю неоткуда.',
    ],
    okText: 'Удалить всё',
    danger: true,
  });
  if(!ok) return;
  let res;
  try{ res = await fetch('/admin/api/metrics/clear', { method: 'POST' }); }
  catch(e){ notifyLevel('Ошибка сети при очистке метрик: '+e, 'error'); return; }
  if(!res.ok){ notifyLevel('Не удалось очистить метрики — '+(await mtErrText(res)), 'error'); return; }
  notifyLevel('Метрики удалены.', 'success');
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
