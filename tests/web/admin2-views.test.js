// Вид под длинные дела панели 2.0.
//
// Проверяется не «есть ли тег», а то, что человек прочтёт на экране в
// каждый момент долгого дела: началось оно или нет, докачка это или
// заливка с нуля, оборвался поток или сборка всё же дошла до конца.
// Именно эти состояния в панели 1.0 не показывались никак.

const test = require('node:test');
const assert = require('node:assert');

const V = require('../../server/admin_ui/v2/views.js');
const News = require('../../server/admin_ui/v2/news.js');
const Gallery = require('../../server/admin_ui/v2/gallery.js');
const Tuning = require('../../server/admin_ui/v2/tuning.js');

/* ---------- Оболочка ---------- */

test('лист называет дело и в заголовке, и в метке для читалки', () => {
  const html = V.sheet({ title: 'Загрузка сборки', lede: 'Файл не уйдёт игрокам сам' });
  assert.match(html, /<h2>Загрузка сборки<\/h2>/);
  assert.match(html, /aria-label="Загрузка сборки"/);
  assert.match(html, /aria-modal="true"/);
  assert.match(html, /Файл не уйдёт игрокам сам/);
});

test('подвал появляется только когда есть чем его занять', () => {
  assert.ok(!/<footer/.test(V.sheet({ title: 'x' })));
  assert.match(V.sheet({ title: 'x', foot: '<button></button>' }), /<footer/);
});

test('разметка внутри заголовка не исполняется', () => {
  const html = V.sheet({ title: '<img src=x onerror=alert(1)>' });
  assert.ok(!html.includes('<img'));
  assert.match(html, /&lt;img/);
});

/* ---------- Загрузка ---------- */

test('до выбора файла экран честно говорит, что дела ещё нет', () => {
  assert.strictEqual(V.uploadStatus({}).text, 'Файл ещё не выбран');
  assert.strictEqual(V.uploadStatus(null).text, 'Файл ещё не выбран');
});

test('докачка называется докачкой, а не заливкой с нуля', () => {
  // Иначе после обрыва человек уверен, что половина работы пропала
  const s = V.uploadStatus({ phase: 'upload', resumed: 148, total: 300, progress: 0.49 });
  assert.match(s.text, /Докачка: 148 из 300 кусков уже на сервере/);
  assert.match(s.text, /49/);
});

test('заливка с нуля показывает, сколько кусков уже дошло', () => {
  const s = V.uploadStatus({ phase: 'upload', done: 12, total: 300, progress: 0.04 });
  assert.match(s.text, /Заливка: 12 из 300/);
  assert.ok(!/Докачка/.test(s.text));
});

test('повтор сорвавшихся кусков — не ошибка, но и не тишина', () => {
  const s = V.uploadStatus({ phase: 'retry', count: 3 });
  assert.strictEqual(s.tone, 'warn');
  assert.match(s.text, /3/);
});

test('после сборки файла есть ещё разбор архива — это отдельный шаг', () => {
  // Он занимает минуты, и без своей строки выглядит как зависание
  assert.match(V.uploadStatus({ phase: 'complete' }).text, /Собираем файл на сервере/);
  assert.match(V.uploadStatus({ phase: 'process' }).text, /Разбираем архив/);
});

test('успех не выдаёт загрузку за публикацию', () => {
  const s = V.uploadStatus({ phase: 'done' });
  assert.strictEqual(s.tone, 'ok');
  assert.match(s.text, /Игрокам версия пока не ушла/);
});

test('провал называет причину, а не только факт', () => {
  const s = V.uploadStatus({ phase: 'failed', message: 'не удалось залить 4 из 300' });
  assert.strictEqual(s.tone, 'bad');
  assert.match(s.text, /4 из 300/);
});

test('отмена обещает, что за собой убрали', () => {
  assert.match(V.uploadStatus({ phase: 'aborted' }).text, /убрано с сервера/);
});

test('пока куски летят, предлагается только отмена', () => {
  // «Закрыть» здесь прочитали бы как «свернуть», а лист загрузку прерывает
  const b = V.uploadButtons({ phase: 'upload' });
  assert.deepStrictEqual(b.map((x) => x.act), ['abort']);
  assert.strictEqual(b[0].danger, true);
});

test('после провала можно повторить, после успеха — только закрыть', () => {
  assert.deepStrictEqual(V.uploadButtons({ phase: 'failed' }).map((x) => x.act), ['retry', 'close']);
  assert.deepStrictEqual(V.uploadButtons({ phase: 'aborted' }).map((x) => x.act), ['retry', 'close']);
  assert.deepStrictEqual(V.uploadButtons({ phase: 'done' }).map((x) => x.act), ['close']);
  assert.deepStrictEqual(V.uploadButtons({}).map((x) => x.act), ['pick']);
});

test('подобранные кусок и потоки видны до нажатия, а не после', () => {
  const html = V.uploadCard({
    phase: 'upload',
    file: { name: 'ChillHub-1.6.25.zip', size: 121400000 },
    chunkSize: 8 * 1024 * 1024,
    streams: 4,
    total: 15,
    done: 3,
    progress: 0.2,
  });
  assert.match(html, /ChillHub-1\.6\.25\.zip/);
  assert.match(html, /8\u00a0МБ/);
  assert.match(html, />4</);
});

test('полоска прогресса называет себя читалке', () => {
  const html = V.uploadCard({ phase: 'upload', file: { name: 'a.zip', size: 10 }, progress: 0.37 });
  assert.match(html, /role="progressbar"/);
  assert.match(html, /aria-valuenow="37"/);
});

test('без файла полоски нет вовсе', () => {
  const html = V.uploadCard({});
  assert.ok(!/progressbar/.test(html));
  assert.match(html, /Файл ещё не выбран/);
});

/* ---------- Журнал сборки ---------- */

test('до первой строки журнал говорит, что сборка началась', () => {
  // Первые секунды тишины иначе читаются как «кнопка не нажалась»
  assert.match(V.buildLog([], 'running'), /Сборка началась/);
  assert.match(V.buildLog([], 'idle'), /Журнала пока нет/);
});

test('строки журнала различимы по роду', () => {
  assert.match(V.logRow({ kind: 'error', message: 'нет пакета' }), /log-row err/);
  assert.match(V.logRow({ kind: 'done', message: 'готово' }), /log-row ok/);
  assert.match(V.logRow({ kind: 'warn', message: 'старая версия' }), /log-row warn/);
});

test('строка без рода не ломает разметку', () => {
  const html = V.logRow({ message: 'что-то' });
  assert.match(html, /class="log-row"/);
  assert.match(html, /info/);
});

test('сообщение сервера не исполняется как разметка', () => {
  // Текст в журнал приходит с сервера и в общем случае произвольный
  const html = V.logRow({ kind: 'info', message: '<script>alert(1)</script>' });
  assert.ok(!html.includes('<script>'));
});

test('оборванный поток не выдаётся за провал', () => {
  // Сборка могла досчитаться и умереть на последней строке
  const o = V.buildOutcome({ ok: false, kind: 'buffered' });
  assert.strictEqual(o.tone, 'warn');
  assert.match(o.text, /могла дойти до конца/);
});

test('успешная сборка не выдаётся за выкатку игрокам', () => {
  const o = V.buildOutcome({ ok: true });
  assert.strictEqual(o.tone, 'ok');
  assert.match(o.text, /отдельное решение/);
});

test('отказ от пропавших пакетов — не ошибка', () => {
  const o = V.buildOutcome({ cancelled: true, kind: 'missing' });
  assert.strictEqual(o.tone, 'warn');
  assert.match(o.text, /остались в списке/);
});

test('настоящая ошибка называет причину', () => {
  const o = V.buildOutcome({ ok: false, kind: 'error', message: 'сервер не отвечает' });
  assert.strictEqual(o.tone, 'bad');
  assert.match(o.text, /сервер не отвечает/);
});

/* ---------- Новость ---------- */

test('ошибка поля стоит рядом со своим полем', () => {
  const post = { slug: '-плохо', markdown: '# Т\n\nтекст' };
  const html = V.newsForm(post, News.problems(post));
  const beforeGame = html.slice(0, html.indexOf('n-game'));
  assert.match(beforeGame, /help--bad/);
});

test('поля новости — те, что знает сервер, и заголовка среди них нет', () => {
  // Отдельное поле «Заголовок» было бы враньём: он берётся первой строкой текста
  const html = V.newsForm({ slug: 'release', markdown: '# Т' }, []);
  for (const f of ['slug', 'gameId', 'coverUrl', 'markdown']) {
    assert.match(html, new RegExp('name="' + f + '"'), 'нет поля ' + f);
  }
  assert.ok(!/name="title"/.test(html), 'в форме есть поле, которого сервер не знает');
});

test('у существующей заметки имя не правят: оно уже в адресе статьи', () => {
  assert.match(V.newsForm({ slug: 'release', existing: true }, []), /name="slug"[^>]*readonly/);
  assert.ok(!/name="slug"[^>]*readonly/.test(V.newsForm({ slug: '' }, [])));
});

test('видно, какой заголовок прочтёт сервер', () => {
  // Заголовок здесь не поле, а первая строка — без подсказки это не увидеть
  assert.match(V.newsHeadline('# Вышла 1.6.25', News), /Вышла 1\.6\.25/);
  assert.match(V.newsHeadline('без решётки', News), /Заголовка нет/);
});

test('пустая игра объясняется, а не остаётся загадкой', () => {
  const html = V.newsForm({ title: 'x', body: 'y' }, []);
  assert.match(html, /новость про лаунчер, а не про игру/);
});

test('текст новости попадает в поле, а не в разметку страницы', () => {
  const html = V.newsForm({ slug: '</textarea><script>', markdown: '"><b>' }, []);
  assert.ok(!html.includes('<script>'));
  assert.ok(!html.includes('value="</textarea>'));
  assert.match(html, /&lt;\/textarea&gt;/);
});

test('черновик предлагается вернуть, только когда он отличается', () => {
  const same = V.draftNote({ post: { markdown: '# Т\n\nтекст' } }, { markdown: '# Т\n\nтекст' }, News);
  assert.strictEqual(same, '');
  const diff = V.draftNote({ post: { markdown: '# Т\n\nдругое' } }, { markdown: '# Т\n\nтекст' }, News);
  assert.match(diff, /data-draft-restore/);
  assert.match(diff, /data-draft-drop/);
});

/* ---------- Галерея ---------- */

test('текущая папка в крошках — не ссылка', () => {
  const html = V.galleryCrumbs('screens/2026', Gallery);
  assert.match(html, /<span aria-current="page">2026<\/span>/);
  assert.match(html, /data-go="screens"/);
});

test('обложка помечена прямо в списке', () => {
  // Иначе узнать, что попадёт на витрину, можно было только на другой вкладке
  const html = V.galleryList([{ name: 'cover.png', size: 100 }, { name: 'other.png', size: 200 }], {
    cover: 'cover.png',
    gallery: Gallery,
  });
  const coverRow = html.slice(html.indexOf('data-name="cover.png"'), html.indexOf('data-name="other.png"'));
  assert.match(coverRow, /badge--accent">обложка/);
  assert.ok(!/data-cover="cover.png"/.test(html));
});

test('обложкой предлагают сделать только картинку', () => {
  const html = V.galleryList([{ name: 'guide.pdf', size: 10 }], { gallery: Gallery });
  assert.ok(!/data-cover/.test(html));
  assert.match(html, /data-remove="guide.pdf"/);
});

test('в папку можно зайти, но не сделать её обложкой', () => {
  const html = V.galleryList([{ name: 'screens', dir: true }], { path: '', gallery: Gallery });
  assert.match(html, /data-go="screens"/);
  assert.ok(!/data-cover/.test(html));
});

test('папки идут выше файлов и в разметке тоже', () => {
  const html = V.galleryList([{ name: 'a.png' }, { name: 'screens', dir: true }], { gallery: Gallery });
  assert.ok(html.indexOf('data-name="screens"') < html.indexOf('data-name="a.png"'));
});

test('пустая папка объясняет, что с ней делать', () => {
  assert.match(V.galleryList([], { gallery: Gallery }), /Перетащите сюда файлы/);
});

/* ---------- Порядок игр ---------- */

test('у первой строки нет «выше», у последней — «ниже»', () => {
  const html = V.orderList([{ gameId: 'a', title: 'А' }, { gameId: 'b', title: 'Б' }]);
  const first = html.slice(html.indexOf('data-id="a"'), html.indexOf('data-id="b"'));
  assert.match(first, /data-up="a" aria-label="Выше" disabled/);
  const last = html.slice(html.indexOf('data-id="b"'));
  assert.match(last, /data-down="b" aria-label="Ниже" disabled/);
});

test('перестановка доступна и мышью, и кнопками', () => {
  // С клавиатуры в перетаскивание не попадают вовсе
  const html = V.orderList([{ gameId: 'a' }, { gameId: 'b' }]);
  assert.match(html, /draggable="true"/);
  assert.match(html, /data-up="b"/);
});

test('игра без названия показывается по своему коду', () => {
  assert.match(V.orderList([{ gameId: 'repo' }]), />repo</);
});

test('неизменный порядок не предлагают сохранять', () => {
  const l = [{ gameId: 'a' }, { gameId: 'b' }];
  const s = V.orderSummary(l, l.slice());
  assert.strictEqual(s.changed, false);
  assert.match(s.text, /сохранять нечего/);
});

test('изменённый порядок называет последствие для игрока', () => {
  const s = V.orderSummary([{ gameId: 'a' }, { gameId: 'b' }], [{ gameId: 'b' }, { gameId: 'a' }]);
  assert.strictEqual(s.changed, true);
  assert.match(s.text, /2\u00a0строки/);
  assert.match(s.text, /Игроки увидят новый порядок сразу/);
});

test('пустые списки не считаются изменением', () => {
  assert.strictEqual(V.orderSummary(null, null).changed, false);
});

/* ---------- Прогоны ---------- */

test('лучший прогон помечен, и применять его повторно не предлагают', () => {
  const html = V.benchTable(
    [
      { chunk: '8 МиБ', streams: 4, mbps: 92.4, retries: 0 },
      { chunk: '2 МиБ', streams: 8, mbps: 79.3, retries: 3 },
    ],
    Tuning
  );
  assert.match(html, /class="best"/);
  assert.match(html, /badge--ok">выбрано/);
  assert.match(html, /data-apply="2 МиБ"/);
  assert.ok(!/data-apply="8 МиБ"/.test(html));
});

test('под таблицей стоит объяснение выбора', () => {
  // Без него подбор выглядит гаданием, и его результату не верят
  const html = V.benchTable([{ chunk: '2 МиБ', streams: 8, mbps: 79.3, retries: 3 }, { chunk: '8 МиБ', streams: 4, mbps: 74 }], Tuning);
  assert.match(html, /Быстрее всех/);
});

test('скорость в таблице пишется по-русски', () => {
  const html = V.benchTable([{ chunk: '8 МиБ', streams: 4, mbps: 10.5 }], Tuning);
  assert.match(html, /10,5/);
  assert.ok(!/10\.5/.test(html));
});

test('без прогонов таблица объясняет, чего стоит прогон', () => {
  const html = V.benchTable([], Tuning);
  assert.match(html, /около минуты и ничего не публикует/);
});
