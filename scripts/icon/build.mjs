// Пересобирает значок во всех местах, где он живёт.
//   node scripts/icon/build.mjs
import { createHash } from 'node:crypto';
import { writeFileSync, readFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { ico, svg, png } from './render.mjs';
import { ICO_SIZES } from './geometry.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

// Страницы админки, которые ссылаются на значок по постоянному адресу.
const ADMIN_PAGES = ['server/admin_ui/index.html', 'server/admin_ui/login.html'];

/**
 * Версия значка — хеш его файлов. Браузер держит значок вкладки в отдельном
 * кэше дольше любых заголовков, и пока адрес тот же, показывает старую картинку.
 * У лендинга адрес меняется сам — выкатка даёт файлам имена с хешем
 * (scripts/landing-stamp.mjs). У админки своей сборки нет, поэтому версия
 * дописывается в ссылку отсюда: `/admin/ui/favicon.svg?v=…`.
 */
export function adminIconVersion() {
  return createHash('sha256')
    .update(svg(32, { title: 'Chill Hub' }))
    .update(ico(ICO_SIZES))
    .digest('hex')
    .slice(0, 10);
}

function put(rel, data) {
  const p = resolve(root, rel);
  mkdirSync(dirname(p), { recursive: true });
  writeFileSync(p, data);
  console.log(String(data.length).padStart(8) + '  ' + rel);
}

export function build() {
  const appIco = ico(ICO_SIZES);
  const webIco = ico([16, 24, 32, 48]); // вкладке больше не нужно
  const mark = svg(32, { title: 'Chill Hub' });

  put('launcher/ChillHub/Assets/app.ico', appIco); // окно, трей, ресурс exe
  put('scripts/app.ico', appIco); // установщик и деинсталлятор NSIS
  put('server/admin_ui/app.ico', appIco); // админка v1 показывает его картинкой
  put('server/admin_ui/favicon.svg', mark);
  put('landing/favicon.ico', webIco);
  put('landing/favicon.svg', mark);
  put('landing/assets/icons/logo.svg', mark);
  put('docs/assets/icon-256.png', png(256)); // для README и витрины
  // Остаток от старой раскладки: на него никто не ссылается. Пересобираем, пока
  // он лежит в репозитории, — иначе это второй значок, который тихо разойдётся
  // с первым. Удалить его можно в любой момент, тогда строку убрать отсюда.
  put('landing/assets/icons/app.ico', webIco);

  const v = adminIconVersion();
  for (const rel of ADMIN_PAGES) {
    const p = resolve(root, rel);
    const html = readFileSync(p, 'utf8');
    const next = html.replace(/(\/admin\/ui\/(?:favicon\.svg|app\.ico))(?:\?v=[a-f0-9]+)?/g, `$1?v=${v}`);
    if (next !== html) {
      writeFileSync(p, next);
      console.log(`  ?v=${v}  ${rel}`);
    }
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) build();
