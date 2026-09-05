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

/* Решение о хвосте журнала принимает модуль 1.0 — панель 2.0
   переиспользует его как есть. */
const Logs = (() => {
  const fs = require('node:fs');
  const vm = require('node:vm');
  const path = require('node:path');
  const sandbox = { window: {} };
  vm.createContext(sandbox);
  vm.runInContext(
    fs.readFileSync(path.join(__dirname, '..', '..', 'server/admin_ui/feedback-logs.js'), 'utf8'),
    sandbox
  );
  return sandbox.window;
})();

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

/* ---------- Состав сборки ---------- */

const Mods = require('../../server/admin_ui/v2/mods.js');

test('план сборки называет пропавшие пакеты поимённо и до сборки', () => {
  // Узнать о них на середине выкатки — значит откатывать уже отданное
  const html = V.resolvePlan(
    { displayName: 'Модпак', version: '1.9.9', packages: 17, totalBytes: 1024 ** 3, missing: ['Ura/Old'] },
    Mods
  );
  assert.match(html, /Ura\/Old/);
  assert.match(html, /больше нет на Thunderstore/);
  assert.match(html, /Собрать без них можно/);
});

test('без пропавших про них и не говорится', () => {
  const html = V.resolvePlan({ displayName: 'М', packages: 3, totalBytes: 10 }, Mods);
  assert.ok(!/больше нет на Thunderstore/.test(html));
});

test('нехватка места в плане показана как беда, а не как справка', () => {
  const html = V.resolvePlan({ packages: 3, totalBytes: 10, spaceOk: false, spaceNote: 'мало места' }, Mods);
  assert.match(html, /note--bad/);
  assert.match(html, /мало места/);
});

/* ---------- Каталог ---------- */

test('каталог показывает пакет с его пространством имён и версией', () => {
  const html = V.catalogList([{ owner: 'Ura', name: 'Modpack', version_number: '1.2.3', download_count: 4200 }], {
    mods: Mods,
  });
  assert.match(html, /Modpack/);
  assert.match(html, /Ura/);
  assert.match(html, /1\.2\.3/);
  assert.match(html, /data-take/);
  assert.match(html, /data-readme/);
});

test('пустой каталог объясняет, что делать со ссылкой', () => {
  // Половина модпаков в раздел «Modpacks» не проставлена и не находится
  const html = V.catalogList([], { query: 'нет такого', mods: Mods });
  assert.match(html, /По запросу ничего нет/);
  assert.match(html, /подставляют ссылкой/);
});

/* ---------- Журналы ---------- */

test('отсутствующий журнал — не ошибка', () => {
  const html = V.logsView('', Logs.feedbackLogsView);
  assert.match(html, /Журнала нет/);
  assert.match(html, /обращение от этого не хуже/);
});

test('журнал не исполняется как разметка', () => {
  // Текст пишет игрок, и в нём бывает что угодно
  const html = V.logsView('<script>alert(1)</script>', Logs.feedbackLogsView);
  assert.ok(!html.includes('<script>'));
});

test('длинный журнал обрезается с конца, а не с начала', () => {
  // Авария всегда в конце, а начало — загрузка лаунчера, одинаковая у всех
  const long = 'строка загрузки\n'.repeat(20000) + 'ВОТ ЗДЕСЬ СЛОМАЛОСЬ';
  const html = V.logsView({ logs: long }, Logs.feedbackLogsView);
  assert.ok(html.includes('ВОТ ЗДЕСЬ СЛОМАЛОСЬ'), 'потерян конец журнала');
  assert.ok(html.length < long.length, 'журнал не обрезан вовсе');
});

test('подпись обрезанного журнала называет полный объём, а не показанный', () => {
  // Иначе «64 КБ» читается как весь журнал, и в файл никто не полезет
  const long = 'x'.repeat(300 * 1024);
  const html = V.logsView({ logs: long }, Logs.feedbackLogsView);
  assert.match(html, /300 КБ/);
  assert.match(html, /показан конец/);
});

test('короткий журнал показывается целиком и без оговорок', () => {
  const html = V.logsView({ logs: 'две строки\nвсего' }, Logs.feedbackLogsView);
  assert.match(html, /две строки/);
  assert.ok(!/показан конец/.test(html));
});

/* ---------- Переезд со старой сборки ---------- */

test('разобранный профиль не выдаётся за выкатку игрокам', () => {
  const html = V.importResult({ version: '1.9.9', packages: 17 });
  assert.match(html, /1\.9\.9/);
  assert.match(html, /игрокам не ушла/);
});

/* ---------- Подсказка из Thunderstore ---------- */

test('выбор игры объясняет, что вписывать во второе поле', () => {
  const html = V.ecosystemPicker([{ gameId: 'repo', title: 'R.E.P.O.' }]);
  assert.match(html, /name="gameId"/);
  assert.match(html, /name="slug"/);
  assert.match(html, /lethal-company/);
});

test('без игр выбирать не из чего, и так и сказано', () => {
  assert.match(V.ecosystemPicker([]), /Сначала добавьте игру/);
});

/* ---------- График ---------- */

test('ряд превращается в точки от нуля до высоты', () => {
  const p = V.sparkPoints([0, 5, 10], 100, 50);
  assert.strictEqual(p, '0.0,50.0 50.0,25.0 100.0,0.0');
});

test('ряд из одних нулей рисуется по низу, а не в NaN', () => {
  // Так выглядит первый день после чистки метрик, и SVG на NaN молчит
  const p = V.sparkPoints([0, 0, 0], 100, 50);
  assert.ok(!/NaN/.test(p), p);
  assert.strictEqual(p, '0.0,50.0 50.0,50.0 100.0,50.0');
});

test('один день не превращает шаг в бесконечность', () => {
  const p = V.sparkPoints([7], 100, 50);
  assert.ok(!/NaN|Infinity/.test(p), p);
  assert.strictEqual(p, '0.0,0.0');
});

test('пустой ряд не рисует битый тег', () => {
  assert.strictEqual(V.sparkPoints([], 100, 50), '');
  assert.strictEqual(V.sparkLine([], { width: 100, height: 50 }), '');
  assert.strictEqual(V.sparkPoints(null, 100, 50), '');
});

test('дырка в данных считается нулём, а не ломает весь график', () => {
  // Одно undefined в ряду делало NaN каждую точку разом
  const p = V.sparkPoints([10, undefined, 5], 100, 50);
  assert.ok(!/NaN/.test(p), p);
});

test('ломаная называет свой цвет и не исполняет его как разметку', () => {
  const html = V.sparkLine([1, 2], { width: 10, height: 10, color: 'var(--ember)' });
  assert.match(html, /stroke="var\(--ember\)"/);
  assert.ok(!/</.test(V.sparkLine([1, 2], { width: 10, height: 10, color: '"><script>' }).split('points=')[1] || ''));
});

/* ---------- Разница модпаков ---------- */

test('разница модпака показывает, что стало с каждым модом', () => {
  // «Какие моды изменились» — вопрос, на который список из полутора
  // сотен полных имён до и после не отвечает
  const html = V.modsDiff([
    { package: 'Ura/Core', change: 'updated', from: '1.0.0', to: '1.1.0' },
    { package: 'Ura/New', change: 'added', to: '2.0.0' },
    { package: 'Ura/Gone', change: 'removed', from: '0.9.0' },
  ]);
  assert.match(html, /Ura\/Core/);
  assert.match(html, /1\.0\.0 → 1\.1\.0/);
  assert.match(html, /1 появилось/);
  assert.match(html, /1 обновилось/);
  assert.match(html, /1 пропало/);
});

test('пустые разряды в сводке не перечисляются', () => {
  // «0 пропало» не по-русски и мешает увидеть то, что изменилось
  const html = V.modsDiff([{ package: 'A', change: 'added', to: '1.0' }]);
  assert.match(html, /1 появилось/);
  assert.ok(!/0 /.test(html.split('note">')[1] || ''));
});

test('одинаковый состав назван одинаковым, а не пустотой', () => {
  assert.match(V.modsDiff([]), /Состав не изменился/);
  assert.match(V.modsDiff([]), /не поменяется ничего/);
});

test('имя пакета с сервера не исполняется как разметка', () => {
  assert.ok(!V.modsDiff([{ package: '<script>x</script>', change: 'added' }]).includes('<script>'));
});

/* ---------- Обложка заметки ---------- */

test('обложку файлом предлагают только у сохранённой заметки', () => {
  // Сервер кладёт её рядом с заметкой, а той ещё нет
  assert.match(V.newsForm({ slug: 'release', existing: true }, []), /data-flow="cover"/);
  const fresh = V.newsForm({ slug: '' }, []);
  assert.ok(!/data-flow="cover"/.test(fresh));
  assert.match(fresh, /после первого сохранения/);
});

/* ---------- События одного кода ---------- */

test('события кода сначала группируются, потом перечисляются', () => {
  // Если весь код собрался на одной версии клиента, чинить надо её
  const html = V.errorEvents({
    items: [
      { ts: '2026-09-04T10:00:00Z', appVersion: '1.6.25', gameId: 'repo', event: 'update' },
      { ts: '2026-09-04T11:00:00Z', appVersion: '1.6.25', gameId: 'peak', event: 'install' },
    ],
  });
  assert.match(html, /Версии клиента/);
  assert.match(html, /1\.6\.25 · 2/);
  assert.match(html, /repo/);
});

test('пустой список объясняется, а не выглядит поломкой', () => {
  assert.match(V.errorEvents({ items: [] }), /Событий не осталось/);
  assert.match(V.errorEvents(null), /метрики чистили/);
});

test('обрезанный список честно назван обрезанным', () => {
  const html = V.errorEvents({ items: [{ ts: '', appVersion: 'x' }], capped: true });
  assert.match(html, /их было больше/);
});

test('поле события не исполняется как разметка', () => {
  assert.ok(!V.errorEvents({ items: [{ gameId: '<script>x</script>' }] }).includes('<script>'));
});

/* ---------- Технические работы ---------- */

test('форма работ спрашивает всё, что понимает сервер', () => {
  const html = V.maintForm({ reason: 'переносим сборки', blocks: { launch: true } });
  for (const n of ['reason', 'startsAt', 'endsAt', 'install', 'update', 'launch']) {
    assert.match(html, new RegExp('name="' + n + '"'), 'нет поля ' + n);
  }
  assert.match(html, /переносим сборки/);
});

test('запуск игр по умолчанию оставляют открытым', () => {
  // Игра стартует локально и серверу не мешает
  const html = V.maintForm({ blocks: {} });
  const launch = html.slice(html.indexOf('name="launch"'), html.indexOf('name="launch"') + 40);
  assert.ok(!/checked/.test(launch), 'запуск закрыт по умолчанию');
});

test('время показывается местное, а уезжает в UTC', () => {
  // Показывать UTC тому, кто назначает работы на свой вечер, — способ
  // ошибиться на три часа
  const local = V.localTime('2026-09-05T18:00:00Z');
  assert.match(local, /^2026-09-05T\d{2}:\d{2}$/);
  assert.strictEqual(V.isoTime(local), '2026-09-05T18:00:00.000Z');
});

test('пустое и битое время не превращается в дату', () => {
  assert.strictEqual(V.localTime(''), '');
  assert.strictEqual(V.localTime('не дата'), '');
  assert.strictEqual(V.isoTime(''), '');
  assert.strictEqual(V.isoTime('не дата'), '');
});

test('работы, которые ничего не закрывают, названы бессмысленными', () => {
  assert.match(V.maintProblem({ enabled: true, blocks: {} }), /ничего и не делают/);
  assert.strictEqual(V.maintProblem({ enabled: true, blocks: { install: true } }), '');
});

test('окно наоборот ловится до нажатия', () => {
  const bad = V.maintProblem({
    enabled: true,
    blocks: { install: true },
    startsAt: '2030-01-01T20:00:00Z',
    endsAt: '2030-01-01T10:00:00Z',
  });
  assert.match(bad, /позже начала/);
});

test('выключение работ проверок не требует', () => {
  assert.strictEqual(V.maintProblem({ enabled: false, blocks: {} }), '');
});

/* ---------- Скорость и остаток ---------- */

test('во время заливки видно скорость и сколько ещё ждать', () => {
  // Заливка на 1,8 ГБ идёт минутами, и один процент не отвечает на
  // единственный вопрос, который в это время задают
  const s = V.uploadStatus({
    phase: 'upload',
    done: 40,
    total: 300,
    progress: 0.13,
    speed: 11 * 1024 * 1024,
    left: 1.5 * 1024 ** 3,
  });
  assert.match(s.text, /11\u00a0МБ\/с/);
  assert.match(s.text, /осталось/);
});

test('пока скорость неизвестна, остаток не выдумывается', () => {
  // Оценщик молчит первые секунды намеренно: там не скорость канала, а
  // байты, принятые буферами, и по ним остаток получается втрое меньше
  const s = V.uploadStatus({ phase: 'upload', done: 1, total: 300, progress: 0.003 });
  assert.ok(!/осталось/.test(s.text), s.text);
  assert.ok(!/МБ\/с/.test(s.text), s.text);
});

test('скорость без остатка показывается, остаток без скорости — нет', () => {
  const onlySpeed = V.uploadStatus({ phase: 'upload', progress: 0.5, speed: 5 * 1024 * 1024 });
  assert.match(onlySpeed.text, /5\u00a0МБ\/с/);
  assert.ok(!/осталось/.test(onlySpeed.text));

  const onlyLeft = V.uploadStatus({ phase: 'upload', progress: 0.5, left: 1000 });
  assert.ok(!/осталось/.test(onlyLeft.text));
});

/* ---------- Версии модпака ---------- */

test('список версий отмечает ту, что у игроков, и не даёт её удалить', () => {
  // Удалив активную, оставишь игроков без модпака посреди сессии
  const html = V.modVersions(
    [
      { version: '1.9.9', createdAt: '2026-09-01T10:00:00Z', packages: 17, bytes: 251000000 },
      { version: '1.9.8', packages: 16, bytes: 250000000 },
    ],
    { active: '1.9.8', gameId: 'repo' }
  );
  const args = [...html.matchAll(/data-act="mods\.delete" data-args='([^']+)'/g)].map((m) => JSON.parse(m[1]).version);
  assert.deepStrictEqual(args, ['1.9.9'], 'удалять предложили активную версию');
  assert.match(html, /у игроков/);
});

test('пропавшие моды названы поимённо, а не числом', () => {
  // «Пропущено 2» не говорит, потерялся ли твик текстур или сам модпак
  const html = V.modVersions([{ version: '1.9.9', missing: ['Ura/Old', 'Ura/Gone'] }], { gameId: 'g' });
  assert.match(html, /Ura\/Old, Ura\/Gone/);
  assert.match(html, /собрана без/);
});

test('без единой сборки список объясняет, что делать', () => {
  const html = V.modVersions([], { gameId: 'g' });
  assert.match(html, /Собранных версий нет/);
  assert.match(html, /игрокам она сама не уйдёт/);
});

test('версия и игра не исполняются как разметка', () => {
  const html = V.modVersions([{ version: '"><script>x</script>' }], { gameId: '"><b>' });
  assert.ok(!html.includes('<script>'));
  assert.ok(!html.includes('<b>'));
});

/* ---------- График ---------- */

test('у графика подписаны обе оси', () => {
  // Ломаная без подписей не отвечает даже на «84 или 8400», и по ней
  // нельзя сказать, три дня она покрывает или девяносто
  const html = V.chart([{ title: 'запуски', color: 'var(--ember)', values: [10, 40, 84] }], {
    from: '06.08',
    to: '05.09',
  });
  assert.match(html, /84/);
  assert.match(html, /42/, 'нет середины шкалы');
  assert.match(html, />0</, 'нет нуля');
  assert.match(html, /06\.08/);
  assert.match(html, /05\.09/);
});

test('ряды делят один масштаб, а не каждый свой', () => {
  // В своём масштабе редкие ошибки выглядят такими же частыми, как запуски
  const html = V.chart(
    [
      { title: 'много', color: 'a', values: [0, 100] },
      { title: 'мало', color: 'b', values: [0, 1] },
    ],
    { width: 100, height: 50 }
  );
  const lines = [...html.matchAll(/points="([^"]+)"/g)].map((m) => m[1]);
  assert.strictEqual(lines.length, 2);
  assert.ok(lines[0].endsWith('0.0'), 'верхняя точка большого ряда не наверху: ' + lines[0]);
  assert.ok(!lines[1].endsWith('0.0'), 'маленький ряд нарисован в своём масштабе: ' + lines[1]);
});

test('в легенде названы все ряды', () => {
  const html = V.chart(
    [
      { title: 'запуски игр', color: 'a', values: [1] },
      { title: 'ошибки', color: 'b', values: [1] },
    ],
    {}
  );
  assert.match(html, /запуски игр/);
  assert.match(html, /ошибки/);
});

test('пустой период объясняется, а не рисует пустую рамку', () => {
  assert.match(V.chart([], {}), /Событий за период нет/);
  assert.match(V.chart([{ title: 'x', values: [] }], {}), /метрики чистили/);
});

test('график называет себя читалке', () => {
  const html = V.chart([{ title: 'x', color: 'a', values: [1, 2] }], { label: 'Запуски за 30 дней' });
  assert.match(html, /aria-label="Запуски за 30 дней"/);
});
