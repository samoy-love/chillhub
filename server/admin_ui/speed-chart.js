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

  // visiblePoints оставляет точки не старше horizonMs от nowTs — окно, которое
  // график реально показывает (старее просто не рисуется, но и не обязано
  // быть вычищено вызывающим кодом отдельно).
  function visiblePoints(points, nowTs, horizonMs) {
    return points.filter(p => nowTs - p.t <= horizonMs);
  }

  // mapPointsToPixels переводит точки {t (мс, как performance.now()), bps} в
  // пиксельные координаты холста width×height с отступом padding: x — давность
  // точки (0 слева = horizonMs назад, width справа = сейчас), y — доля от
  // maxBps (или от пика видимых точек, если maxBps не задан). Чистая функция,
  // без обращения к DOM/canvas — поэтому проверяется в node без браузера.
  function mapPointsToPixels(points, opts) {
    const width = opts.width;
    const height = opts.height;
    const padding = opts.padding || 0;
    const horizonMs = opts.horizonMs || 120000;
    const nowTs = opts.now;
    const vis = visiblePoints(points, nowTs, horizonMs);
    const visMax = vis.reduce((m, p) => Math.max(m, p.bps), 0);
    const maxBps = Math.max(1, opts.maxBps || 0, visMax);
    const innerW = Math.max(1, width - padding * 2);
    const innerH = Math.max(1, height - padding * 2);
    return vis.map(p => {
      const age = Math.min(horizonMs, Math.max(0, nowTs - p.t));
      const xFrac = 1 - (age / horizonMs);
      const yFrac = p.bps / maxBps;
      return {
        x: padding + xFrac * innerW,
        y: padding + (1 - yFrac) * innerH,
        bps: p.bps,
        t: p.t,
      };
    });
  }

  // drawSpeedChart рисует линию скорости на canvas: сетка из трёх горизонталей,
  // сама линия, подпись пикового значения видимого окна. Каждый вызов рисует
  // холст заново целиком (нет накопленного состояния кроме самого массива
  // points, которым владеет вызывающий код) — так проще, чем поддерживать
  // инкрементальный рендер, и на графике из десятков-сотен точек незаметно
  // дороже.
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
    const padding = (opts.padding !== undefined && opts.padding !== null) ? opts.padding : 8;

    ctx.fillStyle = opts.bg || 'rgba(255,255,255,0.04)';
    ctx.fillRect(0, 0, cssW, cssH);

    ctx.strokeStyle = opts.gridColor || 'rgba(255,255,255,0.14)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 2; i++) {
      const y = padding + (cssH - padding * 2) * (i / 2);
      ctx.beginPath();
      ctx.moveTo(padding, Math.round(y) + 0.5);
      ctx.lineTo(cssW - padding, Math.round(y) + 0.5);
      ctx.stroke();
    }

    const pixels = mapPointsToPixels(points, { width: cssW, height: cssH, padding, horizonMs, now: nowTs, maxBps: opts.peakBps });
    if (pixels.length >= 2) {
      ctx.strokeStyle = opts.lineColor || '#0d6efd';
      ctx.lineWidth = 2;
      ctx.beginPath();
      pixels.forEach((p, i) => { if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y); });
      ctx.stroke();
    }

    if (opts.formatSpeed) {
      const visMax = points.reduce((m, p) => (nowTs - p.t <= horizonMs ? Math.max(m, p.bps) : m), 0);
      const maxBps = Math.max(1, opts.peakBps || 0, visMax);
      ctx.fillStyle = opts.textColor || 'rgba(255,255,255,0.7)';
      ctx.font = '11px sans-serif';
      ctx.fillText(opts.formatSpeed(maxBps), padding + 3, padding + 11);
    }
  }

  return { mapPointsToPixels, drawSpeedChart };
});
