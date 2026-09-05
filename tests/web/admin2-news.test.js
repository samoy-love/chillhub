// Новость в панели 2.0.
//
// Модель здесь не выдуманная, а серверная, и это главное, что
// проверяется. Заметка — один markdown-файл: заголовок сервер берёт
// первой строкой `# ...`, отдельного поля под него нет. Адресуется она
// тройкой `scope` + `gameId` + `slug`, а не одним номером. Панель,
// придумавшая себе «id» и «title», молча промахивается мимо каждой
// ручки: сервер отвечает «invalid slug», а человек видит «не
// сохранилось» без единой подсказки почему.

const test = require('node:test');
const assert = require('node:assert');

const N = require('../../server/admin_ui/news.js');

/** Хранилище черновиков, какое бывает в браузере. */
function storage() {
  const map = new Map();
  return {
    map,
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v)),
    removeItem: (k) => map.delete(k),
  };
}

/* ---------- Адрес ---------- */

test('пустая игра означает новость про лаунчер, а не про игру без имени', () => {
  assert.deepStrictEqual(N.address({ slug: 'note' }), { scope: 'launcher', gameId: '', slug: 'note' });
  assert.deepStrictEqual(N.address({ gameId: 'repo', slug: 'p1' }), { scope: 'game', gameId: 'repo', slug: 'p1' });
});

test('адрес не разваливается на пустой заметке', () => {
  assert.deepStrictEqual(N.address(null), { scope: 'launcher', gameId: '', slug: '' });
});

/* ---------- Имя файла ---------- */

test('нормальное имя проходит', () => {
  assert.strictEqual(N.slugProblem('release-1_6_25'), '');
  assert.strictEqual(N.slugProblem('заметка'), '');
});

test('имя, которое сервер не примет, названо до нажатия', () => {
  // Имя становится путём к файлу и частью адреса статьи
  assert.match(N.slugProblem(''), /Без имени/);
  assert.match(N.slugProblem('.скрытая'), /только буквы/);
  assert.match(N.slugProblem('-минус'), /только буквы/);
  assert.match(N.slugProblem('а..б'), /Две точки подряд/);
  assert.match(N.slugProblem('есть пробел'), /только буквы/);
  assert.match(N.slugProblem('слэш/внутри'), /только буквы/);
  assert.match(N.slugProblem('я'.repeat(129)), /128/);
});

test('имя предлагается из заголовка, но не навязывается', () => {
  // Имя попадает в адрес статьи и потом не меняется, заголовок правят свободно
  assert.strictEqual(N.suggestSlug('Вышла версия 1.6.25!'), 'вышла-версия-1-6-25');
  assert.strictEqual(N.suggestSlug('  ---  '), '');
});

test('предложенное имя само по себе годится', () => {
  for (const t of ['Вышла 1.6.25', '...Точки в начале', '— Тире в начале']) {
    const s = N.suggestSlug(t);
    if (s) assert.strictEqual(N.slugProblem(s), '', t + ' -> ' + s);
  }
});

/* ---------- Текст ---------- */

test('заголовок читается так же, как его прочтёт сервер', () => {
  assert.strictEqual(N.titleOf('# Вышла 1.6.25\n\nПочинили обрыв.'), 'Вышла 1.6.25');
  assert.strictEqual(N.titleOf('Вступление\n# Заголовок ниже'), 'Заголовок ниже');
  assert.strictEqual(N.titleOf('## Не тот уровень'), '');
  assert.strictEqual(N.titleOf(''), '');
});

test('текст без заголовка — это текст, а заголовок в него не входит', () => {
  assert.strictEqual(N.bodyOf('# Т\n\nПочинили обрыв.'), 'Починили обрыв.');
  assert.strictEqual(N.bodyOf('# Только заголовок'), '');
});

test('заметка без заголовка не уйдёт в ленту безымянной строкой', () => {
  const p = N.problems({ slug: 'ok', markdown: 'просто текст' });
  assert.strictEqual(p.length, 1);
  assert.strictEqual(p[0].field, 'markdown');
  assert.match(p[0].text, /# Название заметки/);
});

test('заметка из одного заголовка выглядит сбоем загрузки', () => {
  const p = N.problems({ slug: 'ok', markdown: '# Вышла 1.6.25' });
  assert.match(p[0].text, /откроет и закроет/);
});

test('целая заметка проходит', () => {
  const post = { slug: 'release', markdown: '# Вышла 1.6.25\n\nПочинили обрыв скачивания.' };
  assert.deepStrictEqual(N.problems(post), []);
  assert.strictEqual(N.canSave(post), true);
});

/* ---------- Что уезжает ---------- */

test('на сервер уезжают имена полей контракта, а не свои', () => {
  // scope, gameId, slug, markdown, coverUrl, published — и никакого title
  const out = N.payload({ slug: 'p1', gameId: 'repo', markdown: '# Т\n\nтекст', published: true, coverUrl: '/x.png' });
  assert.deepStrictEqual(Object.keys(out).sort(), ['coverUrl', 'gameId', 'markdown', 'published', 'scope', 'slug']);
  assert.strictEqual(out.scope, 'game');
  assert.strictEqual(out.published, 'true');
});

test('новость лаунчера не тащит с собой идентификатор игры', () => {
  const out = N.payload({ slug: 'note', markdown: '# Т\n\nтекст' });
  assert.strictEqual(out.scope, 'launcher');
  assert.ok(!('gameId' in out), 'в новость лаунчера попал gameId');
});

test('«не опубликовано» уезжает словом, а не пропадает', () => {
  // Пропавшее поле на сервере неотличимо от «не прислали»
  assert.strictEqual(N.payload({ slug: 'n', markdown: '# Т' }).published, 'false');
});

/* ---------- Черновик ---------- */

test('черновик сохраняется и читается по адресу заметки', () => {
  const s = storage();
  const post = { slug: 'p1', gameId: 'repo', markdown: '# Т\n\nнедописано' };
  assert.strictEqual(N.saveDraft(s, post), true);
  assert.strictEqual(N.readDraft(s, post).post.markdown, '# Т\n\nнедописано');
});

test('заметка игры и заметка лаунчера с одним именем — разные черновики', () => {
  // Иначе одна затирает другую, и пропажу замечают уже после сохранения
  const s = storage();
  N.saveDraft(s, { slug: 'note', gameId: 'repo', markdown: '# Игра' });
  N.saveDraft(s, { slug: 'note', markdown: '# Лаунчер' });
  assert.strictEqual(N.readDraft(s, { slug: 'note', gameId: 'repo' }).post.markdown, '# Игра');
  assert.strictEqual(N.readDraft(s, { slug: 'note' }).post.markdown, '# Лаунчер');
});

test('очищенное поле убирает черновик, а не сохраняет пустоту', () => {
  const s = storage();
  N.saveDraft(s, { slug: 'p1', markdown: '# Т' });
  assert.strictEqual(N.saveDraft(s, { slug: '', markdown: '   ' }), false);
  assert.strictEqual(N.readDraft(s, { slug: '' }), null);
});

test('мусор в хранилище — это его отсутствие, а не падение редактора', () => {
  const s = storage();
  s.setItem(N.draftKey({ slug: 'p1' }), 'не json');
  assert.strictEqual(N.readDraft(s, { slug: 'p1' }), null);
});

test('закрытое хранилище не роняет редактор', () => {
  // Браузер может запретить запись настройками — черновик приятен, но не обязателен
  const dead = {
    getItem: () => {
      throw new Error('заблокировано');
    },
    setItem: () => {
      throw new Error('заблокировано');
    },
    removeItem: () => {
      throw new Error('заблокировано');
    },
  };
  assert.strictEqual(N.saveDraft(dead, { slug: 'p', markdown: '# Т' }), false);
  assert.strictEqual(N.readDraft(dead, { slug: 'p' }), null);
  N.dropDraft(dead, { slug: 'p' });
});

test('черновика нет — и без хранилища ничего не ломается', () => {
  assert.strictEqual(N.saveDraft(null, { slug: 'p' }), false);
  assert.strictEqual(N.readDraft(null, { slug: 'p' }), null);
  N.dropDraft(null, { slug: 'p' });
});

test('вернуть черновик предлагают, только когда он отличается', () => {
  // Предлагать восстановить ровно то, что открыто, — значит пугать зря
  const same = { post: { markdown: '# Т\n\nтекст' } };
  assert.strictEqual(N.restorable(same, { markdown: '# Т\n\nтекст' }), false);
  assert.strictEqual(N.restorable(same, { markdown: '# Т\n\nдругое' }), true);
  assert.strictEqual(N.restorable(null, {}), false);
});

/* ---------- Вложения ---------- */

test('вложение вставляется тем адресом, по которому его увидит игрок', () => {
  // Раздаются они с /news/assets/, а не по пути внутри админки
  assert.strictEqual(N.normalizePath('2026/shot.png'), '/news/assets/2026/shot.png');
  assert.strictEqual(N.normalizePath('/2026//shot.png'), '/news/assets/2026/shot.png');
  assert.strictEqual(N.normalizePath('2026\\shot.png'), '/news/assets/2026/shot.png');
});

test('путь наружу отвергается целиком, а не чинится молча', () => {
  for (const bad of ['../secrets/x.png', 'a/../../b', '.', '']) {
    assert.strictEqual(N.normalizePath(bad), '', bad);
  }
});

test('картинка вставляется картинкой, остальное — ссылкой', () => {
  assert.strictEqual(N.insertMarkup('2026/shot.png'), '![shot.png](/news/assets/2026/shot.png)');
  assert.strictEqual(N.insertMarkup('guide.pdf'), '[guide.pdf](/news/assets/guide.pdf)');
  assert.strictEqual(N.insertMarkup('../x.png'), '');
});

test('вставка сохраняет то, что уже набрано', () => {
  assert.strictEqual(N.insertAt('раз два', 4, 'X'), 'раз Xдва');
  assert.strictEqual(N.insertAt('раз', 999, '!'), 'раз!');
  assert.strictEqual(N.insertAt('раз', -5, '!'), '!раз');
});
