// Лендинг 2.0 целиком, в настоящем DOM.
//
// Тот же приём, что у панели: реальный index.html грузится в jsdom, все
// его скрипты исполняются в том же порядке, что в браузере, а сеть
// подменена и записана. Проверяется то, за что отвечает страница:
// собирается ли она из данных каталога, что показывает без сервера, и
// доходит ли заявка до эндпоинта.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const V2 = path.join(__dirname, '..', '..', 'landing');

/** Ответы, которыми притворяется сервер. Форма — как у публичного API. */
function fixtures() {
  return {
    '/api/games': {
      items: [
        {
          gameId: 'repo', title: 'R.E.P.O.', hasLatest: true, latestVersion: '1.0.1',
          iconUrl: '/manifests/repo/icon.png',
          mods: { hasLatest: true, displayName: 'Moo Modpack', displayVersion: '1.9.9', community: 'repo', loader: 'bepinex', steamAppId: '3241660' },
        },
        { gameId: 'bodycam', title: 'Bodycam', hasLatest: true, latestVersion: '1.0.0', iconUrl: '/manifests/bodycam/icon.png' },
        { gameId: 'lethal-company', title: 'Lethal Company', hasLatest: true, latestVersion: '1.0.9', iconUrl: '/manifests/lethal-company/icon.png' },
      ],
    },
    '/news/index.json': { items: [{ id: 'n1', title: 'Заметка', createdAt: '2026-08-31T10:00:00Z', summary: 'Текст', published: true }] },
    '/api/maintenance': { enabled: false, blocks: {} },
    '/manifests/launcher/latest.json': { version: '1.6.25' },
    '/downloads/setup.json': { __status: 404 },
  };
}

async function boot(t, overrides) {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://launcher.samoy.love/v2/' });
  const { window } = dom;

  const calls = [];
  const table = Object.assign(fixtures(), overrides || {});

  window.fetch = async (url, init) => {
    const u = String(url).replace('https://launcher.samoy.love', '');
    const method = (init && init.method) || 'GET';
    calls.push({ method, url: u, body: init && init.body ? JSON.parse(init.body) : null });

    const key = Object.keys(table).find((k) => u.startsWith(k));
    if (!key) return { ok: false, status: 404, text: async () => '', json: async () => ({}) };

    const v = table[key];
    if (v && v.__throw) throw new Error('сеть');
    if (v && v.__status) return { ok: false, status: v.__status, text: async () => '', json: async () => ({}) };
    return { ok: true, status: 200, text: async () => JSON.stringify(v), json: async () => v };
  };

  /* jsdom не реализует matchMedia. Заглушка отвечает «нет» на все запросы:
     значит, страница считает, что движение не ограничено и экран широкий, —
     то есть проверяется полный вариант, а не урезанный. */
  window.matchMedia = (query) => ({
    matches: /min-width/.test(query) ? true : false,
    media: query,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
  });

  // Канвас в jsdom не рисует — фон обязан это пережить, а не уронить страницу
  window.HTMLCanvasElement.prototype.getContext = () => ({
    setTransform() {}, fillRect() {}, beginPath() {}, moveTo() {}, lineTo() {}, stroke() {},
    set fillStyle(v) {}, set strokeStyle(v) {}, set lineWidth(v) {}, set globalAlpha(v) {},
  });

  const scripts = [...window.document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src'));
  for (const src of scripts) {
    // Путь разрешается как в браузере: и './', и '../'
    const file = path.resolve(V2, src);
    const code = fs.readFileSync(file, 'utf8');
    vm.runInContext(code, dom.getInternalVMContext(), { filename: file });
  }

  for (let i = 0; i < 60; i++) await new Promise((r) => setTimeout(r, 0));

  /* Окно закрывается после проверки. Эмулятор держит `setInterval` для
     очереди, а фон — `requestAnimationFrame`: без закрытия процесс тестов
     не завершается вовсе. */
  t.after(() => window.close());

  return { window, calls };
}

async function until(fn, tries = 80) {
  for (let i = 0; i < tries; i++) {
    if (fn()) return true;
    await new Promise((r) => setTimeout(r, 0));
  }
  return false;
}

/* ---------- Наполнение из API ---------- */

test('страница собирается из каталога, а не из свёрстанного текста', async (t) => {
  const { window, calls } = await boot(t);
  const asked = calls.map((c) => c.url);
  assert.ok(asked.includes('/api/games'), 'каталог не запрошен');
  assert.ok(asked.includes('/news/index.json'), 'новости не запрошены');
  assert.ok(asked.includes('/api/maintenance'), 'техработы не запрошены');

  const cards = window.document.querySelectorAll('[data-games] .game');
  assert.strictEqual(cards.length, 3);
  assert.match(window.document.body.textContent, /R\.E\.P\.O\./);
  assert.match(window.document.body.textContent, /Bodycam/);
});

test('версия лаунчера приходит из манифеста, а не вписана в разметку', async (t) => {
  const { window } = await boot(t);
  const shown = [...window.document.querySelectorAll('[data-launcher-version]')].map((e) => e.textContent);
  assert.ok(shown.every((v) => v === '1.6.25'), 'версия не подставлена: ' + shown.join(','));
});

test('загрузчик модов печатается человеческим именем', async (t) => {
  const { window } = await boot(t);
  // API отдаёт «bepinex» строчными, а называется он BepInEx
  assert.match(window.document.body.textContent, /BepInEx/);
  assert.ok(!/загрузчик bepinex/.test(window.document.body.textContent));
});

test('игра без модпака не обещает того, чего нет', async (t) => {
  const { window } = await boot(t);
  const cards = [...window.document.querySelectorAll('[data-games] .game')];
  const bodycam = cards.find((c) => /Bodycam/.test(c.textContent));
  assert.match(bodycam.textContent, /Модпака для неё пока нет/);
  assert.ok(!/Модпак Moo/.test(bodycam.textContent));
});

/* ---------- Что видно без сервера ---------- */

test('без каталога страница не пустеет и честно говорит про снимок', async (t) => {
  const { window } = await boot(t, { '/api/games': { __throw: true } });
  const games = window.document.querySelector('[data-games]');
  assert.ok(games.querySelectorAll('.game').length > 0, 'снимок должен наполнить раздел');
  assert.match(games.textContent, /показан сохранённый снимок/);
});

test('о снимке говорится один раз, а не под каждым разделом', async (t) => {
  const { window } = await boot(t, { '/api/games': { __throw: true }, '/news/index.json': { __throw: true } });
  const hits = window.document.body.textContent.match(/показан сохранённый снимок/g) || [];
  assert.strictEqual(hits.length, 1);
});

test('баннер техработ появляется только когда они включены', async (t) => {
  const off = await boot(t);
  assert.strictEqual(off.window.document.querySelector('[data-maint]').hidden, true);

  const on = await boot(t, { '/api/maintenance': { enabled: true, reason: 'Переезд на новый диск' } });
  const banner = on.window.document.querySelector('[data-maint]');
  assert.strictEqual(banner.hidden, false);
  assert.match(banner.textContent, /Переезд на новый диск/);
});

test('баннер техработ называет закрытое, а не обещает своё', async (t) => {
  // Раньше здесь стояла одна выдуманная фраза «уже установленные игры
  // запускаются как обычно». Запуск закрывается отдельным флагом, и с
  // ним обещание становилось ложью — из тех, что проверяют сразу
  const { window } = await boot(t, {
    '/api/maintenance': {
      enabled: true,
      reason: 'Меняем диск на сервере раздачи',
      blocks: { install: true, update: true, launch: true },
    },
  });

  const text = window.document.querySelector('[data-maint]').textContent;
  assert.match(text, /Меняем диск на сервере раздачи\./, 'причина набрана не предложением');
  assert.match(text, /установка новых игр/);
  assert.match(text, /запуск/);
  assert.doesNotMatch(text, /запускаются как обычно/);
});

test('когда закрыто не всё, баннер это и говорит', async (t) => {
  const { window } = await boot(t, {
    '/api/maintenance': { enabled: true, reason: 'Перебираем сборки', blocks: { update: true } },
  });

  const text = window.document.querySelector('[data-maint]').textContent;
  assert.match(text, /обновление уже установленных/);
  assert.doesNotMatch(text, /запуск/);
});

test('баннер без единого запрета не выдумывает запрет', async (t) => {
  // Состояние «работы идут, но ничего не закрыто» законно, и сервер его
  // отдаёт: баннер тогда предупреждает, а не запрещает
  const { window } = await boot(t, {
    '/api/maintenance': { enabled: true, reason: 'Готовим переезд', blocks: {} },
  });

  assert.match(window.document.querySelector('[data-maint]').textContent, /можно как обычно/);
});

test('срок работ считается по часам сервера, а не посетителя', async (t) => {
  // Часы посетителя бывают сбиты на сутки, и по ним ещё не наступивший
  // срок выглядит истёкшим
  const { window } = await boot(t, {
    '/api/maintenance': {
      enabled: true,
      reason: 'Меняем диск',
      blocks: { install: true },
      serverTime: '2026-09-05T10:00:00Z',
      endsAt: '2026-09-05T12:00:00Z',
    },
  });

  assert.match(window.document.querySelector('[data-maint]').textContent, /Ожидаемое окончание/);
});

test('истёкший срок не обещают заново', async (t) => {
  const { window } = await boot(t, {
    '/api/maintenance': {
      enabled: true,
      reason: 'Меняем диск',
      blocks: { install: true },
      serverTime: '2026-09-05T14:00:00Z',
      endsAt: '2026-09-05T12:00:00Z',
    },
  });

  const text = window.document.querySelector('[data-maint]').textContent;
  assert.match(text, /Работы затянулись/);
  assert.doesNotMatch(text, /Ожидаемое окончание/);
});

test('кривой срок не ломает баннер', async (t) => {
  const { window } = await boot(t, {
    '/api/maintenance': { enabled: true, reason: 'Меняем диск', blocks: { install: true }, endsAt: 'завтра' },
  });

  const text = window.document.querySelector('[data-maint]').textContent;
  assert.match(text, /Меняем диск/);
  assert.doesNotMatch(text, /Ожидаемое окончание/);
});

/* ---------- Факты об установщике ---------- */

test('без setup.json размер, дата и хеш не показываются', async (t) => {
  const { window } = await boot(t);
  for (const key of ['size', 'builtAt', 'sha256']) {
    const el = window.document.querySelector(`[data-setup="${key}"]`);
    assert.strictEqual(el.hidden, true, key + ' не должен показываться без значения');
  }
  // Выдуманный хеш рядом с кнопкой скачивания хуже, чем его отсутствие
  assert.ok(!/e3b0c442/.test(window.document.body.innerHTML));
});

test('с setup.json факты появляются и хеш попадает на кнопку', async (t) => {
  const { window } = await boot(t, {
    '/downloads/setup.json': { size: 123731968, builtAt: '2026-09-04T03:12:00Z', sha256: 'abc123' },
  });
  const size = window.document.querySelector('[data-setup="size"]');
  assert.strictEqual(size.hidden, false);
  assert.match(size.textContent, /118 МБ/);

  const btn = window.document.querySelector('.copy-hash');
  assert.strictEqual(btn.dataset.hash, 'abc123');
  assert.strictEqual(window.document.querySelector('p[data-setup="sha256"]').hidden, false);
});

/* ---------- Копия лаунчера ---------- */

test('копия лаунчера наполняется теми же играми, что каталог', async (t) => {
  const { window } = await boot(t);
  const rows = window.document.querySelectorAll('.emu-game');
  assert.strictEqual(rows.length, 3);
  assert.match(rows[0].textContent, /R\.E\.P\.O\./);
});

test('игра без модпака не получает кнопок запуска', async (t) => {
  const { window } = await boot(t);
  const rows = [...window.document.querySelectorAll('[data-emu-select]')];
  const bodycam = rows.find((r) => /Bodycam/.test(r.textContent));
  bodycam.click();
  await until(() => /Bodycam/.test(window.document.querySelector('.emu-hero h3').textContent));
  // У неё нет modpack и steamAppId — вариантов запуска быть не может
  assert.strictEqual(window.document.querySelectorAll('.emu-launch').length, 0);
  assert.ok(window.document.querySelector('[data-emu-action]'), 'кнопка действия обязана остаться');
});

test('постановка в очередь показывает док и подписывает позиции', async (t) => {
  const { window } = await boot(t);
  const rows = [...window.document.querySelectorAll('[data-emu-select]')];
  // Нужна игра, которую ещё предстоит поставить: у установленной кнопка запускает
  const lethal = rows.find((r) => /Lethal Company/.test(r.textContent));
  lethal.click();
  await until(() => /Установить/.test(window.document.querySelector('[data-emu-action]').textContent));
  window.document.querySelector('[data-emu-action]').click();

  const shown = await until(() => window.document.querySelector('.emu-dock'));
  assert.ok(shown, 'док очереди не появился');
  assert.match(window.document.querySelector('.emu-dock').textContent, /Очередь загрузок/);
});

/* ---------- Заявка ---------- */

test('заявка уходит в публичный эндпоинт обратной связи', async (t) => {
  const { window, calls } = await boot(t, { '/feedback/submit': { ok: true } });
  const form = window.document.querySelector('[data-wish]');
  window.document.querySelector('#wish-text').value = 'Добавьте Deep Rock Galactic';
  window.document.querySelector('#wish-contact').value = 'tg: @kostya';
  form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));

  const sent = await until(() => calls.some((c) => c.url === '/feedback/submit'));
  assert.ok(sent, 'заявка не ушла');

  const req = calls.find((c) => c.url === '/feedback/submit');
  assert.strictEqual(req.method, 'POST');
  assert.strictEqual(req.body.type, 'idea');
  assert.strictEqual(req.body.comment, 'Добавьте Deep Rock Galactic');
  assert.strictEqual(req.body.contact, 'tg: @kostya');
  assert.strictEqual(req.body.attachLogs, false, 'журналы с сайта не прикладываются');
});

test('пустая заявка не отправляется', async (t) => {
  const { window, calls } = await boot(t, { '/feedback/submit': { ok: true } });
  const form = window.document.querySelector('[data-wish]');
  form.dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
  await new Promise((r) => setTimeout(r, 10));
  assert.ok(!calls.some((c) => c.url === '/feedback/submit'));
});

test('неудача отправки не выглядит успехом и подсказывает запасной путь', async (t) => {
  const { window } = await boot(t, { '/feedback/submit': { __status: 500 } });
  window.document.querySelector('#wish-text').value = 'Игра';
  window.document.querySelector('[data-wish]').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));

  const shown = await until(() => /Не ушло/.test(window.document.querySelector('[data-wish-note]').textContent));
  assert.ok(shown);
  assert.match(window.document.querySelector('[data-wish-note]').textContent, /tr0llex/);
});

/* ---------- Мелочи, которые ломают страницу молча ---------- */

test('год в подвале проставляется, а не остаётся свёрстанным', async (t) => {
  const { window } = await boot(t);
  const year = window.document.querySelector('[data-year]').textContent;
  assert.strictEqual(year, String(new Date().getFullYear()));
});

test('барабаны автомата скрыты от чтения с экрана', async (t) => {
  const { window } = await boot(t);
  const reels = window.document.querySelector('.reels');
  assert.strictEqual(reels.getAttribute('aria-hidden'), 'true');
  // 96 строк декорации не должны попадать в озвучку страницы
  assert.ok(reels.querySelectorAll('.reel-track div').length > 20);
});

test('у страницы есть заголовок, описание и канонический адрес', async (t) => {
  const { window } = await boot(t);
  const d = window.document;
  assert.ok(d.title.length > 10);
  assert.ok(d.querySelector('meta[name="description"]').content.length > 40);
  assert.ok(d.querySelector('link[rel="canonical"]').href.startsWith('https://'));
  assert.ok(d.querySelector('meta[property="og:image"]').content.startsWith('https://'));
});

test('у каждой картинки есть подпись для скринридера', async (t) => {
  const { window } = await boot(t);
  for (const img of window.document.querySelectorAll('img')) {
    assert.ok(img.hasAttribute('alt'), 'картинка без alt: ' + img.getAttribute('src'));
  }
});

test('не загрузившийся значок игры подменяется буквой, а не пустотой', async (t) => {
  const { window } = await boot(t);
  const img = window.document.querySelector('[data-games] img.game-ico');
  assert.ok(img, 'значок из каталога не отрисован');
  assert.strictEqual(img.dataset.letter, 'R', 'буква для подмены не заготовлена');

  img.dispatchEvent(new window.Event('error'));

  const span = window.document.querySelector('[data-games] .game-ico--letter');
  assert.ok(span, 'на месте битой картинки должна остаться плашка с буквой');
  assert.strictEqual(span.textContent, 'R');
});

test('битая картинка вне каталога убирается, а не зияет прямоугольником', async (t) => {
  const { window } = await boot(t);
  const shot = window.document.querySelector('.hero-shot img');
  shot.dispatchEvent(new window.Event('error'));
  assert.strictEqual(shot.hidden, true);
});

/* ---------- Память о галерее ---------- */

test('оборванный запрос галереи не запоминается как «галереи нет»', async (t) => {
  // Иначе игра остаётся с пустой витриной до перезагрузки страницы,
  // хотя связь давно вернулась. Игра взята без запасного кадра: с ним
  // проверялся бы запасной кадр, а не память об обрыве
  const { window } = await boot(t, {
    '/content/peak/gallery/gallery.json': { __throw: true },
  });

  const api = window.CHILLHUB_API;
  assert.strictEqual((await api.gallery('peak')).length, 0, 'при обрыве галерея не пуста');

  // Связь вернулась: следующий запрос обязан уйти на сервер, а не в память
  let asked = 0;
  window.fetch = async () => {
    asked++;
    return {
      ok: true,
      status: 200,
      json: async () => ({ cover: 'cover.jpg', items: [{ file: 'cover.jpg', caption: 'Смена' }] }),
      text: async () => '',
    };
  };
  const second = await api.gallery('peak');
  assert.strictEqual(asked, 1, 'повторный запрос не ушёл: обрыв запомнили');
  assert.strictEqual(second.length, 1);
});

test('ответ «галереи нет» запоминается: спрашивать второй раз незачем', async (t) => {
  const { window } = await boot(t, {
    '/content/bodycam/gallery/gallery.json': { __status: 404 },
  });

  const api = window.CHILLHUB_API;
  assert.strictEqual((await api.gallery('bodycam')).length, 0);

  let asked = 0;
  window.fetch = async () => {
    asked++;
    return { ok: false, status: 404, json: async () => ({}), text: async () => '' };
  };
  await api.gallery('bodycam');
  assert.strictEqual(asked, 0, 'стабильный ответ спросили заново');
});

/* ---------- Ссылки с сервера в разметке ---------- */

test('обложка со сломанным адресом не дописывает своих правил в CSS', async (t) => {
  // Обложка уезжает в style="background-image:url('…')". Кавычка внутри
  // становится &#39;, но разбор идёт в два шага: HTML раскрывает
  // сущности, и CSS видит уже настоящую кавычку
  const { window } = await boot(t, {
    '/news/index.json': {
      items: [
        {
          id: 'n1',
          slug: 'n1',
          title: 'Заметка',
          createdAt: '2026-09-01T10:00:00Z',
          summary: 'текст',
          coverUrl: "x'); background:url('https://зло/маяк.png",
        },
      ],
    },
  });

  await until(() => window.document.querySelector('.post'));
  const html = window.document.querySelector('[data-news]').innerHTML;
  assert.ok(!html.includes('зло'), 'чужой адрес попал в разметку');
  assert.ok(!/background:url/.test(html.replace('background-image:url', '')), 'дописалось лишнее правило');
});

test('javascript: в адресе обложки не проходит ни в каком виде', async (t) => {
  const { window } = await boot(t, {
    '/news/index.json': {
      items: [
        { id: 'n1', slug: 'n1', title: 'З', createdAt: '2026-09-01T10:00:00Z', coverUrl: 'java\tscript:alert(1)' },
      ],
    },
  });

  await until(() => window.document.querySelector('.post'));
  const html = window.document.querySelector('[data-news]').innerHTML;
  assert.ok(!/script:/.test(html), 'схема прошла через табуляцию');
});

test('обычные адреса обложек и значков не портятся', async (t) => {
  const { window } = await boot(t);
  await until(() => window.document.querySelector('.game-ico, [data-letter]'));
  const html = window.document.querySelector('[data-games]').innerHTML;
  assert.match(html, /\/manifests\/repo\/icon\.png/, 'нормальный значок отфильтровали');
});
