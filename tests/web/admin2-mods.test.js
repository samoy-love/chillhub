// Разбор ссылок и ответов Thunderstore.
//
// Правило разбора ссылки повторяет серверное (`ParsePackageURL` в
// mods/catalog.go) — и это единственный способ не разойтись: две разные
// проверки одного и того же расходятся молча, а расплачивается за это
// человек, которому «ссылка не подходит» без объяснения.

const test = require('node:test');
const assert = require('node:assert');

const M = require('../../server/admin_ui/mods.js');
const format = require('../../server/admin_ui/format.js');

/* ---------- Ссылка на пакет ---------- */

test('нынешняя форма адреса разбирается', () => {
  assert.deepStrictEqual(M.parsePackageUrl('https://thunderstore.io/c/lethal-company/p/Ura/Modpack/'), {
    community: 'lethal-company',
    namespace: 'Ura',
    name: 'Modpack',
  });
});

test('старая форма без сообщества тоже: она живёт в чужих закладках', () => {
  assert.deepStrictEqual(M.parsePackageUrl('https://thunderstore.io/package/Ura/Modpack/'), {
    community: '',
    namespace: 'Ura',
    name: 'Modpack',
  });
});

test('хвост после имени пакета разбору не мешает', () => {
  const p = M.parsePackageUrl('http://thunderstore.io/c/repo/p/Ura/Modpack/v/1.2.3?utm=1');
  assert.strictEqual(p.name, 'Modpack');
});

test('не та ссылка — это null, а не догадка', () => {
  // Догадка отправила бы на сборку пакет, которого никто не выбирал
  for (const bad of [
    'https://example.com/c/x/p/A/B/',
    'thunderstore.io/A/B',
    'https://thunderstore.io/c/game/p/Ura/',
    '',
    null,
  ]) {
    assert.strictEqual(M.parsePackageUrl(bad), null, String(bad));
  }
});

test('полное имя пакета собирается так же, как его пишет Thunderstore', () => {
  assert.strictEqual(M.fullName('Ura', 'Modpack'), 'Ura/Modpack');
});

/* ---------- Строка каталога ---------- */

test('пакет приводится к одному виду, как бы его ни назвал Thunderstore', () => {
  // У результата поиска поля плоские, у пакета — внутри versions[0]
  const flat = M.entry({ owner: 'Ura', name: 'Modpack', version_number: '1.2.3', download_count: 4200 });
  const nested = M.entry({ namespace: 'Ura', name: 'Modpack', versions: [{ version_number: '1.2.3', download_count: 4200 }] });
  assert.strictEqual(flat.version, nested.version);
  assert.strictEqual(flat.downloads, nested.downloads);
  assert.strictEqual(flat.namespace, 'Ura');
});

test('устаревший пакет помечен как устаревший', () => {
  assert.strictEqual(M.entry({ is_deprecated: true }).deprecated, true);
  assert.strictEqual(M.entry({ deprecated: true }).deprecated, true);
  assert.strictEqual(M.entry({}).deprecated, false);
});

test('список читается и из results, и из голого массива', () => {
  assert.strictEqual(M.entries({ results: [{ name: 'a' }] }).length, 1);
  assert.strictEqual(M.entries([{ name: 'a' }, { name: 'b' }]).length, 2);
  assert.deepStrictEqual(M.entries(null), []);
});

/* ---------- Место перед сборкой ---------- */

test('кэш меняет ответ на вопрос «сколько качать»', () => {
  // Разница между «2 ГБ» и «205 МБ» решает, ждать минуту или двадцать
  const s = M.planSpace({ totalBytes: 2 * 1024 ** 3, cachedBytes: 1.8 * 1024 ** 3 }, format);
  assert.match(s.text, /205\u00a0МБ/);
  assert.match(s.text, /уже в кэше/);
  assert.strictEqual(s.tone, 'ok');
});

test('без кэша называется полный размер', () => {
  const s = M.planSpace({ totalBytes: 1024 ** 3 }, format);
  assert.match(s.text, /Скачать 1\u00a0ГБ/);
  assert.ok(!/кэше/.test(s.text));
});

test('нехватка места называется словами сервера и красным', () => {
  const s = M.planSpace({ totalBytes: 1, spaceOk: false, spaceNote: 'на диске 300 МБ, нужно 2 ГБ' }, format);
  assert.strictEqual(s.tone, 'bad');
  assert.match(s.text, /нужно 2 ГБ/);
});

test('неизвестный размер не выдаётся за нулевой', () => {
  assert.match(M.planSpace({}, format).text, /Размер неизвестен/);
});

/* ---------- Можно ли собирать ---------- */

test('пропавшие пакеты сами по себе не запрет', () => {
  // Сервер умеет собрать без них, если попросить, — это отдельный вопрос
  assert.strictEqual(M.planProblem({ packages: 17, missing: ['Ura/Old'] }), '');
});

test('нехватка места — запрет', () => {
  assert.match(M.planProblem({ packages: 17, spaceOk: false, spaceNote: 'мало места' }), /мало места/);
});

test('пустая сборка — запрет', () => {
  assert.match(M.planProblem({ packages: 0 }), /ни одного пакета/);
});

/* КАТАЛОГ СЕРВЕР ОТДАЁТ СВОИМИ ИМЕНАМИ ПОЛЕЙ.
   `/admin/api/mods/catalog` отвечает `namespace`, `name`, `description`,
   `download_count`, `is_deprecated` и `last_updated`. Номера версии в
   нём нет вовсе, а дату обновления разбор искал под именем
   `date_updated` — и не находил никогда. */
test('строка каталога читается по именам полей сервера', () => {
  const [e] = M.entries({
    results: [
      {
        namespace: 'ASTeam',
        name: 'MooModpack',
        description: 'Пак',
        download_count: 41200,
        rating_count: 12,
        last_updated: '2026-09-03T10:00:00Z',
        is_deprecated: false,
        is_pinned: true,
      },
    ],
  });
  assert.strictEqual(e.namespace, 'ASTeam');
  assert.strictEqual(e.downloads, 41200);
  assert.strictEqual(e.updated, '2026-09-03T10:00:00Z', 'дата обновления не прочиталась');
  assert.strictEqual(e.pinned, true);
});
