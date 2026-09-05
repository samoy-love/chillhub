// Копия главного экрана лаунчера на сайте.
//
// Смысл этих проверок в одном: копия обязана вести себя как продукт.
// Правила перенесены из исходников клиента, и каждая проверка ниже
// ссылается на то место, откуда правило взято. Разошлись — значит, на
// сайте показывают не тот лаунчер, который скачают.

const test = require('node:test');
const assert = require('node:assert');

const E = require('../../landing/emu-core.js');

const game = (over) => Object.assign({
  gameId: 'repo',
  title: 'R.E.P.O.',
  latestVersion: '1.0.1',
  hasServerBuild: true,
  mods: { steamAppId: '3241660', displayName: 'Moo Modpack', displayVersion: '1.9.9' },
  installed: false,
  needsUpdate: false,
  unfinished: false,
  error: false,
  bytes: 1.6 * E.GB,
  playtimeMin: 0,
}, over || {});

/* ---------- HomeFormat.cs ---------- */

test('размер пишется с запятой, как в лаунчере', () => {
  assert.strictEqual(E.formatSize(1.6 * E.GB), '1,6 ГБ');
  assert.strictEqual(E.formatSize(10.5 * E.MB), '10,5 МБ');
  assert.strictEqual(E.formatSize(512), '512 Б');
  assert.strictEqual(E.formatSize(-1), '—');
});

test('оставшееся время повторяет FormatEta', () => {
  assert.strictEqual(E.formatEta(43), '43 с');
  assert.strictEqual(E.formatEta(90), '2 мин');
  assert.strictEqual(E.formatEta(3725), '1 ч 02 мин');
  assert.strictEqual(E.formatEta(90000), '1 день 1 ч');
  assert.strictEqual(E.formatEta(172800), '2 дня');
  assert.strictEqual(E.formatEta(NaN), '—');
});

test('склонение дней совпадает с PluralizeDayRu', () => {
  assert.strictEqual(E.pluralizeDayRu(1), 'день');
  assert.strictEqual(E.pluralizeDayRu(11), 'дней');
  assert.strictEqual(E.pluralizeDayRu(3), 'дня');
  assert.strictEqual(E.pluralizeDayRu(14), 'дней');
  assert.strictEqual(E.pluralizeDayRu(21), 'день');
});

/* ---------- ActionButtonState.cs ---------- */

test('кнопка витрины выбирается по тем же ветвям, что в лаунчере', () => {
  assert.strictEqual(E.decideMode(game()), 'Install');
  assert.strictEqual(E.decideMode(game({ installed: true })), 'Play');
  assert.strictEqual(E.decideMode(game({ installed: true, needsUpdate: true })), 'Update');
  assert.strictEqual(E.decideMode(game({ error: true })), 'Retry');
});

test('без сборки на сервере «Установить» не предлагается', () => {
  // Кнопка вела к манифесту, которого не существует, и всё кончалось отказом
  assert.strictEqual(E.decideMode(game({ hasServerBuild: false })), 'SteamOnly');
  // Уже установленную можно запустить: осталась от прежних сборок
  assert.strictEqual(E.decideMode(game({ hasServerBuild: false, installed: true })), 'Play');
});

test('незавершённое обновление не даёт играть, а зовёт докатить', () => {
  const g = game({ installed: true, needsUpdate: false, unfinished: true });
  assert.strictEqual(E.decideMode(g), 'Update');
});

test('ошибка важнее всех прочих признаков', () => {
  assert.strictEqual(E.decideMode(game({ error: true, installed: true, hasServerBuild: false })), 'Retry');
});

test('техработы закрывают только то, что запрещено', () => {
  const maint = { enabled: true, blocks: { install: true, update: true, launch: false } };
  assert.strictEqual(E.blockedByMaintenance('Install', maint), true);
  assert.strictEqual(E.blockedByMaintenance('Update', maint), true);
  assert.strictEqual(E.blockedByMaintenance('Retry', maint), true);
  // Уже скачанное обязано запускаться: игра стартует локально
  assert.strictEqual(E.blockedByMaintenance('Play', maint), false);
  // «Нужна копия в Steam» к серверу отношения не имеет
  assert.strictEqual(E.blockedByMaintenance('SteamOnly', maint), false);
});

test('выключенные техработы ничего не запрещают', () => {
  assert.strictEqual(E.blockedByMaintenance('Install', { enabled: false, blocks: { install: true } }), false);
  assert.strictEqual(E.blockedByMaintenance('Install', null), false);
});

test('очередь важнее состояния: кнопка предлагает отменить', () => {
  const g = game({ installed: true });
  const q = E.enqueue([], g);
  assert.strictEqual(E.effectiveMode(g, q, null), 'Cancel');

  const second = E.enqueue(q, game({ gameId: 'peak' }));
  assert.strictEqual(E.effectiveMode(game({ gameId: 'peak' }), second, null), 'Dequeue');
});

/* ---------- LaunchButtons.cs ---------- */

test('кнопки запуска появляются только в режиме «Играть»', () => {
  const g = game({ installed: true });
  assert.strictEqual(E.launchButtons(g, 'Play').length, 2);
  assert.strictEqual(E.launchButtons(g, 'Install').length, 0);
  assert.strictEqual(E.launchButtons(g, 'Update').length, 0);
});

test('без модпака вариантов запуска нет, остаётся одна кнопка действия', () => {
  const g = game({ installed: true, mods: null });
  assert.deepStrictEqual(E.launchButtons(g, 'Play'), []);
});

test('без идентификатора Steam вариантов запуска тоже нет', () => {
  const g = game({ installed: true, mods: { displayName: 'X' } });
  assert.deepStrictEqual(E.launchButtons(g, 'Play'), []);
});

test('залитая кнопка в ряду ровно одна', () => {
  // Два акцента рядом не читаются как «главный» и «запасной»
  const b = E.launchButtons(game({ installed: true }), 'Play');
  assert.strictEqual(b.filter((x) => x.accent).length, 1);
  assert.strictEqual(b[0].title, 'Steam');
  assert.strictEqual(b[1].title, 'Пиратка');
});

/* ---------- Подписи ---------- */

test('подпись игры в списке повторяет GameStatusConverters', () => {
  assert.strictEqual(E.listSubtitle(game(), []), 'Не установлена');
  assert.strictEqual(E.listSubtitle(game({ installed: true }), []), 'Установлена');
  assert.strictEqual(E.listSubtitle(game({ installed: true, needsUpdate: true }), []), 'Обновление');
});

test('игра в очереди подписана очередью, а не своим состоянием', () => {
  const g = game({ installed: true });
  const q = E.enqueue([], g);
  assert.strictEqual(E.listSubtitle(g, q), 'Скачивание обновления…');
  assert.strictEqual(E.listTone(g, q), 'busy');

  const q2 = E.enqueue(q, game({ gameId: 'peak' }));
  assert.strictEqual(E.listSubtitle(game({ gameId: 'peak' }), q2), 'В очереди');
});

test('наигранное время до часа считается в минутах', () => {
  // «0 ч в игре» выглядит как отсутствие данных, а не как восемь минут
  assert.strictEqual(E.playtime(0), 'ещё не запускали');
  assert.strictEqual(E.playtime(8), '8 мин в игре');
  assert.strictEqual(E.playtime(59), '59 мин в игре');
  assert.strictEqual(E.playtime(60), '1 ч в игре');
  assert.strictEqual(E.playtime(132), '2 ч в игре');
});

test('подсказка о месте показывается, только пока игру ещё качать', () => {
  assert.strictEqual(E.spaceHint('Install', E.GB, 100 * E.GB), 'Нужно: 1,0 ГБ (100,0 ГБ доступно)');
  assert.strictEqual(E.spaceHint('Update', E.GB, 100 * E.GB).startsWith('Нужно:'), true);
  // На главном экране лаунчера строки про «уже установлена» нет
  assert.strictEqual(E.spaceHint('Play', E.GB, E.GB), '');
});

test('строка под заголовком собирает время, версию и модпак', () => {
  const meta = E.heroMeta(game({ playtimeMin: 8 }));
  assert.deepStrictEqual(meta, ['8 мин в игре', 'версия 1.0.1', 'моды: Moo Modpack 1.9.9']);
});

test('игра без модпака не получает пустую строку «моды:»', () => {
  const meta = E.heroMeta(game({ mods: null }));
  assert.strictEqual(meta.length, 2);
});

/* ---------- Очередь ---------- */

test('качается ровно одна игра, остальные ждут', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  q = E.enqueue(q, game({ gameId: 'c' }));
  assert.deepStrictEqual(q.map((x) => x.state), ['run', 'wait', 'wait']);
});

test('одна игра не встаёт в очередь дважды', () => {
  const g = game({ gameId: 'a' });
  const q = E.enqueue(E.enqueue([], g), g);
  assert.strictEqual(q.length, 1);
});

test('уход качающегося передаёт эстафету следующему', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  q = E.dequeue(q, 'a');
  assert.deepStrictEqual(q.map((x) => x.gameId), ['b']);
  assert.strictEqual(q[0].state, 'run');
});

test('уход ждущего не трогает того, кто качается', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  q = E.dequeue(q, 'b');
  assert.strictEqual(q.length, 1);
  assert.strictEqual(q[0].gameId, 'a');
  assert.strictEqual(q[0].state, 'run');
});

test('качающегося нельзя подвинуть с первой позиции', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  // Иначе начатая загрузка оборвалась бы
  assert.deepStrictEqual(E.move(q, 'a', 1).map((x) => x.gameId), ['a', 'b']);
  assert.deepStrictEqual(E.move(q, 'b', -1).map((x) => x.gameId), ['a', 'b']);
});

test('ждущие меняются местами между собой', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  q = E.enqueue(q, game({ gameId: 'c' }));
  assert.deepStrictEqual(E.move(q, 'c', -1).map((x) => x.gameId), ['a', 'c', 'b']);
  assert.deepStrictEqual(E.move(q, 'b', 1).map((x) => x.gameId), ['a', 'c', 'b']);
});

test('перестановка за пределы списка ничего не ломает', () => {
  const q = E.enqueue([], game({ gameId: 'a' }));
  assert.deepStrictEqual(E.move(q, 'a', 5).map((x) => x.gameId), ['a']);
  assert.deepStrictEqual(E.move(q, 'нет', 1).map((x) => x.gameId), ['a']);
});

test('очередь не меняется на месте: исходный список остаётся прежним', () => {
  const q0 = [];
  const q1 = E.enqueue(q0, game({ gameId: 'a' }));
  assert.strictEqual(q0.length, 0, 'исходный список изменять нельзя');
  assert.strictEqual(q1.length, 1);
});

test('номер в очереди считается от места в списке', () => {
  let q = E.enqueue([], game({ gameId: 'a' }));
  q = E.enqueue(q, game({ gameId: 'b' }));
  assert.strictEqual(E.queueLabel(q[0], 0), 'Скачивание обновления…');
  assert.strictEqual(E.queueLabel(q[1], 1), 'В очереди · 2-я');
});

test('счётчик в шапке появляется только когда есть из чего выбирать', () => {
  const one = E.enqueue([], game({ gameId: 'a' }));
  assert.strictEqual(E.dockTitle(one), 'Очередь загрузок');
  const two = E.enqueue(one, game({ gameId: 'b' }));
  assert.strictEqual(E.dockTitle(two), 'Очередь загрузок · качается 1 из 2');
  assert.strictEqual(E.dockTitle([]), 'Очередь загрузок');
});

test('числа прогресса собираются из размера и скорости', () => {
  const item = { done: 0.4 * E.GB, total: 1.6 * E.GB, speed: 10 * E.MB, state: 'run' };
  const p = E.progressText(item);
  assert.strictEqual(p.percent, 25);
  assert.strictEqual(p.size, '409,6 МБ / 1,6 ГБ');
  assert.match(p.rate, /^10,0 МБ\/с · осталось /);
});

test('нулевая скорость не даёт бесконечности в оставшемся времени', () => {
  const p = E.progressText({ done: 0, total: E.GB, speed: 0, state: 'run' });
  assert.match(p.rate, /осталось —/);
});
