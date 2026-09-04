// Разница между двумя сборками лаунчера.
//
// Решение «отдать версию игрокам» необратимо, а принимается оно по двум
// номерам версий, которые сами по себе не говорят ничего. Список
// расходящихся файлов — единственное, по чему видно, что именно поедет
// на чужие компьютеры: три библиотеки или сборка целиком.
//
// Отдельное внимание — разнице между «файлы совпадают» и «сравнить не с
// чем». Старые манифесты на сервере подчищаются, и пустой список вместо
// честного «нет манифеста» означал бы, что решение принимают вслепую,
// думая, что видят всё.

const test = require('node:test');
const assert = require('node:assert');

const M = require('../../server/admin_ui/v2/manifest.js');

const man = (files) => ({ version: '1.0', files });
const f = (path, size, hash) => ({ path, size, blake3: hash });

/* ---------- Чтение ---------- */

test('список файлов читается, как бы он ни был обёрнут', () => {
  assert.strictEqual(M.files({ files: [f('a', 1, 'x')] }).length, 1);
  assert.strictEqual(M.files({ items: [f('a', 1, 'x')] }).length, 1);
  assert.strictEqual(M.files([f('a', 1, 'x')]).length, 1);
  assert.deepStrictEqual(M.files(null), []);
});

test('строка без пути в счёт не идёт', () => {
  assert.strictEqual(M.files({ files: [{ size: 10 }, f('a', 1, 'x')] }).length, 1);
});

test('хеш берётся blake3, а если его нет — sha256', () => {
  assert.strictEqual(M.files({ files: [{ path: 'a', sha256: 's' }] })[0].hash, 's');
  assert.strictEqual(M.files({ files: [{ path: 'a', blake3: 'b', sha256: 's' }] })[0].hash, 'b');
});

/* ---------- Разница ---------- */

test('добавленное, изменённое и пропавшее различаются', () => {
  const d = M.diff(
    man([f('a.dll', 10, 'x'), f('b.dll', 20, 'y'), f('gone.dll', 5, 'z')]),
    man([f('a.dll', 10, 'x'), f('b.dll', 20, 'ДРУГОЙ'), f('new.dll', 7, 'n')])
  );
  assert.deepStrictEqual(
    d.map((r) => r.diff + ' ' + r.path),
    ['mod b.dll', 'del gone.dll', 'add new.dll']
  );
});

test('файл сравнивается по хешу, а не по размеру', () => {
  // Перекомпиляция часто не меняет размер, и по размеру файл выглядит прежним
  const d = M.diff(man([f('a.dll', 100, 'старый')]), man([f('a.dll', 100, 'новый')]));
  assert.strictEqual(d.length, 1);
  assert.strictEqual(d[0].diff, 'mod');
});

test('одинаковые манифесты дают пустую разницу', () => {
  const same = man([f('a.dll', 10, 'x')]);
  assert.deepStrictEqual(M.diff(same, same), []);
});

test('список отсортирован по пути, чтобы соседи одной папки стояли рядом', () => {
  const d = M.diff(man([]), man([f('z/b.dll', 1, '1'), f('a/a.dll', 1, '2'), f('a/b.dll', 1, '3')]));
  assert.deepStrictEqual(d.map((r) => r.path), ['a/a.dll', 'a/b.dll', 'z/b.dll']);
});

test('счётчики считают то, что подписано', () => {
  const d = M.diff(man([f('gone', 1, 'x'), f('same', 1, 'y')]), man([f('same', 1, 'y'), f('new', 2, 'z')]));
  assert.deepStrictEqual(M.counts(d), { add: 1, mod: 0, del: 1, total: 2 });
  assert.deepStrictEqual(M.counts(null), { add: 0, mod: 0, del: 0, total: 0 });
});

test('качать игроку — только то, что появилось и изменилось', () => {
  // Удалённый файл клиент не скачивает, он его стирает
  const d = M.diff(man([f('gone', 1000, 'x')]), man([f('new', 30, 'n')]));
  assert.strictEqual(M.weight(d), 30);
});

/* ---------- Чтение манифеста ---------- */

/** Ответ, каким его отдаёт раздача манифестов. */
const ok = (body) => ({ ok: true, status: 200, text: async () => JSON.stringify(body) });

test('манифест читается по номеру версии', async () => {
  const seen = [];
  const got = await M.load('1.6.46', {
    fetch: async (u) => {
      seen.push(u);
      return ok(man([f('a', 1, 'x')]));
    },
  });
  assert.deepStrictEqual(seen, ['/manifests/launcher/1.6.46.json']);
  assert.strictEqual(M.files(got).length, 1);
});

test('версия в адресе экранируется', async () => {
  let url = '';
  await M.load('../secrets', {
    fetch: async (u) => {
      url = u;
      return ok({});
    },
  });
  assert.ok(!url.includes('../'), url);
});

test('пропавший манифест — это null, а не выдуманный пустой список', async () => {
  assert.strictEqual(await M.load('1.6.44', { fetch: async () => ({ ok: false, status: 404 }) }), null);
  assert.strictEqual(
    await M.load('1.6.44', {
      fetch: async () => {
        throw new Error('нет сети');
      },
    }),
    null
  );
  assert.strictEqual(await M.load('', { fetch: async () => ok({}) }), null);
});

test('оба манифеста читаются разом, а не по очереди', async () => {
  // По одному это два ожидания подряд там, где хватает одного
  let inFlight = 0;
  let peak = 0;
  const res = await M.between('1.6.44', '1.6.46', {
    fetch: async () => {
      inFlight++;
      peak = Math.max(peak, inFlight);
      await new Promise((r) => setTimeout(r, 5));
      inFlight--;
      return ok(man([f('a', 1, 'x')]));
    },
  });
  assert.strictEqual(peak, 2, 'манифесты читались по очереди');
  assert.deepStrictEqual(res.counts, { add: 0, mod: 0, del: 0, total: 0 });
});

test('подчищенный старый манифест — «сравнить не с чем», а не «всё совпало»', async () => {
  // Иначе решение об активации принимают вслепую, думая, что видят всё
  const res = await M.between('1.6.44', '1.6.46', {
    fetch: async (u) => (u.includes('1.6.44') ? { ok: false, status: 404 } : ok(man([f('a', 1, 'x')]))),
  });
  assert.strictEqual(res, null);
});

test('разница называет и вес, и общее число файлов новой сборки', async () => {
  const res = await M.between('1.6.44', '1.6.46', {
    fetch: async (u) =>
      u.includes('1.6.44')
        ? ok(man([f('a.dll', 10, 'x'), f('b.dll', 20, 'y')]))
        : ok(man([f('a.dll', 10, 'x'), f('b.dll', 20, 'ДРУГОЙ'), f('c.dll', 30, 'z')])),
  });
  assert.deepStrictEqual(res.counts, { add: 1, mod: 1, del: 0, total: 2 });
  assert.strictEqual(res.weight, 50);
  assert.strictEqual(res.total, 3);
});
