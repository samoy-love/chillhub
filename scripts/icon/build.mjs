// Пересобирает значок во всех местах, где он живёт.
//   node scripts/icon/build.mjs
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { ico, svg, png } from './render.mjs';
import { ICO_SIZES } from './geometry.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const put = (rel, data) => {
  const p = resolve(root, rel);
  mkdirSync(dirname(p), { recursive: true });
  writeFileSync(p, data);
  console.log(String(data.length).padStart(8) + '  ' + rel);
};

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
