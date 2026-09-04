// Длинные дела панели 2.0 в настоящем DOM.
//
// Шесть дел — загрузка сборки, сборка модпака, новость, галерея,
// порядок игр и подбор параметров — идут минутами и умеют оборваться на
// середине. Правила внутри них уже проверены по отдельности; здесь
// проверяется связывание: открылось ли дело, что ушло на сервер и что
// осталось на экране, когда всё кончилось.
//
// Сеть подменена целиком: наружу не уходит ни один запрос, а каждый
// записывается вместе с методом, адресом и телом.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');

const V2 = path.join(__dirname, '..', '..', 'server', 'admin_ui', 'v2');

function fixtures() {
  return {
    summary: { launcher: { pending: false, newest: '1.6.25', active: '1.6.25' }, mods: [] },
    list: { items: [{ version: '1.6.25', date: '04.09.2026', files: 478, size: 121400000, state: 'active' }] },
    games: {
      items: [
        { gameId: 'repo', title: 'R.E.P.O.', exeRelativePath: 'REPO.exe', order: 0, mods: { steamAppId: '3241660' } },
        { gameId: 'peak', title: 'PEAK', exeRelativePath: 'PEAK.exe', order: 1 },
      ],
    },
    'mods/list': { items: [{ gameId: 'repo', title: 'R.E.P.O.', built: '1.9.9', active: '1.9.9', mods: 17 }] },
    'news/list': { items: [{ id: 'n1', title: 'Заметка', published: false }] },
    'news/get': { id: 'n1', title: 'Заметка', body: 'Текст с сервера', gameId: '' },
    'feedback/list': { items: [] },
    'maintenance/get': { enabled: false, reason: '', blocks: {} },
    'metrics/summary': { days: [] },
    'metrics/errors': { items: [] },
    'system/free': { freeBytes: 214000000000, totalBytes: 480000000000 },
    'mods/cache': { files: 412, bytes: 8900000000 },
    'games/gallery': {
      cover: 'cover.jpg',
      items: [
        { name: 'cover.jpg', size: 240000 },
        { name: 'screens', dir: true },
        { name: 'guide.pdf', size: 90000 },
      ],
    },
  };
}

async function boot(overrides) {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/v2/' });
  const { window } = dom;

  const calls = [];
  const fx = Object.assign(fixtures(), overrides || {});

  window.fetch = async (url, init) => {
    const u = String(url);
    const method = (init && init.method) || 'GET';
    let body;
    try {
      body = init && init.body ? JSON.parse(init.body) : null;
    } catch {
      body = init.body;
    }
    calls.push({ method, url: u, body });

    const key = u.replace('/admin/api/', '').split('?')[0];
    if (Object.prototype.hasOwnProperty.call(fx, key)) {
      const v = fx[key];
      if (v && v.__fail) {
        return { ok: false, status: v.__fail, text: async () => JSON.stringify({ error: v.error || 'сбой' }) };
      }
      return { ok: true, status: 200, text: async () => JSON.stringify(v) };
    }
    return { ok: true, status: 200, text: async () => '{}' };
  };

  for (const src of [...window.document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src'))) {
    const file = path.resolve(V2, src);
    vm.runInContext(fs.readFileSync(file, 'utf8'), dom.getInternalVMContext(), { filename: file });
  }
  for (let i = 0; i < 40; i++) await new Promise((r) => setTimeout(r, 0));
  return { window, calls, dom };
}

async function until(fn, tries = 80) {
  for (let i = 0; i < tries; i++) {
    if (fn()) return true;
    await new Promise((r) => setTimeout(r, 0));
  }
  return false;
}

/* Дать делу договорить до конца: закрытие листа тянет за собой
   перечитывание разделов и перерисовку, и окно нельзя закрывать раньше. */
const settle = async (window) => {
  await until(() => !window.document.querySelector('.sheet'));
  for (let i = 0; i < 30; i++) await new Promise((r) => setTimeout(r, 0));
};

const go = async (window, hash, sel) => {
  window.location.hash = hash;
  await until(() => window.document.querySelector(sel));
  return window.document.querySelector(sel);
};

/* ---------- Оболочка ---------- */

test('дело открывается листом поверх раздела, а раздел остаётся на месте', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  const btn = await go(window, '#launcher', '[data-act="upload"]');
  btn.click();
  assert.ok(await until(() => window.document.querySelector('.sheet')), 'лист не открылся');

  assert.match(window.document.querySelector('.sheet').textContent, /Загрузка сборки/);
  // Раздел под листом никуда не делся: видно, к чему вернуться
  assert.ok(window.document.querySelector('h1'));
});

test('лист закрывается по Escape', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  (await go(window, '#launcher', '[data-act="upload"]')).click();
  await until(() => window.document.querySelector('.sheet'));

  window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.ok(await until(() => !window.document.querySelector('.sheet')), 'лист не закрылся');
});

test('открытая загрузка сама на сервер не ходит, пока файл не выбран', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  const btn = await go(window, '#launcher', '[data-act="upload"]');
  const before = calls.length;
  btn.click();
  await until(() => window.document.querySelector('.sheet'));

  assert.strictEqual(calls.length, before, 'лист сходил на сервер до выбора файла');
  assert.match(window.document.querySelector('.sheet').textContent, /Файл ещё не выбран/);
});

/* ---------- Галерея ---------- */

test('галерея открывается для той игры, в строке которой нажали', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  await go(window, '#games', '[data-act="gallery"]');
  const rows = [...window.document.querySelectorAll('[data-act="gallery"]')];
  const peak = rows.find((b) => (b.dataset.args || '').includes('peak'));
  assert.ok(peak, 'у строки игры нет своей кнопки галереи');
  peak.click();

  assert.ok(await until(() => calls.some((c) => c.url.includes('games/gallery') && c.url.includes('peak'))), 'галерея не запрошена');
});

test('в галерее видно, какой файл уйдёт на витрину', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="gallery"]')).click();
  assert.ok(await until(() => /обложка/.test(window.document.querySelector('.sheet').textContent)));

  const sheet = window.document.querySelector('.sheet');
  assert.match(sheet.textContent, /cover\.jpg/);
  // PDF обложкой быть не может, и предлагать это незачем
  assert.ok(!sheet.querySelector('[data-cover="guide.pdf"]'));
});

test('переименование в занятое имя до сервера не доходит', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="gallery"]')).click();
  await until(() => window.document.querySelector('[data-rename]'));

  // Windows не различает регистр: «Cover.JPG» затрёт «cover.jpg» молча
  window.prompt = () => 'Cover.JPG';
  const before = calls.length;
  window.document.querySelector('[data-rename="guide.pdf"]').click();
  await until(() => /уже занято/.test(window.document.body.textContent));

  assert.match(window.document.body.textContent, /уже занято/);
  assert.strictEqual(calls.length, before, 'запрос ушёл при заведомо плохом имени');
});

test('отказ от переименования ничего не меняет', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="gallery"]')).click();
  await until(() => window.document.querySelector('[data-rename]'));

  window.prompt = () => null;
  const before = calls.length;
  window.document.querySelector('[data-rename="guide.pdf"]').click();
  for (let i = 0; i < 10; i++) await new Promise((r) => setTimeout(r, 0));
  assert.strictEqual(calls.length, before);
});

test('удаление обложки сначала называет последствие для игрока', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="gallery"]')).click();
  await until(() => window.document.querySelector('[data-remove="cover.jpg"]'));
  window.document.querySelector('[data-remove="cover.jpg"]').click();

  assert.ok(await until(() => window.document.querySelector('.modal')), 'вопрос не показан');
  assert.match(window.document.querySelector('.modal').textContent, /витрина останется с градиентом/);
  assert.strictEqual(calls.filter((c) => c.url.includes('gallery/delete')).length, 0);
});

/* ---------- Порядок игр ---------- */

test('пока ничего не переехало, сохранять нечего', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="order"]')).click();
  await until(() => window.document.querySelector('.order'));

  const save = window.document.querySelector('[data-flow="save"]');
  assert.strictEqual(save.disabled, true);
  assert.match(window.document.querySelector('.sheet').textContent, /сохранять нечего/);
});

test('перестановка называет последствие и уходит на сервер пересчитанной', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#games', '[data-act="order"]')).click();
  await until(() => window.document.querySelector('[data-down="repo"]'));
  window.document.querySelector('[data-down="repo"]').click();
  await until(() => !window.document.querySelector('[data-flow="save"]').disabled);

  assert.match(window.document.querySelector('.sheet').textContent, /Игроки увидят новый порядок сразу/);

  window.document.querySelector('[data-flow="save"]').click();
  assert.ok(await until(() => calls.some((c) => c.url.includes('games/save'))), 'порядок не сохранён');

  const sent = calls.find((c) => c.url.includes('games/save')).body.items;
  // Лаунчер помнит игру по её месту, поэтому номера пересчитываются целиком
  assert.deepStrictEqual(sent.map((g) => g.gameId), ['peak', 'repo']);
  assert.deepStrictEqual(sent.map((g) => g.order), [0, 1]);
  await settle(window);
});

/* ---------- Новость ---------- */

test('правка новости открывает её текст, а не чужой', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#news', '[data-act="edit-post"]')).click();
  assert.ok(await until(() => window.document.querySelector('[name="body"]')), 'редактор не открылся');

  assert.ok(calls.some((c) => c.url.includes('news/get')));
  assert.strictEqual(window.document.querySelector('[name="body"]').value, 'Текст с сервера');
});

test('черновик пишется в браузер по ходу набора', async (t) => {
  const { window } = await boot();
  t.after(() => window.close());

  (await go(window, '#news', '[data-act="edit-post"]')).click();
  await until(() => window.document.querySelector('[name="body"]'));

  const body = window.document.querySelector('[name="body"]');
  body.value = 'Дописал абзац';
  body.dispatchEvent(new window.Event('input', { bubbles: true }));

  assert.ok(
    await until(() => JSON.stringify(Object.entries(window.localStorage)).includes('Дописал абзац')),
    'черновик не сохранён'
  );
});

test('новость без заголовка на сервер не уходит', async (t) => {
  const { window, calls } = await boot({ 'news/get': { id: 'n1', title: '', body: 'Текст', gameId: '' } });
  t.after(() => window.close());

  (await go(window, '#news', '[data-act="edit-post"]')).click();
  await until(() => window.document.querySelector('[name="title"]'));

  const before = calls.length;
  window.document.querySelector('[data-flow="save"]').click();
  await until(() => /Не хватает/.test(window.document.body.textContent));

  assert.match(window.document.body.textContent, /Не хватает/);
  assert.strictEqual(calls.filter((c) => c.url.includes('news/save')).length, 0);
  assert.strictEqual(calls.length, before);
});

test('сохранение новости не выдаёт её за опубликованную', async (t) => {
  const { window, calls } = await boot();
  t.after(() => window.close());

  (await go(window, '#news', '[data-act="edit-post"]')).click();
  await until(() => window.document.querySelector('[name="title"]'));
  window.document.querySelector('[data-flow="save"]').click();

  assert.ok(await until(() => calls.some((c) => c.url.includes('news/save'))), 'новость не сохранена');
  await until(() => /уйдёт после публикации/.test(window.document.body.textContent));
  assert.match(window.document.body.textContent, /уйдёт после публикации/);
  await settle(window);
});

/* ---------- Подбор параметров ---------- */

test('прогон ничего не публикует и убирает за собой', async (t) => {
  const { window, calls } = await boot({ 'upload/init': { uploadId: 'bench-1', chunkSize: 4194304, totalChunks: 1 } });
  t.after(() => window.close());

  (await go(window, '#transfer', '[data-act="bench"]')).click();
  await until(() => window.document.querySelector('.sheet'));
  window.document.querySelector('[data-flow="run"]').click();

  assert.ok(await until(() => calls.filter((c) => c.url.includes('upload/abort')).length >= 3, 400), 'заявки не отменены');
  // Ни активации, ни сохранения — прогон только меряет
  assert.strictEqual(calls.filter((c) => /activate|games\/save|news\/save/.test(c.url)).length, 0);
});

test('после прогона видно, почему выбран именно этот набор', async (t) => {
  const { window } = await boot({ 'upload/init': { uploadId: 'bench-1', chunkSize: 4194304, totalChunks: 1 } });
  t.after(() => window.close());

  (await go(window, '#transfer', '[data-act="bench"]')).click();
  await until(() => window.document.querySelector('.sheet'));
  window.document.querySelector('[data-flow="run"]').click();

  assert.ok(await until(() => window.document.querySelector('tr.best'), 400), 'лучший прогон не помечен');
  assert.match(window.document.querySelector('.sheet').textContent, /прогон|повторами/);
});

/* ---------- Панель без сервера ---------- */

/* Сервер, не отвечающий ни на один запрос, — не выдуманный случай: так
   выглядит истёкшая сессия, упавший процесс и просто открытая на ночь
   вкладка. Панель обязана в этом состоянии показать снимок и сказать,
   что записывать нельзя, а не остаться скелетом навсегда. */
async function bootDead() {
  const html = fs.readFileSync(path.join(V2, 'index.html'), 'utf8');
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://example.test/admin/ui/v2/' });
  const { window } = dom;
  const errors = [];
  window.addEventListener('error', (e) => errors.push((e.error && e.error.message) || 'ошибка'));
  window.fetch = async () => ({ ok: false, status: 404, text: async () => 'not found' });

  for (const src of [...window.document.querySelectorAll('script[src]')].map((x) => x.getAttribute('src'))) {
    const file = path.resolve(V2, src);
    vm.runInContext(fs.readFileSync(file, 'utf8'), dom.getInternalVMContext(), { filename: file });
  }
  for (let i = 0; i < 60; i++) await new Promise((r) => setTimeout(r, 0));
  return { window, errors };
}

test('молчащий сервер не оставляет панель скелетом', async (t) => {
  const { window } = await bootDead();
  t.after(() => window.close());

  const h1 = window.document.querySelector('h1');
  assert.ok(h1 && h1.textContent.trim().length > 2, 'раздел не отрисовался');
  assert.match(window.document.body.textContent, /Сервер не отвечает/);
  assert.match(window.document.body.textContent, /записывать нельзя/);
});

test('на снимке открываются все разделы до единого', async (t) => {
  // Поля, которых нет в ответе сервера, ломали раздел молча
  const { window } = await bootDead();
  t.after(() => window.close());

  for (const id of ['overview', 'launcher', 'packs', 'games', 'news', 'inbox', 'maint', 'errors', 'transfer']) {
    window.location.hash = '#' + id;
    await until(() => window.document.querySelector('h1'));
    const h1 = window.document.querySelector('h1');
    assert.ok(h1 && h1.textContent.trim().length > 2, 'раздел ' + id + ' не открылся без сервера');
  }
});

test('на снимке открываются все длинные дела', async (t) => {
  const { window } = await bootDead();
  t.after(() => window.close());

  for (const [hash, act] of [
    ['#launcher', 'upload'],
    ['#packs', 'build'],
    ['#news', 'new-post'],
    ['#games', 'gallery'],
    ['#games', 'order'],
    ['#transfer', 'bench'],
  ]) {
    const btn = await go(window, hash, `[data-act="${act}"]`);
    assert.ok(btn, 'нет кнопки ' + act);
    btn.click();
    assert.ok(await until(() => window.document.querySelector('.sheet')), 'дело ' + act + ' не открылось');
    window.document.querySelector('[data-sheet-close]').click();
    await until(() => !window.document.querySelector('.sheet'));
  }
});
