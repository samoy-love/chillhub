// Хранилище разделов панели 2.0.
//
// Проверяются решения, ради которых оно и заведено: разделы независимы,
// у каждого явное состояние, повторный щелчок не порождает второго
// запроса, а упавший раздел не выбрасывает то, что уже показывал.

const test = require('node:test');
const assert = require('node:assert');

const { createStore, IDLE, LOADING, READY, FAILED } = require('../../server/admin_ui/store.js');

const defer = () => {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
};

test('разделы начинаются с состояния «ещё не грузили»', () => {
  const s = createStore({ a: async () => 1, b: async () => 2 }, {});
  assert.strictEqual(s.get('a').status, IDLE);
  assert.strictEqual(s.get('a').data, null);
  assert.deepStrictEqual(s.names, ['a', 'b']);
});

test('загрузка проходит через «грузится» и доходит до «готово»', async () => {
  const seen = [];
  const d = defer();
  const s = createStore({ a: () => d.promise }, {});
  s.subscribe((name, st) => seen.push(st.status));

  const p = s.load('a');
  assert.strictEqual(s.get('a').status, LOADING);
  d.resolve({ items: [1] });
  await p;

  assert.strictEqual(s.get('a').status, READY);
  assert.deepStrictEqual(s.get('a').data, { items: [1] });
  assert.deepStrictEqual(seen, [LOADING, READY]);
});

test('упавший раздел помечается, но показанное раньше не выбрасывает', async () => {
  let attempt = 0;
  const s = createStore({
    a: async () => {
      attempt++;
      if (attempt === 1) return 'первое';
      throw new Error('сервер молчит');
    },
  }, {});

  await s.load('a');
  assert.strictEqual(s.get('a').data, 'первое');

  await s.load('a', { force: true });
  assert.strictEqual(s.get('a').status, FAILED);
  assert.strictEqual(s.get('a').error.message, 'сервер молчит');
  // Вчерашнее с пометкой честнее пустого экрана
  assert.strictEqual(s.get('a').data, 'первое');
});

test('падение раздела не мешает остальным', async () => {
  const s = createStore({
    ok: async () => 'да',
    bad: async () => { throw new Error('нет'); },
  }, {});

  await s.loadAll();
  assert.strictEqual(s.get('ok').status, READY);
  assert.strictEqual(s.get('bad').status, FAILED);

  const h = s.health();
  assert.deepStrictEqual(h.live, ['ok']);
  assert.deepStrictEqual(h.failed, ['bad']);
  assert.strictEqual(h.total, 2);
});

test('два щелчка подряд не порождают второго запроса', async () => {
  let calls = 0;
  const d = defer();
  const s = createStore({ a: () => { calls++; return d.promise; } }, {});

  const p1 = s.load('a');
  const p2 = s.load('a');
  // Присоединение видно по тому, что это буквально одно и то же ожидание
  assert.strictEqual(p1, p2, 'второй щелчок обязан присоединиться к первому');

  d.resolve('готово');
  await Promise.all([p1, p2]);
  assert.strictEqual(calls, 1, 'на сервер ушёл ровно один запрос');
});

test('готовый раздел не перезапрашивается без нужды', async () => {
  let calls = 0;
  const s = createStore({ a: async () => { calls++; return calls; } }, {});
  await s.load('a');
  await s.load('a');
  assert.strictEqual(calls, 1);

  await s.load('a', { force: true });
  assert.strictEqual(calls, 2, 'явное обновление обязано сходить на сервер');
});

test('устаревание перечитывает только то, что уже показывали', async () => {
  let a = 0;
  let b = 0;
  const s = createStore({ a: async () => ++a, b: async () => ++b }, {});

  await s.load('a'); // b не читали ни разу
  await s.invalidate(['a', 'b']);

  assert.strictEqual(a, 2, 'показанный раздел перечитан');
  assert.strictEqual(b, 0, 'непоказанный раздел не тревожим до открытия');
  assert.strictEqual(s.get('b').status, IDLE);
});

test('устаревание принимает и одно имя, и список', async () => {
  let a = 0;
  const s = createStore({ a: async () => ++a }, {});
  await s.load('a');
  await s.invalidate('a');
  assert.strictEqual(a, 2);
});

test('неизвестный раздел в списке устаревания молча пропускается', async () => {
  const s = createStore({ a: async () => 1 }, {});
  await s.load('a');
  await assert.doesNotReject(s.invalidate(['a', 'такого-нет']));
});

test('загрузка неизвестного раздела — ошибка в коде, а не тихий отказ', async () => {
  const s = createStore({ a: async () => 1 }, {});
  await assert.rejects(s.load('нет'), /нет такого раздела/);
});

test('раздел считается требующим загрузки, пока не прочитан или упал', async () => {
  const s = createStore({ a: async () => 1, b: async () => { throw new Error('x'); } }, {});
  assert.strictEqual(s.isStale('a'), true);
  await s.loadAll();
  assert.strictEqual(s.isStale('a'), false);
  assert.strictEqual(s.isStale('b'), true, 'упавший обязан перечитываться при открытии');
});

test('загрузчик получает api из зависимостей', async () => {
  let got = null;
  const api = { marker: 1 };
  const s = createStore({ a: async (a) => { got = a; return 1; } }, { api });
  await s.load('a');
  assert.strictEqual(got, api);
});

test('подписка отключается', async () => {
  let n = 0;
  const s = createStore({ a: async () => 1 }, {});
  const off = s.subscribe(() => n++);
  await s.load('a');
  const afterFirst = n;
  off();
  await s.load('a', { force: true });
  assert.strictEqual(n, afterFirst, 'после отписки уведомлений быть не должно');
});

test('синхронно брошенная ошибка загрузчика не роняет хранилище', async () => {
  const s = createStore({ a: () => { throw new Error('сразу'); } }, {});
  await s.load('a');
  assert.strictEqual(s.get('a').status, FAILED);
  assert.strictEqual(s.get('a').error.message, 'сразу');
});

/* ---------- Гонка записи и чтения ---------- */

test('перечитывание после записи не подменяется идущим запросом', async () => {
  // Запрос, ушедший ДО записи, ответит тем, что было до неё. Вернув его,
  // хранилище пометило бы раздел свежим — и экран остался бы врать
  let answer = 'до записи';
  let calls = 0;
  let release;
  const gate = new Promise((r) => (release = r));

  const store = createStore({
    games: async () => {
      calls++;
      if (calls === 1) await gate;
      return answer;
    },
  });

  const slow = store.load('games');
  await new Promise((r) => setTimeout(r, 0));

  // Запись прошла, пока первый запрос ещё в пути
  answer = 'после записи';
  const fresh = store.invalidate(['games']);

  release();
  await slow;
  await fresh;

  assert.strictEqual(calls, 2, 'второй запрос не ушёл');
  assert.strictEqual(store.get('games').data, 'после записи');
  assert.strictEqual(store.get('games').status, READY);
});

test('обычное чтение по-прежнему не плодит второй запрос', async () => {
  // Щелчок по «Обновить» дважды подряд — обычное дело
  let calls = 0;
  const store = createStore({
    games: async () => {
      calls++;
      await new Promise((r) => setTimeout(r, 0));
      return 'данные';
    },
  });

  await Promise.all([store.load('games'), store.load('games'), store.load('games')]);
  assert.strictEqual(calls, 1);
});
