// Геометрия значка Chill Hub. Один источник правды для .ico, .png и .svg.
//
// Знак — блочные буквы CH: C — квадратная скобка, H — две стойки и перекладина,
// всё одной толщины и без скруглений. Белым по скруглённой плашке с диагональным
// градиентом из красок лаунчера: сверху-слева его акцент под курсором #8f73ff,
// снизу-справа нажатая заливка #5a3fcc.
//
// Почему блоки. Знак целиком собран из прямоугольников, поэтому каждый его край
// можно положить ровно на границу пикселя на любом размере — на 16 px буквы
// остаются буквами, а не серым пятном сглаживания.
//
// Почему без канта. Насыщенный фиолетовый сам отделяется и от тёмной панели
// задач, и от светлой вкладки браузера. Замеры по середине градиента (#7458ee):
//
//   плашка / тёмная панель задач   3,3  — плашка видна и без канта
//   плашка / светлая вкладка        4,4  — и на светлом тоже
//   знак / низ градиента            7,0  — белый на тёмном краю плашки
//   знак / верх градиента           3,6  — на светлом краю, толстые буквы
//
// Цифры проверяет тест, а не глаз.

export const COLORS = {
  top: '#8f73ff',
  bottom: '#5a3fcc',
  mark: '#ffffff',
};

// Плашка: поле вокруг и скругление — таблицей на каждый размер.
const PLATE = {
  16: { pad: 0, r: 4 },
  24: { pad: 0, r: 6 },
  32: { pad: 1, r: 7 },
  48: { pad: 2, r: 10 },
  64: { pad: 3, r: 14 },
  96: { pad: 4, r: 21 },
  128: { pad: 6, r: 28 },
  256: { pad: 12, r: 56 },
};

// Пропорции букв — доли холста. Каждая величина округляется до целого пикселя
// отдельно, с нижними пределами: на мелких размерах важнее, чтобы у C оставался
// просвет, а у H — щель между стойками, чем точная доля.
//
//   t  — толщина штриха
//   h  — высота букв
//   wc — ширина C
//   g  — промежуток между C и H
//   wh — ширина H
const T = 0.11;
const H = 0.44;
const WC = 0.27;
const G = 0.06;
const WH = 0.28;

function plateOf(size) {
  if (PLATE[size]) return PLATE[size];
  const known = Object.keys(PLATE).map(Number);
  const near = known.reduce((a, b) => (Math.abs(b - size) < Math.abs(a - size) ? b : a));
  const k = size / near;
  return { pad: Math.round(PLATE[near].pad * k), r: Math.max(2, Math.round(PLATE[near].r * k)) };
}

export function geometry(size) {
  const { pad, r } = plateOf(size);
  const plate = { x: pad, y: pad, w: size - 2 * pad, h: size - 2 * pad, r };

  const t = Math.max(2, Math.round(size * T));
  const h = Math.round(size * H);
  const wc = Math.max(t + 2, Math.round(size * WC)); // у C остаётся просвет
  const g = Math.max(1, Math.round(size * G));
  const wh = Math.max(2 * t + 1, Math.round(size * WH)); // у H щель между стойками
  const w = wc + g + wh;

  // По центру плашки. Нечётный остаток не делится пополам без дробей — лишний
  // пиксель уходит вправо и вниз: полупиксель размыл бы края, а разница полей в
  // пиксель глазу не видна.
  const x = Math.floor((size - w) / 2);
  const y = Math.floor((size - h) / 2);
  const hx = x + wc + g;
  const barY = y + Math.floor((h - t) / 2);

  const letterC = [
    { x, y, w: wc, h: t }, // верхняя полка
    { x, y, w: t, h }, // спинка
    { x, y: y + h - t, w: wc, h: t }, // нижняя полка
  ];
  const letterH = [
    { x: hx, y, w: t, h }, // левая стойка
    { x: hx + wh - t, y, w: t, h }, // правая стойка
    { x: hx + t, y: barY, w: wh - 2 * t, h: t }, // перекладина
  ];

  return { size, plate, t, box: { x, y, w, h }, letterC, letterH, blocks: [...letterC, ...letterH] };
}

export const ICO_SIZES = [16, 24, 32, 48, 64, 96, 128, 256];
