// Галерея игры в панели 2.0.
//
// Единственное место, где человек ходит по дереву папок и переименовывает
// файлы. Ошибиться легко и дорого: путь с `..` уводит операцию из
// каталога игры, переименование в занятое имя затирает чужой файл, а
// удаление обложки оставляет витрину с градиентом — и заметит это уже
// игрок.

const test = require('node:test');
const assert = require('node:assert');

const G = require('../../server/admin_ui/gallery.js');

/* ---------- Пути ---------- */

test('обычный путь проходит как есть', () => {
  assert.strictEqual(G.safePath('screens/2026'), 'screens/2026');
  assert.strictEqual(G.safePath(''), '');
});

test('путь наружу каталога игры отвергается целиком', () => {
  // Молча «чинить» такой путь нельзя: тихая подмена сделает не то, чего ждали
  for (const bad of ['../secrets', 'a/../../b', '..', 'a/..', './a', '/etc/passwd', '/']) {
    assert.strictEqual(G.safePath(bad), '', 'пропущен опасный путь: ' + bad);
  }
});

test('обратные и двойные слэши приводятся к нормальному виду', () => {
  assert.strictEqual(G.safePath('screens\\2026'), 'screens/2026');
  assert.strictEqual(G.safePath('screens//2026'), 'screens/2026');
});

test('крошки ведут от корня галереи до текущей папки', () => {
  assert.deepStrictEqual(G.crumbs('screens/2026'), [
    { name: 'Галерея', path: '' },
    { name: 'screens', path: 'screens' },
    { name: '2026', path: 'screens/2026' },
  ]);
});

test('в корне галереи крошка одна', () => {
  assert.deepStrictEqual(G.crumbs(''), [{ name: 'Галерея', path: '' }]);
  assert.deepStrictEqual(G.crumbs('../x'), [{ name: 'Галерея', path: '' }]);
});

test('вверх из вложенной папки ведёт к родителю, из корня — никуда', () => {
  assert.strictEqual(G.parent('screens/2026'), 'screens');
  assert.strictEqual(G.parent('screens'), '');
  assert.strictEqual(G.parent(''), '');
});

test('полный путь собирается из папки и имени', () => {
  assert.strictEqual(G.entryPath('screens', 'cover.png'), 'screens/cover.png');
  assert.strictEqual(G.entryPath('', 'cover.png'), 'cover.png');
});

/* ---------- Имена ---------- */

test('нормальное имя проходит', () => {
  assert.strictEqual(G.nameProblem('cover.png', ['other.png']), '');
  assert.strictEqual(G.canRename('cover.png', []), true);
});

test('пустое имя и имя со слэшем отвергаются с объяснением', () => {
  assert.match(G.nameProblem('', []), /Пустое имя/);
  assert.match(G.nameProblem('   ', []), /Пустое имя/);
  assert.match(G.nameProblem('a/b.png', []), /это путь, а не имя/);
  assert.match(G.nameProblem('a\\b.png', []), /это путь, а не имя/);
});

test('точки и запрещённые символы не проходят', () => {
  assert.match(G.nameProblem('..', []), /означает папку/);
  assert.match(G.nameProblem('a?.png', []), /запрещённые в файловой системе/);
  assert.match(G.nameProblem('a:b.png', []), /запрещённые в файловой системе/);
});

test('занятое имя ловится без учёта регистра', () => {
  // Windows не различает регистр, и «Cover.png» затрёт «cover.png» молча
  assert.match(G.nameProblem('Cover.png', ['cover.png']), /уже занято/);
  assert.strictEqual(G.canRename('Cover.png', ['cover.png']), false);
});

test('своё же имя не считается занятым чужим', () => {
  assert.match(G.nameProblem('cover.png', ['cover.png']), /уже занято/);
  // Список соседей не должен включать сам переименовываемый файл
  assert.strictEqual(G.nameProblem('cover.png', ['other.png']), '');
});

/* ---------- Обложка ---------- */

test('обложкой становится только картинка', () => {
  assert.strictEqual(G.coverProblem({ name: 'cover.png' }), '');
  assert.strictEqual(G.coverProblem({ name: 'cover.JPG' }), '');
  assert.match(G.coverProblem({ name: 'guide.pdf' }), /только картинка/);
  assert.match(G.coverProblem({ name: 'screens', dir: true }), /Папка не может/);
});

test('картинка распознаётся по расширению', () => {
  for (const n of ['a.png', 'b.jpg', 'c.jpeg', 'd.gif', 'e.webp', 'f.avif']) {
    assert.strictEqual(G.isImage(n), true, n);
  }
  for (const n of ['a.svg', 'b.pdf', 'c']) {
    assert.strictEqual(G.isImage(n), false, n);
  }
});

/* ---------- Удаление ---------- */

test('удаление обложки предупреждает о последствии для игрока', () => {
  const w = G.deleteWarning({ name: 'cover.png' }, 'cover.png');
  assert.match(w, /витрина останется с градиентом/);
});

test('удаление обычного файла обходится без предупреждения', () => {
  assert.strictEqual(G.deleteWarning({ name: 'other.png' }, 'cover.png'), '');
});

test('удаление папки предупреждает о содержимом', () => {
  assert.match(G.deleteWarning({ name: 'screens', dir: true }, 'cover.png'), /со всем, что в ней лежит/);
});

/* ---------- Порядок ---------- */

test('папки идут первыми, дальше файлы', () => {
  const out = G.sortEntries([
    { name: 'b.png' },
    { name: 'screens', dir: true },
    { name: 'a.png' },
    { name: 'archive', dir: true },
  ]);
  assert.deepStrictEqual(out.map((x) => x.name), ['archive', 'screens', 'a.png', 'b.png']);
});

test('сортировка по имени русская, а не по кодам символов', () => {
  // Обычный sort ставит «Ящик» перед «арка»
  const out = G.sortEntries([{ name: 'Ящик' }, { name: 'арка' }, { name: 'Берег' }]);
  assert.deepStrictEqual(out.map((x) => x.name), ['арка', 'Берег', 'Ящик']);
});

test('сортировка не меняет исходный список', () => {
  const before = [{ name: 'b' }, { name: 'a' }];
  G.sortEntries(before);
  assert.deepStrictEqual(before.map((x) => x.name), ['b', 'a']);
});

test('пустой список сортируется в пустой', () => {
  assert.deepStrictEqual(G.sortEntries([]), []);
  assert.deepStrictEqual(G.sortEntries(null), []);
});
