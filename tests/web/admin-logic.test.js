// Тесты чистой логики админки: версии, разбор дат и форматирование.
//
// Функции вытаскиваются из admin.js так же, как в admin-sanitize.test.js: скрипт
// браузерный, целиком в node не загружается, а эти функции чистые.
//
// Запуск: node --test tests/web/*.test.js

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const src = fs.readFileSync(
  path.join(__dirname, '..', '..', 'server', 'admin_ui', 'admin.js'), 'utf8');

function extract(name) {
  const re = new RegExp('function\\s+' + name + '\\s*\\([\\s\\S]*?\\n\\}', 'm');
  const m = src.match(re);
  if (!m) throw new Error('не найдена функция ' + name);
  return m[0];
}

function load(...names) {
  // Функции ссылаются друг на друга (mtLocalToUtc зовёт toRfc3339),
  // поэтому объявляем их в одной области.
  const body = names.map(extract).join('\n');
  return new Function(body + '\nreturn {' + names.join(',') + '};')();
}

function extractConst(name) {
  const re = new RegExp('const\\s+' + name + '\\s*=\\s*\\{[\\s\\S]*?\\};');
  const m = src.match(re);
  if (!m) throw new Error('не найдена константа ' + name);
  return m[0];
}

const { bumpSemverPatch } = load('bumpSemverPatch');
const { formatBytes } = load('formatBytes');
const { toRfc3339, mtLocalToUtc, mxLocalToUtcEnd } = load('toRfc3339', 'mtLocalToUtc', 'mxLocalToUtcEnd');

// sectionFromHash зовёт HASH_TAB_MAP, а не другую function, поэтому load()
// (которая склеивает только function-объявления) сюда не годится — константа
// добавляется в тело отдельно.
const { sectionFromHash } = new Function(
  extractConst('HASH_TAB_MAP') + '\n' + extract('sectionFromHash') +
  '\nreturn {sectionFromHash};')();

test('bumpSemverPatch поднимает патч, а не что-то другое', () => {
  assert.strictEqual(bumpSemverPatch('1.2.2'), '1.2.3');
  assert.strictEqual(bumpSemverPatch('0.0.0'), '0.0.1');
  // Перенос через девятку: 9 -> 10, а не 1.2.91 и не 1.3.0
  assert.strictEqual(bumpSemverPatch('1.2.9'), '1.2.10');
  assert.strictEqual(bumpSemverPatch('1.9.99'), '1.9.100');
});

test('bumpSemverPatch терпит пробелы и неполную версию', () => {
  assert.strictEqual(bumpSemverPatch('  1.2.3  '), '1.2.4');
  // Только major.minor — достраивается до патча
  assert.strictEqual(bumpSemverPatch('1.2'), '1.2.1');
});

test('bumpSemverPatch на мусоре даёт валидную версию, а не мусор', () => {
  // Важно: результат подставляется в поле версии и уходит в манифест.
  // Пустая или битая строка там означала бы неустановимую сборку.
  for (const bad of ['', '   ', 'abc', '1', 'v1.2.3', '1.2.3.4', null, undefined]) {
    const out = bumpSemverPatch(bad);
    assert.match(out, /^\d+\.\d+\.\d+$/, 'мусор ' + JSON.stringify(bad) + ' дал ' + JSON.stringify(out));
  }
});

test('formatBytes читаем и не выдаёт NaN', () => {
  for (const n of [0, 1, 1023, 1024, 1024 * 1024, 1024 ** 3, 1024 ** 4, -1, null, undefined, NaN]) {
    const s = String(formatBytes(n));
    assert.ok(!/NaN|Infinity|undefined/.test(s), 'formatBytes(' + n + ') = ' + s);
  }
});

test('mtLocalToUtc переводит значение datetime-local в RFC3339', () => {
  // Единственная форма, которую вообще может отдать <input type="datetime-local">.
  const out = mtLocalToUtc('2026-01-15T10:30');
  assert.match(out, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/, out);
});

test('mtLocalToUtc различает пустое поле и неразбираемое значение', () => {
  // '' — это «границы нет», null — «строку понять не удалось»: фильтры
  // обращений и метрик разводят эти случаи по разным веткам.
  assert.strictEqual(mtLocalToUtc(''), '');
  assert.strictEqual(mtLocalToUtc('   '), '');
  assert.strictEqual(mtLocalToUtc('вчера'), null);
});

test('mxLocalToUtcEnd дотягивает верхнюю границу до конца минуты', () => {
  // datetime-local не даёт секунд: без этого «по 19:17» отбрасывало бы всё,
  // что случилось в 19:17:30 — включая только что отправленное событие.
  const start = mtLocalToUtc('2026-01-15T19:17');
  const end = mxLocalToUtcEnd('2026-01-15T19:17');
  assert.ok(new Date(end) > new Date(start), start + ' .. ' + end);
  assert.match(end, /T\d{2}:\d{2}:59Z$/, end);
});

test('toRfc3339 всегда выдаёт UTC с суффиксом Z', () => {
  const out = toRfc3339(new Date(Date.UTC(2026, 0, 15, 10, 30, 45)));
  assert.strictEqual(out, '2026-01-15T10:30:45Z');
});

// sectionFromHash сама читает глобальный location — не аргумент, а свойство
// window в браузере. В Node глобала location нет, поэтому тест подставляет
// его через global (в теле, порождённом new Function, это то же самое, что
// window в браузере) и обязательно убирает за собой.
function withHash(hash, fn) {
  const had = 'location' in global;
  const prev = global.location;
  global.location = { hash };
  try {
    return fn();
  } finally {
    if (had) global.location = prev; else delete global.location;
  }
}

test('sectionFromHash понимает каждый известный слаг', () => {
  const cases = {
    launcher: 'secLauncher', manifests: 'secManifests', news: 'secNews',
    mods: 'secMods', inbox: 'secInbox', maint: 'secMaint', metrics: 'secMetrics',
  };
  for (const [slug, sec] of Object.entries(cases)) {
    assert.strictEqual(withHash('#' + slug, sectionFromHash), sec, slug);
  }
});

test('sectionFromHash регистронезависима и терпит пробелы', () => {
  assert.strictEqual(withHash('#LAUNCHER', sectionFromHash), 'secLauncher');
  assert.strictEqual(withHash('#  metrics  ', sectionFromHash), 'secMetrics');
});

test('sectionFromHash даёт null на пустом или неизвестном хэше', () => {
  // null — это "не решать за вызывающего": showSection получает либо
  // конкретную секцию, либо явное "смотри дальше" (сохранённая вкладка),
  // а не тихую подмену на дефолт прямо здесь.
  for (const hash of ['', '#', '#nonsense', '#secLauncher']) {
    assert.strictEqual(withHash(hash, sectionFromHash), null, hash);
  }
});

test('toRfc3339 дополняет однозначные числа нулём', () => {
  const out = toRfc3339(new Date(Date.UTC(2026, 0, 5, 4, 3, 2)));
  assert.strictEqual(out, '2026-01-05T04:03:02Z');
});

// ---- Диф манифестов ----
//
// diffManifests отвечает на главный вопрос релиза («что изменилось с прошлой
// версии»), и ошибиться здесь дороже всего: ложное «отличий нет» читается как
// «сборка та же», хотя выкатывается другая.
const { manifestFileMap, diffManifests } = load('manifestFileMap', 'diffManifests');

const mf = (files) => ({ files });

test('diffManifests различает добавленные, изменённые и пропавшие файлы', () => {
  const cur = manifestFileMap(mf([
    { path: 'a.dll', size: 10, blake3: 'h1' },
    { path: 'b.dll', size: 20, blake3: 'h2-new' },
    { path: 'new.dll', size: 5, blake3: 'h3' },
  ]));
  const base = manifestFileMap(mf([
    { path: 'a.dll', size: 10, blake3: 'h1' },
    { path: 'b.dll', size: 20, blake3: 'h2-old' },
    { path: 'gone.dll', size: 7, blake3: 'h4' },
  ]));
  const d = diffManifests(cur, base);
  assert.strictEqual(d.added, 1);
  assert.strictEqual(d.modified, 1);
  assert.deepStrictEqual(d.removed, ['gone.dll']);
  assert.strictEqual(d.status.get('a.dll'), 'same');
  assert.strictEqual(d.status.get('b.dll'), 'mod');
  assert.strictEqual(d.status.get('new.dll'), 'add');
});

test('diffManifests ловит изменение по размеру, когда хеша нет', () => {
  // Старые манифесты могли не нести blake3: сравнение по размеру — последнее,
  // что остаётся, и молча считать такие файлы одинаковыми нельзя.
  const cur = manifestFileMap(mf([{ path: 'a.bin', size: 11 }]));
  const base = manifestFileMap(mf([{ path: 'a.bin', size: 10 }]));
  assert.strictEqual(diffManifests(cur, base).modified, 1);
});

test('diffManifests на одинаковых манифестах не находит отличий', () => {
  const files = mf([{ path: 'a.dll', size: 1, blake3: 'h' }]);
  const d = diffManifests(manifestFileMap(files), manifestFileMap(files));
  assert.deepStrictEqual([d.added, d.modified, d.removed.length], [0, 0, 0]);
});

test('manifestFileMap срезает ведущий слэш и пропускает пустые пути', () => {
  const m = manifestFileMap(mf([{ path: '/x/a.dll', size: 1 }, { path: '', size: 2 }]));
  assert.deepStrictEqual([...m.keys()], ['x/a.dll']);
});

// ---- Проверка версии перед заливкой ----
const { uploadVersionValid } = load('uploadVersionValid');

test('uploadVersionValid пропускает только три числа через точку', () => {
  for (const ok of ['1.2.3', '0.0.1', '10.20.30', ' 1.2.3 ']) {
    assert.ok(uploadVersionValid(ok), ok);
  }
  // «1.39» — самая правдоподобная опечатка: раньше она уезжала на сервер
  // вместе со всем ZIP и падала только там.
  for (const bad of ['1.39', '1.2.3.4', 'v1.2.3', '1.2.x', '', 'latest']) {
    assert.ok(!uploadVersionValid(bad), bad);
  }
});

// ---- Сводка исходов в метриках ----
const { mxOutcomeNote } = new Function(
  extract('mxNum') + '\n' + extract('mxOutcomeNote') + '\nreturn {mxOutcomeNote};')();

test('mxOutcomeNote объясняет остаток, а не оставляет числа несходящимися', () => {
  // 19 = 4 + 8 + 7: седьмую часть событий пользователю приходилось угадывать.
  // Остаток — это почти всегда отменённые: сводка считает отдельно только ok и
  // fail, чтобы брошенная закачка не попадала в долю неудач.
  assert.strictEqual(mxOutcomeNote(19, 4, 8), 'успешно 4, с ошибкой 8, отменено или без результата 7');
});

test('mxOutcomeNote молчит про остаток, когда его нет', () => {
  assert.strictEqual(mxOutcomeNote(12, 10, 2), 'успешно 10, с ошибкой 2');
  // Отрицательного остатка быть не может даже на битых данных.
  assert.strictEqual(mxOutcomeNote(1, 5, 5), 'успешно 5, с ошибкой 5');
});

// ---- Экономия трафика и проверки целостности ----
const { mxSavedNote, mxIntegrityNote } = load('mxNum', 'mxPct', 'formatBytes', 'mxSavedNote', 'mxIntegrityNote');

test('mxSavedNote показывает, сколько лаунчер не дал скачать', () => {
  // Ради этой строки лаунчер и написан: «скачано 500 Б» само по себе не значит
  // ничего, смысл появляется только рядом с полным весом сборки.
  const note = mxSavedNote(500, 5000);
  assert.match(note, /вместо/);
  assert.match(note, /90,0 %/);
});

test('mxSavedNote не выдаёт отсутствие данных за стопроцентную экономию', () => {
  // События старых лаунчеров полного веса не сообщали: сравнивать не с чем,
  // и «сэкономлено 100 %» здесь было бы прямой ложью.
  assert.strictEqual(mxSavedNote(500, 0), 'сумма поля bytes');
  // Полный вес меньше скачанного — данные противоречивы, молчим так же.
  assert.strictEqual(mxSavedNote(500, 100), 'сумма поля bytes');
});

test('mxIntegrityNote называет долю проверок, нашедших расхождение', () => {
  assert.strictEqual(
    mxIntegrityNote({ integrityChecks: 3, integrityFailed: 2, hashMismatches: 4 }),
    'с расхождением 2, файлов не сошлось 4');
  // Без проверок число расхождений не значит ничего — объясняем, откуда они берутся.
  assert.strictEqual(mxIntegrityNote({ integrityChecks: 0 }), 'запускает сам пользователь');
});

// ---- Определение картинок в галерее ----
const { isImageName } = new Function(
  src.match(/const IMAGE_EXT_RE[\s\S]*?\n/)[0] + extract('isImageName') + '\nreturn {isImageName};')();

test('isImageName отличает картинку от прочего файла', () => {
  for (const ok of ['a.png', 'B.JPG', 'x.jpeg', 'y.webp', 'z.svg', 'q.gif?v=2']) {
    assert.ok(isImageName(ok), ok);
  }
  // ping.txt лежит в той же галерее и раньше рисовался битым <img>.
  for (const bad of ['ping.txt', 'archive.zip', 'noext', '', 'a.png.txt']) {
    assert.ok(!isImageName(bad), bad);
  }
});

// ---- Время обращений ----
// Зона зашита в саму функцию, поэтому тест проверяет её и на машине с TZ=UTC,
// и на машине в другом поясе: ответ должен быть один и тот же.
const { fbFmtTime } = new Function(
  src.match(/const FB_TZ = '[^']*';/)[0] + '\n' + extract('fbFmtTime') +
  '\nreturn {fbFmtTime};')();

test('fbFmtTime показывает время обращения по Москве, а не по UTC', () => {
  // Летом Москва — UTC+3 круглый год: перевода часов нет с 2014-го.
  assert.strictEqual(fbFmtTime('2026-08-17T18:35:29Z'), '2026-08-17 21:35:29 МСК');
  // Переход через полночь: дата тоже должна съехать на сутки вперёд.
  assert.strictEqual(fbFmtTime('2026-08-17T22:10:00Z'), '2026-08-18 01:10:00 МСК');
  // Зимой смещение то же самое — проверяем, что не приехал переход на зимнее время.
  assert.strictEqual(fbFmtTime('2026-01-05T09:00:00Z'), '2026-01-05 12:00:00 МСК');
});

test('fbFmtTime не превращает пустое и битое значение в Invalid Date', () => {
  assert.strictEqual(fbFmtTime(''), '');
  assert.strictEqual(fbFmtTime(null), '');
  assert.strictEqual(fbFmtTime(undefined), '');
  // Нераспознанную строку показываем как есть — это честнее прочерка.
  assert.strictEqual(fbFmtTime('когда-то'), 'когда-то');
});
