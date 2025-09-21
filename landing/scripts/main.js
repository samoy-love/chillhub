/* Waves background on Canvas + Parallax + Reveal + Tilt */
(function(){
  const mqMobile = window.matchMedia('(max-width: 640px)');
  const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const isMobile = () => mqMobile.matches;

  const canvas = document.getElementById('waves-canvas');
  const ctx = canvas.getContext('2d');
  // Lower DPR on mobile to reduce GPU load
  let dpr = (()=>{
    const base = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    return isMobile() ? 1 : Math.min(1.5, base);
  })();
  let W, H, time = 0;

  function resize(){
    W = canvas.clientWidth; H = canvas.clientHeight;
    canvas.width = Math.floor(W * dpr);
    canvas.height = Math.floor(H * dpr);
    ctx.setTransform(dpr,0,0,dpr,0,0);
  }
  window.addEventListener('resize', resize, {passive:true});
  resize();

  // Define wave layers
  const waves = [
    { amp: 28, len: 420, spd: 0.6, phase: Math.random()*Math.PI*2, hue: 265 },
    { amp: 36, len: 560, spd: 0.45, phase: Math.random()*Math.PI*2, hue: 310 },
    { amp: 52, len: 780, spd: 0.32, phase: Math.random()*Math.PI*2, hue: 190 },
  ];

  let rafId = 0;
  let running = true;
  function step(){
    time += 0.016;
    ctx.clearRect(0,0,W,H);

    // background gradient (deep night to indigo)
    const g = ctx.createLinearGradient(0,0,W,H);
    g.addColorStop(0, '#0b0b1a');
    g.addColorStop(1, '#11122a');
    ctx.fillStyle = g; ctx.fillRect(0,0,W,H);

    // composite waves with soft light
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';

    for(const w of waves){
      w.phase += 0.008 * w.spd;
      const grad = ctx.createLinearGradient(0, 0, 0, H);
      const c1 = `hsla(${w.hue}, 85%, 60%, 0.14)`;
      const c2 = `hsla(${(w.hue+40)%360}, 85%, 60%, 0.10)`;
      grad.addColorStop(0.0, 'rgba(0,0,0,0)');
      grad.addColorStop(0.35, c1);
      grad.addColorStop(0.9, c2);
      grad.addColorStop(1.0, 'rgba(0,0,0,0)');
      ctx.fillStyle = grad;

      ctx.beginPath();
      const baseY = H*0.35 + (waves.indexOf(w)*28);
      ctx.moveTo(0, H);
      ctx.lineTo(0, baseY);
      const stepX = 3; // pixel step for smooth curve
      for(let x=0; x<=W; x+=stepX){
        const y = baseY + Math.sin((x + w.phase*w.len) / w.len) * w.amp
                    + Math.sin((x*0.5 + time*120) / (w.len*0.6)) * (w.amp*0.25);
        ctx.lineTo(x, y);
      }
      ctx.lineTo(W, H);
      ctx.closePath();
      ctx.fill();
    }

    ctx.restore();

    // subtle horizon glow
    const glow = ctx.createRadialGradient(W*0.5, H*0.25, 0, W*0.5, H*0.25, Math.max(W,H)*0.7);
    glow.addColorStop(0, 'rgba(255, 196, 120, 0.06)');
    glow.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = glow;
    ctx.fillRect(0,0,W,H);

    if(running) rafId = requestAnimationFrame(step);
  }
  step();

  // Pause canvas animation when tab is hidden to save battery/CPU
  document.addEventListener('visibilitychange', ()=>{
    if(document.hidden){ running = false; if(rafId) cancelAnimationFrame(rafId); }
    else { running = true; rafId = requestAnimationFrame(step); }
  });

  // Parallax scroll
  const layers = document.querySelectorAll('.layer');
  const enableParallax = !isMobile() && !prefersReducedMotion.matches;
  if(enableParallax){
    window.addEventListener('scroll', ()=>{
      const y = window.scrollY || window.pageYOffset;
      layers.forEach(l => {
        const sp = parseFloat(l.dataset.speed || '0.2');
        l.style.transform = `translateY(${y*sp}px)`;
      });
    }, {passive:true});
  } else {
    layers.forEach(l=>{ l.style.transform = ''; });
  }

  // Reveal on scroll
  const revealEls = document.querySelectorAll('[data-animate]');
  const io = new IntersectionObserver((entries)=>{
    entries.forEach(e=>{ if(e.isIntersecting){ e.target.classList.add('in'); io.unobserve(e.target);} });
  },{threshold:0.15});
  revealEls.forEach(el=>io.observe(el));

  // Tilt on hover for game cards (disabled on touch devices and when reduced motion)
  const tiltEls = document.querySelectorAll('[data-tilt]');
  const touchCapable = 'ontouchstart' in window || navigator.maxTouchPoints > 0;
  if(!touchCapable && !prefersReducedMotion.matches){
    tiltEls.forEach(card =>{
      let rAF=0;
      function onMove(ev){
        const rect = card.getBoundingClientRect();
        const cx = rect.left + rect.width/2; const cy = rect.top + rect.height/2;
        const dx = (ev.clientX - cx)/rect.width; const dy = (ev.clientY - cy)/rect.height;
        cancelAnimationFrame(rAF);
        rAF = requestAnimationFrame(()=>{
          card.style.transform = `rotateX(${(-dy*6).toFixed(2)}deg) rotateY(${(dx*8).toFixed(2)}deg) translateY(-4px)`;
        });
      }
      function reset(){ card.style.transform = ''; }
      card.addEventListener('mousemove', onMove);
      card.addEventListener('mouseleave', reset);
    });
  }

  // Year in footer
  const y = document.getElementById('year'); if(y) y.textContent = new Date().getFullYear();

  // Screenshots autoplay with hover pause
  (function setupShotsCarousel(){
    const sc = document.querySelector('.shots');
    if(!sc) return;
    const imgs = Array.from(sc.querySelectorAll('img'));
    if(imgs.length < 2) return;
    // autoplay disabled; no need to track index/hover state

    let animRAF = 0;
    function stopAnim(){ if(animRAF){ cancelAnimationFrame(animRAF); animRAF = 0; sc.dataset.anim = '0'; } }
    function easeInOutCubic(t){ return t<0.5 ? 4*t*t*t : 1 - Math.pow(-2*t+2, 3)/2; }
    function _animateScrollTo(xTarget, duration=1800){
      stopAnim(); sc.dataset.anim = '1';
      const start = sc.scrollLeft; const delta = xTarget - start; const t0 = performance.now();
      function step(now){
        const t = Math.min(1, (now - t0)/duration);
        sc.scrollLeft = start + delta * easeInOutCubic(t);
        if(t < 1 && sc.dataset.anim==='1') animRAF = requestAnimationFrame(step); else { sc.dataset.anim='0'; animRAF=0; }
      }
      animRAF = requestAnimationFrame(step);
    }
    // scrollToIndex helper removed (unused)
    // Autoplay disabled by request: no timer, only user drag and page scroll sync

    // hover-related handlers removed (no autoplay)
    // No visibility change handler needed since autoplay is disabled

    // No automatic movement on load
  })();

  // Drag-to-scroll for screenshots (mouse & touch)
  (function setupShotsDrag(){
    const sc = document.querySelector('.shots');
    if(!sc) return;
    let isDown = false, startX = 0, startLeft = 0;
    let lastX = 0, lastT = 0, vx = 0; // px/sec
    let momentumRAF = 0;

    function stopMomentum(){ if(momentumRAF){ cancelAnimationFrame(momentumRAF); momentumRAF = 0; } }

    function onDown(e){
      isDown = true;
      sc.classList.add('dragging');
      sc.dataset.pause = '1';
      // autoplay is disabled; nothing to stop
      startX = (e.touches? e.touches[0].clientX : e.clientX);
      startLeft = sc.scrollLeft;
      lastX = startX; lastT = performance.now(); vx = 0;
      stopMomentum();
    }
    function onMove(e){
      if(!isDown) return;
      e.preventDefault();
      const x = (e.touches? e.touches[0].clientX : e.clientX);
      const dx = x - startX;
      sc.scrollLeft = startLeft - dx;
      // velocity calc
      const now = performance.now();
      const dt = Math.max(1, now - lastT);
      const instV = (x - lastX) / dt * 1000; // px/sec
      vx = vx * 0.8 + instV * 0.2;
      lastX = x; lastT = now;
    }
    function onUp(){
      if(!isDown) return;
      isDown = false; sc.classList.remove('dragging');
      // inertial scrolling with friction
      const friction = 0.94; // per frame decay at 60fps
      let prev = performance.now();
      function step(){
        const now = performance.now();
        const dt = (now - prev) / 1000; // seconds
        prev = now;
        sc.scrollLeft -= vx * dt;
        // decay velocity approximately per 60fps frame
        vx *= Math.pow(friction, dt*60);
        if(Math.abs(vx) < 15) { sc.dataset.pause = '0'; momentumRAF = 0; return; }
        momentumRAF = requestAnimationFrame(step);
      }
      if(Math.abs(vx) > 50){ momentumRAF = requestAnimationFrame(step); } else { sc.dataset.pause = '0'; }
    }

    sc.addEventListener('mousedown', onDown);
    sc.addEventListener('mousemove', onMove);
    sc.addEventListener('mouseup', onUp);
    sc.addEventListener('mouseleave', onUp);
    sc.addEventListener('touchstart', onDown, {passive:true});
    sc.addEventListener('touchmove', onMove, {passive:false});
    sc.addEventListener('touchend', onUp, {passive:true});
    sc.addEventListener('touchcancel', onUp, {passive:true});
  })();

  // Scroll-synced horizontal gallery: vertical scroll moves the gallery horizontally
  (function setupShotsScrollSync(){
    const sc = document.querySelector('.shots');
    const sec = document.querySelector('.section--shots');
    if(!sc || !sec) return;
    if(isMobile() || prefersReducedMotion.matches) return; // disable on mobile / reduced motion
    let ticking = false;
    let raf = 0; let targetX = 0; let currX = 0;
    function step(){
      raf = 0;
      if(sc.classList.contains('dragging') || sc.dataset.pause==='1' || sc.dataset.anim==='1') return;
      // smooth approach
      const alpha = 0.06; // smoothing factor (even slower)
      currX += (targetX - currX) * alpha;
      // stop when close enough
      if(Math.abs(targetX - currX) < 0.5){ currX = targetX; }
      sc.scrollLeft = currX;
      if(currX !== targetX) raf = requestAnimationFrame(step);
    }
    function onScroll(){
      if(ticking) return; ticking = true;
      requestAnimationFrame(()=>{
        ticking = false;
        const rect = sec.getBoundingClientRect();
        const vh = window.innerHeight || document.documentElement.clientHeight;
        const visible = Math.max(0, Math.min(rect.bottom, vh) - Math.max(rect.top, 0));
        if(visible <= 0) return; // not visible
        // progress of section within viewport height (0..1)
        const span = rect.height + vh; // distance from before entering to after leaving
        const centerProgress = (vh - rect.top) / span; // 0..1
        const p = Math.max(0, Math.min(1, centerProgress));
        const max = sc.scrollWidth - sc.clientWidth;
        targetX = max * p;
        if(!raf) raf = requestAnimationFrame(step);
      });
    }
    window.addEventListener('scroll', onScroll, {passive:true});
    window.addEventListener('resize', onScroll, {passive:true});
    // initial position after layout
    setTimeout(()=>{ currX = sc.scrollLeft; onScroll(); }, 400);
  })();

  // Randomize reels columns order so categories appear in random columns each load
  (function shuffleReels(){
    function run(){
      const reels = document.querySelector('.reels');
      if(!reels) return;
      const cols = Array.from(reels.querySelectorAll('.reel'));
      if(cols.length < 2) return;
      // Fisher-Yates shuffle
      for(let i=cols.length-1; i>0; i--){
        const j = Math.floor(Math.random()*(i+1));
        [cols[i], cols[j]] = [cols[j], cols[i]];
      }
      // Assign explicit grid columns and track classes for animation variance
      const tracks = ['track-a','track-b','track-c','track-d','track-e'];
      cols.forEach((el, idx)=>{
        // remove existing track-* classes
        el.classList.forEach(c=>{ if(/^track-/.test(c)) el.classList.remove(c); });
        el.classList.add(tracks[idx % tracks.length]);
        el.style.gridColumn = String(idx + 1);
      });
      // Re-append in shuffled order to ensure visual order in grid auto-flow
      cols.forEach(el=>reels.appendChild(el));

      // Now randomize individual slot items across columns (columns are neutral)
      const catMap = {
        mode: ['Coop','Solo','Online','Local','PvE','PvP','Arena','Raids','Ranked','Casual'],
        genre: ['Action','Horror','Rogue','Shooter','Puzzle','RPG','Sim','Arcade','Strategy','Sandbox'],
        persp: ['FPP','TPP','TopDown','Isomet','Side','VR','2D','3D','Fixed','FreeCam'],
        mech: ['Loot','Craft','Build','Parkour','Stealth','Tactic','Combo','Skill','Cards','Trade'],
        tone: ['Dark','Grim','Funny','Chill','Cozy','Epic','Retro','Mythic','Moody','Noir']
      };
      const tracksEls = cols.map(col=>col.querySelector('.reel-track')).filter(Boolean);
      if(tracksEls.length === 0) return;
      // Collect all slots from all tracks
      const slots = [];
      tracksEls.forEach(t=>{
        Array.from(t.querySelectorAll('.slot')).forEach(s=>{
          slots.push(s);
        });
      });
      // Clear all tracks
      tracksEls.forEach(t=>{ t.innerHTML = ''; });
      // Shuffle slots
      for(let i=slots.length-1; i>0; i--){ const j = Math.floor(Math.random()*(i+1)); [slots[i], slots[j]] = [slots[j], slots[i]]; }
      // Assign category color class per slot by its textContent
      function getCat(txt){
        for(const k in catMap){ if(catMap[k].includes(txt)) return k; }
        return null;
      }
      slots.forEach((s, _idx)=>{
        // remove previous slot--* classes
        s.classList.forEach(c=>{ if(/^slot--/.test(c)) s.classList.remove(c); });
        const name = (s.textContent || '').trim();
        const cat = getCat(name);
        if(cat){ s.classList.add('slot--'+cat); }
        // place to a random track for more randomness
        const target = tracksEls[Math.floor(Math.random()*tracksEls.length)];
        target.appendChild(s);
      });
    }
    if(document.readyState === 'loading'){
      document.addEventListener('DOMContentLoaded', run, { once: true });
    } else {
      run();
    }
  })();
})();
