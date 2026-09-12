// Геометрия значка Chill Hub. Один источник правды для .ico, .png и .svg.
//
// Знак — аркадный стик: коралловый шар на стальной ножке, стальная шайба и
// сиреневое основание. Плашка — тёмный индиго с объёмом (градиент сверху вниз
// и блик по верхнему краю), за шаром — тёплое свечение.
//
// Почему тёмная плашка. Все места, где живёт значок, тёмные: заголовок окна,
// шапка лаунчера, трей, навбар админки, сайт. Светлая только вкладка браузера.
// На тёмном плашка растворяется в панели, и силуэт держит сам предмет — как у
// значков Steam и Epic в их же тёмных интерфейсах. На светлом силуэт держит
// плашка: индиго на белой вкладке различим без канта. Замеры проверяет тест:
//
//   шар / плашка          ≥ 4,5  — предмет читается на своей плашке
//   основание / плашка    ≥ 3
//   шар / тёмная панель   ≥ 3    — предмет читается и без плашки
//   плашка / светлая вкладка ≥ 3
//
// Почему ни один цвет не совпадает с фиолетовым кнопок лаунчера (#7c5cff).
// В шапке значок стоит рядом с кнопкой Steam того же оттенка, и знак того же
// цвета читался как ещё один элемент управления. Плашка темнее кнопок, шар
// тёплый, основание светлее.
//
// Почему шар коралловый, а не красный. Красная точка в трее и в шапке — это
// сигнал ошибки. Коралл остаётся аркадным, но ошибкой не читается.
//
// Два режима. От 24 px и выше значок — сглаженные фигуры с градиентами,
// заданные в единицах эталона 32 × 32 и масштабируемые линейно. На 16 px
// градиенты и эллипсы превращаются в кашу, поэтому там своя, плоская версия:
// три сплошных цвета, все края на целых пикселях, шайбы и бликов нет.

export const COLORS = {
  plateTop: '#2e2260',
  plateBottom: '#120a30',
  glow: '#ff6a55',
  ballLight: '#ffd0c0',
  ball: '#ff6a55',
  ballDark: '#9a2020',
  steelLight: '#ececf4',
  steel: '#9a9aae',
  washerLight: '#f4f4fa',
  washerDark: '#8a8aa0',
  baseTop: '#c0b4ff',
  baseBottom: '#8f73ff',
  // Плоская версия 16 px — по одному цвету на деталь.
  flatPlate: '#241a48',
  flatBall: '#ff6a55',
  flatStem: '#cfcfe0',
  flatBase: '#a48eff',
};

export const ICO_SIZES = [16, 24, 32, 48, 64, 96, 128, 256];

// Эталон 32 × 32. Всё остальное — умножение на size / 32.
const U = 32;

// Скругление плашки — четверть её стороны; поле до края холста — 1/32.
// Оба снапятся к целым, чтобы край плашки лежал на границе пикселя.
function plateOf(size) {
  const pad = Math.max(1, Math.round(size / U));
  const w = size - 2 * pad;
  return { x: pad, y: pad, w, h: w, r: Math.round(w / 4) };
}

const solid = (color, alpha = 1) => ({ type: 'solid', color, alpha });
const linear = (x1, y1, x2, y2, stops) => ({ type: 'linear', x1, y1, x2, y2, stops });
const radial = (cx, cy, r, stops) => ({ type: 'radial', cx, cy, r, stops });

/**
 * Фигуры сглаженной версии в порядке рисования, в единицах эталона.
 * Координаты градиентов — в долях рамки фигуры (objectBoundingBox в SVG),
 * поэтому масштабируются вместе с ней без пересчёта.
 */
function shapesOf(k, plate) {
  const s = (v) => v * k;
  return [
    {
      name: 'plate',
      kind: 'rrect',
      ...plate,
      fill: linear(0, 0, 0.6, 1, [[0, COLORS.plateTop, 1], [1, COLORS.plateBottom, 1]]),
    },
    {
      // Блик по верхнему краю плашки — тот же контур, обрезанный по высоте.
      name: 'shine',
      kind: 'rrect',
      x: plate.x,
      y: plate.y,
      w: plate.w,
      h: plate.h * 0.4,
      r: plate.r,
      fill: linear(0, 0, 0, 1, [[0, '#ffffff', 0.16], [1, '#ffffff', 0]]),
    },
    {
      name: 'glow',
      kind: 'ellipse',
      cx: s(16),
      cy: s(12),
      rx: s(14),
      ry: s(12),
      fill: radial(0.5, 0.5, 0.5, [[0, COLORS.glow, 0.42], [1, COLORS.glow, 0]]),
    },
    {
      name: 'stem',
      kind: 'rrect',
      x: s(14),
      y: s(15),
      w: s(4),
      h: s(8),
      r: s(1),
      fill: linear(0, 0, 1, 0, [[0, COLORS.steelLight, 1], [1, COLORS.steel, 1]]),
    },
    {
      name: 'base',
      kind: 'rrect',
      x: s(7),
      y: s(22),
      w: s(18),
      h: s(5),
      r: s(2),
      fill: linear(0, 0, 0, 1, [[0, COLORS.baseTop, 1], [1, COLORS.baseBottom, 1]]),
    },
    {
      name: 'baseShine',
      kind: 'rrect',
      x: s(8.5),
      y: s(23),
      w: s(6),
      h: s(1.3),
      r: s(0.6),
      fill: solid('#ffffff', 0.35),
    },
    // Шайба ниже 32 px — в один пиксель высотой, серое пятно между ножкой и
    // основанием. На таких размерах её нет.
    ...(k < 1 ? [] : [{
      name: 'washer',
      kind: 'ellipse',
      cx: s(16),
      cy: s(22.2),
      rx: s(5),
      ry: s(1.7),
      fill: linear(0, 0, 0, 1, [[0, COLORS.washerLight, 1], [1, COLORS.washerDark, 1]]),
    }]),
    {
      name: 'ball',
      kind: 'circle',
      cx: s(16),
      cy: s(10),
      r: s(6),
      fill: radial(0.35, 0.3, 0.7, [[0, COLORS.ballLight, 1], [0.45, COLORS.ball, 1], [1, COLORS.ballDark, 1]]),
    },
  ];
}

// Плоская версия 16 px: шар — пиксельный круг 6 × 6, ножка 2 × 3, основание 10 × 3.
// Всё на целых пикселях, поэтому растр даёт ЧИСТЫЕ цвета без сглаживания.
// Стик стоит по центру плашки: по два пикселя поля сверху и снизу.
const FLAT_16 = {
  plate: { x: 0, y: 0, w: 16, h: 16, r: 4 },
  ball: [
    { x: 6, y: 2, w: 4, h: 1 },
    { x: 5, y: 3, w: 6, h: 4 },
    { x: 6, y: 7, w: 4, h: 1 },
  ],
  stem: [{ x: 7, y: 8, w: 2, h: 3 }],
  base: [{ x: 3, y: 11, w: 10, h: 3 }],
};

export function geometry(size) {
  if (size <= 16) {
    if (size !== 16) throw new Error(`значок ${size}: плоская версия есть только для 16 px`);
    const { plate, ball, stem, base } = FLAT_16;
    const block = (list, color, name) => list.map((b) => ({ name, kind: 'rrect', ...b, r: 0, fill: solid(color) }));
    return {
      size,
      flat: true,
      plate,
      blocks: { ball, stem, base },
      shapes: [
        { name: 'plate', kind: 'rrect', ...plate, fill: solid(COLORS.flatPlate) },
        ...block(ball, COLORS.flatBall, 'ball'),
        ...block(stem, COLORS.flatStem, 'stem'),
        ...block(base, COLORS.flatBase, 'base'),
      ],
    };
  }
  const k = size / U;
  const plate = plateOf(size);
  return { size, flat: false, plate, shapes: shapesOf(k, plate) };
}
