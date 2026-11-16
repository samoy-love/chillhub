/* Транспорт админ-панели: адреса, CSRF, продление сессии.
   ------------------------------------------------------------------
     - `/admin/...` переписывается в `/admin/api/...`, чтобы у морды был
       один префикс и она не конфликтовала со статикой в nginx;
     - CSRF-токен из куки уходит заголовком `X-CSRF-Token` ТОЛЬКО для
       небезопасных методов и ТОЛЬКО на свой origin — иначе секрет сессии
       уедет к чужому хосту;
     - запросы, получившие 401, используют общее продление сессии;
     - то же самое навешивается на XMLHttpRequest: чанковая загрузка идёт
       через него ради побайтового прогресса.

   Файл подключается ПЕРВЫМ, до остальных скриптов панели.
   ------------------------------------------------------------------ */

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
  function canRefresh(input) {
    if (!isSameOrigin(input)) return false;
    const url = new URL(input && input.url || input, window.location.href);
    return url.pathname.startsWith(ADMIN_API_PREFIX) &&
      !['login', 'logout', 'refresh'].some((name) => url.pathname === ADMIN_API_PREFIX + 'auth/' + name);
  }
  // Low-level fetch wrapper: rewrite URL, attach CSRF for unsafe, auto-refresh on 401 once
  try {
    const origFetch = window.fetch;
    let refreshing = null;
    let sessionRevision = 0;
    function refreshSession() {
      if (!refreshing) {
        refreshing = origFetch.call(window, '/admin/api/auth/refresh', { method: 'POST' })
          .then((res) => {
            if (res.ok) sessionRevision++;
            return res.ok;
          }, () => false)
          .finally(() => { refreshing = null; });
      }
      return refreshing;
    }
    async function doFetchOnce(input, init){
      const opts = init ? { ...init } : {};
      // Метод может быть задан и в init, и в самом Request — учитываем оба.
      const method = (opts.method || (input && typeof input === 'object' && input.method) || 'GET');
      if (needsCsrf(method, input)) {
        opts.mode = 'same-origin';
        opts.headers = new Headers(opts.headers || (input && typeof input === 'object' ? input.headers : undefined) || {});
        if (!opts.headers.has('X-CSRF-Token')) {
          const csrf = getCsrf(); if (csrf) opts.headers.set('X-CSRF-Token', csrf);
        }
      }
      return origFetch.call(window, input, opts);
    }
    window.fetch = async function(input, init){
      const request = rewrite(input);
      const revision = sessionRevision;
      const retryable = canRefresh(request) && !(init && window.ReadableStream && init.body instanceof window.ReadableStream);
      const retry = retryable && typeof Request !== 'undefined' && request instanceof Request ? request.clone() : request;
      const res = await doFetchOnce(request, init);
      if (res && res.status === 401 && retryable && (revision !== sessionRevision || await refreshSession())) {
        return doFetchOnce(retry, init);
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
