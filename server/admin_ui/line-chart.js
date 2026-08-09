// Самодостаточный многосерийный линейный график — без uPlot и без CDN.
//
// Второе (и последнее) место в admin.js, которое ещё грузило uPlot с
// unpkg.com — график «Динамика по дням» в разделе «Метрики». У него уже был
// запасной путь на случай недоступного CDN (таблица вместо графика), так что
// он не ломался молча, как график скорости загрузки (см. speed-chart.js), но
// после того случая держать в проекте вторую внешнюю зависимость ради одного
// графика, который прекрасно рисуется штатным canvas, уже не было смысла —
// отсюда этот файл и полное удаление uPlot из admin.html.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // mapSeriesToPixels раскладывает несколько рядов чисел (одинаковой длины,
  // индекс — общая ось X) в пиксельные координаты. Чистая функция, без
  // обращения к DOM/canvas — проверяется в node.
  function mapSeriesToPixels(series, opts) {
    const width = opts.width;
    const height = opts.height;
    const pad = opts.padding || { left: 0, right: 0, top: 0, bottom: 0 };
    const n = series.reduce((m, s) => Math.max(m, s.values.length), 0);
    const maxY = Math.max(1, opts.maxY || 0, ...series.flatMap(s => s.values));
    const innerW = Math.max(1, width - pad.left - pad.right);
    const innerH = Math.max(1, height - pad.top - pad.bottom);
    return series.map(s => s.values.map((v, i) => {
      const xFrac = n > 1 ? i / (n - 1) : 0;
      const yFrac = v / maxY;
      return {
        x: pad.left + xFrac * innerW,
        y: pad.top + (1 - yFrac) * innerH,
        v,
      };
    }));
  }

  // drawMultiLineChart рисует сетку, N цветных линий и подписи осей на canvas.
  // Легенда — отдельным HTML-блоком через opts.legendHost (проще и надёжнее
  // в лоб рисовать цветные подписи в DOM, чем размечать их пикселями внутри
  // canvas), опционально: без legendHost функция просто не трогает легенду.
  function drawMultiLineChart(canvas, xs, series, opts) {
    opts = opts || {};
    const dpr = (typeof window !== 'undefined' && window.devicePixelRatio) || 1;
    const cssW = canvas.clientWidth || opts.width || 600;
    const cssH = canvas.clientHeight || opts.height || 280;
    const pxW = Math.max(1, Math.round(cssW * dpr));
    const pxH = Math.max(1, Math.round(cssH * dpr));
    if (canvas.width !== pxW) canvas.width = pxW;
    if (canvas.height !== pxH) canvas.height = pxH;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssW, cssH);

    const formatY = opts.formatY || String;
    const xLabelFor = opts.xLabelFor || (i => String(i));
    const pad = { left: opts.axisLeft || 44, right: 10, top: 10, bottom: 20 };

    ctx.fillStyle = opts.bg || 'rgba(255,255,255,0.04)';
    ctx.fillRect(0, 0, cssW, cssH);

    const maxY = Math.max(1, ...series.flatMap(s => s.values), 0);
    const innerH = cssH - pad.top - pad.bottom;
    ctx.font = (opts.fontSize || 10) + 'px sans-serif';
    for (let i = 0; i <= 2; i++) {
      const frac = i / 2;
      const y = pad.top + innerH * frac;
      ctx.strokeStyle = opts.gridColor || 'rgba(255,255,255,0.14)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(pad.left, Math.round(y) + 0.5);
      ctx.lineTo(cssW - pad.right, Math.round(y) + 0.5);
      ctx.stroke();
      const val = maxY * (1 - frac);
      ctx.fillStyle = opts.textColor || 'rgba(255,255,255,0.7)';
      ctx.textBaseline = i === 0 ? 'top' : (i === 2 ? 'bottom' : 'middle');
      ctx.fillText(val > 0 ? formatY(val) : '0', 2, y);
    }

    const n = xs.length;
    ctx.textBaseline = 'bottom';
    ctx.fillStyle = opts.textColor || 'rgba(255,255,255,0.7)';
    [0, 0.5, 1].forEach((frac) => {
      const idx = Math.round((n - 1) * frac);
      if (idx < 0 || idx >= n) return;
      const x = pad.left + (cssW - pad.left - pad.right) * frac;
      ctx.textAlign = frac === 0 ? 'left' : (frac === 1 ? 'right' : 'center');
      ctx.fillText(xLabelFor(idx), x, cssH);
    });
    ctx.textAlign = 'left';
    ctx.textBaseline = 'alphabetic';

    const paths = mapSeriesToPixels(series, { width: cssW, height: cssH, padding: pad, maxY });
    paths.forEach((pts, si) => {
      if (pts.length < 2) return;
      ctx.strokeStyle = (series[si] && series[si].color) || '#0d6efd';
      ctx.lineWidth = 2;
      ctx.beginPath();
      pts.forEach((p, i) => { if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y); });
      ctx.stroke();
    });

    // series[].label is only ever a literal string from the caller's own
    // source (see mxRenderChart in admin.js) — never server/user data — so
    // no HTML-escaping is needed here, unlike the rest of this admin UI's
    // handling of anything that comes from a response body.
    if (opts.legendHost) {
      opts.legendHost.innerHTML = series.map(s =>
        '<span style="display:inline-flex;align-items:center;gap:4px;margin-right:12px">'
        + '<span style="width:10px;height:10px;border-radius:2px;background:' + (s.color || '#0d6efd') + ';display:inline-block"></span>'
        + String(s.label || '')
        + '</span>'
      ).join('');
    }
  }

  return { mapSeriesToPixels, drawMultiLineChart };
});
