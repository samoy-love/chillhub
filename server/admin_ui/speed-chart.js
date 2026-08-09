// Самодостаточный график скорости загрузки — без uPlot и без CDN.
//
// Прежняя реализация рисовала график через uPlot, который грузится с unpkg.com
// отдельным <script> в admin.html. Для части пользователей (в частности из РФ)
// такие CDN бывают недоступны или блокируются — window.uPlot тогда просто не
// определён, код проверял это (`if (speedWrap && window.uPlot)`) и молча не
// рисовал график вообще, без единой ошибки в консоли. Снаружи это выглядело
// ровно как «график ничего не рисует», хотя вся остальная логика (проценты,
// байты, скорость текстом) работала нормально — они uPlot не требуют.
//
// Здесь график рисуется на обычном <canvas> средствами самого браузера:
// внешних скриптов не грузит, значит не может «не загрузиться».
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // resolvePadding принимает либо число (одинаковый отступ со всех сторон,
  // как раньше), либо объект {left,right,top,bottom} — нужно раздельно, чтобы
  // слева хватило места под подписи скорости, а снизу под подписи времени, не
  // раздувая отступы там, где подписей нет (сверху и справа).
  function resolvePadding(padding) {
    if (padding && typeof padding === 'object') {
      return {
        left: padding.left || 0,
        right: padding.right || 0,
        top: padding.top || 0,
        bottom: padding.bottom || 0,
      };
    }
    const p = padding || 0;
    return { left: p, right: p, top: p, bottom: p };
  }

  // visiblePoints оставляет точки не старше horizonMs от nowTs — окно, которое
  // график реально показывает (старее просто не рисуется, но и не обязано
  // быть вычищено вызывающим кодом отдельно).
  function visiblePoints(points, nowTs, horizonMs) {
    return points.filter(p => nowTs - p.t <= horizonMs);
  }

  // mapPointsToPixels переводит точки {t (мс, как performance.now()), bps} в
  // пиксельные координаты холста width×height: x — давность точки (0 у левого
  // края = horizonMs назад, у правого = сейчас), y — доля от maxBps (или от
  // пика видимых точек, если maxBps не задан). Чистая функция, без обращения
  // к DOM/canvas — поэтому проверяется в node без браузера.
  function mapPointsToPixels(points, opts) {
    const width = opts.width;
    const height = opts.height;
    const pad = resolvePadding(opts.padding);
    const horizonMs = opts.horizonMs || 120000;
    const nowTs = opts.now;
    const vis = visiblePoints(points, nowTs, horizonMs);
    const visMax = vis.reduce((m, p) => Math.max(m, p.bps), 0);
    const maxBps = Math.max(1, opts.maxBps || 0, visMax);
    const innerW = Math.max(1, width - pad.left - pad.right);
    const innerH = Math.max(1, height - pad.top - pad.bottom);
    return vis.map(p => {
      const age = Math.min(horizonMs, Math.max(0, nowTs - p.t));
      const xFrac = 1 - (age / horizonMs);
      const yFrac = p.bps / maxBps;
      return {
        x: pad.left + xFrac * innerW,
        y: pad.top + (1 - yFrac) * innerH,
        bps: p.bps,
        t: p.t,
      };
    });
  }

  // formatAge renders "Ns"/"Nм Nс" for an x-axis tick — same rough shape as
  // formatEta in admin.js, but kept local: this module has zero dependencies
  // on the rest of admin.js on purpose, so it can be reused (or tested) on
  // its own.
  function formatAge(ms) {
    const s = Math.round(ms / 1000);
    if (s < 60) return s + 'с';
    const m = Math.floor(s / 60);
    const rem = s % 60;
    return rem ? (m + 'м ' + rem + 'с') : (m + 'м');
  }

  // drawSpeedChart рисует линию скорости на canvas: сетка из трёх горизонталей
  // с подписями скорости слева, подписи времени снизу (сколько секунд назад),
  // сама линия, подпись пикового значения видимого окна сверху. Каждый вызов
  // рисует холст заново целиком (нет накопленного состояния кроме самого
  // массива points, которым владеет вызывающий код) — так проще, чем
  // поддерживать инкрементальный рендер, и на графике из десятков-сотен точек
  // незаметно дороже.
  function drawSpeedChart(canvas, points, opts) {
    opts = opts || {};
    const dpr = (typeof window !== 'undefined' && window.devicePixelRatio) || 1;
    const cssW = canvas.clientWidth || opts.width || 300;
    const cssH = canvas.clientHeight || opts.height || 120;
    const pxW = Math.max(1, Math.round(cssW * dpr));
    const pxH = Math.max(1, Math.round(cssH * dpr));
    if (canvas.width !== pxW) canvas.width = pxW;
    if (canvas.height !== pxH) canvas.height = pxH;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);

    const nowTs = (opts.now !== undefined && opts.now !== null) ? opts.now : (typeof performance !== 'undefined' ? performance.now() : Date.now());
    const horizonMs = opts.horizonMs || 120000;
    const formatSpeed = opts.formatSpeed || String;
    // Слева — место под подписи скорости (3 строки вида "12.3 МБ/с"), снизу —
    // под подписи времени ("60с", "30с", "0с"). Сверху/справа отступ чисто
    // косметический, там ничего не подписывается.
    const pad = { left: opts.axisLeft || 54, right: 6, top: 8, bottom: 16 };

    ctx.fillStyle = opts.bg || 'rgba(255,255,255,0.04)';
    ctx.fillRect(0, 0, cssW, cssH);

    const visMax = points.reduce((m, p) => (nowTs - p.t <= horizonMs ? Math.max(m, p.bps) : m), 0);
    const maxBps = Math.max(1, opts.peakBps || 0, visMax);

    ctx.strokeStyle = opts.gridColor || 'rgba(255,255,255,0.14)';
    ctx.fillStyle = opts.textColor || 'rgba(255,255,255,0.7)';
    ctx.font = (opts.fontSize || 10) + 'px sans-serif';
    ctx.lineWidth = 1;
    const innerH = cssH - pad.top - pad.bottom;
    for (let i = 0; i <= 2; i++) {
      const frac = i / 2; // 0=верх(max), 1=низ(0)
      const y = pad.top + innerH * frac;
      ctx.strokeStyle = opts.gridColor || 'rgba(255,255,255,0.14)';
      ctx.beginPath();
      ctx.moveTo(pad.left, Math.round(y) + 0.5);
      ctx.lineTo(cssW - pad.right, Math.round(y) + 0.5);
      ctx.stroke();
      // Y-axis speed label: top=max, middle=half, bottom=0. formatSpeed(0)
      // deliberately returns '' elsewhere in admin.js (it hides the live
      // speed readout before the first chunk lands), but a blank bottom
      // label on an axis reads as a bug, not as "not measured yet" — spell
      // the zero out explicitly instead of forwarding the empty string.
      const val = maxBps * (1 - frac);
      ctx.fillStyle = opts.textColor || 'rgba(255,255,255,0.7)';
      ctx.textBaseline = i === 0 ? 'top' : (i === 2 ? 'bottom' : 'middle');
      ctx.fillText(val > 0 ? formatSpeed(val) : '0', 2, y);
    }

    // X-axis time labels: horizon ago (left), half (middle), now (right).
    ctx.textBaseline = 'bottom';
    const xTickFracs = [0, 0.5, 1];
    xTickFracs.forEach((frac) => {
      const x = pad.left + (cssW - pad.left - pad.right) * frac;
      const ageMs = horizonMs * (1 - frac);
      const label = ageMs <= 0 ? 'сейчас' : formatAge(ageMs) + ' назад';
      ctx.textAlign = frac === 0 ? 'left' : (frac === 1 ? 'right' : 'center');
      ctx.fillText(label, x, cssH);
    });
    ctx.textAlign = 'left';
    ctx.textBaseline = 'alphabetic';

    const pixels = mapPointsToPixels(points, { width: cssW, height: cssH, padding: pad, horizonMs, now: nowTs, maxBps: opts.peakBps });
    if (pixels.length >= 2) {
      ctx.strokeStyle = opts.lineColor || '#0d6efd';
      ctx.lineWidth = 2;
      ctx.beginPath();
      pixels.forEach((p, i) => { if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y); });
      ctx.stroke();
    }
  }

  return { mapPointsToPixels, drawSpeedChart, formatAge };
});
