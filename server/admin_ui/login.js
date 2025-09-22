(function(){
  const form = document.getElementById('loginForm');
  const btn = document.getElementById('btnLogin');
  const msg = document.getElementById('msg');
  function setMsg(t){ if(msg) msg.textContent = t||''; }
  form?.addEventListener('submit', async (e)=>{
    e.preventDefault();
    const username = document.getElementById('username')?.value?.trim()||'';
    const password = document.getElementById('password')?.value||'';
    if(!username || !password){ setMsg('Укажите логин и пароль'); return; }
    btn?.setAttribute('disabled','disabled'); setMsg('Вход...');
    try {
      const res = await fetch('/admin/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password })
      });
      if(!res.ok){ setMsg('Ошибка: '+res.status+' '+res.statusText); btn?.removeAttribute('disabled'); return; }
      // success -> go to /admin/
      location.href = '/admin/';
    } catch (e) {
      setMsg('Ошибка сети: '+e);
      btn?.removeAttribute('disabled');
    }
  });
})();
