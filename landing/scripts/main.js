/* Waves background on Canvas + Parallax + Reveal + Tilt */
(function(){
  const mqMobile = window.matchMedia('(max-width: 640px)');
  const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const isMobile = () => mqMobile.matches;
  const isLowEndDevice = (() => {
    const hc = navigator.hardwareConcurrency || 2;
    // deviceMemory is not on iOS Safari; assume low if missing and mobile
    const mem = navigator.deviceMemory || (isMobile() ? 1 : 4);
    const ua = navigator.userAgent || '';
    const oldIOS = /OS\s(1[0-3]|[0-9]_)/.test(ua) || /iPhone OS [0-9_]{1,3}/.test(ua);
    const oldAndroid = /Android\s(4|5|6|7|8)\./.test(ua);
    return hc <= 2 || mem <= 2 || oldIOS || oldAndroid;
  })();

  // Note: Header uses a fixed CSS variable height; no JS syncing required.

  const canvas = document.getElementById('waves-canvas');
  const ctx = canvas.getContext('2d');
  // Lower DPR on mobile and low-end to reduce GPU load
  let dpr = (()=>{
    const base = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    if (isLowEndDevice) return 1; // force 1x
    return isMobile() ? 1 : Math.min(1.5, base);
  })();

  // Reels engine with sounds (spin button)
  (function reelsEngine(){
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const forceAnimate = !isLowEndDevice; // on low-end respect reduced motion path
    const coming = document.querySelector('.game-card.coming');
    const reelsWrap = coming?.querySelector('.reels');
    if(!coming || !reelsWrap) return;

    // Controls
    const btnSpin = coming.querySelector('.reels-spin');
    const statusEl = coming.querySelector('.reels-status');
    // Use var to ensure definition before any handler runs (avoid TDZ issues)
    var spinning = false;
    var demoMode = true; // auto-spin until user interacts
    var demoAllowed = true; // gated by visibility
    var raf = 0;
    if(statusEl){ statusEl.style.display = 'none'; }

    // Activate JS mode: disable CSS keyframes
    reelsWrap.classList.add('js-active');

    // Audio (lightweight mixer with compressor, debounced ticks, ducking during chime)
    const DISABLE_REEL_SOUNDS = true; // global flag to completely disable sounds on landing
    let audioCtx = null;
    let isMuted = true;
    let masterGain = null, comp = null;
    let tickOsc = null, tickGain = null; // reused oscillator for smooth gating
    let chimeGain = null;
    let chimeActiveUntil = 0; // suppress ticks while chime is active

    function ensureAudio(){
      if(DISABLE_REEL_SOUNDS) return; // do not initialize audio at all
      if(audioCtx) return;
      try{
        audioCtx = new (window.AudioContext||window.webkitAudioContext)();
        comp = audioCtx.createDynamicsCompressor();
        comp.threshold.setValueAtTime(-24, audioCtx.currentTime);
        comp.knee.setValueAtTime(20, audioCtx.currentTime);
        comp.ratio.setValueAtTime(8, audioCtx.currentTime);
        comp.attack.setValueAtTime(0.003, audioCtx.currentTime);
        comp.release.setValueAtTime(0.05, audioCtx.currentTime);

        masterGain = audioCtx.createGain();
        masterGain.gain.setValueAtTime(0.8, audioCtx.currentTime);
        masterGain.connect(comp).connect(audioCtx.destination);

        tickGain = audioCtx.createGain();
        tickGain.gain.value = 0.0001;
        tickGain.connect(masterGain);

        chimeGain = audioCtx.createGain();
        chimeGain.gain.value = 0.9;
        chimeGain.connect(masterGain);

        // Create and start a single tick oscillator, gate via gain for each tick
        tickOsc = audioCtx.createOscillator();
        tickOsc.type = 'square';
        tickOsc.frequency.value = 900;
        tickOsc.connect(tickGain);
        tickOsc.start();
      }catch{}
    }

    let lastTickWall = 0;
    function playTick(){
      if(isMuted || !audioCtx) return;
      const wallNow = performance.now();
      // Debounce ticks more aggressively and duck during chime
      if(wallNow - lastTickWall < 220) return;
      const t = audioCtx.currentTime;
      if(t < chimeActiveUntil) return; // suppress during chime

      lastTickWall = wallNow;
      // Smoothly gate the gain and glide the frequency a bit for a non-clicky tick
      tickGain.gain.cancelScheduledValues(t);
      tickGain.gain.setValueAtTime(0.0001, t);
      tickGain.gain.linearRampToValueAtTime(0.035, t + 0.012);
      tickGain.gain.exponentialRampToValueAtTime(0.0001, t + 0.10);
      // Small frequency glide for a pleasant feel
      tickOsc.frequency.cancelScheduledValues(t);
      tickOsc.frequency.setValueAtTime(820, t);
      tickOsc.frequency.linearRampToValueAtTime(980, t + 0.08);
    }

    function playChime(){
      if(isMuted || !audioCtx) return;
      const t0 = audioCtx.currentTime;
      chimeActiveUntil = t0 + 0.7; // duck ticks while chime plays
      const seq = [ {f:880, d:0.12}, {f:1175, d:0.14}, {f:1567, d:0.16} ];
      let t = t0;
      seq.forEach(({f,d}, _idx)=>{
        const o = audioCtx.createOscillator();
        o.type = 'sine';
        o.frequency.setValueAtTime(f, t);
        const g = audioCtx.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.linearRampToValueAtTime(0.08, t + 0.03);
        g.gain.exponentialRampToValueAtTime(0.0001, t + d);
        o.connect(g).connect(chimeGain);
        o.start(t);
        o.stop(t + d + 0.02);
        t += d * 0.7;
      });
    }

    // Engine
    const reelEls = Array.from(reelsWrap.querySelectorAll('.reel'));
    if(reelEls.length === 0) return;

    // Prepare track data per column
    const tracks = reelEls.map(reel => {
      const track = reel.querySelector('.reel-track');
      const baseSlots = Array.from(track.querySelectorAll('.slot'));
      return { reel, track, baseSlots };
    });

    const REPEAT_BLOCKS = isLowEndDevice ? 12 : 24; // fewer DOM nodes on low-end
    const data = tracks.map(() => ({
      // metrics
      step: 0,
      slotH: 0,
      gap: 0,
      baseCount: 0,
      repeat: REPEAT_BLOCKS,
      total: 0,
      cy: 0,
      // animation state
      y: 0,
      yStart: 0,
      target: 0,
      snap: 0,
      chosenIdx: 0,
      startT: 0,
      dur: 0,
      done: false,
      lastTickT: 0,
      lastYMod: 0,
    }));

    function computeMetrics(){
      const wrapH = reelsWrap.clientHeight;
      tracks.forEach((t, i)=>{
        const d = data[i];
        // Build repeated content fresh to avoid drift
        const base = t.baseSlots;
        d.baseCount = base.length;
        // Measure slot height using first base slot appended temporarily if needed
        // Clear and rebuild track
        t.track.innerHTML = '';
        const frag = document.createDocumentFragment();
        for(let r=0; r<d.repeat; r++){
          base.forEach(s=> frag.appendChild(s.cloneNode(true)));
        }
        t.track.appendChild(frag);
        // After DOM is ready, measure
        const s0 = t.track.querySelector('.slot');
        d.slotH = s0 ? s0.getBoundingClientRect().height : 56;
        const csTrack = window.getComputedStyle(t.track);
        d.gap = parseFloat(csTrack.rowGap||csTrack.gap||'0') || 0;
        // include vertical margins from slot into step
        let mTop = 0, mBottom = 0;
        if(s0){
          const csSlot = window.getComputedStyle(s0);
          mTop = parseFloat(csSlot.marginTop||'0')||0;
          mBottom = parseFloat(csSlot.marginBottom||'0')||0;
        }
        d.step = d.slotH + d.gap + mTop + mBottom;
        d.total = d.baseCount * d.step * d.repeat;
        d.cy = wrapH/2 - d.step/2;
        // Reset transform to a safe normalized value (middle block start)
        d.y = ((d.baseCount * Math.floor(d.repeat/2)) * d.step) - d.cy;
        // Use integer pixel translation to avoid hairline gaps/flicker
        const initY = d.y % d.total; const initYPx = -Math.round(initY);
        t.track.style.transform = `translate3d(0, ${initYPx}px, 0)`;
      });
    }

    computeMetrics();

    function renderAll(){
      tracks.forEach((t, i)=>{
        const d = data[i];
        let renderY = d.y % d.total; if(renderY < 0) renderY += d.total;
        const yPx = -Math.round(renderY);
        t.track.style.transform = `translate3d(0, ${yPx}px, 0)`;
      });
    }

    // easing helper not used; removed to satisfy ESLint no-unused-vars

    // Curated combos: [жанр, поджанр, особенность]
    const combos = [
      ['Выживание','Хоррор-выживание','Кооператив на 4 игрока'],
      ['Рогалик','Экшен-рогалик','Случайная генерация уровней'],
      ['Шутер','Тактический шутер','Разрушаемое окружение'],
      ['Песочница','Крафтовая песочница','Мастерская Steam'],
      ['Пати-игра','Социальная дедукция','Кроссплей'],
      ['Экшен','Souls-like','Хардкорная боёвка'],
      ['Стратегия','Тактика в реальном времени','Совместимость модов'],
      ['Симулятор','Космосим','Система экипажа'],
      ['Приключение','Метроидвания','Нелинейное прохождение'],
      ['Хоррор','Кооперативный хоррор','Случайные события'],
    ];
  
    function findIndexByText(track, text){
      const items = Array.from(track.querySelectorAll('.slot'));
      const idx = items.findIndex(s => (s.textContent||'').trim().toLowerCase() === text.toLowerCase());
      return idx >= 0 ? idx : Math.floor(Math.random()*items.length);
    }

    function computeTargets(preset){
      const now = performance.now();
      tracks.forEach((t, i)=>{
        const d = data[i];
        // Determine chosen index within base slots only
        let chosenIdx = (preset && preset[i]) ? findIndexByText(t.track, preset[i]) : Math.floor(Math.random()*d.baseCount);
        // Normalize in case findIndexByText returned an index from repeated content
        chosenIdx = ((chosenIdx % d.baseCount) + d.baseCount) % d.baseCount;
        // Compute current centered base index to avoid no-op in reduced motion
        const currBaseIdx = ((Math.round((d.y + d.cy) / d.step) % d.baseCount) + d.baseCount) % d.baseCount;
        if(chosenIdx === currBaseIdx){ chosenIdx = (chosenIdx + 1) % d.baseCount; }
        const blockCenter = Math.floor(d.repeat/2);
        const baseTop = (blockCenter * d.baseCount + chosenIdx) * d.step;
        const snap = baseTop - d.cy; // exact snapped position at finish
        let target = snap;
        // Ensure forward motion and add extra loops for feel
        while(target <= d.y + d.step){ target += d.baseCount * d.step; }
        const maxExtra = Math.max(3, 3 + i); // aim 3+ loops
        target += Math.min(maxExtra, REPEAT_BLOCKS - 2) * d.baseCount * d.step;
        d.yStart = d.y;
        d.target = target;
        d.snap = snap;
        d.chosenIdx = chosenIdx;
        d.done = false;
        d.startT = now + i*180;
        d.dur = (1600 + i*300) * 3;
      });
    }

    let rowLitTO = 0;
    function step(){
      raf = 0;
      const now = performance.now();
      let allDone = true;
      tracks.forEach((t, i)=>{
        const d = data[i];
        if(!d.done){
          allDone = false;
          const tt = now - d.startT;
          if(tt <= 0){
            // wait for start
          } else if(tt >= d.dur){
            // Snap to exact center to avoid any subpixel drift and blank gaps
            d.y = d.snap; d.done = true;
          } else {
            const p = Math.min(1, tt / d.dur);
            const e = 1 - Math.pow(1-p, 3);
            d.y = d.yStart + (d.target - d.yStart) * e;
          }
          const yMod = (d.y % d.step + d.step) % d.step;
          const crossed = yMod < 6 && d.lastYMod >= 6;
          if(crossed && (now - d.lastTickT) > 200 && !isMuted && audioCtx){ d.lastTickT = now; playTick(); }
          d.lastYMod = yMod;
          // Normalize and render
          let renderY = d.y % d.total; if(renderY < 0) renderY += d.total;
          const yPx = -Math.round(renderY);
          t.track.style.transform = `translate3d(0, ${yPx}px, 0)`;
        }
      });
      if(allDone){
        spinning = false;
        reelsWrap.classList.remove('spinning');
        // Add vibrant row highlight on finish
        reelsWrap.classList.add('row-lit');
        clearTimeout(rowLitTO);
        rowLitTO = setTimeout(()=>{ reelsWrap.classList.remove('row-lit'); }, 2200);
        // Highlight centered slot in each column
        tracks.forEach((t,i)=>{
          const d = data[i];
          const activeIdx = Math.floor(d.repeat/2)*d.baseCount + d.chosenIdx;
          // Remove previous highlights
          t.track.querySelectorAll('.slot--active').forEach(el=>el.classList.remove('slot--active'));
          const slots = t.track.querySelectorAll('.slot');
          const el = slots[activeIdx] || null;
          if(el) el.classList.add('slot--active');
        });
        playChime();
        // If demo mode is active and allowed, spin again after a short pause
        if(demoMode && demoAllowed){
          setTimeout(()=>{ if(!spinning) spin(null); }, 800);
        }
        return;
      }
      raf = requestAnimationFrame(step);
    }

    function spin(preset){
      if(spinning) return;
      const pick = preset || (Math.random()<0.6 ? combos[Math.floor(Math.random()*combos.length)] : null);
      if(prefersReducedMotion.matches && !forceAnimate){
        // Low-motion path: instantly snap to targets and render once
        computeTargets(pick);
        data.forEach(d=>{ d.y = d.snap; d.done = true; });
        renderAll();
        ensureAudio(); playChime();
        reelsWrap.classList.remove('spinning');
        spinning = false;
        return;
      }
      ensureAudio();
      spinning = true;
      reelsWrap.classList.add('spinning');
      computeTargets(pick);
      if(!raf) raf = requestAnimationFrame(step);
    }

    // Controls bindings
    if(btnSpin){
      btnSpin.addEventListener('click', ()=>{
        // disable demo mode on first explicit user spin
        demoMode = false;
        spin(null);
      });
    }
    // No mute button currently rendered; keep sound on user gesture via ensureAudio in spin

    // Start demo mode automatically (auto-spin until user clicks Spin)
    setTimeout(()=>{ if(!spinning && demoMode && demoAllowed) spin(null); }, 400);

    // Pause/resume demo mode based on visibility in viewport
    try {
      const reelsSection = coming.closest('.section') || coming;
      const ioReels = new IntersectionObserver((entries)=>{
        entries.forEach(e=>{
          demoAllowed = e.isIntersecting;
        });
      }, { threshold: 0.25 });
      ioReels.observe(reelsSection);
    } catch {}

    // Recompute metrics on resize/orientation change to keep center alignment stable
    let resizeTO = 0;
    function onResize(){
      clearTimeout(resizeTO);
      resizeTO = setTimeout(()=>{ computeMetrics(); renderAll(); }, 120);
    }
    window.addEventListener('resize', onResize, { passive: true });
    window.addEventListener('orientationchange', onResize, { passive: true });
  })();

  // Back-to-top arrow visibility after the screenshots section (robust for mobile/desktop)
  (function setupToTop(){
    const btn = document.getElementById('to-top');
    const sec = document.querySelector('.section--shots');
    if(!btn || !sec) return;
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    let ticking = false;

    let threshold = 0; // Y-pos in px where button should appear
    function computeThreshold(){
      const rect = sec.getBoundingClientRect();
      const scrollY = window.scrollY || window.pageYOffset || 0;
      // Appear once user scrolled past bottom of screenshots by 64px
      threshold = scrollY + rect.top + rect.height - 64;
    }

    function update(){
      ticking = false;
      const y = window.scrollY || window.pageYOffset || 0;
      const show = y > threshold;
      btn.classList.toggle('show', show);
    }

    function onScroll(){ if(!ticking){ ticking = true; requestAnimationFrame(update); } }
    function onResize(){ computeThreshold(); onScroll(); }
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onResize, { passive: true });
    window.addEventListener('orientationchange', onResize, { passive: true });
    window.addEventListener('load', onResize, { passive: true });

    // Recompute when screenshots images load (they affect section height)
    sec.querySelectorAll('img').forEach(img=>{
      if(img.complete){ return; }
      img.addEventListener('load', onResize, { once: true, passive: true });
      img.addEventListener('error', onResize, { once: true, passive: true });
    });

    // initial
    computeThreshold();
    update();

    btn.addEventListener('click', (e)=>{
      e.preventDefault();
      window.scrollTo({ top: 0, behavior: prefersReducedMotion.matches ? 'auto' : 'smooth' });
    });
  })();

  // Smooth scroll for header nav links (#games, #features, #download) without affecting general scroll
  (function smoothScrollHeaderNav(){
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const nav = document.querySelector('.site-header .nav');
    if(!nav) return;
    nav.querySelectorAll('a[href^="#"]').forEach(a=>{
      a.addEventListener('click', (e)=>{
        const id = a.getAttribute('href').slice(1);
        const target = document.getElementById(id);
        if(!target) return;
        e.preventDefault();
        target.scrollIntoView({ behavior: prefersReducedMotion.matches ? 'auto' : 'smooth', block: 'start' });
      });
    });
  })();

  // Smooth scroll only for the "Смотреть игры" button in hero CTA
  (function smoothScrollGames(){
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const btn = document.querySelector('.cta a[href="#games"]');
    if(!btn) return;
    btn.addEventListener('click', (e)=>{
      const target = document.getElementById('games');
      if(!target) return;
      e.preventDefault();
      if(prefersReducedMotion.matches){
        // Respect user OS setting: jump without animation
        target.scrollIntoView({ behavior: 'auto', block: 'start' });
      } else {
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    });
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
  let frameSkip = isLowEndDevice ? 1 : 0; // skip every other frame on low-end
  function step(){
    time += 0.016;
    // simple frame skipping
    if(frameSkip){ frameSkip = 0; return rafId = requestAnimationFrame(step); }
    frameSkip = isLowEndDevice ? 1 : 0;
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

  // Pause canvas animation when tab is hidden or when waves are offscreen to save CPU
  document.addEventListener('visibilitychange', ()=>{
    if(document.hidden){ running = false; if(rafId) cancelAnimationFrame(rafId); }
    else { running = true; rafId = requestAnimationFrame(step); }
  });

  try {
    const wavesWrap = document.querySelector('.waves-bg');
    if(wavesWrap){
      const ioWaves = new IntersectionObserver((entries)=>{
        const e = entries[0];
        if(!e) return;
        if(e.isIntersecting){ if(!running){ running = true; rafId = requestAnimationFrame(step); } }
        else { running = false; if(rafId) cancelAnimationFrame(rafId); }
      }, { threshold: 0.05 });
      ioWaves.observe(wavesWrap);
    }
  } catch {}

  // Parallax scroll
  const layers = document.querySelectorAll('.layer');
  const enableParallax = !isMobile() && !prefersReducedMotion.matches && !isLowEndDevice;
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
    const touchCapable = 'ontouchstart' in window || navigator.maxTouchPoints > 0;
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
      // Do not prevent default on touch devices to allow vertical scroll
      if(!(e.touches)) e.preventDefault();
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
      // inertial scrolling with friction (only for mouse-driven drag)
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

    // Desktop: mouse drag with synthetic inertia
    sc.addEventListener('mousedown', onDown);
    sc.addEventListener('mousemove', onMove);
    sc.addEventListener('mouseup', onUp);
    sc.addEventListener('mouseleave', onUp);
    // Mobile: rely on native touch scrolling for best responsiveness
    if(!touchCapable){
      // No-op: touch handlers intentionally not attached on phones
    }
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

  // Removed randomization: curated reels stay fixed per column
})();
