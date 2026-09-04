// Правка реестра игр в панели 2.0.
//
// Реестр сохраняется целиком одним запросом: одна пустая строка в
// середине откатывает весь список, и человек узнаёт об этом после
// нажатия, потеряв правки. Отсюда проверки — и требование, чтобы каждая
// называла строку и поле, а не «проверьте данные».
//
// Порядок значим до самого лаунчера: он запоминает игру номером в
// массиве ответа. Перетащить строку и не пересчитать номера — значит
// отдать игрокам прежний порядок при новом виде в панели.

const test = require('node:test');
const assert = require('node:assert');

const R = require('../../server/admin_ui/v2/registry.js');
const diff = require('../../server/admin_ui/registry-diff.js');

const game = (id, over) =>
  Object.assign({ gameId: id, title: id.toUpperCase(), exeRelativePath: id + '.exe', order: 0 }, over || {});

const list = () => R.reorder([game('repo'), game('peak'), game('lethal')]);

/* ---------- Порядок ---------- */

test('перестановка меняет соседей местами', () => {
  const out = R.move(list(), 'peak', -1);
  assert.deepStrictEqual(out.map((x) => x.gameId), ['peak', 'repo', 'lethal']);
});

test('перестановка за край списка ничего не меняет', () => {
  assert.deepStrictEqual(R.move(list(), 'repo', -1).map((x) => x.gameId), ['repo', 'peak', 'lethal']);
  assert.deepStrictEqual(R.move(list(), 'lethal', 1).map((x) => x.gameId), ['repo', 'peak', 'lethal']);
});

test('неизвестная игра не ломает список', () => {
  assert.deepStrictEqual(R.move(list(), 'нет-такой', 1).map((x) => x.gameId), ['repo', 'peak', 'lethal']);
});

test('перетаскивание переносит строку на нужное место', () => {
  const out = R.moveTo(list(), 'lethal', 0);
  assert.deepStrictEqual(out.map((x) => x.gameId), ['lethal', 'repo', 'peak']);
});

test('перетаскивание за пределы списка прижимается к краю', () => {
  assert.deepStrictEqual(R.moveTo(list(), 'repo', 99).map((x) => x.gameId), ['peak', 'lethal', 'repo']);
  assert.deepStrictEqual(R.moveTo(list(), 'lethal', -5).map((x) => x.gameId), ['lethal', 'repo', 'peak']);
});

test('после любой перестановки номера пересчитываются подряд', () => {
  // Иначе игроки получат прежний порядок при новом виде в панели
  const out = R.moveTo(list(), 'lethal', 0);
  assert.deepStrictEqual(out.map((x) => x.order), [0, 1, 2]);
});

test('исходный список не меняется на месте', () => {
  const before = list();
  R.move(before, 'peak', -1);
  assert.deepStrictEqual(before.map((x) => x.gameId), ['repo', 'peak', 'lethal']);
});

/* ---------- Добавление и удаление ---------- */

test('добавленная строка встаёт в конец и ждёт заполнения', () => {
  const out = R.add(list(), 'bodycam');
  assert.strictEqual(out.length, 4);
  assert.strictEqual(out[3].gameId, 'bodycam');
  assert.strictEqual(out[3].order, 3);
  assert.strictEqual(out[3].title, '');
});

test('удаление строки пересчитывает номера', () => {
  const out = R.remove(list(), 'repo');
  assert.deepStrictEqual(out.map((x) => x.gameId), ['peak', 'lethal']);
  assert.deepStrictEqual(out.map((x) => x.order), [0, 1]);
});

test('правка поля не задевает соседей', () => {
  const out = R.patch(list(), 'peak', 'title', 'Пик');
  assert.strictEqual(out[1].title, 'Пик');
  assert.strictEqual(out[0].title, 'REPO');
});

/* ---------- Проверки перед сохранением ---------- */

test('заполненный реестр сохраняется', () => {
  assert.deepStrictEqual(R.problems(list()), []);
  assert.strictEqual(R.canSave(list()), true);
});

test('пустой идентификатор называет номер строки', () => {
  const p = R.problems([game('repo'), { gameId: '', title: 'X', exeRelativePath: 'x.exe' }]);
  assert.strictEqual(p.length, 1);
  assert.match(p[0].message, /строка 2/);
  assert.strictEqual(p[0].field, 'gameId');
});

test('идентификатор с недопустимыми символами не проходит', () => {
  // Он уезжает в пути на диске и в адреса API — пробелы и кириллица там ломают всё молча
  for (const bad of ['Repo', 'my game', 'игра', 'a/b', '-repo', '']) {
    const p = R.problems([Object.assign(game('x'), { gameId: bad })]);
    assert.ok(p.some((x) => x.field === 'gameId'), 'пропущен недопустимый идентификатор: ' + bad);
  }
});

test('допустимые идентификаторы проходят', () => {
  for (const good of ['repo', 'how-to-fish', 'lethal_company', 'game2']) {
    const p = R.problems([Object.assign(game('x'), { gameId: good })]);
    assert.ok(!p.some((x) => x.field === 'gameId'), 'зря отвергнут: ' + good);
  }
});

test('повтор идентификатора указывает на первую строку', () => {
  const p = R.problems([game('repo'), game('peak'), game('repo')]);
  const dup = p.find((x) => /уже есть/.test(x.message));
  assert.ok(dup);
  assert.match(dup.message, /строке 1/);
});

test('пустое название и пустой исполняемый файл — тоже замечания', () => {
  const p = R.problems([{ gameId: 'repo', title: '', exeRelativePath: '' }]);
  assert.strictEqual(p.length, 2);
  assert.ok(p.some((x) => x.field === 'title'));
  assert.ok(p.some((x) => x.field === 'exeRelativePath'));
  assert.strictEqual(R.canSave([{ gameId: 'repo', title: '', exeRelativePath: '' }]), false);
});

test('замечание объясняет последствие, а не просто «заполните»', () => {
  const p = R.problems([{ gameId: 'repo', title: '', exeRelativePath: '' }]);
  assert.match(p.find((x) => x.field === 'title').message, /игрок увидит идентификатор/);
  assert.match(p.find((x) => x.field === 'exeRelativePath').message, /запускать будет нечего/);
});

test('пробелы вокруг значений не считаются заполнением', () => {
  const p = R.problems([{ gameId: '  ', title: ' ', exeRelativePath: '\t' }]);
  assert.strictEqual(p.length, 3);
});

/* ---------- Подсказка из Thunderstore ---------- */

test('подсказка заполняет пустые поля', () => {
  const out = R.applyEcosystem(
    { gameId: 'repo', title: '', exeRelativePath: '' },
    { exeNames: ['REPO.exe'], steamAppId: '3241660', steamFolder: 'REPO', displayName: 'R.E.P.O.' }
  );
  assert.strictEqual(out.exeRelativePath, 'REPO.exe');
  assert.strictEqual(out.steamAppId, '3241660');
  assert.strictEqual(out.title, 'R.E.P.O.');
});

test('подсказка не затирает то, что человек уже поправил', () => {
  const out = R.applyEcosystem(
    { gameId: 'repo', title: 'Моё название', exeRelativePath: 'my.exe' },
    { exeNames: ['REPO.exe'], displayName: 'R.E.P.O.' }
  );
  assert.strictEqual(out.title, 'Моё название');
  assert.strictEqual(out.exeRelativePath, 'my.exe');
});

test('пустой ответ экосистемы ничего не портит', () => {
  const before = { gameId: 'repo', title: 'X', exeRelativePath: 'x.exe' };
  assert.deepStrictEqual(R.applyEcosystem(before, null), before);
  assert.deepStrictEqual(R.applyEcosystem(before, {}), before);
});

/* ---------- Стыковка с подсчётом изменений версии 1.0 ---------- */

test('перестановка видна модулю сравнения как изменение нескольких строк', () => {
  const before = list();
  const after = R.moveTo(before, 'lethal', 0);
  const counts = diff.countRegistryChanges(before, after);
  // Одно действие человека меняет `order` у трёх строк — так это и считается
  assert.strictEqual(counts.changed, 3);
  assert.strictEqual(counts.added, 0);
});

test('правка одного поля видна как одна изменённая строка', () => {
  const before = list();
  const after = R.patch(before, 'peak', 'title', 'Пик');
  const counts = diff.countRegistryChanges(before, after);
  assert.strictEqual(counts.changed, 1);
});

test('добавление и удаление считаются отдельно', () => {
  const before = list();
  const added = diff.countRegistryChanges(before, R.add(before, 'bodycam'));
  assert.strictEqual(added.added, 1);

  const removed = diff.countRegistryChanges(before, R.remove(before, 'repo'));
  assert.strictEqual(removed.removed, 1);
});
