// Реестр пишущих действий панели 2.0.
//
// Проверяется правило, ради которого реестр и заведён: необратимое
// действие обязано спросить и назвать объект, обратимое — не спрашивать.
// Плюс отказ человека не должен выглядеть как сбой.

const test = require('node:test');
const assert = require('node:assert');

const A = require('../../server/admin_ui/actions.js');

/** Все действия, которые уводят изменение к игрокам необратимо. */
const DANGER = [
  'launcher.activate',
  'launcher.delete',
  'launcher.prune',
  'games.purge',
  'mods.activate',
  'mods.delete',
  'news.publish',
  'news.delete',
  'inbox.delete',
  'inbox.clear',
  'maint.on',
  'metrics.clear',
  'cache.clear',
];

/** Действия, которые можно повторить или отменить обычным способом. */
const SAFE = [
  'games.scan',
  'news.rebuild',
  'inbox.important',
  'inbox.read',
  'maint.off',
  'maint.save',
  'cache.sweep',
];

test('опись действий и реестр не расходятся', () => {
  const declared = Object.keys(A.ACTIONS).sort();
  const listed = DANGER.concat(SAFE).sort();
  assert.deepStrictEqual(declared, listed);
});

test('необратимые действия спрашивают', () => {
  for (const id of DANGER) {
    assert.strictEqual(A.needsConfirm(id), true, id + ' обязано спрашивать');
  }
});

test('обратимые действия не спрашивают', () => {
  // Спрашивать про всё подряд так же вредно: человек привыкает жать «да»
  for (const id of SAFE) {
    assert.strictEqual(A.needsConfirm(id), false, id + ' спрашивать не должно');
    assert.strictEqual(A.question(id, {}), null);
  }
});

test('вопрос называет объект, а не спрашивает «вы уверены»', () => {
  const q = A.question('launcher.activate', { version: '1.6.25' });
  assert.match(q.title, /1\.6\.25/);
  assert.ok(!/уверены/i.test(q.title + q.body), 'вопрос не должен быть безличным');
  // У кнопки согласия свой глагол, а не «ОК»
  assert.strictEqual(q.ok, 'Отдать игрокам');
  assert.strictEqual(q.cancel, 'Отмена');
});

test('каждый вопрос содержит имя объекта или его число', () => {
  const args = {
    'launcher.activate': { version: '1.6.25' },
    'launcher.delete': { version: '1.6.24' },
    'launcher.prune': { keep: 5 },
    'games.purge': { gameId: 'repo', title: 'R.E.P.O.' },
    'gallery.delete': { path: 'cover.jpg' },
    'mods.activate': { version: '1.9.9' },
    'mods.delete': { version: '1.9.8' },
    'news.publish': { title: 'Заметка', published: true },
    'news.delete': { title: 'Заметка' },
    'inbox.delete': {},
    'inbox.clear': { count: 12 },
    'maint.on': {},
    'metrics.clear': {},
  };
  for (const id of DANGER) {
    const q = A.question(id, args[id]);
    assert.ok(q.title && q.title.length > 8, id + ': пустой заголовок');
    assert.ok(q.body && q.body.length > 20, id + ': вопрос без последствий');
    assert.ok(q.ok && q.ok.length > 2, id + ': кнопка без глагола');
  }
});

test('вопрос про публикацию меняется вместе с направлением', () => {
  const on = A.question('news.publish', { title: 'Заметка', published: true });
  const off = A.question('news.publish', { title: 'Заметка', published: false });
  assert.match(on.title, /Опубликовать/);
  assert.match(off.title, /Снять с публикации/);
  assert.notStrictEqual(on.body, off.body);
});

test('каждое действие знает, что после него устарело', () => {
  for (const id of Object.keys(A.ACTIONS)) {
    const after = A.stale(id);
    assert.ok(Array.isArray(after) && after.length > 0, id + ': не сказано, что перечитать');
  }
});

test('действия, меняющие видимое игроками, обновляют и «Что решить»', () => {
  for (const id of ['launcher.activate', 'mods.activate', 'maint.on', 'news.publish']) {
    assert.ok(A.stale(id).includes('overview'), id + ': первый экран останется врать');
  }
});

test('успешное действие отчитывается человеческой строкой', async () => {
  const api = { launcherActivate: async () => ({ ok: true }) };
  const res = await A.run('launcher.activate', { version: '1.6.25' }, { api, confirm: async () => true });
  assert.strictEqual(res.ok, true);
  assert.strictEqual(res.message, 'Игроки получают версию 1.6.25');
  assert.deepStrictEqual(res.stale, ['launcher', 'overview']);
});

test('отказ человека — не ошибка', async () => {
  let called = false;
  const api = { launcherDelete: async () => { called = true; } };
  const res = await A.run('launcher.delete', { version: '1.0' }, { api, confirm: async () => false });
  assert.strictEqual(res.ok, false);
  assert.strictEqual(res.cancelled, true);
  assert.strictEqual(res.error, undefined, 'отказ не должен выглядеть сбоем');
  assert.strictEqual(called, false, 'запрос не должен был уйти');
});

test('без функции подтверждения необратимое действие не выполняется', async () => {
  let called = false;
  const api = { metricsClear: async () => { called = true; } };
  const res = await A.run('metrics.clear', {}, { api });
  assert.strictEqual(res.ok, false);
  assert.strictEqual(res.cancelled, true);
  assert.strictEqual(called, false);
});

test('обратимое действие уходит без вопроса', async () => {
  let called = false;
  const api = { gamesScan: async () => { called = true; return 'ok'; } };
  const res = await A.run('games.scan', {}, { api });
  assert.strictEqual(res.ok, true);
  assert.strictEqual(called, true);
});

test('ошибка сервера доносится текстом, а не молчанием', async () => {
  const api = { newsRebuild: async () => { throw new Error('индекс занят'); } };
  const res = await A.run('news.rebuild', {}, { api });
  assert.strictEqual(res.ok, false);
  assert.strictEqual(res.message, 'индекс занят');
  assert.ok(res.error instanceof Error);
});

test('неизвестное действие — это ошибка в коде, а не тихий отказ', async () => {
  await assert.rejects(A.run('нет.такого', {}, { api: {} }), /нет такого действия/);
});

test('чтение прочитанного и возврат в новые — одно действие с направлением', async () => {
  const seen = [];
  const api = {
    feedbackRead: async (id) => seen.push('read:' + id),
    feedbackUnread: async (id) => seen.push('unread:' + id),
  };
  await A.run('inbox.read', { id: '7', read: true }, { api });
  await A.run('inbox.read', { id: '7', read: false }, { api });
  assert.deepStrictEqual(seen, ['read:7', 'unread:7']);
});

/* ---------- Что обещает вопрос ---------- */

test('удаление активной версии предупреждает об откате всех лаунчеров', () => {
  // Сервер не убирает latest.json, а переставляет его на предыдущую
  // версию — и все установленные лаунчеры уезжают на неё
  const q = A.question('launcher.delete', { version: '1.6.25', active: true });
  assert.match(q.body, /ПРЕДЫДУЩАЯ|предыдущая/);
  assert.match(q.body, /откат/i);
});

test('удаление старой версии не пугает откатом, которого не будет', () => {
  // Пугать там, где ничего не произойдёт, — верный способ приучить
  // жать «да» не читая
  const q = A.question('launcher.delete', { version: '1.6.20', active: false });
  assert.ok(!/откат/i.test(q.body), q.body);
  assert.match(q.body, /ничего не изменится/);
});

test('оба вопроса называют версию, которую удаляют', () => {
  for (const active of [true, false]) {
    assert.match(A.question('launcher.delete', { version: '1.6.25', active }).title, /1\.6\.25/);
  }
});

test('вопрос о чистке называет версии поимённо и то, что останется', () => {
  // «Удалить старые» без списка — просьба довериться, а доверяться нечему
  const q = A.question('launcher.prune', { victims: ['1.0.0', '1.0.1'], active: '1.0.4' });
  assert.match(q.title, /2 старых/);
  assert.match(q.body, /1\.0\.0, 1\.0\.1/);
  assert.match(q.body, /Останутся активная 1\.0\.4/);
  assert.match(q.body, /две перед ней/);
});

/* РАЗДЕЛ, КОТОРОГО НЕТ, ПЕРЕЧИТАН НЕ БУДЕТ — И НИКТО НЕ СКАЖЕТ.
   `store.invalidate` молча пропускает незнакомое имя: опечатка в списке
   `after` не роняет ничего, просто раздел остаётся со старыми данными
   после действия, которое его изменило. Со стороны это «панель показывает
   не то», и искать причину неоткуда. */
test('каждое действие обновляет только существующие разделы', () => {
  const sections = require('../../server/admin_ui/sections.js');
  const known = new Set(Object.keys(sections.LOADERS));
  for (const id of Object.keys(A.ACTIONS)) {
    for (const name of A.stale(id)) {
      assert.ok(known.has(name), `действие ${id} перечитывает несуществующий раздел ${name}`);
    }
  }
});
