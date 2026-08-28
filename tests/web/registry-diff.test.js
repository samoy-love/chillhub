// Счётчик правок списка игр — то, что стоит на кнопке «Сохранить».
//
// Кнопка обещает «столько игр уедет изменёнными». Обещание проверяемое, и
// ошибка в нём видна сразу: «Сохранить (1)» при полном совпадении со снимком
// или «Сохранено» поверх настоящих правок — оба случая хуже, чем прежний
// безымянный флаг.
'use strict';

const test = require('node:test');
const assert = require('node:assert');

const {
  countRegistryChanges, describeSaveButton, plural,
} = require('../../server/admin_ui/registry-diff.js');

// row — запись в том виде, в каком она уходит в /admin/games/save.
function row(gameId, over) {
  return Object.assign({
    gameId: gameId,
    title: gameId,
    iconUrl: '',
    exeRelativePath: gameId + '.exe',
    order: 0,
    pinned: false,
    unpublished: false,
  }, over || {});
}

test('без правок — ноль', () => {
  const list = [row('a'), row('b', { order: 1 })];

  assert.deepStrictEqual(countRegistryChanges(list, list.map((r) => Object.assign({}, r))), {
    changed: 0, added: 0, removed: 0, total: 0,
  });
});

test('число возвращается к нулю, когда правку отменили', () => {
  // Напечатал букву в названии и стёр её — правок снова нет. Прежний флаг
  // «стало грязно» в такой ситуации оставался поднятым.
  const was = [row('a', { title: 'Lethal Company' })];
  const typed = [row('a', { title: 'Lethal Companyy' })];
  const back = [row('a', { title: 'Lethal Company' })];

  assert.strictEqual(countRegistryChanges(was, typed).total, 1);
  assert.strictEqual(countRegistryChanges(was, back).total, 0);
});

test('изменённое поле считается один раз, а не по нажатию клавиши', () => {
  const was = [row('a'), row('b', { order: 1 })];
  const now = [row('a', { title: 'Другое' }), row('b', { order: 1 })];

  const diff = countRegistryChanges(was, now);
  assert.strictEqual(diff.changed, 1);
  assert.strictEqual(diff.total, 1);
});

test('перетаскивание считает все игры, у которых уехал порядок', () => {
  // Для человека это одно действие, для реестра — пять изменившихся записей.
  // На кнопке стоит то, чем пользователь рискует.
  const was = ['a', 'b', 'c', 'd', 'e'].map((id, i) => row(id, { order: i }));
  const now = ['e', 'a', 'b', 'c', 'd'].map((id, i) => row(id, { order: i }));

  assert.strictEqual(countRegistryChanges(was, now).changed, 5);
});

test('добавленная и удалённая игры считаются отдельно', () => {
  const was = [row('a'), row('b', { order: 1 })];
  const now = [row('a'), row('c', { order: 1 })];

  const diff = countRegistryChanges(was, now);
  assert.strictEqual(diff.added, 1);
  assert.strictEqual(diff.removed, 1);
  assert.strictEqual(diff.changed, 0);
  assert.strictEqual(diff.total, 2);
});

test('переименование в другом регистре — одна правка, а не удаление с добавлением', () => {
  // Реестр опознаёт игру по идентификатору без учёта регистра, поэтому
  // Lethal-Company и lethal-company — одна и та же строка. Написание при этом
  // действительно уедет на сервер, так что это правка; но считать её сразу
  // удалением и добавлением значило бы пугать числом вдвое большим правды.
  const was = [row('Lethal-Company')];
  const now = [row('lethal-company', { title: 'Lethal-Company', exeRelativePath: 'Lethal-Company.exe' })];

  const diff = countRegistryChanges(was, now);
  assert.strictEqual(diff.changed, 1);
  assert.strictEqual(diff.added, 0);
  assert.strictEqual(diff.removed, 0);
});

test('одно и то же значение в разных типах — не правка', () => {
  // order приходит числом, а из разметки могло бы прийти строкой. Строгое
  // сравнение дало бы «правку» там, где на сервер уедет ровно то же самое, и
  // кнопка звала бы сохранять несуществующие изменения.
  assert.strictEqual(countRegistryChanges([row('a', { order: 0 })], [row('a', { order: '0' })]).total, 0);
  assert.strictEqual(countRegistryChanges([row('a', { pinned: false })], [row('a', { pinned: false })]).total, 0);

  // А настоящая смена значения правкой быть обязана.
  assert.strictEqual(countRegistryChanges([row('a', { pinned: false })], [row('a', { pinned: true })]).total, 1);
});

test('пустой снимок: всё новое, но не всё сломано', () => {
  assert.strictEqual(countRegistryChanges(null, [row('a')]).added, 1);
  assert.strictEqual(countRegistryChanges([row('a')], null).removed, 1);
  assert.strictEqual(countRegistryChanges(null, null).total, 0);
});

test('кнопка без правок неактивна и подписана «Сохранено»', () => {
  const look = describeSaveButton({ changed: 0, added: 0, removed: 0, total: 0 });

  assert.strictEqual(look.enabled, false);
  assert.strictEqual(look.label, 'Сохранено');
  assert.match(look.title, /сохранять нечего/);
});

test('кнопка с правками называет их число и разбивку', () => {
  const look = describeSaveButton({ changed: 2, added: 1, removed: 0, total: 3 });

  assert.strictEqual(look.enabled, true);
  assert.strictEqual(look.label, 'Сохранить (3)');
  assert.match(look.title, /2 изменены/);
  assert.match(look.title, /1 добавлена/);
  assert.doesNotMatch(look.title, /удален/);
});

test('число согласовано со словом рядом', () => {
  assert.strictEqual(plural(1, 'игра', 'игры', 'игр'), 'игра');
  assert.strictEqual(plural(2, 'игра', 'игры', 'игр'), 'игры');
  assert.strictEqual(plural(5, 'игра', 'игры', 'игр'), 'игр');
  // Одиннадцать — не «одна»: это то место, где наивное правило по последней
  // цифре и ломается.
  assert.strictEqual(plural(11, 'игра', 'игры', 'игр'), 'игр');
  assert.strictEqual(plural(21, 'игра', 'игры', 'игр'), 'игра');
  assert.strictEqual(plural(112, 'игра', 'игры', 'игр'), 'игр');
});
