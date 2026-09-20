// Сборка лендинга для выкатки: копия landing/ с хешем содержимого в именах
// стилей, скриптов, шрифтов и картинок, на которые ссылаются страницы.
//
//   node scripts/landing-stamp.mjs landing stage-site
//
// ЗАЧЕМ. Страницы nginx отдаёт с no-store, а стили и скрипты без хеша в имени —
// с max-age=300 (deploy-kit, nginx/sites/chillhub-launcher.conf). Пока имена не
// менялись, выкатка доезжала до человека не сразу: браузер брал свежий HTML и
// ещё пять минут подставлял к нему старые styles.css и app.js, не спрашивая
// сервер. Сайт после перекраски так и выглядел угольным, хотя на сервере уже
// лежал фиолетовый.
//
// С хешем в имени новая страница ссылается на новый файл, и старая копия в
// кеше просто не подходит. Для таких имён в том же конфиге nginx есть блок с
// кешем на год и immutable — повторный заход не ходит на сервер вовсе.
//
// ЧТО ОСТАЁТСЯ КАК БЫЛО. Исходные файлы тоже лежат в артефакте, без хеша: на
// них ссылаются превью ссылок (og:image с полным адресом), браузерный запрос
// /favicon.ico по умолчанию и пути, которые JS собирает сам. Страницы, ссылки
// на другие страницы и всё, чего нет в каталоге (/downloads/…), не трогаются.
import { createHash } from 'node:crypto';
import { cpSync, existsSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { basename, extname, join, posix, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// Расширения совпадают с теми, что nginx узнаёт как статику с хешем.
export const STAMPED = /\.(?:css|js|mjs|png|jpe?g|webp|gif|svg|ico|woff2?|ttf|eot)$/i;

const EXTERNAL = /^(?:[a-z][a-z0-9+.-]*:|\/\/|#|data:)/i;

export const hashOf = (buf) => createHash('sha256').update(buf).digest('hex').slice(0, 10);

// Имя с хешем: styles.css → styles.3f9a1c07b2.css.
export const stampedName = (file, hash) => {
  const ext = extname(file);
  return file.slice(0, file.length - ext.length) + '.' + hash + ext;
};

const toPosix = (p) => p.split(sep).join('/');

// Ссылка из файла `from` (путь от корня сайта) на `ref` → путь файла от корня
// сайта, или null, если ссылка внешняя или ведёт не на файл каталога.
function resolveRef(root, from, ref) {
  if (EXTERNAL.test(ref)) return null;
  const clean = ref.split(/[?#]/)[0];
  if (!clean || !STAMPED.test(clean)) return null;
  const abs = clean.startsWith('/')
    ? posix.normalize(clean).replace(/^\/+/, '')
    : posix.normalize(posix.join(posix.dirname(from), clean));
  if (abs.startsWith('..')) return null;
  const onDisk = join(root, ...abs.split('/'));
  return existsSync(onDisk) && statSync(onDisk).isFile() ? abs : null;
}

// Ссылка на новый файл в том же виде, в каком была исходная: `./x`, `/x` или `x`.
function rewriteRef(ref, target, stampedTarget) {
  const clean = ref.split(/[?#]/)[0];
  const tail = ref.slice(clean.length);
  const oldBase = posix.basename(target);
  const newBase = posix.basename(stampedTarget);
  if (!clean.endsWith(oldBase)) return ref;
  return clean.slice(0, clean.length - oldBase.length) + newBase + tail;
}

export function stamp(src, out) {
  const root = resolve(out);
  rmSync(root, { recursive: true, force: true });
  cpSync(resolve(src), root, { recursive: true });

  const done = new Map(); // путь от корня → путь файла с хешем

  // CSS сперва переписывает свои url(): иначе шрифт, предзагруженный из HTML под
  // именем с хешем, и тот же шрифт из @font-face разошлись бы и качались дважды.
  // Хеш CSS считается уже от переписанного текста — он меняется вместе с тем,
  // на что ссылается.
  function stampFile(rel) {
    if (done.has(rel)) return done.get(rel);
    const onDisk = join(root, ...rel.split('/'));
    let body = readFileSync(onDisk);
    if (/\.css$/i.test(rel)) {
      const text = body.toString('utf8').replace(/url\(\s*(['"]?)([^'")]+)\1\s*\)/g, (m, q, ref) => {
        const target = resolveRef(root, rel, ref);
        return target ? `url(${q}${rewriteRef(ref, target, stampFile(target))}${q})` : m;
      });
      body = Buffer.from(text, 'utf8');
    }
    const stamped = stampedName(rel, hashOf(body));
    writeFileSync(join(root, ...stamped.split('/')), body);
    done.set(rel, stamped);
    return stamped;
  }

  const pages = [];
  (function walk(dir) {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else if (/\.html$/i.test(name)) pages.push(toPosix(relative(root, full)));
    }
  })(root);

  for (const page of pages) {
    const file = join(root, ...page.split('/'));
    const html = readFileSync(file, 'utf8').replace(/\b(src|href)=(["'])([^"']+)\2/g, (m, attr, q, ref) => {
      const target = resolveRef(root, page, ref);
      return target ? `${attr}=${q}${rewriteRef(ref, target, stampFile(target))}${q}` : m;
    });
    writeFileSync(file, html);
  }

  return { pages, stamped: Object.fromEntries(done) };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [src = 'landing', out = 'stage-site'] = process.argv.slice(2);
  const { pages, stamped } = stamp(src, out);
  for (const [from, to] of Object.entries(stamped)) console.log(`  ${from} → ${basename(to)}`);
  console.log(`страниц: ${pages.length}, файлов с хешем: ${Object.keys(stamped).length}`);
}
