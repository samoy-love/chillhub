/* Waves background on Canvas + Parallax + Reveal + Tilt */
(function(){
  // Отметка «скрипт выполнился». За неё в стилях спрятаны две вещи, которые
  // без скрипта до конца не доводятся: начальная прозрачность блоков с
  // data-animate (класс `in` им ставит IntersectionObserver ниже) и заглушка
  // фона со спиннером (её снимает отрисовка волн). Ставим первой строкой,
  // чтобы страница не успела мигнуть видимым содержимым.
  document.documentElement.classList.add('js');

  const mqMobile = window.matchMedia('(max-width: 640px)');
  const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const isMobile = () => mqMobile.matches;
  // Basic UA detection: target iOS Safari (including iPadOS desktop-mode Safari)
  const ua = navigator.userAgent || navigator.vendor || window.opera || '';
  const isIOS = /iP(hone|od|ad)/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  const isWebKit = /WebKit\//.test(ua) && !/Edge\//.test(ua);
  const isSafari = isWebKit && /Safari\//.test(ua) && !/Chrome\//.test(ua) && !/CriOS\//.test(ua);
  const isLowEndDevice = (() => {
    const hc = navigator.hardwareConcurrency || 2;
    // deviceMemory is not on iOS Safari; assume low if missing and mobile
    const mem = navigator.deviceMemory || (isMobile() ? 1 : 4);
    const ua = navigator.userAgent || '';
    const oldIOS = /OS\s(1[0-3]|[0-9]_)/.test(ua) || /iPhone OS [0-9_]{1,3}/.test(ua);
    const oldAndroid = /Android\s(4|5|6|7|8)\./.test(ua);
    return hc <= 2 || mem <= 2 || oldIOS || oldAndroid;
  })();

  // Image fallbacks — replaces the inline onerror="" attributes that used to
  // live in index.html. Those attributes forced script-src 'unsafe-inline' in
  // the site's Content-Security-Policy, which defeats most of the point of
  // having a CSP at all. The intent now lives in a data-fallback attribute:
  //   data-fallback="hide"          -> hide the element if it fails to load
  //   data-fallback="<url>"         -> swap in this URL if it fails to load
  //
  // This must cope with images that have ALREADY failed by the time this runs:
  // main.js is loaded with defer, so parsing is finished and an eager image
  // (or a cached failure) may have fired its error event before any listener
  // existed. A decoded-but-broken image reports complete === true together
  // with naturalWidth === 0, which is the check used below.
  (function setupImageFallbacks(){
    const apply = (img) => {
      if(img.dataset.fallbackApplied) return;   // never react twice
      img.dataset.fallbackApplied = '1';
      const fb = img.getAttribute('data-fallback');
      if(!fb) return;
      if(fb === 'hide'){ img.style.display = 'none'; return; }
      // If the fallback itself 404s we simply stop: the guard above means the
      // error handler cannot re-enter and start an infinite src-swap loop.
      img.src = fb;
    };
    document.querySelectorAll('img[data-fallback]').forEach((img)=>{
      img.addEventListener('error', ()=> apply(img), { once:true });
      if(img.complete && img.naturalWidth === 0) apply(img);
    });
  })();

  // Ctrl/Cmd+клик, Shift+клик и средняя кнопка — это просьба открыть ссылку в
  // новой вкладке или окне. Безусловный preventDefault() такую просьбу
  // отменяет: вместо новой вкладки страница просто прокручивается или
  // перезагружается. Поэтому каждый обработчик ссылок сначала спрашивает здесь.
  const isPlainClick = (e) => !(e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0);

  // Brand click: reload page, clear hash, and scroll to top
  (function setupBrandReload(){
    const brand = document.querySelector('.site-header .brand');
    if(!brand) return;
    brand.addEventListener('click', (e)=>{
      if(!isPlainClick(e)) return;
      // Always handle ourselves to ensure hash reset and scroll-to-top
      e.preventDefault();
      try {
        // Clear hash without a jump
        if(location.hash){
          history.replaceState(null, '', location.pathname + location.search);
        }
      } catch {}
      // Ensure we are at the top before reload to avoid preserved scroll
      try { window.scrollTo({ top: 0, behavior: 'auto' }); } catch {}
      // Reload the current page
      try { location.reload(); } catch { location.href = './'; }
    });
  })();

  // Note: Header uses a fixed CSS variable height; no JS syncing required.

  // iOS Safari URL bar collapse assistance
  (function iosSafariUrlbarFix(){
    if(!(isIOS && isSafari)) return;
    // Append a tiny bottom spacer to ensure the document can always scroll by at least 1px
    function ensureSpacer(){
      try {
        if(document.querySelector('.ios-urlbar-poke')) return;
        const spacer = document.createElement('div');
        spacer.className = 'ios-urlbar-poke';
        document.body.appendChild(spacer);
      } catch {}
    }
    // Nudge scroll to encourage URL bar to collapse; keep it minimal and safe
    function nudgeScroll(){
      try {
        // Only nudge if near the very top to avoid disrupting user position
        const y = window.scrollY || window.pageYOffset || 0;
        if(y <= 0) {
          // Two-step to bypass some throttling cases
          window.scrollTo(0, 1);
          setTimeout(()=>{ try{ window.scrollTo(0, 1); }catch{} }, 50);
        }
      } catch {}
    }
    // Run on DOM ready and after full load
    if(document.readyState !== 'loading') { ensureSpacer(); nudgeScroll(); }
    else document.addEventListener('DOMContentLoaded', ()=>{ ensureSpacer(); nudgeScroll(); }, { once: true });
    window.addEventListener('load', ()=>{ ensureSpacer(); nudgeScroll(); }, { once: true });
    // Also nudge on first user interaction and on orientation changes
    const once = (el, ev, fn)=>{ const h = ()=>{ el.removeEventListener(ev, h, { passive:true }); fn(); }; el.addEventListener(ev, h, { passive:true }); };
    once(window, 'touchstart', ()=>{ ensureSpacer(); nudgeScroll(); });
    once(window, 'scroll', ()=>{ ensureSpacer(); });
    window.addEventListener('orientationchange', ()=>{ setTimeout(()=>{ ensureSpacer(); nudgeScroll(); }, 120); }, { passive: true });
  })();

  const canvas = document.getElementById('waves-canvas');
  const ctx = canvas.getContext('2d');
  // Lower DPR on mobile and low-end to reduce GPU load
  let dpr = (()=>{
    const base = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    if (isLowEndDevice) return 1; // force 1x
    // Cap desktop DPR more aggressively to keep FPS high
    return isMobile() ? 1 : Math.min(1.2, base);
  })();

  // Reels engine with sounds (spin button) + idle gentle scroll
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
    var idle = true; // gentle infinite scroll until user clicks
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

    // Base fallback; will be overridden per column in computeMetrics() adaptively
    const REPEAT_BLOCKS = 2;
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

    // Toggle heavy rendering hints only while animating to keep FPS high when static
    let animActive = false;
    function setAnimating(on){
      if(on === animActive) return;
      animActive = on;
      tracks.forEach((t)=>{
        // Always keep minimal, cheap hints to help scrolling performance on desktop
        t.track.style.backfaceVisibility = 'hidden';
        t.track.style.transformStyle = 'preserve-3d';
        t.track.style.contain = 'paint';
        // Toggle only the costly will-change during active animation
        t.track.style.willChange = on ? 'transform' : '';
      });
    }

    function computeMetrics(){
      const wrapH = reelsWrap.clientHeight;
      tracks.forEach((t, i)=>{
        const d = data[i];
        // Build repeated content fresh to avoid drift
        const base = t.baseSlots;
        d.baseCount = base.length;
        // Measure step using a single probe slot to compute minimal repeat count needed
        t.track.innerHTML = '';
        let probe = base[0] ? base[0].cloneNode(true) : null;
        if(probe){ t.track.appendChild(probe); }
        // After DOM is ready, measure — normalize all slot heights to the max to ensure perfect alignment later
        const s0 = t.track.querySelector('.slot');
        let maxH = 0;
        if(s0){ maxH = Math.round(s0.getBoundingClientRect().height) || 56; }
        if(maxH <= 0) maxH = 56;
        // Approx margins using computed style of the probe
        let mTop = 0, mBottom = 0, gapApprox = 0;
        if(s0){
          const csSlot = window.getComputedStyle(s0);
          mTop = Math.round(parseFloat(csSlot.marginTop||'0')||0);
          mBottom = Math.round(parseFloat(csSlot.marginBottom||'0')||0);
        }
        const stepApprox = Math.max(1, Math.round(maxH + gapApprox + mTop + mBottom));
        // Decide repeat so that total height >= ~4x viewport to avoid visible edge refills
        const periodApprox = Math.max(1, d.baseCount * stepApprox);
        let needRepeat = Math.max(REPEAT_BLOCKS, Math.ceil((wrapH * 4) / periodApprox));
        needRepeat = Math.max(3, Math.min(10, needRepeat));
        d.repeat = needRepeat;
        // Clear probe and rebuild track with chosen repeat count
        t.track.innerHTML = '';
        const frag = document.createDocumentFragment();
        for(let r=0; r<d.repeat; r++){
          base.forEach(s=> frag.appendChild(s.cloneNode(true)));
        }
        t.track.appendChild(frag);
        // Normalize zebra pattern across the entire repeated list to avoid visual glitches
        const allSlots = t.track.querySelectorAll('.slot');
        allSlots.forEach((el, idx)=>{
          if(idx % 2 === 1) el.classList.add('alt'); else el.classList.remove('alt');
        });
        // After DOM is ready, measure — normalize all slot heights to the max to ensure perfect alignment
        const allForH = t.track.querySelectorAll('.slot');
        allForH.forEach(el=>{ maxH = Math.max(maxH, Math.round(el.getBoundingClientRect().height)); });
        if(maxH <= 0 && s0){ maxH = Math.round(s0.getBoundingClientRect().height) || 56; }
        if(maxH <= 0) maxH = 56;
        allForH.forEach(el=>{ el.style.height = `${maxH}px`; });
        // Quantize to integers to prevent subpixel drift between measurements and transforms
        d.slotH = maxH;
        const csTrack = window.getComputedStyle(t.track);
        d.gap = Math.round(parseFloat(csTrack.rowGap||csTrack.gap||'0') || 0);
        // include vertical margins from slot into step (use a fresh slot from rebuilt track)
        mTop = 0; mBottom = 0;
        const s1 = t.track.querySelector('.slot');
        if(s1){ const csSlot2 = window.getComputedStyle(s1); mTop = Math.round(parseFloat(csSlot2.marginTop||'0')||0); mBottom = Math.round(parseFloat(csSlot2.marginBottom||'0')||0); }
        d.step = Math.max(1, Math.round(d.slotH + d.gap + mTop + mBottom));
        d.total = Math.max(1, Math.round(d.baseCount * d.step * d.repeat));
        // Precompute period and central safety window so we never approach edges
        d.period = d.baseCount * d.step;
        d.center = Math.floor(d.total / 2);
        // Safety window size: large enough (>= wrap height, >= 8 steps, >= 1.5 periods), but < half total
        const maxSafe = Math.max(1, Math.floor(d.total / 2) - d.step);
        d.safety = Math.min(maxSafe, Math.max(Math.floor(wrapH * 1.0), d.step * 8, Math.floor(d.period * 1.5)));
        d.cy = Math.round(wrapH/2 - d.step/2);
        // Reset transform to a safe normalized value (middle block start)
        d.y = ((d.baseCount * Math.floor(d.repeat/2)) * d.step) - d.cy;
        // Use integer pixel translation to avoid hairline gaps/flicker
        const initY = ((d.y % d.total) + d.total) % d.total;
        const initYPx = -Math.floor(initY);
        t.track.style.transform = `translate3d(0, ${initYPx}px, 0)`;
        // Base hints are applied via setAnimating (kept minimal when idle)
      });
    }

    // computeMetrics will be called after we augment base slots from combos

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
      ['Выживание','Хоррор-выживание','Кооператив'],
      ['Рогалик','Экшен-рогалик','Случайная генерация уровней'],
      ['Шутер','Тактический шутер','Разрушаемое окружение'],
      ['Песочница','Крафтовая песочница','Мастерская Steam'],
      ['Пати-игра','Социальная дедукция','Кроссплей'],
      ['Экшен','Souls-like','Хардкорная боёвка'],
      ['Стратегия','Тактика в реальном времени','Совместимость модов'],
      ['Симулятор','Космосим','Система экипажа'],
      ['Приключение','Метроидвания','Нелинейное прохождение'],
      ['Хоррор','Кооперативный хоррор','Случайные события'],
      // User additions
      ['Рогалик','Карточный рогалик','Открытие карт'],
      ['Шутер','Пулевой ад','Экраны врагов'],
      ['Песочница','Физическая песочница','Смешные баги'],
      ['Стратегия','Градостроение','Управление жителями'],
      ['Симулятор','Ферма-сим','Смена сезонов'],
      ['Экшен','Слэшер','Комбо-система'],
      ['Приключение','Квест','Головоломки'],
      ['Выживание','Автоматизация','Фабрики и цепи'],
      ['Пати-игра','Мини-игры','Локальный мультиплеер'],
      ['Рогалик','Платформер-рогалик','Рост персонажа'],
      ['Хоррор','Психологический хоррор','Четвёртая стена'],
      ['Шутер','Арена-шутер','Физика оружия'],
      ['Приключение','Визуальная новелла','Множественные концовки'],
      ['Стратегия','Пошаговая тактика','Классы юнитов'],
      ['Симулятор','Жизнь в деревне','Отношения с NPC'],
      ['Экшен','Ритм-экшен','Игра в такт'],
      ['Песочница','Воксельная песочница','Разрушаемый мир'],
    ];

    // Ensure all texts from combos exist in the base slots per column (0: жанр, 1: поджанр, 2: особенность)
    (function augmentBaseSlotsFromCombos(){
      // Build column-wise sets of existing texts
      const colSets = [new Set(), new Set(), new Set()];
      tracks.forEach((t, colIdx)=>{
        t.baseSlots.forEach(s=> colSets[colIdx].add((s.textContent||'').trim()));
      });
      // For each combo, if a text is missing in a column, create and append a slot to that column's base list
      combos.forEach(row=>{
        row.forEach((txt, colIdx)=>{
          const norm = String(txt||'').trim();
          if(!norm) return;
          if(!colSets[colIdx].has(norm)){
            const el = document.createElement('div');
            el.className = 'slot';
            el.textContent = norm;
            tracks[colIdx].baseSlots.push(el);
            colSets[colIdx].add(norm);
          }
        });
      });
    })();

    // Now that base slots include everything from combos, build repeated tracks and measure
    computeMetrics();
  
    function findIndexByText(track, text){
      const items = Array.from(track.querySelectorAll('.slot'));
      const idx = items.findIndex(s => (s.textContent||'').trim().toLowerCase() === text.toLowerCase());
      return idx >= 0 ? idx : Math.floor(Math.random()*items.length);
    }

    function computeTargets(preset, opts){
      const now = performance.now();
      const strong = !!(opts && opts.strong);
      // durations staggered so columns stop one-by-one (left -> right) — same on all devices
      const baseDur = 4200; // longer base duration like casino reels
      const durStep = 800;  // softer staggering between reels
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
        // Strong user spin: more extra loops for momentum (feel like casino)
        const baseLoops = strong ? 5 : 4; // results in ~5/6/7 loops for columns 0/1/2
        const extra = Math.min(baseLoops + i, Math.max(0, data[i].repeat - 2));
        target += extra * d.baseCount * d.step;
        d.yStart = d.y;
        d.target = target;
        d.snap = snap;
        d.chosenIdx = chosenIdx;
        d.done = false;
        // Start all reels together for a unified blast
        d.startT = now;
        // Stagger stop by increasing durations per reel
        d.dur = baseDur + i * durStep;
      });
    }

    // Central-window stabilization: keep render within a safe band around the center
    function stabilizeY(d){
      if(!d || !d.total || !d.step || !d.baseCount) return;
      const period = d.period || (d.baseCount * d.step);
      const center = d.center || Math.floor(d.total/2);
      const safety = d.safety || Math.max(d.step * 8, Math.floor(period * 1.5));
      let rY = d.y % d.total; if(rY < 0) rY += d.total;
      // Bring close to the nearest period around center
      const dp = Math.round((rY - center) / period);
      if(dp){ d.y -= dp * period; rY = d.y % d.total; if(rY < 0) rY += d.total; }
      const low = Math.max(0, center - safety);
      const high = Math.min(d.total, center + safety);
      if(rY < low){ d.y += period; }
      else if(rY > high){ d.y -= period; }
    }

    // Idle gentle scroll: continuous, infinite, dt-based velocity (like user scroll)
    // Run continuously (do not pause on scroll), but remain efficient.
    const idleSpeedBase = isMobile() ? 36 : 120; // px/sec (faster as requested)
    const idleVariance = [0.98, 1.06, 1.12];
    // Fixed-step accumulator to keep constant idle speed during scroll
    let idleLastWall = 0; // wall-clock in ms
    let idleAcc = 0;      // seconds accumulated
    function idleStep(){
      raf = 0;
      if(!idle || document.hidden){ return; }
      // Keep guideline and heavy hints OFF during idle to lower GPU/paint overhead
      const now = performance.now();
      if(!idleLastWall) idleLastWall = now;
      let elapsed = (now - idleLastWall) / 1000; // seconds
      // Clamp big gaps (e.g. tab switched) to avoid jumps
      if(elapsed > 0.25) elapsed = 0.25;
      // During scroll we still advance with fixed steps; no freeze to keep motion continuous
      idleLastWall = now;
      idleAcc += elapsed;
      const FIXED_DT = 1/60;
      const MAX_STEPS = 3;
      const steps = Math.min(MAX_STEPS, Math.floor(idleAcc / FIXED_DT));
      if(steps > 0){
        idleAcc -= steps * FIXED_DT;
        // Apply fixed steps, then render once
        for(let s = 0; s < steps; s++){
          tracks.forEach((t,i)=>{
            const d = data[i];
            const v = idleSpeedBase * idleVariance[i % idleVariance.length];
            d.y += v * FIXED_DT;
            stabilizeY(d);
            if(d.y > 1e7 || d.y < -1e7){ d.y = ((d.y % d.total) + d.total) % d.total; }
          });
        }
      }
      tracks.forEach((t,i)=>{
        const d = data[i];
        let renderY = d.y % d.total; if(renderY < 0) renderY += d.total;
        const yPx = -Math.round(renderY);
        t.track.style.transform = `translate3d(0, ${yPx}px, 0)`;
      });
      raf = requestAnimationFrame(idleStep);
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
            // Natural accelerate then decelerate to target (easeInOutCubic)
            const p = Math.min(1, tt / d.dur);
            const e = (p < 0.5)
              ? 4 * p * p * p
              : 1 - Math.pow(-2 * p + 2, 3) / 2;
            d.y = d.yStart + (d.target - d.yStart) * e;
          }
          // Tick sound on row crossing
          const yMod = (d.y % d.step + d.step) % d.step;
          const crossed = yMod < 6 && d.lastYMod >= 6;
          if(crossed && (now - d.lastTickT) > 200 && !isMuted && audioCtx){ d.lastTickT = now; playTick(); }
          d.lastYMod = yMod;

          // Keep window centered without breaking easing: shift y,yStart,target together by full periods
          if(d.total && d.step){
            const period = d.period || (d.baseCount * d.step);
            const center = d.center || Math.floor(d.total/2);
            const safety = d.safety || Math.max(d.step * 8, Math.floor(period * 1.5));
            let rY = d.y % d.total; if(rY < 0) rY += d.total;
            let delta = 0;
            const dp = Math.round((rY - center) / period);
            if(dp) delta -= dp * period;
            rY = (d.y + delta) % d.total; if(rY < 0) rY += d.total;
            const low = Math.max(0, center - safety);
            const high = Math.min(d.total, center + safety);
            if(rY < low) delta += period; else if(rY > high) delta -= period;
            if(delta){ d.y += delta; d.yStart += delta; d.target += delta; }
          }

          // Normalize extremely large values
          if(d.y > 1e7 || d.y < -1e7){ d.y = ((d.y % d.total) + d.total) % d.total; }
          let renderY = d.y % d.total; if(renderY < 0) renderY += d.total;
          const yPx = -Math.round(renderY);
          t.track.style.transform = `translate3d(0, ${yPx}px, 0)`;
        }
      });
      if(allDone){
        // Batch highlight in a single frame to avoid transient wrong selections
        requestAnimationFrame(()=>{
          spinning = false;
          reelsWrap.classList.remove('spinning');
          // Hide center guideline when final combo is displayed
          reelsWrap.classList.remove('guideline');
          // Disable costly will-change in static state; minimal hints stay applied
          setAnimating(false);
          // Highlight centered slot in each column simultaneously
          highlightCentered();
          // Add vibrant row highlight on finish
          reelsWrap.classList.add('row-lit');
          clearTimeout(rowLitTO);
          rowLitTO = setTimeout(()=>{ reelsWrap.classList.remove('row-lit'); }, 2200);
          // Desktop lamps strong blink for 5 seconds; CSS hides lamps on mobile
          reelsWrap.classList.add('lamps-blink');
          setTimeout(()=>{ reelsWrap.classList.remove('lamps-blink'); }, 5000);
          playChime();
        });
        // Never auto-spin again after a manual spin; remain stopped
        return;
      }
      raf = requestAnimationFrame(step);
    }

    // Подсвечивает символ под направляющей в каждой колонке.
    function highlightCentered(){
      tracks.forEach((t,i)=>{
        const d = data[i];
        const activeIdx = Math.floor(d.repeat/2)*d.baseCount + d.chosenIdx;
        t.track.querySelectorAll('.slot--active').forEach(el=>el.classList.remove('slot--active'));
        const slots = t.track.querySelectorAll('.slot');
        const el = slots[activeIdx] || null;
        if(el) el.classList.add('slot--active');
      });
    }

    // Показанная комбинация: null — барабаны ещё ни разу не останавливались.
    // Нужна, чтобы пережить смену ширины окна: геометрия слота от неё зависит
    // (на узком экране поля 1px и шаг 58px, на широком 4px и 64px), и без
    // пересчёта сохранённое смещение указывает уже на ЧУЖОЙ символ.
    var shownPick = null;

    function spin(preset, opts){
      if(spinning) return;
      // Disable idle mode
      idle = false;
      // If idle loop holds a pending RAF, cancel it to avoid blocking spin RAF
      if(raf){ cancelAnimationFrame(raf); raf = 0; }
      const pick = preset || combos[Math.floor(Math.random()*combos.length)];
      // Clear any previous highlights immediately so no wrong slots flash at the end
      tracks.forEach(t=> t.track.querySelectorAll('.slot--active').forEach(el=>el.classList.remove('slot--active')));
      const force = !!(opts && opts.force);
      if(prefersReducedMotion.matches && !forceAnimate && !force){
        // Low-motion path: instantly snap to targets and render once
        computeTargets(pick, { strong: false });
        data.forEach(d=>{ d.y = d.snap; d.done = true; });
        renderAll();
        shownPick = pick;
        highlightCentered();
        ensureAudio(); playChime();
        reelsWrap.classList.remove('spinning');
        // Hide guideline when final combo is shown instantly
        reelsWrap.classList.remove('guideline');
        // Disable costly will-change in static state; minimal hints stay applied
        setAnimating(false);
        spinning = false;
        return;
      }
      ensureAudio();
      spinning = true;
      // Show center guideline while spinning
      reelsWrap.classList.add('spinning');
      reelsWrap.classList.add('guideline');
      setAnimating(true);
      // On user click: strong spin
      shownPick = pick;
      computeTargets(pick, { strong: true });
      if(!raf) raf = requestAnimationFrame(step);
    }

    // Controls bindings
    if(btnSpin){
      btnSpin.addEventListener('click', ()=>{
        // Force animation even if user has reduced motion, since it's an explicit gesture
        spin(null, { force: true });
      });
    }
    // No mute button currently rendered; keep sound on user gesture via ensureAudio in spin

    // Start idle gentle scroll (all devices) until user clicks — no initial delay
    if(idle && demoAllowed){ idleLastWall = performance.now(); if(!raf) raf = requestAnimationFrame(idleStep); }

    // Pause/resume idle based on visibility in viewport
    try {
      // Observe the actual reels viewport, not the whole section, and compensate for fixed header
      const header = document.querySelector('.site-header');
      const headerH = header ? Math.round(header.getBoundingClientRect().height) : 0;
      const ioReels = new IntersectionObserver((entries)=>{
        // Сама запись не используется намеренно: логику «останавливать вне экрана»
        // отсюда убрали, и реакция одинакова для входа и выхода из области видимости.
        // Параметр поэтому не объявляем — иначе он висит неиспользуемым.
        entries.forEach(()=>{
          // Still avoid heavy hints when off-screen
          if(!spinning) setAnimating(false);
          // If we were off-screen and come back, ensure idle is ticking
          if(idle && !spinning && !raf){ idleLastWall = performance.now(); raf = requestAnimationFrame(idleStep); }
        });
      }, { threshold: 0.0, rootMargin: `${headerH}px 0px ${Math.max(0, Math.floor(headerH/2))}px 0px` });
      ioReels.observe(reelsWrap);
    } catch {}

    // Recompute metrics on resize/orientation change to keep center alignment stable
    let resizeTO = 0;
    var lastWidth = window.innerWidth;
    function onResize(){
      // На телефоне пересчёт срабатывал от одного скролла: схлопнулась
      // адресная строка, изменился innerHeight — и результат крутки пропадал,
      // потому что пересчёт пересобирает дорожки, а подсветка живёт на узлах
      // старой. Но геометрия слота зависит от ШИРИНЫ, а не от высоты, поэтому
      // сторожим именно её: событие без смены ширины пропускаем.
      //
      // Совсем отключать пересчёт после крутки нельзя: шаг дорожки на узком
      // экране 58px, на широком 64px, и поворот телефона из портрета в
      // ландшафт при сохранённом смещении подставляет под направляющую чужой
      // символ. Поэтому ширина изменилась — пересчитываем и показываем ту же
      // комбинацию заново.
      if(window.innerWidth === lastWidth) return;
      lastWidth = window.innerWidth;
      clearTimeout(resizeTO);
      resizeTO = setTimeout(()=>{
        computeMetrics();
        if(shownPick && !spinning){
          computeTargets(shownPick, { strong: false });
          data.forEach(d=>{ d.y = d.snap; d.done = true; });
        }
        renderAll();
        if(shownPick && !spinning) highlightCentered();
      }, 120);
    }
    window.addEventListener('resize', onResize, { passive: true });
    window.addEventListener('orientationchange', onResize, { passive: true });
  })();


  // Smooth scroll for header nav links (#games, #features, #download) without affecting general scroll
  (function smoothScrollHeaderNav(){
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const nav = document.querySelector('.site-header .nav');
    if(!nav) return;
    nav.querySelectorAll('a[href^="#"]').forEach(a=>{
      a.addEventListener('click', (e)=>{
        if(!isPlainClick(e)) return;
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
      if(!isPlainClick(e)) return;
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

  // Lucky scroll: single, minimal handler for a[href="#casino"] – smooth scroll from current pos and center below header
  function setupLuckyCenterScroll(){
    const anchorSel = 'a[href="#casino"]';
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    function onLuckyClick(e){
      if(!isPlainClick(e)) return;
      const target = document.getElementById('casino');
      if(!target) return;
      e.preventDefault();
      const header = document.querySelector('.site-header');
      const headerH = header ? Math.round(header.getBoundingClientRect().height) : 0;
      const titleEl = document.querySelector('.reels-title');
      const titleH = titleEl ? Math.round(titleEl.offsetHeight || 0) : 0;
      const artH = Math.round(target.offsetHeight || 0);
      const currY = window.scrollY || window.pageYOffset || 0;
      const rect = target.getBoundingClientRect();
      const docTop = currY + rect.top;
      // Distance to target's top (aligned under header)
      const baseTarget = Math.max(0, Math.round(docTop - headerH - 8));
      const dist = baseTarget - currY; // positive means scroll down
      // Scroll less by (title + article) but keep reasonable bounds
      const reduce = titleH + artH;
      const move = dist > 0
        ? Math.max(24, Math.min(dist, dist - reduce))
        : Math.min(-24, Math.max(dist, dist + reduce));
      const finalTop = Math.max(0, Math.round(currY + move));
      const behavior = prefersReducedMotion.matches ? 'auto' : 'smooth';
      const before = window.scrollY || 0;
      window.scrollTo({ top: finalTop, behavior });
      // Fallback: if the browser blocked smooth scroll or nothing changed, use scrollIntoView
      setTimeout(()=>{
        const after = window.scrollY || 0;
        if(Math.abs(after - before) < 2){
          // Ensure visible movement
          target.scrollIntoView({ behavior, block: 'center' });
          // Nudge to account for fixed header on next frame
          requestAnimationFrame(()=>{
            const nowY = window.scrollY || 0;
            const adjustTop = Math.max(0, nowY - Math.round(headerH/2));
            if(Math.abs(adjustTop - nowY) > 1){
              window.scrollTo({ top: adjustTop, behavior });
            }
          });
        }
      }, 250);
      // Update URL without native jump
      if(history.pushState){ history.pushState(null, '', '#casino'); }
    }
    // Только делегирование. Раньше обработчик вешался ещё и напрямую на
    // найденную ссылку, поэтому один клик по ней запускал onLuckyClick дважды:
    // два history.pushState (лишняя запись в истории, «назад» не возвращал) и
    // два конкурирующих window.scrollTo с разными расчётами позиции.
    // Делегирование покрывает и уже существующие ссылки, и добавленные позже.
    document.addEventListener('click', (e)=>{
      const a = e.target.closest(anchorSel);
      if(!a) return;
      onLuckyClick(e);
    }, { passive: false });
  }
  if(document.readyState === 'loading'){
    document.addEventListener('DOMContentLoaded', setupLuckyCenterScroll, { once: true });
  } else {
    setupLuckyCenterScroll();
  }

  // Smooth scroll for ALL links to #download ("Готов начать?") across the page
  (function smoothScrollDownload(){
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const links = Array.from(document.querySelectorAll('a[href="#download"]'));
    if(links.length === 0) return;
    const target = document.getElementById('download');
    if(!target) return;
    links.forEach(a=>{
      a.addEventListener('click', (e)=>{
        if(!isPlainClick(e)) return;
        e.preventDefault();
        target.scrollIntoView({ behavior: prefersReducedMotion.matches ? 'auto' : 'smooth', block: 'start' });
      });
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

  // Mark waves background as ready so the loader hides and canvas fades in
  try {
    const wavesBgEl = document.querySelector('.waves-bg');
    if (wavesBgEl) { wavesBgEl.classList.add('ready'); }
  } catch {}

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
      // Slightly coarser sampling on very wide screens to keep FPS high
      const stepX = (W > 1400 ? 5 : (W > 1000 ? 4 : 3));
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
  // Запуск и остановка цикла анимации — только через эту пару функций.
  // Раньше visibilitychange и IntersectionObserver независимо выполняли
  // `running = true; requestAnimationFrame(step)`, не проверяя, что цикл уже
  // идёт: после нескольких переключений вкладки в фоне крутилось несколько
  // параллельных rAF-циклов на один и тот же canvas — время шло кратно
  // быстрее, а нагрузка на CPU росла с каждым переключением.
  // Кроме того, visibilitychange запускал анимацию даже при
  // prefers-reduced-motion: reduce, хотя на старте она осознанно выключена, —
  // достаточно было один раз свернуть и развернуть вкладку.
  let wavesOnScreen = true; // до первого срабатывания IntersectionObserver
  function wavesShouldRun(){
    return !prefersReducedMotion.matches && !document.hidden && wavesOnScreen;
  }
  function startWaves(){
    if(running) return;             // цикл уже идёт — второй не нужен
    if(!wavesShouldRun()) return;   // сейчас анимация не должна работать
    running = true;
    rafId = requestAnimationFrame(step);
  }
  function stopWaves(){
    running = false;
    if(rafId){ cancelAnimationFrame(rafId); rafId = 0; }
  }

  // Respect reduced motion preference: don't start waves animation
  running = false;
  startWaves();

  // React to changes in reduced motion setting at runtime
  try {
    prefersReducedMotion.addEventListener('change', (e)=>{
      if(e.matches) stopWaves(); else startWaves();
    }, { passive: true });
  } catch {}

  // Pause canvas animation when tab is hidden or when waves are offscreen to save CPU
  document.addEventListener('visibilitychange', ()=>{
    if(document.hidden) stopWaves(); else startWaves();
  });

  try {
    const wavesWrap = document.querySelector('.waves-bg');
    if(wavesWrap){
      const ioWaves = new IntersectionObserver((entries)=>{
        const e = entries[0];
        if(!e) return;
        wavesOnScreen = e.isIntersecting;
        if(wavesOnScreen) startWaves(); else stopWaves();
      }, { threshold: 0.05 });
      ioWaves.observe(wavesWrap);
    }
  } catch {}

  // Parallax scroll
  // Parallax removed: avoid attaching scroll listener if no layers exist
  const layers = document.querySelectorAll('.layer');
  const enableParallax = (!isMobile() && !prefersReducedMotion.matches && !isLowEndDevice && layers.length > 0);
  if(enableParallax){
    window.addEventListener('scroll', ()=>{
      const y = window.scrollY || window.pageYOffset;
      layers.forEach(l => {
        const sp = parseFloat(l.dataset.speed || '0.2');
        l.style.transform = `translateY(${y*sp}px)`;
      });
    }, {passive:true});
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

  // Remove skeleton shimmer on image load
  (function clearSkeletonOnLoad(){
    const imgs = Array.from(document.querySelectorAll('img.skeleton'));
    function done(img){ img.classList.remove('skeleton'); }
    imgs.forEach(img=>{
      if(img.complete){ done(img); return; }
      img.addEventListener('load', ()=> done(img), { once:true, passive:true });
      img.addEventListener('error', ()=> done(img), { once:true, passive:true });
    });
  })();

  // Screenshot: lightbox on click for the single launcher image
  (function setupLightbox(){
    const lb = document.createElement('div'); lb.className = 'lightbox';
    // Это модальное окно, а не просто div: без role/aria-modal скринридер
    // продолжает читать страницу под ним и не сообщает, что открыт диалог.
    lb.setAttribute('role', 'dialog');
    lb.setAttribute('aria-modal', 'true');
    lb.setAttribute('aria-label', 'Скриншот лаунчера');
    lb.setAttribute('aria-hidden', 'true');
    const lbImg = document.createElement('img'); lbImg.className = 'lightbox__img'; lbImg.alt = 'Скриншот лаунчера';
    const btn = document.createElement('button'); btn.type = 'button'; btn.className = 'lightbox__close'; btn.setAttribute('aria-label','Закрыть'); btn.innerHTML = '✕';
    lb.appendChild(lbImg); lb.appendChild(btn);
    document.body.appendChild(lb);

    // Куда вернуть фокус после закрытия. Без этого фокус после Esc оказывался
    // в начале документа, и пользователь клавиатуры терял место на странице.
    let lastFocused = null;
    const FOCUSABLE = 'a[href], button:not([disabled]), input, select, textarea, [tabindex]:not([tabindex="-1"])';

    function open(src){
      lastFocused = document.activeElement;
      lbImg.src = src;
      lb.classList.add('show');
      lb.removeAttribute('aria-hidden');
      document.body.classList.add('modal-open');
      btn.focus();
    }
    function close(){
      lb.classList.remove('show');
      document.body.classList.remove('modal-open');
      lbImg.src = '';
      // aria-hidden ставим только после того, как фокус ушёл наружу:
      // фокус внутри скрытого от AT поддерева — это ошибка доступности.
      if(lastFocused && typeof lastFocused.focus === 'function') lastFocused.focus();
      lastFocused = null;
      lb.setAttribute('aria-hidden', 'true');
    }
    const isOpen = ()=> lb.classList.contains('show');

    lb.addEventListener('click', (e)=>{ if(e.target === lb) close(); });
    btn.addEventListener('click', close);
    window.addEventListener('keydown', (e)=>{
      if(!isOpen()) return;
      if(e.key === 'Escape'){ close(); return; }
      if(e.key !== 'Tab') return;
      // Ловушка фокуса: Tab не должен уводить в страницу под диалогом.
      const items = Array.from(lb.querySelectorAll(FOCUSABLE));
      if(items.length === 0){ e.preventDefault(); return; }
      const first = items[0];
      const last = items[items.length - 1];
      if(e.shiftKey && (document.activeElement === first || !lb.contains(document.activeElement))){
        e.preventDefault(); last.focus();
      } else if(!e.shiftKey && (document.activeElement === last || !lb.contains(document.activeElement))){
        e.preventDefault(); first.focus();
      }
    });

    // Bind for the before/after screenshot
    const zoomBtn = document.querySelector('.compare__zoom');
    const currentShot = document.querySelector('.compare__stage > .compare__img');
    if(zoomBtn && currentShot){
      zoomBtn.addEventListener('click', ()=> open(currentShot.currentSrc || currentShot.src));
    }

    // Bind for the single screenshot window
    const single = document.querySelector('.screenshot-win .win-body img');
    const triggerBtn = document.querySelector('.screenshot-win .win-body');
    if(single && triggerBtn){
      // .win-body — обычный div: без роли и tabindex открыть лайтбокс с
      // клавиатуры было нельзя вовсе.
      if(!triggerBtn.hasAttribute('role')) triggerBtn.setAttribute('role', 'button');
      if(!triggerBtn.hasAttribute('tabindex')) triggerBtn.setAttribute('tabindex', '0');
      if(!triggerBtn.hasAttribute('aria-label')) triggerBtn.setAttribute('aria-label', 'Открыть скриншот лаунчера');
      const openSingle = ()=> open(single.currentSrc || single.src);
      triggerBtn.addEventListener('click', openSingle);
      triggerBtn.addEventListener('keydown', (e)=>{
        if(e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar'){
          e.preventDefault();
          openSingle();
        }
      });
    }
  })();

  // Before/after: бегунок двигает шов между старым и нынешним скриншотом
  (function setupCompare(){
    const fig = document.querySelector('.screenshot-win.compare');
    const range = fig && fig.querySelector('.compare__range');
    if(!fig || !range) return;
    const apply = ()=>{
      fig.style.setProperty('--compare-pos', range.value + '%');
      // В узкой полосе метка «Было» не помещается и наезжает на новый кадр.
      fig.classList.toggle('compare--narrow', Number(range.value) < 12);
    };
    range.addEventListener('input', apply);
    // На старте показываем «до» узкой полосой: сравнение важнее ностальгии.
    apply();
  })();

  // (removed) Gallery-specific behaviors

  // Removed randomization: curated reels stay fixed per column

  /* Счётчик нажатий «Скачать».
     Сама загрузка /downloads/ChillHub-Setup.exe видна серверу и считается по
     журналу nginx. А кнопки в шапке и на первом экране ведут якорем на
     #download — до сервера не доходит ничего, и намерение остаётся невидимым.
     Разница между двумя числами и есть отвал на пути к загрузке.

     Что уходит: пустой POST на /e/download_click. Ни тела, ни параметров, ни
     cookie, ни идентификатора; в журнале нет ни IP, ни User-Agent. */
  (function(){
    document.addEventListener('click', (e)=>{
      const el = e.target && e.target.closest ? e.target.closest('a[href="#download"]') : null;
      if(!el) return;
      try{
        if(typeof navigator.sendBeacon === 'function'){
          navigator.sendBeacon('/e/download_click');
        }else if(typeof fetch === 'function'){
          fetch('/e/download_click', { method: 'POST', keepalive: true }).catch(()=>{});
        }
      }catch{
        // Блокировщик, офлайн — счётчик не важнее кнопки.
      }
    });
  })();
})();
