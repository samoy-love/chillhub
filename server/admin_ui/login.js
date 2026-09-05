/* Вход в панель.

   Единственный скрипт, который выполняется без сессии, поэтому он ни от
   чего не зависит: ни от transport.js, ни от api.js — их анониму не
   отдают. */
(function () {
  const form = document.getElementById('loginForm');
  const btn = document.getElementById('btnLogin');
  const msg = document.getElementById('msg');

  /* Ошибку показываем словами, а не кодом ответа: за 401 стоит «логин
     или пароль не подошли», и человеку нужно именно это. Что там было
     на самом деле, знает журнал сервера. */
  function reason(status) {
    if (status === 401 || status === 403) return 'Логин или пароль не подошли';
    if (status === 429) return 'Слишком много попыток подряд, подождите минуту';
    if (status >= 500) return 'Сервер не отвечает, попробуйте ещё раз';
    return 'Войти не получилось (' + status + ')';
  }

  function say(text, bad) {
    if (!msg) return;
    msg.textContent = text || '';
    if (bad) msg.setAttribute('data-bad', '');
    else msg.removeAttribute('data-bad');
  }

  form?.addEventListener('submit', async (e) => {
    e.preventDefault();
    const username = document.getElementById('username')?.value?.trim() || '';
    const password = document.getElementById('password')?.value || '';
    if (!username || !password) {
      say('Заполните оба поля', true);
      return;
    }

    btn?.setAttribute('disabled', 'disabled');
    say('Проверяем…');
    try {
      const res = await fetch('/admin/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      });
      if (!res.ok) {
        say(reason(res.status), true);
        btn?.removeAttribute('disabled');
        return;
      }
      location.href = '/admin/';
    } catch {
      say('Сервер не ответил — проверьте связь', true);
      btn?.removeAttribute('disabled');
    }
  });
})();
