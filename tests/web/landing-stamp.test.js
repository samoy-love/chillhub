// Сборка лендинга для выкатки (scripts/landing-stamp.mjs). Ломается она молча:
// забытая ссылка без хеша возвращает ровно ту беду, ради которой всё затеяно, —
// сайт после выкатки ещё пять минут показывает старые стили, а красным это не
// горит нигде.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

let stamp;
test.before(async () => {
  ({ stamp } = await import('../../scripts/landing-stamp.mjs'));
});

// Та же регулярка, по которой nginx отдаёт файл с кешем на год
// (deploy-kit, nginx/sites/chillhub-launcher.conf, блок с {8,}).
const NGINX_HASHED = /\.[a-f0-9]{8,}\.(?:css|js|mjs|map|png|jpg|jpeg|webp|gif|svg|ico|woff2?|ttf|eot)$/i;

function tmp() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'stamp-'));
}

function write(root, rel, body) {
  const file = path.join(root, rel);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, body);
}

function fixture() {
  const src = tmp();
  write(src, 'index.html', [
    '<link rel="preload" href="./fonts/a.woff2">',
    '<link rel="stylesheet" href="./fonts/fonts.css">',
    '<link rel="stylesheet" href="./styles.css">',
    '<img src="/img/shot.png?x=1#top">',
    '<script src="app.js"></script>',
    '<a href="./other.html">x</a>',
    '<a href="/downloads/Setup.exe">x</a>',
    '<a href="https://example.test/styles.css">x</a>',
    '<meta property="og:image" content="https://example.test/img/shot.png">',
  ].join('\n'));
  write(src, 'other.html', '<link rel="stylesheet" href="./styles.css">');
  write(src, 'styles.css', 'body{color:red}');
  write(src, 'app.js', 'console.log(1)');
  write(src, 'img/shot.png', 'PNG');
  write(src, 'fonts/a.woff2', 'FONT-A');
  write(src, 'fonts/fonts.css', "@font-face{src:url('./a.woff2') format('woff2')}");
  return src;
}

test('ссылки страниц переписаны на имена с хешем, а сами файлы лежат рядом', () => {
  const src = fixture();
  const out = tmp();
  stamp(src, out);
  const html = fs.readFileSync(path.join(out, 'index.html'), 'utf8');

  const refs = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map((m) => m[1]);
  for (const ref of refs) {
    if (/^https?:|\.html$|^\/downloads\//.test(ref)) continue;
    const clean = ref.split(/[?#]/)[0];
    assert.match(clean, NGINX_HASHED, `ссылка без хеша: ${ref}`);
    const file = path.join(out, clean.replace(/^\.?\//, ''));
    assert.ok(fs.existsSync(file), `нет файла ${clean}`);
  }

  // Вид ссылки сохранён: ./, / и голое имя остаются собой, хвост ?#… — тоже.
  assert.match(html, /href="\.\/styles\.[a-f0-9]{10}\.css"/);
  assert.match(html, /src="\/img\/shot\.[a-f0-9]{10}\.png\?x=1#top"/);
  assert.match(html, /src="app\.[a-f0-9]{10}\.js"/);
});

test('страницы, внешние адреса и то, чего нет в каталоге, не трогаются', () => {
  const src = fixture();
  const out = tmp();
  stamp(src, out);
  const html = fs.readFileSync(path.join(out, 'index.html'), 'utf8');
  assert.match(html, /href="\.\/other\.html"/);
  assert.match(html, /href="\/downloads\/Setup\.exe"/);
  assert.match(html, /href="https:\/\/example\.test\/styles\.css"/);
  // Превью ссылок читают полный адрес без хеша — он обязан остаться рабочим.
  assert.match(html, /content="https:\/\/example\.test\/img\/shot\.png"/);
});

test('исходные файлы остаются в артефакте без хеша', () => {
  const src = fixture();
  const out = tmp();
  stamp(src, out);
  for (const rel of ['styles.css', 'app.js', 'img/shot.png', 'fonts/a.woff2', 'fonts/fonts.css']) {
    assert.ok(fs.existsSync(path.join(out, rel)), `пропал исходный ${rel}`);
  }
});

test('CSS ссылается на тот же шрифт, что и предзагрузка в HTML', () => {
  // Разойдутся имена — браузер скачает шрифт дважды: раз по preload, раз по
  // @font-face, и предзагрузка станет лишним запросом.
  const src = fixture();
  const out = tmp();
  const { stamped } = stamp(src, out);
  const font = path.posix.basename(stamped['fonts/a.woff2']);
  const css = fs.readFileSync(path.join(out, stamped['fonts/fonts.css']), 'utf8');
  const html = fs.readFileSync(path.join(out, 'index.html'), 'utf8');
  assert.ok(css.includes(`url('./${font}')`), 'CSS ссылается на шрифт без хеша');
  assert.ok(html.includes(`href="./fonts/${font}"`), 'предзагрузка ссылается на другое имя');
});

test('хеш меняется вместе с содержимым — в том числе у CSS, когда меняется его шрифт', () => {
  const a = fixture();
  const first = stamp(a, tmp()).stamped;
  fs.writeFileSync(path.join(a, 'fonts/a.woff2'), 'FONT-A-NEW');
  const second = stamp(a, tmp()).stamped;
  assert.notEqual(first['fonts/a.woff2'], second['fonts/a.woff2']);
  assert.notEqual(first['fonts/fonts.css'], second['fonts/fonts.css'], 'CSS не заметил, что шрифт сменился');
  assert.equal(first['styles.css'], second['styles.css'], 'хеш styles.css поменялся без причины');
});

test('на настоящем лендинге не остаётся ни одной ссылки на файл без хеша', () => {
  const out = tmp();
  const { pages } = stamp(path.join(__dirname, '../../landing'), out);
  assert.ok(pages.length >= 3, 'страниц меньше, чем ожидалось');
  for (const page of pages) {
    const html = fs.readFileSync(path.join(out, page), 'utf8');
    for (const m of html.matchAll(/\b(?:src|href)="(\.?\/?[^":]+)"/g)) {
      const clean = m[1].split(/[?#]/)[0];
      if (!/\.(?:css|js|mjs|png|jpe?g|webp|gif|svg|ico|woff2?)$/i.test(clean)) continue;
      const file = path.join(out, path.posix.dirname(page), clean);
      if (!fs.existsSync(file)) continue; // /downloads/… и прочее, чего нет в каталоге
      assert.match(clean, NGINX_HASHED, `${page}: ${m[1]} уедет без хеша и застрянет в кеше`);
    }
  }
});
