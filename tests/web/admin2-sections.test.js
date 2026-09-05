// Разбор ответов админ-API и первый экран панели 2.0.
//
// Разбор проверяется на синонимах, которыми обросло API: список приходит
// то массивом, то в `items`, размер зовётся `size` и `bytes`, дата —
// `date` и `createdAt`. В панели 1.0 каждое место разбирало это по-своему,
// и одна игра выглядела по-разному на двух вкладках.
//
// Первый экран проверяется по правилу: решения — отдельно, наблюдение —
// отдельно. Смешаешь — и экран снова станет витриной цифр.

const test = require('node:test');
const assert = require('node:assert');

const S = require('../../server/admin_ui/v2/sections.js');

/* ---------- Общее ---------- */

test('список достаётся из любой из трёх форм ответа', () => {
  assert.deepStrictEqual(S.items([1, 2]), [1, 2]);
  assert.deepStrictEqual(S.items({ items: [1] }), [1]);
  assert.deepStrictEqual(S.items({ list: [3] }), [3]);
  assert.deepStrictEqual(S.items(null), []);
  assert.deepStrictEqual(S.items({ nope: 1 }), []);
});

test('пустая строка не считается значением поля', () => {
  // Иначе `title: ""` победил бы запасной вариант и строка осталась пустой
  assert.strictEqual(S.pick({ title: '', name: 'Имя' }, ['title', 'name'], '—'), 'Имя');
  assert.strictEqual(S.pick({}, ['title'], '—'), '—');
});

/* ---------- Лаунчер ---------- */

test('активная версия берётся по метке, а не по месту в списке', () => {
  const l = S.launcher({
    items: [
      { version: '1.6.23', state: 'old' },
      { version: '1.6.24', state: 'active' },
      { version: '1.6.25', state: 'uploaded' },
    ],
  });
  // Порядок ответа не гарантирован, и «первая» однажды оказалась старой
  assert.strictEqual(l.active, '1.6.24');
  assert.strictEqual(l.uploaded.length, 1);
  assert.strictEqual(l.pending, true);
});

test('без активной версии решать нечего', () => {
  const l = S.launcher({ items: [{ version: '1.0', state: 'uploaded' }] });
  assert.strictEqual(l.pending, false, 'первая публикация — это не «ждёт решения»');
});

test('без загруженной сверх активной решать тоже нечего', () => {
  const l = S.launcher({ items: [{ version: '1.0', state: 'active' }] });
  assert.strictEqual(l.pending, false);
});

test('пустой ответ лаунчера не роняет разбор', () => {
  const l = S.launcher(null);
  assert.deepStrictEqual(l.versions, []);
  assert.strictEqual(l.pending, false);
  assert.strictEqual(l.active, '');
});

test('размер и число файлов читаются из обоих имён', () => {
  const l = S.launcher([{ version: '1', bytes: 10, fileCount: 3, state: 'active' }]);
  assert.strictEqual(l.versions[0].size, 10);
  assert.strictEqual(l.versions[0].files, 3);
});

/* ---------- Игры ---------- */

test('игра без заголовка подписывается идентификатором', () => {
  const g = S.games([{ gameId: 'repo' }]);
  assert.strictEqual(g[0].title, 'repo');
});

test('снятая с публикации игра распознаётся, остальные считаются видимыми', () => {
  // В реестре поле называется `unpublished`, и нуль в нём означает «видно»:
  // иначе каждая игра, записанная до появления поля, пропала бы у игроков
  const g = S.games([{ gameId: 'a', unpublished: true }, { gameId: 'b' }, { gameId: 'c', published: false }]);
  assert.strictEqual(g[0].published, false);
  assert.strictEqual(g[1].published, true);
  assert.strictEqual(g[2].published, false);
});

test('идентификатор Steam берётся оттуда, где он лежит, — из модпака', () => {
  // На верхнем уровне его нет, и колонка была пустой у всех сразу
  const g = S.games([{ gameId: 'repo', mods: { steamAppId: '3241660' } }, { gameId: 'peak' }]);
  assert.strictEqual(g[0].steamId, '3241660');
  assert.strictEqual(g[1].steamId, '');
});

test('иконка считается по тому, записан ли её адрес', () => {
  const g = S.games([{ gameId: 'a', iconUrl: '/manifests/a/icon.png' }, { gameId: 'b' }]);
  assert.strictEqual(g[0].icon, true);
  assert.strictEqual(g[1].icon, false);
});

/* ---------- Сборки модов ---------- */

test('устаревание и отставание — разные признаки', () => {
  const p = S.packs([
    { gameId: 'a', behind: true },
    { gameId: 'b', deprecated: true },
    { gameId: 'c' },
  ]);
  assert.deepStrictEqual([p[0].behind, p[0].deprecated], [true, false]);
  assert.deepStrictEqual([p[1].behind, p[1].deprecated], [false, true]);
  assert.deepStrictEqual([p[2].behind, p[2].deprecated], [false, false]);
});

test('свежая версия с Thunderstore читается и из вложенного поля', () => {
  const p = S.packs([{ gameId: 'a', upstream: { version: '2.3.0' } }]);
  assert.strictEqual(p[0].latest, '2.3.0');
});

/* ---------- Обращения ---------- */

test('фильтры обращений совпадают с набором вкладки 1.0', () => {
  const list = S.inbox([
    { id: '1', type: 'bug', status: 'new', important: true, comment: 'обрывается', createdAt: '2026-09-01' },
    { id: '2', type: 'idea', status: 'read', comment: 'добавьте игру', createdAt: '2026-09-03' },
    { id: '3', type: 'bug', status: 'new', comment: 'не видит игру', createdAt: '2026-09-05' },
  ]);

  assert.deepStrictEqual(S.filterInbox(list, { type: 'bug' }).map((x) => x.id), ['1', '3']);
  assert.deepStrictEqual(S.filterInbox(list, { status: 'new' }).map((x) => x.id), ['1', '3']);
  assert.deepStrictEqual(S.filterInbox(list, { important: true }).map((x) => x.id), ['1']);
  assert.deepStrictEqual(S.filterInbox(list, { query: 'ИГРУ' }).map((x) => x.id), ['2', '3']);
  assert.deepStrictEqual(S.filterInbox(list, { from: '2026-09-03' }).map((x) => x.id), ['2', '3']);
  assert.deepStrictEqual(S.filterInbox(list, { to: '2026-09-03' }).map((x) => x.id), ['1', '2']);
});

test('пустой фильтр ничего не отсеивает', () => {
  const list = S.inbox([{ id: '1' }, { id: '2' }]);
  assert.strictEqual(S.filterInbox(list, {}).length, 2);
  assert.strictEqual(S.filterInbox(list).length, 2);
});

/* ---------- Технические работы ---------- */

test('запреты по умолчанию: каталог и обновления закрыты, запуск нет', () => {
  const m = S.maintenance({ enabled: true, reason: 'переезд' });
  assert.strictEqual(m.on, true);
  assert.strictEqual(m.blocks.install, true);
  assert.strictEqual(m.blocks.update, true);
  // Уже скачанное обязано запускаться: игра стартует локально
  assert.strictEqual(m.blocks.launch, false);
});

test('выключенные работы разбираются из пустого ответа', () => {
  const m = S.maintenance(null);
  assert.strictEqual(m.on, false);
  assert.strictEqual(m.reason, '');
});

/* ---------- Метрики ---------- */

test('доля ошибок считается по показанному списку, а не приходит с сервера', () => {
  const e = S.errors([{ code: 'a', n: 3 }, { code: 'b', n: 1 }]);
  assert.strictEqual(e[0].share, 0.75);
  assert.strictEqual(e[1].share, 0.25);
});

test('пустой список ошибок не делит на ноль', () => {
  assert.deepStrictEqual(S.errors([]), []);
  const one = S.errors([{ code: 'a', n: 0 }]);
  assert.strictEqual(one[0].share, 0);
});

test('дни метрик читаются и из обёртки, и из голого массива', () => {
  const a = S.metrics({ days: [{ date: '01', launcherStarts: 5 }] });
  const b = S.metrics([{ date: '01', starts: 5 }]);
  assert.strictEqual(a[0].starts, 5);
  assert.strictEqual(b[0].starts, 5);
});

/* ---------- Первый экран ---------- */

test('нечего решать — список решений пуст', () => {
  const d = S.decisions({
    launcher: S.launcher([{ version: '1', state: 'active' }]),
    packs: S.packs([{ gameId: 'a', built: '1', active: '1' }]),
  });
  assert.deepStrictEqual(d, []);
});

test('загруженная версия лаунчера становится решением с действием', () => {
  const d = S.decisions({
    launcher: S.launcher([
      { version: '1.6.25', state: 'uploaded' },
      { version: '1.6.24', state: 'active' },
    ]),
  });
  assert.strictEqual(d.length, 1);
  assert.match(d[0].title, /1\.6\.25/);
  assert.strictEqual(d[0].action, 'launcher.activate');
  assert.deepStrictEqual(d[0].args, { version: '1.6.25' });
});

test('отставший и устаревший модпаки дают разные советы', () => {
  const d = S.decisions({
    packs: S.packs([
      { gameId: 'a', title: 'A', behind: true, built: '1.0', upstream: { version: '2.0' } },
      { gameId: 'b', title: 'B', deprecated: true },
    ]),
  });
  assert.match(d[0].title, /Пересобрать/);
  assert.match(d[0].detail, /2\.0/);
  assert.match(d[1].title, /Заменить/);
  // Устаревшему пересборка не поможет — действия у него нет
  assert.strictEqual(d[1].action, undefined);
});

test('собранная, но не активированная сборка — тоже решение', () => {
  const d = S.decisions({
    packs: S.packs([{ gameId: 'a', title: 'A', built: '1.9.9', active: '1.9.8' }]),
  });
  assert.strictEqual(d.length, 1);
  assert.strictEqual(d[0].action, 'mods.activate');
  assert.deepStrictEqual(d[0].args, { gameId: 'a', version: '1.9.9' });
});

test('одна игра не порождает двух решений сразу', () => {
  // Отставший пакет, у которого ещё и собранное не активировано: звать
  // пересобирать и активировать одновременно — значит спорить с собой
  const d = S.decisions({
    packs: S.packs([{ gameId: 'a', title: 'A', behind: true, built: '1.0', active: '0.9' }]),
  });
  assert.strictEqual(d.length, 1);
  assert.match(d[0].title, /Пересобрать/);
});

test('наблюдение не попадает в решения', () => {
  const d = S.decisions({
    inbox: S.inbox([{ id: '1', status: 'new' }]),
    news: S.news([{ id: '1', published: false }]),
    maintenance: S.maintenance({ enabled: true }),
  });
  assert.deepStrictEqual(d, [], 'обращения и черновики решениями не являются');
});

test('наблюдение считает новые обращения и помечает важные', () => {
  const w = S.watch({
    inbox: S.inbox([
      { id: '1', status: 'new', important: true },
      { id: '2', status: 'new' },
      { id: '3', status: 'read' },
    ]),
  });
  const inbox = w.find((x) => x.id === 'inbox');
  assert.strictEqual(inbox.value, '2');
  assert.match(inbox.note, /1 помечено важным/);
});

test('включённые техработы окрашены тревожно и говорят про игроков', () => {
  const w = S.watch({ maintenance: S.maintenance({ enabled: true }) });
  const m = w.find((x) => x.id === 'maint');
  assert.strictEqual(m.value, 'включены');
  assert.strictEqual(m.tone, 'bad');
  assert.match(m.note, /игроки/);
});

test('кончающееся место на диске выделяется', () => {
  const low = S.watch({ disk: S.disk({ freeBytes: 5, totalBytes: 100 }) }).find((x) => x.id === 'disk');
  const ok = S.watch({ disk: S.disk({ freeBytes: 50, totalBytes: 100 }) }).find((x) => x.id === 'disk');
  assert.strictEqual(low.tone, 'bad');
  assert.strictEqual(ok.tone, '');
});

test('наблюдение переживает полностью пустые данные', () => {
  const w = S.watch({});
  assert.ok(w.length >= 4);
  for (const x of w) assert.ok(x.label && x.href, 'у показателя должны быть имя и адрес');
});

/* ---------- Загрузчики ---------- */

test('у каждого раздела есть загрузчик, и он зовёт свою ручку', async () => {
  const called = [];
  const api = new Proxy({}, {
    get: (_, name) => (...args) => {
      called.push(String(name));
      // Ответы разной формы: загрузчик обязан пережить любую
      return Promise.resolve({ items: [], enabled: false, days: [], freeBytes: 0 });
    },
  });

  for (const name of Object.keys(S.LOADERS)) {
    await S.LOADERS[name](api);
  }
  // «Сборки» сначала читают реестр: `mods/list` отвечает про одну игру
  // и без gameId даёт 400. Коды ошибок берутся из сводки — `metrics/errors`
  // отвечает событиями одного кода и без него тоже даёт 400
  assert.deepStrictEqual(called, [
    'summary', 'launcherVersions', 'games', 'games', 'games', 'newsList',
    'feedbackList', 'maintenanceGet', 'metricsSummary', 'metricsSummary',
    'freeSpace', 'modsCache',
  ]);
});

test('про моды спрашивают только игры, у которых они включены', async () => {
  // У остальных `mods/list` отвечает «у игры не включены моды» кодом 400
  const asked = [];
  const api = {
    games: async () => ({
      items: [
        { gameId: 'repo', title: 'R.E.P.O.', mods: { enabled: true } },
        { gameId: 'bodycam', title: 'Bodycam' },
        { gameId: 'peak', title: 'PEAK', mods: { enabled: false } },
      ],
    }),
    modsList: async (gameId) => {
      asked.push(gameId);
      return { items: [] };
    },
  };
  await S.LOADERS.packs(api);
  assert.deepStrictEqual(asked, ['repo']);
});

test('игра, ответившая ошибкой, не уносит с собой весь раздел', async () => {
  // Один упавший запрос из пяти не повод показать пустой список
  const api = {
    games: async () => ({
      items: [
        { gameId: 'a', title: 'А', mods: { enabled: true } },
        { gameId: 'b', title: 'Б', mods: { enabled: true } },
      ],
    }),
    modsList: async (gameId) => {
      if (gameId === 'a') throw new Error('сервер не в духе');
      return { gameId: 'b', title: 'Б', built: '1.0', active: '1.0' };
    },
  };
  const out = await S.LOADERS.packs(api);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].gameId, 'b');
});

test('в разделе новостей видны и лента лаунчера, и ленты игр', async () => {
  // У каждой игры своя лента: спрашивать только про лаунчер — значит не
  // показать половину написанного и не дать её править
  const asked = [];
  const api = {
    games: async () => ({ items: [{ gameId: 'repo', title: 'R.E.P.O.' }] }),
    newsList: async (scope, gameId) => {
      asked.push(scope + ':' + (gameId || '-'));
      return { items: [{ slug: (gameId || 'launcher') + '-note', title: 'Заметка' }] };
    },
  };
  const out = await S.LOADERS.news(api);
  assert.deepStrictEqual(asked, ['launcher:-', 'game:repo']);
  assert.deepStrictEqual(out.map((n) => n.scope), ['launcher', 'game']);
  assert.strictEqual(out[1].game, 'repo');
});

test('упавшая лента одной игры не уносит остальные', async () => {
  const api = {
    games: async () => ({ items: [{ gameId: 'a' }, { gameId: 'b' }] }),
    newsList: async (scope, gameId) => {
      if (gameId === 'a') throw new Error('нет такой ленты');
      return { items: [{ slug: 's', title: 'Есть' }] };
    },
  };
  const out = await S.LOADERS.news(api);
  assert.strictEqual(out.length, 2, 'потерялись ленты');
});

/* ---------- Строка сборки ---------- */

test('собранная версия берётся из списка: отдельным полем её нет', () => {
  // Раздел показывал пустое место ровно там, где решают «отдать игрокам»
  const row = S.packRow(
    {
      gameId: 'repo',
      active: '1.9.8',
      items: [
        { version: '1.9.9', displayName: 'Moo Modpack', createdAt: '2026-09-01', packages: 17, bytes: 251000000 },
        { version: '1.9.8', packages: 16 },
      ],
    },
    { gameId: 'repo', title: 'R.E.P.O.' }
  );
  assert.strictEqual(row.built, '1.9.9');
  assert.strictEqual(row.active, '1.9.8');
  assert.strictEqual(row.pack, 'Moo Modpack');
  assert.strictEqual(row.mods, 17);
  assert.strictEqual(row.size, 251000000);
});

test('«собрано, но не отдано» отличается от «Thunderstore ушёл вперёд»', () => {
  // Первое закрывается кнопкой, второе — пересборкой
  const staged = S.packRow({ gameId: 'g', active: '1.0', items: [{ version: '1.1' }] }, {});
  assert.strictEqual(staged.staged, true);
  assert.strictEqual(staged.behind, false);

  const behind = S.packRow(
    { gameId: 'g', active: '1.1', items: [{ version: '1.1' }], updates: [{ latest: '2.0' }] },
    {}
  );
  assert.strictEqual(behind.staged, false);
  assert.strictEqual(behind.behind, true);
  assert.strictEqual(behind.latest, '2.0');
});

test('устаревший пакет — не то же самое, что вышедший вперёд', () => {
  const row = S.packRow({ gameId: 'g', items: [{ version: '1.0' }], updates: [{ deprecated: true }] }, {});
  assert.strictEqual(row.deprecated, true);
  assert.strictEqual(row.behind, false, 'устаревший посчитали за отставший');
});

test('игра без единой сборки не роняет строку', () => {
  const row = S.packRow({ gameId: 'fresh', items: [] }, { gameId: 'fresh', title: 'Свежая' });
  assert.strictEqual(row.built, '');
  assert.strictEqual(row.staged, false);
  assert.strictEqual(row.title, 'Свежая');
});

test('пропавшие пакеты доезжают до строки списком, а не числом', () => {
  // «Пропущено 2» не говорит, потерялся ли твик текстур или сам модпак
  const row = S.packRow({ gameId: 'g', items: [{ version: '1.0', missing: ['Ura/Old'] }] }, {});
  assert.deepStrictEqual(row.missing, ['Ura/Old']);
});

test('признак «собрано, но не отдано» одинаков у снимка и у ответа сервера', () => {
  // Два одинаковых условия в разных местах расходятся молча — и как раз
  // на краях: у игры без сборок и у игры без активной версии
  const fromServer = S.packRow({ gameId: 'g', active: '1.0', items: [{ version: '1.1' }] }, {});
  const fromSnapshot = S.packs([{ gameId: 'g', built: '1.1', active: '1.0' }])[0];
  assert.strictEqual(fromServer.staged, fromSnapshot.staged);

  assert.strictEqual(S.isStaged('', ''), false, 'игра без сборок числится ждущей');
  assert.strictEqual(S.isStaged('1.0', ''), true, 'первая сборка не считается ждущей');
  assert.strictEqual(S.isStaged('', '1.0'), false);
  assert.strictEqual(S.isStaged('1.0', '1.0'), false);
});

/* ---------- Итоги за период ---------- */

test('экономия считается от того, сколько весила бы полная загрузка', () => {
  // «40 МБ перевезено» без «вместо 12 ГБ» не значит ничего, а вместе
  // это единственная цифра про смысл разностной синхронизации
  const t = S.totals({ totals: { bytesDownloaded: 40 * 1024 ** 2, fullBytes: 12 * 1024 ** 3 } });
  assert.strictEqual(t.moved, 40 * 1024 ** 2);
  assert.ok(t.saved > 0);
  assert.ok(t.savedShare > 0.99 && t.savedShare < 1);
});

test('без полного размера экономия не выдумывается', () => {
  const t = S.totals({ totals: { bytesDownloaded: 100 } });
  assert.strictEqual(t.saved, 0);
  assert.strictEqual(t.savedShare, 0);
});

test('доля отказов считается от попыток, а не от всех событий', () => {
  // Иначе она тонет: событий тысячи, а установок сотни
  const t = S.totals({ totals: { installs: 200, updates: 800, installFail: 4, updateFail: 20, events: 100000 } });
  assert.strictEqual(t.tries, 1000);
  assert.strictEqual(t.failed, 24);
  assert.ok(Math.abs(t.failShare - 0.024) < 1e-9);
});

test('проверки целостности доезжают до панели', () => {
  // Игрок, проверяющий свои файлы, был не виден в панели, которая
  // существует ровно для того, чтобы это замечать
  const t = S.totals({ totals: { integrityChecks: 120, integrityFailed: 3 } });
  assert.strictEqual(t.checks, 120);
  assert.strictEqual(t.checksFailed, 3);
  assert.ok(Math.abs(t.checksShare - 0.025) < 1e-9);
});

test('пустая сводка даёт нули, а не NaN', () => {
  const t = S.totals({});
  for (const k of ['moved', 'full', 'saved', 'savedShare', 'checks', 'tries', 'failShare']) {
    assert.strictEqual(Number.isFinite(t[k]), true, k + ' = ' + t[k]);
  }
});

test('дни и итоги приезжают одной сводкой, а не тремя запросами', async () => {
  let asked = 0;
  const raw = {
    byDay: [{ date: '2026-09-04', launcherStarts: 10 }],
    totals: { bytesDownloaded: 5, fullBytes: 50 },
  };
  const out = await S.LOADERS.metrics({
    metricsSummary: async () => {
      asked++;
      return raw;
    },
  });
  assert.strictEqual(asked, 1);
  assert.strictEqual(out.days.length, 1);
  assert.strictEqual(out.totals.moved, 5);
});

test('признак «иконка есть» не подставляется в поле адреса', () => {
  // Снимок кладёт в `icon` булево, и в поле правки уезжало слово «true»
  const [a, b, c] = S.games([{ gameId: 'a', icon: true }, { gameId: 'b', iconUrl: '/x.png' }, { gameId: 'c' }]);
  assert.strictEqual(a.iconUrl, '', 'булево попало в адрес');
  assert.strictEqual(a.icon, true, 'потерян признак наличия иконки');
  assert.strictEqual(b.iconUrl, '/x.png');
  assert.strictEqual(c.icon, false);
});
