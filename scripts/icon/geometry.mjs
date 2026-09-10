// Геометрия значка Chill Hub. Один источник правды для .ico, .png и .svg.
//
// Знак — геймпад: светлый корпус на плашке цвета индиго, крестовина и кнопки
// прорезаны в корпусе цветом плашки. Всё собрано из скруглённых
// прямоугольников — кнопка-кружок это прямоугольник со скруглением в половину
// стороны, — поэтому растеризатор остаётся тем же простым и точным.
//
// Цветов два, и замеры контраста объясняют, зачем кант того же цвета, что знак:
//
//   знак / плашка              8,73  — корпус читается всегда
//   плашка / тёмная панель     1,87  — на тёмном фоне плашка почти не видна
//   кант / тёмная панель      16,32  — поэтому силуэт на тёмном держит кант
//   плашка / светлая панель   10,44  — на светлом фоне силуэт даёт сама плашка
//
// Отсюда два правила.
//
// Первое: кант несущий. Его нельзя рисовать штрихом — штрих размазывается
// сглаживанием и на 16 px теряет половину плотности. Он рисуется заливкой и
// прибивается к целым пикселям на каждом размере.
//
// Второе: кант и корпус одного цвета, поэтому стыкаться им нельзя ни на одном
// размере — иначе геймпад прилипает к краю значка и теряет форму. Между ними
// всегда остаётся плашка, и это поле проверяется тестом, а не глазом.

export const COLORS = {
  plate: '#3b2a9e',
  ring: '#ece8ff',
  mark: '#ece8ff',
};

// Значок не масштабируется линейно: кант обязан быть относительно толще на
// мелких размерах, иначе теряется, и тоньше на крупных, иначе значок читается
// рамкой, а не знаком. Поэтому пропорции заданы таблицей на каждый размер, а
// не одним коэффициентом.
//
//   pad  — отступ от края холста до плашки
//   ring — толщина канта
//   r    — радиус скругления плашки, около пятой части холста
const TABLE = {
  16: { pad: 0, ring: 1, r: 3 },
  24: { pad: 0, ring: 2, r: 5 },
  32: { pad: 0, ring: 2, r: 7 },
  48: { pad: 1, ring: 3, r: 10 },
  64: { pad: 2, ring: 3, r: 13 },
  96: { pad: 3, ring: 4, r: 20 },
  128: { pad: 4, ring: 5, r: 27 },
  256: { pad: 8, ring: 9, r: 54 },
};

const snap = (v, min = 0) => Math.max(min, Math.round(v));

// Промежуточный размер (например 28 в шапке сайта) берёт пропорции ближайшего
// табличного и пересчитывает их — так значок остаётся собой на любом числе.
function metrics(size) {
  if (TABLE[size]) return TABLE[size];
  const known = Object.keys(TABLE).map(Number);
  const near = known.reduce((a, b) => (Math.abs(b - size) < Math.abs(a - size) ? b : a));
  const k = size / near;
  const m = TABLE[near];
  return {
    pad: snap(m.pad * k),
    ring: snap(m.ring * k, 1),
    r: snap(m.r * k, 2),
  };
}

// Пропорции геймпада — доли холста. Корпус широкий и низкий, торцы скруглены
// в полукруг: так силуэт читается контроллером, а не таблеткой или кнопкой.
const BODY_W = 0.72;
const BODY_H = 0.375;
const ARM = 0.18; // толщина планки крестовины, доля высоты корпуса
const CROSS = 0.55; // размах крестовины, доля высоты корпуса
const BUTTON = 0.25; // диаметр кнопки, доля высоты корпуса
const END = 0.28; // отступ крестовины от торца, доля высоты корпуса
const MIN_CLEAR = 1; // поле между корпусом и кантом, пикселей — не меньше

// Отрезок длиной want, который встаёт по центру окна inner в целых пикселях:
// остаток обязан быть чётным, иначе поля с двух сторон разъедутся на пиксель.
// Из двух соседей нужной чётности берётся ближайший к желаемому; при равенстве —
// в сторону up. Ширину корпуса тянем вверх, высоту вниз: на 24 px оба округления
// «к меньшему» ужимали корпус по длине, а «к большему» делали его квадратным, и в
// обоих случаях кнопкам не хватало места у скруглённого торца.
function centered(inner, want, min = 1, up = false) {
  const v = Math.max(min, Math.round(want));
  if ((inner - v) % 2 === 0) return v;
  const dUp = Math.abs(v + 1 - want);
  const dDown = Math.abs(v - 1 - want);
  if (dUp === dDown) return up ? v + 1 : Math.max(min, v - 1);
  return dUp < dDown ? v + 1 : Math.max(min, v - 1);
}

// Точка внутри скруглённого прямоугольника — тот же счёт, что у растеризатора.
function inside(x, y, R) {
  if (x < R.x || y < R.y || x > R.x + R.w || y > R.y + R.h) return false;
  const r = R.r || 0;
  if (r <= 0) return true;
  const cx = Math.min(Math.max(x, R.x + r), R.x + R.w - r);
  const cy = Math.min(Math.max(y, R.y + r), R.y + R.h - r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}

// Вырез окружён корпусом: пиксель вокруг него целиком внутри корпуса. У круглой
// кнопки угол описанного квадрата пуст, поэтому точки берутся ближе к центру.
export function framed(h, body) {
  const mx = h.x + h.w / 2;
  const my = h.y + h.h / 2;
  const k = h.r ? 0.72 : 1;
  return [
    [h.x - 1, h.y - 1], [h.x + h.w + 1, h.y - 1],
    [h.x - 1, h.y + h.h + 1], [h.x + h.w + 1, h.y + h.h + 1],
  ].every(([x, y]) => inside(mx + (x - mx) * k, my + (y - my) * k, body));
}

// Между двумя вырезами — хотя бы пиксель корпуса.
const apart = (a, b) =>
  a.x >= b.x + b.w + 1 || b.x >= a.x + a.w + 1 || a.y >= b.y + b.h + 1 || b.y >= a.y + a.h + 1;

// Целое той же чётности, что ref, ближайшее к want и не меньше min.
function sameParity(want, ref, min) {
  let v = Math.max(min, Math.round(want));
  if (Math.abs(v - ref) % 2 === 1) v += want >= v || v - 1 < min ? 1 : -1;
  return v;
}

export function geometry(size) {
  const { pad, ring, r: radius } = metrics(size);

  const plate = { x: pad, y: pad, w: size - 2 * pad, h: size - 2 * pad, r: radius };
  const inner = {
    x: plate.x + ring,
    y: plate.y + ring,
    w: plate.w - 2 * ring,
    h: plate.h - 2 * ring,
    r: Math.max(1, radius - ring),
  };

  // Корпус — по центру внутреннего окна, с равными полями со всех сторон.
  let bw = centered(inner.w, size * BODY_W, 1, true);
  let bh = centered(inner.h, size * BODY_H, 4, false);
  while ((inner.w - bw) / 2 < MIN_CLEAR) bw -= 2;
  while ((inner.h - bh) / 2 < MIN_CLEAR) bh -= 2;
  const body = {
    x: inner.x + (inner.w - bw) / 2,
    y: inner.y + (inner.h - bh) / 2,
    w: bw,
    h: bh,
    r: Math.floor(bh / 2),
  };

  // Крестовина: квадрат размаха cross у левого торца, две планки толщины arm
  // крест-накрест. Чётность размаха совпадает с высотой корпуса, а толщины —
  // с размахом: тогда и квадрат, и планки встают по центру в целых пикселях.
  const cross = sameParity(bh * CROSS, bh, 3);
  const arm = sameParity(bh * ARM, cross, 1);
  const vIn = (bh - cross) / 2;
  const cy = body.y + vIn;
  const dpadAt = (hIn) => {
    const cx = body.x + hIn;
    return [
      { x: cx + (cross - arm) / 2, y: cy, w: arm, h: cross, r: 0 },
      { x: cx, y: cy + (cross - arm) / 2, w: cross, h: arm, r: 0 },
    ];
  };
  // Торцы корпуса скруглены, и у торца корпус ниже, чем в середине. Крестовина,
  // поставленная по доле, на мелких размерах врезалась в это скругление: вокруг
  // выреза не оставалось целого пикселя корпуса, и силуэт выгрызался. Поэтому
  // она отодвигается от торца, пока пиксель вокруг неё не встанет целиком.
  let hIn = Math.max(vIn, Math.round(bh * END));
  while (!dpadAt(hIn).every((h) => framed(h, body)) && hIn < bw / 2) hIn++;
  const dpad = dpadAt(hIn);

  // Кнопки — зеркально крестовине у правого торца. На 16 px кнопка одна: две
  // точки по пикселю сливаются в полоску, а одна остаётся точкой.
  const d = Math.max(1, Math.round(bh * BUTTON));
  const dot = (x, y) => ({ x, y, w: d, h: d, r: d >= 3 ? Math.floor(d / 2) : 0 });
  const mirror = body.x + bw - hIn - cross / 2; // центр, зеркальный центру крестовины
  // Две кнопки по диагонали; между ними целый пиксель и по вертикали, и по
  // горизонтали — иначе на мелком размере они слипаются углами.
  const step = d + 1;
  const place = size <= 16
    ? (gx) => [dot(gx, body.y + Math.floor((bh - d) / 2))]
    : (gx) => {
      const gy = body.y + Math.floor((bh - step - d) / 2);
      return [dot(gx, gy), dot(gx + step, gy + step)];
    };
  const width = size <= 16 ? d : step + d;
  const ideal = Math.round(mirror - width / 2);
  // Зеркально крестовине, если там хватает места; иначе — ближайшее место, где
  // кнопки не задевают ни крестовину, ни скруглённый торец.
  const spots = [];
  for (let gx = body.x; gx + width <= body.x + bw; gx++) spots.push(gx);
  spots.sort((a, b) => Math.abs(a - ideal) - Math.abs(b - ideal));
  const gx = spots.find((x) => {
    const bs = place(x);
    return bs.every((b) => framed(b, body) && dpad.every((c) => apart(b, c)));
  });
  if (gx === undefined) throw new Error(`значок ${size}: кнопкам нет места на корпусе`);
  const buttons = place(gx);

  return {
    size,
    plate,
    inner,
    ring,
    body,
    holes: [...dpad, ...buttons],
    clear: Math.min((inner.w - bw) / 2, (inner.h - bh) / 2),
  };
}

export const ICO_SIZES = [16, 24, 32, 48, 64, 96, 128, 256];
