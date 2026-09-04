// Редактор новостей в панели 2.0.
//
// Между «написал» и «опубликовал» теряется больше всего: в панели 1.0
// набранный текст жил только в поле, и закрытая вкладка стоила работы.
// Отсюда черновик на диске браузера — и требование, чтобы восстановление
// предлагалось, а не случалось само.

const test = require('node:test');
const assert = require('node:assert');

const N = require('../../server/admin_ui/v2/news.js');

/** Хранилище браузера в памяти, с возможностью сломаться. */
function storage(opts) {
  const o = opts || {};
  const map = new Map();
  return {
    map,
    getItem: (k) => (o.readThrows ? (() => { throw new Error('нет доступа'); })() : (map.has(k) ? map.get(k) : null)),
    setItem: (k, v) => {
      if (o.writeThrows) throw new Error('переполнено');
      map.set(k, v);
    },
    removeItem: (k) => map.delete(k),
  };
}

const post = (over) => Object.assign({ id: 'n1', title: 'Заголовок', body: 'Текст' }, over || {});

/* ---------- Проверки ---------- */

test('заполненная заметка сохраняется', () => {
  assert.deepStrictEqual(N.problems(post()), []);
  assert.strictEqual(N.canSave(post()), true);
});

test('без заголовка сохранять нельзя, и сказано почему', () => {
  const p = N.problems(post({ title: '' }));
  assert.strictEqual(p.length, 1);
  assert.strictEqual(p[0].field, 'title');
  assert.match(p[0].message, /сбоем загрузки/);
});

test('пустой текст — тоже замечание', () => {
  const p = N.problems(post({ body: '   ' }));
  assert.strictEqual(p[0].field, 'body');
  assert.strictEqual(N.canSave(post({ body: '' })), false);
});

test('пустой считается заметка без заголовка и без текста', () => {
  assert.strictEqual(N.isEmpty({ title: '', body: '' }), true);
  assert.strictEqual(N.isEmpty({ title: ' ', body: '\n' }), true);
  assert.strictEqual(N.isEmpty({ title: 'Есть', body: '' }), false);
  assert.strictEqual(N.isEmpty(null), true);
});

/* ---------- Что уезжает на сервер ---------- */

test('в запрос уходит только заполненное', () => {
  assert.deepStrictEqual(N.payload({ title: ' Заголовок ', body: 'Текст' }), {
    title: 'Заголовок', body: 'Текст',
  });
});

test('пустая игра означает новость лаунчера, а не игру с пустым именем', () => {
  assert.strictEqual(N.payload({ title: 'A', body: 'B', game: '' }).game, undefined);
  assert.strictEqual(N.payload({ title: 'A', body: 'B', game: 'repo' }).game, 'repo');
});

test('перенос строки в тексте не съедается', () => {
  const body = 'Первая\n\nВторая';
  assert.strictEqual(N.payload({ title: 'A', body }).body, body);
});

/* ---------- Черновик ---------- */

test('черновик пишется и читается', () => {
  const s = storage();
  assert.strictEqual(N.saveDraft(s, 'n1', post()), true);

  const d = N.readDraft(s, 'n1');
  assert.strictEqual(d.post.title, 'Заголовок');
  assert.ok(d.at > 0, 'у черновика должно быть время');
});

test('черновики разных заметок не смешиваются', () => {
  const s = storage();
  N.saveDraft(s, 'n1', post({ title: 'Первая' }));
  N.saveDraft(s, 'n2', post({ title: 'Вторая' }));
  assert.strictEqual(N.readDraft(s, 'n1').post.title, 'Первая');
  assert.strictEqual(N.readDraft(s, 'n2').post.title, 'Вторая');
});

test('новая заметка получает свой ключ, а не чужой', () => {
  assert.notStrictEqual(N.draftKey(''), N.draftKey('n1'));
  assert.match(N.draftKey(''), /new$/);
});

test('очищенное поле стирает черновик, а не сохраняет пустоту', () => {
  const s = storage();
  N.saveDraft(s, 'n1', post());
  N.saveDraft(s, 'n1', { title: '', body: '' });
  assert.strictEqual(N.readDraft(s, 'n1'), null);
});

test('мусор в хранилище равносилен его отсутствию', () => {
  const s = storage();
  s.map.set(N.draftKey('n1'), 'не json');
  assert.strictEqual(N.readDraft(s, 'n1'), null);
});

test('недоступное хранилище не роняет редактор', () => {
  // Приватный режим и переполнение — обычное дело, терять из-за них редактор нельзя
  assert.strictEqual(N.saveDraft(storage({ writeThrows: true }), 'n1', post()), false);
  assert.strictEqual(N.readDraft(storage({ readThrows: true }), 'n1'), null);
  assert.strictEqual(N.saveDraft(null, 'n1', post()), false);
  assert.strictEqual(N.readDraft(null, 'n1'), null);
});

test('черновик удаляется явно', () => {
  const s = storage();
  N.saveDraft(s, 'n1', post());
  N.dropDraft(s, 'n1');
  assert.strictEqual(N.readDraft(s, 'n1'), null);
  assert.doesNotThrow(() => N.dropDraft(null, 'n1'));
});

/* ---------- Восстановление ---------- */

test('восстановление предлагается только когда черновик отличается', () => {
  // Иначе панель предлагает восстановить ровно то, что уже открыто,
  // и это предложение перестают читать
  const same = { post: { title: 'Заголовок', body: 'Текст' } };
  assert.strictEqual(N.restorable(same, post()), false);

  const other = { post: { title: 'Заголовок', body: 'Другой текст' } };
  assert.strictEqual(N.restorable(other, post()), true);
});

test('черновик новой заметки предлагается, когда с сервера ничего нет', () => {
  const draft = { post: { title: 'Набросок', body: 'Текст' } };
  assert.strictEqual(N.restorable(draft, null), true);
});

test('отсутствующий черновик восстанавливать нечего', () => {
  assert.strictEqual(N.restorable(null, post()), false);
  assert.strictEqual(N.restorable({}, post()), false);
});

/* ---------- Вложения ---------- */

test('картинка распознаётся по расширению, а не по вере', () => {
  for (const n of ['a.png', 'b.JPG', 'c.jpeg', 'd.webp', 'e.svg', 'f.avif', 'g.gif']) {
    assert.strictEqual(N.isImage(n), true, n);
  }
  for (const n of ['a.zip', 'b.md', 'c', 'd.png.txt']) {
    assert.strictEqual(N.isImage(n), false, n);
  }
});

test('путь вложения приводится к виду, который откроется', () => {
  // Обратные слэши приезжают из проводника Windows, двойные — делают чужой хост
  assert.strictEqual(N.normalizePath('img\\cover.png'), 'img/cover.png');
  assert.strictEqual(N.normalizePath('./img/cover.png'), 'img/cover.png');
  assert.strictEqual(N.normalizePath('//evil.example/x.png'), 'evil.example/x.png');
  assert.strictEqual(N.normalizePath('/img//cover.png'), 'img/cover.png');
});

test('картинка вставляется картинкой, файл — ссылкой', () => {
  assert.strictEqual(N.insertMarkup('img/cover.png'), '![cover.png](img/cover.png)');
  assert.strictEqual(N.insertMarkup('files/guide.pdf'), '[guide.pdf](files/guide.pdf)');
});

test('подпись вложения можно задать', () => {
  assert.strictEqual(N.insertMarkup('img/a.png', 'Скриншот'), '![Скриншот](img/a.png)');
});

test('вставка идёт в позицию курсора и не съедает набранное', () => {
  assert.strictEqual(N.insertAt('раз два', 4, 'X'), 'раз Xдва');
  assert.strictEqual(N.insertAt('раз', 0, 'X'), 'Xраз');
  assert.strictEqual(N.insertAt('раз', 99, 'X'), 'разX');
  assert.strictEqual(N.insertAt('', 0, 'X'), 'X');
});

test('вставка переживает отсутствие позиции', () => {
  assert.strictEqual(N.insertAt('раз', null, 'X'), 'Xраз');
  assert.strictEqual(N.insertAt(null, 0, 'X'), 'X');
});
