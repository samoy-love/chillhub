/* Данные для превью админ-панели 2.0
   ------------------------------------------------------------------
   ЭТО НЕ БОЕВОЙ СЛОЙ ДАННЫХ. Правдоподобные ответы тех же эндпоинтов,
   что отдаёт админ-API, в той же форме. Точка перехода на настоящие
   данные — функция `load` внизу файла.

   Формы взяты из кода, а не выдуманы: см. adminapi/mods/handlers.go
   (Catalog, Resolve, Build, List, Activate, Diff, Cache),
   adminapi/feedback, adminapi/metrics, admin_ui/pending-badges.js.
   ------------------------------------------------------------------ */

(() => {
  'use strict';

  const rnd = (seed) => () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646;
  const r = rnd(20260904);
  const pick = (a) => a[Math.floor(r() * a.length)];

  /* --- Лаунчер: /admin/api/list, /admin/api/activate ---
     `pending` в pending-badges.js означает ровно одно: загружена версия
     новее активной, и человек ещё не решил, отдавать ли её игрокам. */

  const launcher = {
    active: '1.6.24',
    newest: '1.6.25',
    pending: true,
    versions: [
      { version: '1.6.25', date: '04.09.2026 03:12', files: 478, size: 121_400_000, state: 'uploaded' },
      { version: '1.6.24', date: '31.08.2026 22:41', files: 476, size: 121_100_000, state: 'active' },
      { version: '1.6.23', date: '28.08.2026 19:05', files: 476, size: 120_900_000, state: 'old' },
      { version: '1.6.22', date: '24.08.2026 14:22', files: 471, size: 120_200_000, state: 'old' },
      { version: '1.6.21', date: '19.08.2026 11:58', files: 471, size: 120_200_000, state: 'old' },
    ],
  };

  /* --- Дифф между активной и загруженной: /admin/api/... diff ---
     Именно ради этого списка дерево вообще существует: решение
     «активировать или нет» принимается по нему. */

  const launcherDiff = [
    { path: 'ChillHub.dll', size: 1_040_000, diff: 'mod' },
    { path: 'ChillHub.exe', size: 148_000, diff: 'mod' },
    { path: 'Core/Changelog/ChangelogGate.dll', size: 22_400, diff: 'add' },
    { path: 'Core/Changelog/ChangelogText.dll', size: 18_900, diff: 'add' },
    { path: 'Core/Net/OfflineMessage.dll', size: 9_800, diff: 'add' },
    { path: 'Core/Shell/ShortcutOpen.dll', size: 14_200, diff: 'add' },
    { path: 'Core/Shell/ShortcutTarget.dll', size: 11_500, diff: 'add' },
    { path: 'Pages/HomePage.xaml', size: 31_700, diff: 'mod' },
    { path: 'Pages/GamePage.xaml', size: 26_300, diff: 'mod' },
    { path: 'Assets/old-splash.png', size: 240_000, diff: 'del' },
    { path: 'ShortcutLaunchWindow.xaml', size: 8_400, diff: 'add' },
  ];

  /* Полный манифест — для фильтра и для контекста «сколько всего». */
  const DIRS = ['', 'Core/', 'Core/Game/', 'Core/Home/', 'Core/Net/', 'Pages/', 'Assets/', 'runtimes/win-x64/native/'];
  const manifest = launcherDiff.slice();
  for (let i = manifest.length; i < 478; i++) {
    manifest.push({
      path: `${pick(DIRS)}${pick(['System.Text.Json', 'Microsoft.Web.WebView2.Core', 'HomePage', 'GamePage', 'logo', 'icudt', 'PresentationCore'])}.${pick(['dll', 'dll', 'dll', 'json', 'xaml', 'png'])}`,
      size: Math.round(4096 + r() * 4_000_000),
      diff: '',
    });
  }

  /* --- Сборки модов: /admin/api/mods/list ---
     `behind` — на Thunderstore вышла версия новее собранной.
     `deprecated` — автор объявил пакет устаревшим.
     Ровно эти два признака зажигают значок в pending-badges.js. */

  const packs = [
    {
      gameId: 'lethal', title: 'Lethal Company', pack: 'ASTeam-LethalReloaded',
      active: '2.2.12', built: '2.2.12', builtAt: '28.08.2026 20:14',
      mods: 24, size: 398_000_000, behind: true, deprecated: false,
      upstream: { version: '2.3.0', at: '03.09.2026' },
    },
    {
      gameId: 'repo', title: 'R.E.P.O.', pack: 'ASTeam-MooModpack',
      active: '1.9.8', built: '1.9.9', builtAt: '03.09.2026 19:02',
      mods: 17, size: 251_000_000, behind: false, deprecated: false,
      upstream: { version: '1.9.9', at: '03.09.2026' },
    },
    {
      gameId: 'peak', title: 'PEAK', pack: 'ASTeam-PeakEssentials',
      active: '0.7.1', built: '0.7.1', builtAt: '02.09.2026 12:30',
      mods: 12, size: 534_000_000, behind: false, deprecated: true,
      upstream: { version: '0.7.1', at: '02.09.2026' },
    },
  ];

  /* --- Состав сборки: /admin/api/mods/resolve (предпросмотр без скачивания) --- */

  const resolved = [
    { name: 'BepInExPack', ns: 'BepInEx', version: '5.4.2100', size: 12_400_000, why: 'корневой пакет' },
    { name: 'MoreCompany', ns: 'notnotnotswipez', version: '1.11.0', size: 1_240_000, why: 'из модпака' },
    { name: 'LethalLib', ns: 'Evaisa', version: '0.16.2', size: 860_000, why: 'зависимость MoreCompany' },
    { name: 'ReservedItemSlot', ns: 'FlipMods', version: '2.0.6', size: 320_000, why: 'из модпака' },
    { name: 'LC_API', ns: 'Skyzooo', version: '3.4.1', size: 540_000, why: 'зависимость ReservedItemSlot' },
    { name: 'HotbarPlus', ns: 'FlipMods', version: '1.6.3', size: 210_000, why: 'из модпака' },
  ];

  /* --- Каталог Thunderstore: /admin/api/mods/catalog --- */

  const catalog = [
    { name: 'MooModpack', ns: 'ASTeam', version: '1.9.9', downloads: 41_200, updated: '03.09.2026', deprecated: false },
    { name: 'LethalCompanyChaos', ns: 'partyhard', version: '2.2.1', downloads: 18_900, updated: '01.09.2026', deprecated: false },
    { name: 'CozyCompany', ns: 'mellow', version: '0.8.4', downloads: 7_310, updated: '22.08.2026', deprecated: false },
    { name: 'OldSchoolPack', ns: 'archive', version: '1.0.0', downloads: 2_140, updated: '11.04.2026', deprecated: true },
  ];

  /* --- Журнал сборки: NDJSON-поток /admin/api/mods/build ---
     Сборка тянет до 1.8 ГБ полутора сотнями запросов и идёт до двадцати
     минут. Строки ниже — то, что реально прилетает в поток. */

  const buildLog = [
    { t: '19:02:11', k: 'info', m: 'разбор модпака ASTeam-MooModpack-1.9.9' },
    { t: '19:02:12', k: 'info', m: 'зависимостей после разрешения: 26' },
    { t: '19:02:14', k: 'cache', m: 'BepInEx-BepInExPack-5.4.2100 — из кэша' },
    { t: '19:02:14', k: 'get', m: 'notnotnotswipez-MoreCompany-1.11.0 — 1.2 МБ' },
    { t: '19:02:18', k: 'get', m: 'Evaisa-LethalLib-0.16.2 — 860 КБ' },
    { t: '19:03:02', k: 'warn', m: 'archive-OldSchoolPack-1.0.0 помечен автором устаревшим, пропущен' },
    { t: '19:04:41', k: 'info', m: 'скачано 24 из 26, 218 МБ' },
    { t: '19:05:03', k: 'info', m: 'раскладка файлов в BepInEx/plugins' },
    { t: '19:05:20', k: 'ok', m: 'версия 1.9.9 собрана, 251 МБ, хеши сошлись' },
  ];

  /* --- Кэш архивов: /admin/api/mods/cache --- */

  const cache = { files: 412, bytes: 8_900_000_000, oldest: '11.06.2026' };

  /* --- Игры: /admin/api/games, /admin/api/games/gallery --- */

  const games = [
    { gameId: 'howtofish', title: 'How To Fish', steamId: '2379780', exe: 'How To Fish.exe', gallery: 5, cover: true, icon: true },
    { gameId: 'peak', title: 'PEAK', steamId: '3527290', exe: 'PEAK.exe', gallery: 7, cover: true, icon: true },
    { gameId: 'repo', title: 'R.E.P.O.', steamId: '3241660', exe: 'REPO.exe', gallery: 4, cover: true, icon: true },
    { gameId: 'drivebeyond', title: 'Drive Beyond Horizons', steamId: '2947450', exe: 'DriveBeyondHorizons.exe', gallery: 0, cover: false, icon: false },
    { gameId: 'lethal', title: 'Lethal Company', steamId: '1966720', exe: 'Lethal Company.exe', gallery: 6, cover: true, icon: false },
  ];

  /* --- Новости: /admin/api/news/list --- */

  const news = [
    { id: 'n-141', title: 'Что за игра: R.E.P.O.', game: 'repo', at: '31.08.2026', state: 'published' },
    { id: 'n-140', title: 'Как перенести игры на другой диск', game: '', at: '29.08.2026', state: 'published' },
    { id: 'n-139', title: 'Какие моды идут в комплекте: Moo Modpack', game: 'repo', at: '30.08.2026', state: 'draft' },
    { id: 'n-138', title: 'Очередь загрузок: качается одна игра, остальные ждут', game: '', at: '27.08.2026', state: 'published' },
  ];

  /* --- Обращения: /admin/api/feedback/list --- */

  const inbox = [
    { id: 'f-2291', type: 'bug', name: 'Костя', contact: 'tg: @kostya', comment: 'На 87% скачивание обрывается и начинается заново. Третий раз подряд, интернет нормальный.', at: '04.09 01:22', status: 'new', important: true, logBytes: 184_000 },
    { id: 'f-2290', type: 'question', name: '', contact: '', comment: 'А можно ставить на диск D? У меня C почти забит.', at: '03.09 21:05', status: 'new', important: false, logBytes: 0 },
    { id: 'f-2289', type: 'idea', name: 'Аня', contact: 'anya@example.com', comment: 'Добавьте Deep Rock Galactic, у нас компания как раз на четверых.', at: '03.09 15:48', status: 'new', important: false, logBytes: 0 },
    { id: 'f-2288', type: 'bug', name: '', contact: '', comment: 'После обновления лаунчер не видит установленную Lethal Company.', at: '02.09 19:31', status: 'read', important: false, logBytes: 92_000 },
    { id: 'f-2287', type: 'other', name: 'Дима', contact: 'tg: @dmy', comment: 'Спасибо, всё работает.', at: '01.09 12:00', status: 'read', important: false, logBytes: 0 },
  ];

  const maint = { on: false, message: '', until: '' };

  /* --- Метрики: /admin/api/metrics/summary и /admin/api/metrics/errors ---
     Ошибки — единственное, ради чего сбор вообще существует: без кодов
     обрыв на большом файле не отличить от отказа сервера. */

  const days = [];
  for (let i = 29; i >= 0; i--) {
    const d = new Date(Date.UTC(2026, 8, 4) - i * 86400000);
    days.push({
      date: d.toISOString().slice(0, 10),
      launcherStarts: Math.round(40 + r() * 70),
      installs: Math.round(2 + r() * 12),
      updates: Math.round(10 + r() * 40),
      gameLaunches: Math.round(25 + r() * 60),
      errors: Math.round(r() * 6),
    });
  }

  const errors = [
    { code: 'download_reset', n: 34, share: 0.41, what: 'соединение оборвано на середине файла', where: 'lethal 2.2.12' },
    { code: 'hash_mismatch', n: 19, share: 0.23, what: 'файл скачался, но хеш не сошёлся', where: 'repo 1.9.9' },
    { code: 'disk_full', n: 12, share: 0.15, what: 'на диске игрока кончилось место', where: 'все игры' },
    { code: 'game_not_found', n: 9, share: 0.11, what: 'игра не установлена в Steam', where: 'peak 0.7.1' },
    { code: 'update_locked', n: 8, share: 0.10, what: 'файл занят другим процессом', where: 'лаунчер 1.6.24' },
  ];

  /* --- Подбор параметров загрузки: upload-bench / upload-tuning --- */

  const bench = [
    { at: '03.09 22:10', chunk: '8 МиБ', streams: 4, mbps: 92.4, retries: 0, best: true },
    { at: '03.09 22:04', chunk: '4 МиБ', streams: 4, mbps: 88.1, retries: 1 },
    { at: '03.09 21:58', chunk: '8 МиБ', streams: 2, mbps: 61.7, retries: 0 },
    { at: '03.09 21:51', chunk: '2 МиБ', streams: 8, mbps: 79.3, retries: 3 },
  ];

  const disk = { freeBytes: 214_000_000_000, totalBytes: 480_000_000_000 };

  /* ---------- Боевые данные ---------- */

  /* Панель читает те же эндпоинты, что и версия 1.0 (см. cmd/admin/routes.go).
     Каждая секция запрашивается отдельно и падает на демо-данные сама по
     себе: один недоступный эндпоинт не должен оставлять пустой всю панель,
     а молча показывать выдумку под видом прода — тем более.

     ПИШУЩИЕ ДЕЙСТВИЯ СЮДА НЕ ПОДКЛЮЧЕНЫ, И ЭТО РЕШЕНИЕ. Активация версии
     лаунчера, пересборка модпака, включение техработ и удаление версий
     необратимы для всех игроков сразу. Подключать их вслепую, без
     возможности прогнать на живой сессии, — не та цена за галочку
     «доделано». Кнопки по-прежнему честно говорят, что ничего не делают. */

  const demo = { launcher, launcherDiff, manifest, packs, resolved, catalog, buildLog, cache, games, news, inbox, maint, days, errors, bench, disk };

  async function get(path) {
    const r = await fetch('/admin/api/' + path, {
      headers: { accept: 'application/json' },
      credentials: 'same-origin',
    });
    if (!r.ok) throw new Error(path + ': ' + r.status);
    return r.json();
  }

  /* Один раздел. `pick` получает ответ и обязан вернуть данные в той форме,
     которую ждёт разметка, либо бросить — тогда останется демо. */
  async function section(live, key, path, pick) {
    try {
      const data = pick(await get(path));
      if (data == null) throw new Error(path + ': пустой ответ');
      live.add(key);
      return data;
    } catch {
      return demo[key];
    }
  }

  const arr = (v) => (Array.isArray(v) ? v : Array.isArray(v?.items) ? v.items : null);

  async function loadLive() {
    const live = new Set();

    const [versions, packList, gameList, newsList, inboxList, maintState, summary, errList, free] =
      await Promise.all([
        section(live, 'launcher', 'list', (d) => {
          const items = arr(d);
          if (!items) return null;
          return {
            active: items.find((v) => v.state === 'active' || v.active)?.version || '',
            newest: items[0]?.version || '',
            pending: items.some((v) => v.state === 'uploaded'),
            versions: items.map((v) => ({
              version: v.version,
              date: v.date || v.createdAt || '',
              files: v.files ?? 0,
              size: v.size ?? v.bytes ?? 0,
              state: v.state || (v.active ? 'active' : 'old'),
            })),
          };
        }),
        section(live, 'packs', 'mods/list', (d) => arr(d)),
        section(live, 'games', 'games', (d) => arr(d)),
        section(live, 'news', 'news/list', (d) => arr(d)),
        section(live, 'inbox', 'feedback/list', (d) => arr(d)),
        section(live, 'maint', 'maintenance/get', (d) =>
          d && typeof d.enabled === 'boolean' ? { on: d.enabled, reason: d.reason || '' } : null
        ),
        section(live, 'days', 'metrics/summary', (d) => arr(d?.days ?? d)),
        section(live, 'errors', 'metrics/errors', (d) => arr(d)),
        section(live, 'disk', 'system/free', (d) =>
          d && (d.freeBytes ?? d.free) != null
            ? { freeBytes: d.freeBytes ?? d.free, totalBytes: d.totalBytes ?? d.total ?? 0 }
            : null
        ),
      ]);

    return {
      ...demo,
      launcher: versions,
      packs: packList,
      games: gameList,
      news: newsList,
      inbox: inboxList,
      maint: maintState,
      days: summary,
      errors: errList,
      disk: free,
      live: [...live],
    };
  }

  window.CHILLHUB_DATA = {
    /* Панель открывают и вне сервера — просто файлом, чтобы посмотреть
       оформление. Признак живых данных считается по факту ответа, а не по
       адресу: за `/admin/ui/` может стоять и локальный прокси. */
    async load() {
      const data = await loadLive();
      data.demo = !data.live.length;
      return data;
    },
  };
})();
