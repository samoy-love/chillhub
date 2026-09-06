// Статика версии 2.0: шрифты, индексация, картинки.
//
// Всё это ломается молча. Шрифт с чужого домена не приезжает — заголовки
// съезжают на системную гарнитуру, и никакой ошибки нигде нет. Превью,
// открытое поиску, соревнуется с настоящей страницей за место в выдаче.
// Картинка, закрытая robots.txt, превращает ссылку на сайт в карточку
// без изображения. Ни одно из этих трёх не видно, пока не посмотришь.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const LANDING = path.join(ROOT, 'landing');
const ADMIN = path.join(ROOT, 'server', 'admin_ui');

const read = (...p) => fs.readFileSync(path.join(...p), 'utf8');
const PAGES = ['index.html', 'privacy.html', 'terms.html'];

/* ---------- Шрифты у себя ---------- */

test('страницы сайта не ходят за шрифтами на чужой домен', () => {
  // Там, где fonts.googleapis.com не отвечает, шрифт не приезжает молча
  for (const page of PAGES) {
    const html = read(LANDING, page);
    const links = html.match(/<link[^>]*>/g) || [];
    for (const link of links) {
      assert.ok(!/fonts\.(googleapis|gstatic)\.com/.test(link), page + ' тянет шрифты с чужого домена: ' + link);
    }
    assert.match(html, /vendor\/fonts\/fonts\.css/, page + ' не подключает свои шрифты');
  }
});

test('панель тоже держит шрифты у себя', () => {
  const html = read(ADMIN, 'index.html');
  const links = html.match(/<link[^>]*>/g) || [];
  for (const link of links) {
    assert.ok(!/fonts\.(googleapis|gstatic)\.com/.test(link), 'панель тянет шрифты с чужого домена');
  }
  assert.match(html, /vendor\/fonts\/fonts\.css/);
});

for (const [where, dir] of [['сайта', LANDING], ['панели', ADMIN]]) {
  test(`каждый шрифт ${where} лежит там, где на него ссылаются`, () => {
    const dirFonts = path.join(dir, 'vendor', 'fonts');
    const css = read(dirFonts, 'fonts.css');
    const files = [...css.matchAll(/url\('\.\/([^']+)'\)/g)].map((m) => m[1]);
    assert.ok(files.length > 0, 'в fonts.css нет ни одного шрифта');
    for (const f of files) {
      assert.ok(fs.existsSync(path.join(dirFonts, f)), 'нет файла шрифта: ' + f);
    }
  });

  test(`лишних шрифтов у ${where} не лежит`, () => {
    // Файл, на который никто не ссылается, уезжает на прод и висит там
    const dirFonts = path.join(dir, 'vendor', 'fonts');
    const css = read(dirFonts, 'fonts.css');
    for (const f of fs.readdirSync(dirFonts).filter((n) => n.endsWith('.woff2'))) {
      assert.ok(css.includes(f), 'шрифт лежит, но не подключён: ' + f);
    }
  });
}

test('переменная гарнитура объявлена диапазоном, а не тремя копиями', () => {
  // Три ссылки на один и тот же файл — это тот же файл, скачанный трижды
  for (const dir of [LANDING, ADMIN]) {
    const css = read(dir, 'vendor', 'fonts', 'fonts.css');
    const srcs = [...css.matchAll(/url\('\.\/([^']+)'\)/g)].map((m) => m[1]);
    assert.strictEqual(new Set(srcs).size, srcs.length, 'один файл подключён несколько раз: ' + dir);
  }
  assert.match(read(LANDING, 'vendor', 'fonts', 'fonts.css'), /font-weight: 400 600;/);
});

test('текст читается, пока шрифт едет', () => {
  // Без swap на медленной связи страница несколько секунд пустая
  for (const dir of [LANDING, ADMIN]) {
    const css = read(dir, 'vendor', 'fonts', 'fonts.css');
    const faces = css.split('@font-face').slice(1);
    for (const face of faces) {
      assert.match(face, /font-display:\s*swap/, 'шрифт без font-display: swap в ' + dir);
    }
  }
});

/* ---------- Индексация ---------- */

test('страницы открыты поиску', () => {
  // Пока сайт лежал превью рядом с настоящим, страницы были закрыты от
  // обхода: два адреса с одним текстом соревнуются между собой. Сайт
  // переехал в корень, соревноваться стало не с чем — закрытие обязано
  // было уехать тем же коммитом, иначе сайт просто пропадёт из поиска.
  for (const page of PAGES) {
    assert.doesNotMatch(read(LANDING, page), /content="noindex/, page + ' закрыт от поиска');
  }
});

test('страница называет своим адресом корень', () => {
  // Канонический адрес превью вёл на /v2/ — оставшись, он увёл бы поиск
  // на страницу, которой больше нет
  const html = read(LANDING, 'index.html');
  assert.match(html, /<link rel="canonical" href="https:\/\/launcher\.samoy\.love\/">/);
  assert.doesNotMatch(html, /launcher\.samoy\.love\/v2/);
});

/* ---------- Политика говорит о том, что есть ---------- */

test('политика приватности не обещает сторонних запросов, которых нет', () => {
  // Политика полгода уверяла, что шрифты идут с Google Fonts и Google
  // видит IP читателя. Шрифты давно лежат у себя. Ошибка в свою пользу
  // всё равно ошибка: политику читают как обязательство, а не как эссе
  const clean = (html) => html.replace(/<!--[\s\S]*?-->/g, '');
  const own = (u) => u.startsWith('https://launcher.samoy.love');
  const links = (html) => (clean(html).match(/https?:\/\/[^"'\s>]+/g) || []).filter((u) => !own(u));
  const third = PAGES.some((page) => links(read(LANDING, page)).length > 0);

  if (!third) {
    assert.doesNotMatch(read(LANDING, 'privacy.html'), /Google Fonts/, 'политика описывает запрос, которого нет');
  }
});

/* ---------- Стили оформляют то, что есть ---------- */

// Мёртвое правило в CSS не падает и не мешает — оно просто едет к каждому
// читателю страницы и живёт там годами. Так в стилях сайта пережил свой
// блок целый экран «Демо»: его заменил работающий эмулятор со своими
// классами и своим файлом, а сто семьдесят строк остались.
//
// Проверка грубая нарочно: класс считается живым, если его имя вообще
// встречается в коде, который эту страницу собирает. Так она не ловит
// класс, собранный из кусков строки, — зато не требует разбирать разметку
// и не врёт в другую сторону.

const cssClasses = (file) => {
  const css = read(...file).replace(/\/\*[\s\S]*?\*\//g, '');
  return [...new Set([...css.matchAll(/\.([a-zA-Z][\w-]*)/g)].map((m) => m[1]))];
};

const sourceOf = (dir, names) => names.map((n) => read(dir, n)).join('\n');

test('в стилях сайта нет классов, которых нет в коде', () => {
  const src = sourceOf(LANDING, ['index.html', 'privacy.html', 'terms.html', 'app.js', 'api.js']);
  for (const cls of cssClasses([LANDING, 'styles.css'])) {
    assert.ok(src.includes(cls), 'класс ничего не оформляет: .' + cls);
  }
});

test('в стилях панели нет классов, которых нет в коде', () => {
  const names = fs.readdirSync(ADMIN).filter((n) => n.endsWith('.js') || n.endsWith('.html'));
  const src = sourceOf(ADMIN, names);
  for (const cls of cssClasses([ADMIN, 'admin.css'])) {
    assert.ok(src.includes(cls), 'класс ничего не оформляет: .' + cls);
  }
});

/* ---------- Точка входа панели ---------- */

// ИМЕНА ЭТИХ ФАЙЛОВ — ДОГОВОР С КОНФИГОМ NGINX, КОТОРЫЙ ЛЕЖИТ В ДРУГОМ
// РЕПОЗИТОРИИ. Nginx отдаёт /admin/ точным совпадением и просит файл по
// имени; переименованная точка входа отвечает 404 — и только вошедшему:
// анонима редиректит на форму входа, а смоук после выкатки проверяет как
// раз форму входа. То есть выкатка остаётся зелёной, а панель не
// открывается. Так и случилось при переезде на 2.0.
//
// Здесь имена и закреплены. Тест не проверяет прод — он не даёт
// переименовать файл, не заметив, что правка обязана уехать и в
// deploy-kit тем же заходом.

test('точка входа панели названа так, как её просит nginx', () => {
  for (const name of ['index.html', 'login.html', 'login.js', 'app.ico', 'favicon.svg']) {
    assert.ok(fs.existsSync(path.join(ADMIN, name)), 'nginx просит этот файл по имени: ' + name);
  }
});

test('сервер админки отдаёт ту же точку входа, что и nginx', () => {
  // В деве статику раздаёт сам сервер, на проде — nginx. Разойдясь, они
  // дают «локально работает, на проде 404» — ровно тот случай, который
  // не ловится ничем до открытия панели руками
  const go = read(ROOT, 'server', 'cmd', 'admin', 'main.go');
  assert.match(go, /filepath\.Join\(uiDir, "index\.html"\)/, 'сервер отдаёт не index.html');
  assert.match(go, /filepath\.Join\(uiDir, "login\.html"\)/, 'сервер отдаёт анониму не login.html');
});

test('панель просит свои файлы по абсолютным адресам', () => {
  // СТРАНИЦА ЛЕЖИТ НЕ ТАМ, ГДЕ ЕЁ ФАЙЛЫ. Разметку отдают по /admin/, а
  // модули, стили и шрифты — по /admin/ui/. Относительный './views.js'
  // браузер разрешит от адреса страницы, попросит /admin/views.js и
  // получит HTML вместо скрипта: nginx на неизвестном пути отвечает
  // страницей, а браузер отказывается её исполнять по MIME.
  //
  // Так и вышло при переезде: панель открылась и осталась голой — ни
  // одного стиля, ни одного модуля. Проверить это снаружи нельзя (за
  // авторизацией), поэтому адреса закреплены здесь.
  const html = read(ADMIN, 'index.html');
  for (const [, url] of html.matchAll(/(?:src|href)="([^"#]+)"/g)) {
    assert.ok(
      url.startsWith('/admin/ui/'),
      'адрес разрешится от /admin/, а файл лежит в /admin/ui/: ' + url
    );
  }
});

test('каждый файл, который просит панель, лежит на месте', () => {
  const html = read(ADMIN, 'index.html');
  for (const [, url] of html.matchAll(/(?:src|href)="(\/admin\/ui\/[^"#]+)"/g)) {
    const rel = url.replace('/admin/ui/', '');
    assert.ok(fs.existsSync(path.join(ADMIN, rel)), 'панель просит несуществующий файл: ' + url);
  }
});

/* ---------- Страница входа ---------- */

// Она стоит особняком: её открывают БЕЗ сессии, и nginx отдаёт анониму
// лишь несколько файлов из /admin/ui/. Всё, что страница попросит сверх
// этого, вернётся 401 — молча, без единой ошибки на экране.

test('страница входа обходится без стороннего кода', () => {
  // На ней набирают пароль администратора. Чужой скрипт здесь выполняется
  // в её origin и видит поле пароля целиком
  const html = read(ADMIN, 'login.html');
  for (const tag of html.match(/<(script|link)[^>]*>/g) || []) {
    assert.ok(!/https?:\/\//.test(tag), 'страница входа тянет чужое: ' + tag);
  }
});

test('страница входа не просит того, чего анониму не отдадут', () => {
  // admin.css, шрифты и модули панели закрыты авторизацией: попросив их,
  // страница получит 401 и останется без оформления
  const html = read(ADMIN, 'login.html');
  const allowed = ['/admin/ui/login.js', '/admin/ui/app.ico', '/admin/ui/favicon.svg'];
  const asked = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map((m) => m[1]);
  for (const url of asked) {
    assert.ok(allowed.includes(url), 'страница входа просит закрытое: ' + url);
  }
  assert.ok(html.includes('<style>'), 'оформление вынесено наружу — анониму его не отдадут');
});

test('страница входа закрыта от поиска', () => {
  assert.match(read(ADMIN, 'login.html'), /content="noindex/);
});

test('картинка для карточки в мессенджерах открыта обходу', () => {
  const robots = read(ROOT, 'landing', 'robots.txt');
  const og = read(LANDING, 'index.html').match(/og:image" content="([^"]+)"/);
  assert.ok(og, 'у страницы нет og:image');

  const p = new URL(og[1]).pathname;
  const rules = robots
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => /^(Allow|Disallow):/.test(l))
    .map((l) => ({ allow: l.startsWith('Allow'), p: l.split(':')[1].trim() }))
    .filter((r) => p.startsWith(r.p));

  // Побеждает самое длинное правило — так читают robots.txt поисковики
  rules.sort((a, b) => b.p.length - a.p.length);
  assert.ok(rules.length && rules[0].allow, 'og:image закрыт robots.txt: ' + p);
});

/* ---------- Картинки ---------- */

test('у каждой картинки страницы есть размеры и подпись', () => {
  // Без width/height страница дёргается на догрузке, без alt — не читается
  const html = read(LANDING, 'index.html');
  for (const tag of html.match(/<img[^>]*>/g) || []) {
    assert.match(tag, /\salt=/, 'картинка без alt: ' + tag);
    if (!/data-fallback|data-src/.test(tag)) {
      assert.match(tag, /\swidth="\d+"/, 'картинка без ширины: ' + tag);
      assert.match(tag, /\sheight="\d+"/, 'картинка без высоты: ' + tag);
    }
  }
});

test('каждая своя картинка страницы существует', () => {
  const html = read(LANDING, 'index.html');
  for (const m of html.matchAll(/<img[^>]*\ssrc="(\.[^"]+)"/g)) {
    assert.ok(fs.existsSync(path.join(LANDING, m[1])), 'нет файла картинки: ' + m[1]);
  }
});

/* ---------- Скрытое ---------- */

test('скрытое остаётся скрытым, что бы ни задавали правила', () => {
  // `[hidden]` браузера слабее любого своего `display`, и спрятанная
  // строка показывалась подписью без значения: «размер», «собран» и пустота
  for (const [what, file] of [
    ['сайта', path.join(LANDING, 'styles.css')],
    ['панели', path.join(ADMIN, 'admin.css')],
  ]) {
    const css = fs.readFileSync(file, 'utf8');
    assert.match(css, /\[hidden\] \{ display: none !important; \}/, 'у ' + what + ' нет общего правила для скрытого');
  }
});

test('факты об установщике спрятаны в разметке, а не выдуманы', () => {
  // Свёрстанный хеш — это опубликованная рядом с кнопкой скачивания ложь
  const html = read(LANDING, 'index.html');
  for (const m of html.matchAll(/<li([^>]*data-setup="[^"]+"[^>]*)>(.*?)<\/li>/g)) {
    assert.match(m[1], /hidden/, 'факт об установщике показан до того, как приехал: ' + m[0]);
    assert.ok(!/[0-9a-f]{16}/i.test(m[2]), 'в разметке свёрстан хеш: ' + m[0]);
  }
});
