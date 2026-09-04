/* Данные сайта: тот же публичный API, что у лаунчера
   ------------------------------------------------------------------
   Сайт больше ничего не рассказывает о продукте из свёрстанного текста.
   Список игр, версии сборок, названия модпаков, новости и режим
   технических работ приходят из тех же эндпоинтов, которые опрашивает
   сам лаунчер:

     GET /api/games                        — каталог (GameInfo[])
     GET /news/index.json                  — новости лаунчера
     GET /news/games/{gameId}/index.json    — новости игры
     GET /api/maintenance                  — технические работы
     GET /manifests/launcher/latest.json   — версия лаунчера

   Формы ответов — из server/cmd/api/main.go (GameInfo, ModsInfo),
   internal/adminapi/news (newsItem) и internal/maintenance (State).

   Когда API недоступен — открыли index.html файлом, сервер лежит, сайт
   смотрят с чужого домена — в дело идут моки ниже. Они не «примерные»:
   это снимок настоящего ответа, поэтому вёрстка на них не расходится с
   продом ни по составу полей, ни по длине строк.
   ------------------------------------------------------------------ */

(() => {
  'use strict';

  /* ---------- Моки ---------- */

  /* Снимок настоящего ответа `/api/games`, а не выдумка: те же
     идентификаторы, версии, модпаки и адреса значков, что отдаёт прод.
     Четыре игры из восьми идут без модпака (`mods: null`) — у них на
     витрине нет кнопок запуска, только действие, и это тоже надо видеть.

     Откуда что берётся (проверено по коду, а не по догадке):

       значок   `iconUrl` в GameInfo -> /manifests/{gameId}/icon.png
                (adminapi/games/games.go: IconUpload кладёт файл с этим
                фиксированным именем и возвращает этот адрес)
       обложка  первый кадр галереи -> /content/{gameId}/gallery/gallery.json
                (HomePage.LoadHeroGalleryAsync берёт images.First())
       новость  `coverUrl` из /news/index.json
                (HomeFeed.NormalizeCoverUrls дописывает базу к пути от корня)

     Адреса значков здесь абсолютные: превью открывают и локально, где
     /manifests/ не раздаётся. На проде мок не используется вовсе —
     адреса приходят от API относительными, как и должно быть. */

  const LIVE = 'https://launcher.samoy.love';
  const iconOf = (gameId) => `${LIVE}/manifests/${gameId}/icon.png`;

  const modpack = (displayName, displayVersion, community, steamAppId) => ({
    hasLatest: true,
    version: `${displayName}-${displayVersion}`,
    displayName,
    displayVersion,
    community,
    loader: 'bepinex',
    steamAppId,
  });

  const MOCK_GAMES = {
    items: [
      { gameId: 'bodycam', title: 'Bodycam', hasLatest: true, latestVersion: '1.0.0', iconUrl: iconOf('bodycam'), exeRelativePath: 'Bodycam.exe' },
      { gameId: 'farfarwest', title: 'Far Far West', hasLatest: true, latestVersion: '1.0.0', iconUrl: iconOf('farfarwest'), exeRelativePath: 'FarFarWest.exe' },
      { gameId: 'how-to-fish', title: 'How To Fish', hasLatest: true, latestVersion: '1.0.0', iconUrl: iconOf('how-to-fish'), exeRelativePath: 'How To Fish.exe',
        mods: modpack('Enhanced HowToFish', '1.0.8', 'how-to-fish', '4001890') },
      { gameId: 'repo', title: 'R.E.P.O.', hasLatest: true, latestVersion: '1.0.1', iconUrl: iconOf('repo'), exeRelativePath: 'REPO.exe',
        mods: modpack('Moo Modpack', '1.9.9', 'repo', '3241660') },
      { gameId: 'lethal-company', title: 'Lethal Company', hasLatest: true, latestVersion: '1.0.9', iconUrl: iconOf('lethal-company'), exeRelativePath: 'Lethal Company.exe',
        mods: modpack('LethalReloaded', '2.2.12', 'lethal-company', '1966720') },
      { gameId: 'drive-beyond-horizons', title: 'Drive Beyond Horizons', hasLatest: true, latestVersion: '1.1.0', iconUrl: iconOf('drive-beyond-horizons'), exeRelativePath: 'DriveBeyondHorizons.exe' },
      { gameId: 'peak', title: 'PEAK', hasLatest: true, latestVersion: '1.0.1', iconUrl: iconOf('peak'), exeRelativePath: 'PEAK.exe',
        mods: modpack('PeakFriendsEdition', '1.9.1', 'peak', '3527290') },
      { gameId: 'machine-party', title: 'Machine Party', hasLatest: true, latestVersion: '1.0.1', iconUrl: iconOf('machine-party'), exeRelativePath: 'MachineParty.exe' },
    ],
  };

  const MOCK_NEWS = {
    items: [
      {
        id: 'n-141',
        title: 'Что за игра: R.E.P.O.',
        slug: 'chto-za-igra-repo',
        createdAt: '2026-08-31T10:00:00Z',
        summary: 'Ваза стоит десять тысяч, пока не встретится с дверным косяком.',
        coverUrl: '/assets/images/repo.jpg',
        published: true,
      },
      {
        id: 'n-140',
        title: 'Какие моды идут в комплекте: Moo Modpack',
        slug: 'moo-modpack',
        createdAt: '2026-08-30T10:00:00Z',
        summary: 'Пять новых объектов и сотни ценностей, но главный мод в сборке — сканер по клавише F.',
        coverUrl: '',
        published: true,
      },
      {
        id: 'n-139',
        title: 'Очередь загрузок: качается одна игра, остальные ждут',
        slug: 'ochered-zagruzok',
        createdAt: '2026-08-27T10:00:00Z',
        summary: 'Правильные значки, честный объём и никаких вылетов из-за одного пропавшего файла.',
        coverUrl: '/assets/images/launcher-updater.jpg',
        published: true,
      },
    ],
  };

  const MOCK_MAINT = { enabled: false, reason: '', blocks: { install: false, update: false, launch: false } };

  const MOCK_LAUNCHER = { version: '1.6.25' };

  /* --- Галерея игры: /content/{gameId}/gallery/gallery.json ---
     Отдельный запрос, не часть /api/games: лаунчер ходит за ней так же
     (Core/Game/GalleryClient.cs). Формат — {cover, items:[{file,caption}]},
     адреса картинок относительные, база — папка самой галереи. */

  const MOCK_GALLERY = {
    repo: { cover: 'cover.jpg', items: [{ file: 'cover.jpg', caption: 'Смена на объекте' }] },
    'lethal-company': { cover: 'cover.jpg', items: [{ file: 'cover.jpg', caption: 'Спуск на луну' }] },
  };

  /* Локальная подмена кадра: сам gallery.json с чужого домена не читается
     (на /content/ нет CORS), а на проде мок и не нужен — там галерея
     своя и своим origin. */
  const MOCK_GALLERY_FILE = { repo: '/assets/images/repo.jpg', 'lethal-company': '/assets/images/lethal.jpg' };

  const galleryBase = (gameId) => `/content/${encodeURIComponent(gameId)}/gallery/`;

  /* Обложка идёт первой и не дублируется, порядок остальных — как в
     items. Нет манифеста (404, сеть) — пустой список, а не исключение. */
  function orderGallery(manifest, base, mockFile) {
    const cover = (manifest.cover || '').trim();
    const items = Array.isArray(manifest.items) ? manifest.items : [];
    const url = (file) => (mockFile ? mockFile : base + String(file).replace(/^\/+/, ''));

    const out = [];
    if (cover) out.push({ url: url(cover), caption: '', isCover: true });
    items.forEach((i) => {
      if (!i || !i.file) return;
      if (cover && i.file === cover) return;
      out.push({ url: url(i.file), caption: i.caption || '', isCover: false });
    });
    return out;
  }

  const galleryCache = new Map();

  async function gallery(gameId) {
    if (!gameId) return [];
    if (galleryCache.has(gameId)) return galleryCache.get(gameId);

    let result = [];
    try {
      const r = await fetch(galleryBase(gameId) + 'gallery.json', { headers: { accept: 'application/json' } });
      if (r.ok) result = orderGallery(await r.json(), galleryBase(gameId), null);
    } catch {
      /* ниже — мок или пустой список */
    }
    if (!result.length && MOCK_GALLERY[gameId]) {
      result = orderGallery(MOCK_GALLERY[gameId], galleryBase(gameId), MOCK_GALLERY_FILE[gameId]);
    }
    galleryCache.set(gameId, result);
    return result;
  }

  /* ---------- Загрузка ---------- */

  /* Один общий разбор ответа. Сеть отвечает четырьмя способами — не
     ответила, ответила ошибкой, ответила не тем, ответила как надо, — и
     первые три для страницы означают одно: берём мок и живём дальше. */
  async function get(url, mock) {
    try {
      const r = await fetch(url, { headers: { accept: 'application/json' } });
      if (!r.ok) return { data: mock, live: false };
      const data = await r.json();
      return { data, live: true };
    } catch {
      return { data: mock, live: false };
    }
  }

  /* Размер, дата сборки и SHA-256 установщика. Их нельзя ни вычислить на
     странице, ни свёрстать: свёрстанный хеш — это опубликованная рядом с
     кнопкой скачивания ЛОЖЬ, как только соберётся следующая версия.
     Поэтому их пишет релиз в /downloads/setup.json, а чего нет — того на
     странице не показывается вовсе. */
  const MOCK_SETUP = {};

  async function load() {
    const [games, news, maint, launcher, setup] = await Promise.all([
      get('/api/games', MOCK_GAMES),
      get('/news/index.json', MOCK_NEWS),
      get('/api/maintenance', MOCK_MAINT),
      get('/manifests/launcher/latest.json', MOCK_LAUNCHER),
      get('/downloads/setup.json', MOCK_SETUP),
    ]);

    return {
      live: games.live,
      games: (games.data.items || []).filter((g) => g && g.gameId),
      news: (news.data.items || []).filter((n) => n && n.published !== false),
      maintenance: maint.data || MOCK_MAINT,
      launcherVersion: (launcher.data.version || launcher.data.Version || '').trim(),
      setup: setup.data || {},
    };
  }

  /* Страница и эмулятор просят одно и то же. Без памятки это четыре
     лишних запроса и два разных ответа на одном экране, если каталог
     успел измениться между ними. */
  let once = null;
  const cached = () => (once ??= load());

  window.CHILLHUB_API = { load: cached, reload: () => (once = load()), gallery, MOCK_GAMES, MOCK_NEWS };
})();
