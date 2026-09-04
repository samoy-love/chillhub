// Вкладка «Моды»: чтение потока сборки и разметка каталога/версий.
//
// Чистые функции проверяются здесь, а не через DOM: разметка карточки и таблицы
// версий — это то, что операторпоказывает себе перед выкаткой модпака на
// игроков, и молча уехавшее поле («доступна новая версия», «пропущено 3 мода»)
// стоит дороже, чем сломавшаяся кнопка.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const {
  splitNdjson, parseEvents, readNdjsonStream,
} = require('../../server/admin_ui/ndjson.js');

const {
  catalogCardHtml, versionsTableHtml, diffHtml, formatCount, formatDate,
} = require('../../server/admin_ui/mods-panel.js');

// Время в панели общее: в браузере admin-time.js кладёт форматтер в window,
// здесь — в globalThis. Без него таблица версий печатала бы отметку сервера
// как есть, то есть UTC под видом местного времени.
Object.assign(globalThis, require('../../server/admin_ui/admin-time.js'));

// ---------- NDJSON ----------

test('splitNdjson отдаёт готовые строки и оставляет хвост', () => {
  const first = splitNdjson('{"a":1}\n{"b":2}\n{"c":');
  assert.deepStrictEqual(first.lines, ['{"a":1}', '{"b":2}']);
  // Чанк почти никогда не кончается ровно на переводе строки — незакрытый
  // объект обязан дождаться следующего куска, а не разобраться как мусор.
  assert.strictEqual(first.rest, '{"c":');

  const second = splitNdjson(first.rest + '3}\n');
  assert.deepStrictEqual(second.lines, ['{"c":3}']);
  assert.strictEqual(second.rest, '');
});

test('splitNdjson понимает CRLF и пустые строки', () => {
  const out = splitNdjson('{"a":1}\r\n\r\n{"b":2}\r\n');
  assert.deepStrictEqual(out.lines, ['{"a":1}', '{"b":2}']);
});

test('parseEvents пропускает битую строку, но не теряет соседние', () => {
  const events = parseEvents(['{"type":"start"}', 'не json', '{"type":"done"}']);
  assert.deepStrictEqual(events.map((e) => e.type), ['start', 'done']);
});

// fakeStreamResponse изображает ответ с телом-потоком.
function fakeStreamResponse(chunks) {
  const enc = new TextEncoder();
  let i = 0;
  return {
    body: {
      getReader() {
        return {
          read() {
            return i < chunks.length
              ? Promise.resolve({ done: false, value: enc.encode(chunks[i++]) })
              : Promise.resolve({ done: true, value: undefined });
          },
        };
      },
    },
  };
}

test('readNdjsonStream собирает события из кусков произвольной нарезки', async () => {
  const seen = [];
  // Разрез приходится на середину и ключа, и русского слова: TextDecoder со
  // stream:true обязан склеить многобайтовый символ через границу чанка.
  const n = await readNdjsonStream(
    fakeStreamResponse(['{"type":"start","message":"Ска', 'чив', 'ание"}\n{"type":"done"}\n']),
    (e) => seen.push(e));

  assert.strictEqual(n, 2);
  assert.deepStrictEqual(seen.map((e) => e.type), ['start', 'done']);
  assert.strictEqual(seen[0].message, 'Скачивание');
});

test('readNdjsonStream не теряет последнее событие без перевода строки', async () => {
  const seen = [];
  const n = await readNdjsonStream(fakeStreamResponse(['{"type":"done"}']), (e) => seen.push(e));
  assert.strictEqual(n, 1);
  assert.strictEqual(seen[0].type, 'done');
});

test('readNdjsonStream работает и на буферизованном ответе без потока', async () => {
  // Прокси между админкой и сервером может отдать тело целиком в конце —
  // тогда res.body.getReader отсутствует, а события всё равно должны дойти.
  const seen = [];
  const n = await readNdjsonStream(
    { text: () => Promise.resolve('{"type":"start"}\n{"type":"error","message":"нет места"}\n') },
    (e) => seen.push(e));

  assert.strictEqual(n, 2);
  assert.strictEqual(seen[1].message, 'нет места');
});

test('readNdjsonStream переживает отсутствие обработчика', async () => {
  assert.strictEqual(await readNdjsonStream(fakeStreamResponse(['{"type":"x"}\n']), null), 1);
});

// ---------- разметка ----------

test('карточка каталога показывает автора, загрузки и метку устаревшего', () => {
  const html = catalogCardHtml({
    namespace: 'ASTeam',
    name: 'LethalReloaded',
    description: 'Большой модпак',
    icon_url: 'https://gcdn.example/icon.png',
    download_count: 2699026,
    rating_count: 52,
    last_updated: new Date(Date.now() - 3 * 86400000).toISOString(),
    is_deprecated: true,
  }, Date.now());

  assert.match(html, /LethalReloaded/);
  assert.match(html, /ASTeam/);
  assert.match(html, /2\.7M/);
  assert.match(html, /устарел/);
  assert.match(html, /3 дн\. назад/);
  assert.match(html, /data-mc-readme="ASTeam\/LethalReloaded"/);
  assert.match(html, /data-mc-build="ASTeam\/LethalReloaded"/);
});

test('карточка каталога не показывает размер пакета', () => {
  // В листинге Thunderstore size — это вес архива самого модпака, а он почти
  // пустой: у LethalReloaded 9 МБ против 1.8 ГБ настоящего дерева. Показать
  // это число значит соврать оператору о том, сколько будет качаться.
  const html = catalogCardHtml({
    namespace: 'A', name: 'B', size: 9108334, download_count: 1, rating_count: 0,
  }, Date.now());
  assert.doesNotMatch(html, /9108334|8\.7 ?МБ|9 ?МБ/);
});

test('карточка каталога экранирует чужой текст', () => {
  const html = catalogCardHtml({
    namespace: 'A', name: 'B',
    description: '<img src=x onerror=alert(1)>',
    icon_url: '"><script>bad()</script>',
  }, Date.now());
  assert.doesNotMatch(html, /<img src=x/);
  assert.doesNotMatch(html, /<script>bad/);
});

test('таблица версий отмечает активную, обновление и пропущенные моды', () => {
  const html = versionsTableHtml({
    active: 'ASTeam-LethalReloaded-2.2.12',
    items: [
      {
        version: 'ASTeam-LethalReloaded-2.2.12',
        displayName: 'Lethal Reloaded',
        packageUrl: 'https://thunderstore.io/c/lethal-company/p/ASTeam/LethalReloaded/',
        active: true, packages: 151, files: 2400, bytes: 123, missing: [],
        createdAt: '2026-08-27T10:00:00', rebuildable: true,
      },
      {
        version: 'Other-Pack-1.0.0', displayName: 'Other', active: false,
        packages: 10, files: 40, bytes: 5, createdAt: '2026-08-20T10:00:00',
        missing: ['Some-Tweak-1.0.0', 'Some-Skin-2.0.0', 'Some-Map-3.0.0'],
        rebuildable: true,
      },
    ],
    updates: [{
      version: 'ASTeam-LethalReloaded-2.2.12',
      namespace: 'ASTeam', name: 'LethalReloaded', latest: '2.2.13', deprecated: false,
    }],
  });

  assert.match(html, /Lethal Reloaded/);
  assert.match(html, /активен/);
  assert.match(html, /доступна 2\.2\.13/);
  // ЗНАЧОК НАЗЫВАЕТ ПРОПАВШИЕ МОДЫ: по числу нельзя понять, потерялся ли твик
  // текстур или мод, ради которого пакет и собирали.
  assert.match(html, /собран без 3 модов/);
  assert.match(html, /Some-Tweak-1\.0\.0, Some-Skin-2\.0\.0, Some-Map-3\.0\.0/);
  // У версии без потерь значка нет вовсе.
  assert.strictEqual(html.match(/собран без \d/g).length, 1);
  // Активную версию нельзя ни удалить, ни активировать повторно.
  assert.doesNotMatch(html, /data-md-delete="ASTeam-LethalReloaded-2\.2\.12"/);
  assert.doesNotMatch(html, /data-md-activate="ASTeam-LethalReloaded-2\.2\.12"/);
  assert.match(html, /data-md-activate="Other-Pack-1\.0\.0"/);
  // «Собрать 2.2.13» берёт с Thunderstore другую версию пакета; «Пересобрать»
  // раскладывает эту же. Раньше обе назывались «Пересобрать», и по названию
  // было не понять, что нажатие опубликует НОВУЮ версию.
  assert.match(html, /data-md-newer="ASTeam\/LethalReloaded" data-md-newer-version="2\.2\.13"[^>]*>Собрать 2\.2\.13</);
  assert.match(html, /data-md-again="ASTeam-LethalReloaded-2\.2\.12"/);
});

test('«Пересобрать» выключена у версии, состав которой не записан', () => {
  // Импорт профиля r2modman до появления записи о составе пересобрать не из
  // чего. Кнопка, которая на нажатие отвечает ошибкой сервера, — хуже
  // выключенной: выключенная объясняет себя подписью.
  const html = versionsTableHtml({
    items: [
      {
        version: 'lethal-1.0.7', displayName: 'Импорт', active: false,
        createdAt: '2026-08-27T10:00:00', rebuildable: false,
      },
      {
        version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true,
        createdAt: '2026-08-20T10:00:00', rebuildable: true,
      },
    ],
  });
  assert.match(html, /data-md-again="lethal-1\.0\.7" disabled title="Версия импортирована/);
  assert.doesNotMatch(html, /data-md-again="Team-Pack-1\.0\.0"[^>]*disabled/);
  // У активной версии кнопка помечена: подтверждение перед пересборкой обязано
  // сказать, что заменяет то, что игроки качают прямо сейчас.
  assert.match(html, /data-md-again="Team-Pack-1\.0\.0" data-md-again-active=""/);
});

test('пересечения между пакетами видны в списке версий', () => {
  const html = versionsTableHtml({
    items: [{
      version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true,
      createdAt: '2026-08-27T10:00:00', rebuildable: true,
      collisions: [
        { kind: 'path', what: 'BepInEx/core/BepInEx.dll', by: ['A-Core-1.0.0', 'B-Core-2.0.0'] },
        { kind: 'assembly', what: 'DriverMod.dll', by: ['rob_gaming-Driver-1.0.0', 'public_ParticleSystem-Driver-2.0.0'] },
      ],
    }],
  });
  assert.match(html, /2 пересечения/);
  // ЗНАЧОК НАЗЫВАЕТ МЕСТА И ПАКЕТЫ: по одному числу нельзя решить, спорят ли
  // два README или две DLL с одним именем, из которых загрузчик возьмёт одну.
  assert.match(html, /файл BepInEx\/core\/BepInEx\.dll — A-Core-1\.0\.0 и B-Core-2\.0\.0/);
  assert.match(html, /DLL DriverMod\.dll — rob_gaming-Driver-1\.0\.0 и public_ParticleSystem-Driver-2\.0\.0/);
});

test('время сборки показано по Москве и подписано', () => {
  // Отметка сервера — UTC. «собран 2026-08-30 21:43:27» читается как местное
  // время и расходится с часами на три часа, а у сборки, сделанной поздно
  // вечером, съезжает ещё и дата.
  const html = versionsTableHtml({
    items: [{
      version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true,
      createdAt: '2026-08-30T21:43:27Z', rebuildable: true,
    }],
  });

  assert.match(html, /собран 2026-08-31 00:43:27 МСК/);
  assert.doesNotMatch(html, /21:43:27/);
});

test('«Дифф» у единственной версии выключен и говорит почему', () => {
  // Кнопка, которая на нажатие отвечает тостом «сравнивать не с чем», просит
  // нажать себя ради отказа. Выключенная — обязана объяснить себя подписью.
  const one = versionsTableHtml({
    items: [{ version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true, createdAt: '2026-08-27T10:00:00' }],
  });
  assert.match(one, /data-md-diff="Team-Pack-1\.0\.0" disabled title="Собрана одна версия/);

  const two = versionsTableHtml({
    items: [
      { version: 'Team-Pack-1.0.0', displayName: 'Pack', active: true, createdAt: '2026-08-27T10:00:00' },
      { version: 'Team-Pack-0.9.0', displayName: 'Pack', active: false, createdAt: '2026-08-20T10:00:00' },
    ],
  });
  assert.doesNotMatch(two, /data-md-diff="Team-Pack-1\.0\.0" disabled/);
  assert.match(two, /title="Сравнить состав/);
});

test('до четырёх версий показываются карточками, дальше таблицей', () => {
  // Семь заголовков на одну строку занимают ровно столько же места, сколько
  // данные, и читатель ищет в них то, что и так написано в первой ячейке.
  const one = (n) => ({
    items: Array.from({ length: n }, (_, i) => ({
      version: 'Team-Pack-1.0.' + i,
      displayName: 'Pack',
      active: i === 0,
      packages: 3, files: 7, bytes: 1024, missing: 0,
      createdAt: '2026-08-27T10:00:00',
    })),
  });

  assert.doesNotMatch(versionsTableHtml(one(1)), /<table/);
  assert.doesNotMatch(versionsTableHtml(one(3)), /<table/);
  assert.match(versionsTableHtml(one(4)), /<table/);

  // Действия и метки одинаковы в обеих разметках: иначе они разъедутся на
  // первой же правке.
  for (const html of [versionsTableHtml(one(2)), versionsTableHtml(one(5))]) {
    assert.match(html, /data-md-activate="Team-Pack-1\.0\.1"/);
    assert.match(html, /активен/);
    assert.match(html, /title="Сравнить состав/);
  }
});

test('пустой список версий объясняется словами', () => {
  assert.match(versionsTableHtml({ items: [] }), /Ни одного модпака ещё не собрано/);
  assert.match(versionsTableHtml(null), /Ни одного модпака/);
});

test('дифф состава называет добавленные, удалённые и обновлённые моды', () => {
  const html = diffHtml([
    { package: 'A-New', to: '1.0.0', change: 'added' },
    { package: 'B-Gone', from: '2.0.0', change: 'removed' },
    { package: 'C-Bumped', from: '1.0.0', to: '1.1.0', change: 'updated' },
  ]);
  assert.match(html, /добавлен.*A-New/s);
  assert.match(html, /удалён.*B-Gone/s);
  assert.match(html, /1\.0\.0 → 1\.1\.0/);
  assert.match(diffHtml([]), /Состав не изменился/);
});

test('formatCount и formatDate дают короткие подписи', () => {
  assert.strictEqual(formatCount(999), '999');
  assert.strictEqual(formatCount(2500), '2.5K');
  assert.strictEqual(formatCount(2699026), '2.7M');
  assert.strictEqual(formatCount(undefined), '0');

  const now = Date.parse('2026-08-27T12:00:00Z');
  assert.strictEqual(formatDate('2026-08-27T09:00:00Z', now), 'сегодня');
  assert.strictEqual(formatDate('2026-08-26T09:00:00Z', now), 'вчера');
  assert.strictEqual(formatDate('2026-08-01T09:00:00Z', now), '26 дн. назад');
  assert.strictEqual(formatDate('2026-01-01T09:00:00Z', now), '7 мес. назад');
  assert.strictEqual(formatDate('', now), '');
});

// ---------- браузерный режим ----------

function loadAsBrowserScript(relPath) {
  const abs = path.join(__dirname, '..', '..', relPath);
  const src = fs.readFileSync(abs, 'utf8');
  const sandbox = { window: {} };
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: abs });
  return sandbox.window;
}

test('ndjson.js и mods-panel.js в браузерном режиме кладут функции в window', () => {
  const n = loadAsBrowserScript('server/admin_ui/ndjson.js');
  assert.strictEqual(typeof n.readNdjsonStream, 'function');
  assert.strictEqual(typeof n.splitNdjson, 'function');

  const m = loadAsBrowserScript('server/admin_ui/mods-panel.js');
  assert.strictEqual(typeof m.createModsPanel, 'function');
  assert.strictEqual(typeof m.versionsTableHtml, 'function');
});
