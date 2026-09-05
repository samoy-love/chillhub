// Разделы: что запросить и как разобрать ответ.
//
// ЗАЧЕМ РАЗБОР ОТДЕЛЬНО ОТ ОТРИСОВКИ. Ответы админ-API за годы обросли
// синонимами: список приходит то массивом, то `{items:[…]}`, размер зовётся
// `size` и `bytes`, дата — `date` и `createdAt`. В панели 1.0 каждое место
// разбирало это по-своему, и одна и та же игра выглядела по-разному на
// двух вкладках. Здесь разбор один, он не трогает DOM и потому проверяется
// тестом целиком.
//
// ПЕРВЫЙ ЭКРАН — ЭТО РЕШЕНИЯ, А НЕ СВОДКА. Человек в этой панели принимает
// ровно два необратимых решения: отдать игрокам новую сборку лаунчера и
// пересобрать модпак под вышедшее обновление. Всё остальное — наблюдение.
// Поэтому `decisions()` возвращает список того, что ЖДЁТ ДЕЙСТВИЯ, а
// `watch()` — то, за чем просто следят; смешивать их нельзя, иначе первый
// экран снова превратится в витрину цифр.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Sections = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /** Достаёт массив из ответа, каким бы из двух способов он ни пришёл. */
  function items(raw) {
    if (Array.isArray(raw)) return raw;
    if (raw && Array.isArray(raw.items)) return raw.items;
    if (raw && Array.isArray(raw.list)) return raw.list;
    return [];
  }

  /** Первое непустое значение из перечисленных полей. */
  function pick(obj, names, fallback) {
    if (!obj) return fallback;
    for (const n of names) {
      const v = obj[n];
      if (v !== undefined && v !== null && v !== '') return v;
    }
    return fallback;
  }

  const num = (v, fallback) => {
    const n = Number(v);
    return Number.isFinite(n) ? n : fallback;
  };

  /* ---------- Лаунчер ---------- */

  /**
   * Версии лаунчера.
   *
   * Активной считается та, что помечена сервером, а не первая в списке:
   * порядок ответа не гарантирован, и «первая» однажды оказалась старой.
   */
  function launcher(raw) {
    const list = items(raw).map((v) => ({
      version: String(pick(v, ['version', 'name'], '')),
      date: pick(v, ['date', 'createdAt', 'builtAt'], ''),
      files: num(pick(v, ['files', 'fileCount'], 0), 0),
      size: num(pick(v, ['size', 'bytes'], 0), 0),
      state: v && (v.state || (v.active ? 'active' : '')) ? String(v.state || 'active') : 'old',
    }));

    const active = list.find((v) => v.state === 'active') || null;
    const uploaded = list.filter((v) => v.state === 'uploaded');

    return {
      versions: list,
      active: active ? active.version : '',
      newest: list.length ? list[0].version : '',
      uploaded: uploaded,
      // Ждёт решения только когда есть И активная, И загруженная сверх неё.
      // Пустой список — это не «требует внимания», а «решать нечего».
      pending: Boolean(active && uploaded.length),
    };
  }

  /* ---------- Игры ---------- */

  function games(raw) {
    return items(raw).map((g) => {
      const mods = g && g.mods ? g.mods : {};
      const iconUrl = String(pick(g, ['iconUrl', 'icon'], ''));
      return {
        gameId: String(pick(g, ['gameId', 'id'], '')),
        title: String(pick(g, ['title', 'name'], pick(g, ['gameId', 'id'], ''))),
        exe: String(pick(g, ['exeRelativePath', 'exe'], '')),
        iconUrl: iconUrl,
        icon: iconUrl !== '',

        /* Идентификатор Steam лежит внутри `mods`, а не рядом с полями
           игры: он есть только у игр с модпаком. Искать его на верхнем
           уровне значит показывать пустую колонку у всех сразу. */
        steamId: String(pick(mods, ['steamAppId', 'steamId'], pick(g, ['steamAppId', 'steamId'], ''))),

        /* Есть ли у игры модпак. Спрашивать про моды у игры без них
           бесполезно: `mods/list` отвечает «у игры не включены моды»
           кодом 400, и раздел падал бы на ровном месте. */
        modsEnabled: Boolean(mods && mods.enabled),

        /* Поле в реестре называется `unpublished`, и нуль в нём означает
           «видно». Перевернуть его при чтении, а не при отрисовке: иначе
           каждое место, где спрашивают «опубликована ли», обязано помнить
           про двойное отрицание. */
        published: pick(g, ['unpublished'], false) !== true && pick(g, ['published'], true) !== false,
        order: num(pick(g, ['order'], 0), 0),
      };
    });
  }

  /* ---------- Сборки модов ---------- */

  /**
   * Модпаки. `behind` — на Thunderstore вышло новее собранного,
   * `deprecated` — автор объявил пакет устаревшим. Это два разных повода,
   * и советы по ним противоположные: первый пересобирают, второму ищут
   * замену. Сливать их в «требует внимания» нельзя.
   */
  /**
   * Собрано, но игрокам не отдано.
   *
   * Правило одно на панель и живёт здесь: два одинаковых условия в
   * разных местах расходятся молча, а расходятся они как раз на краях —
   * у игры без единой сборки и у игры, которой ещё ни разу ничего не
   * активировали.
   */
  const isStaged = (built, active) => Boolean(built) && built !== active;

  /**
   * Строка раздела «Сборки» из ответа `mods/list` по одной игре.
   *
   * Ручка отвечает не готовой строкой, а списком версий: `items` со
   * всеми собранными, `active` с той, что у игроков, и `updates` с
   * пакетами, которые на Thunderstore успели уйти вперёд. Собранной
   * версии отдельным полем в ответе нет — она первая в списке
   * (`ListPublished` отдаёт от новых к старым). Складывать строку
   * приходится здесь; без этого раздел показывал пустое место ровно
   * там, где принимается решение «отдать игрокам».
   */
  function packRow(raw, game) {
    const r = raw || {};
    const list = items(r);
    const g = game || {};
    const built = list[0] || {};
    const updates = Array.isArray(r.updates) ? r.updates : [];

    return {
      gameId: String(r.gameId || g.gameId || ''),
      title: String(g.title || r.gameId || g.gameId || ''),
      pack: String(built.displayName || ''),
      active: String(r.active || ''),
      built: String(built.version || ''),
      builtAt: built.createdAt || '',
      mods: num(built.packages, 0),
      size: num(built.bytes, 0),

      /* «Собрано, но не отдано» и «Thunderstore ушёл вперёд» — разные
         поводы: первое закрывается кнопкой, второе пересборкой. */
      staged: isStaged(String(built.version || ''), String(r.active || '')),
      behind: updates.some((u) => u && !u.deprecated),
      deprecated: updates.some((u) => u && u.deprecated),
      latest: String((updates[0] && updates[0].latest) || ''),
      latestAt: '',
      missing: Array.isArray(built.missing) ? built.missing : [],
    };
  }

  function packs(raw) {
    return items(raw).map((p) => ({
      gameId: String(pick(p, ['gameId', 'id'], '')),
      title: String(pick(p, ['title', 'name'], pick(p, ['gameId'], ''))),
      pack: String(pick(p, ['pack', 'package'], '')),
      active: String(pick(p, ['active', 'activeVersion'], '')),
      built: String(pick(p, ['built', 'builtVersion', 'version'], '')),
      builtAt: pick(p, ['builtAt', 'date'], ''),
      mods: num(pick(p, ['mods', 'modCount'], 0), 0),
      size: num(pick(p, ['size', 'bytes'], 0), 0),
      behind: Boolean(p && p.behind),
      deprecated: Boolean(p && p.deprecated),
      /* Версия с Thunderstore приходит плоским полем `latest`. Снимок
         держал её вложенной в `upstream`; читать обе формы приходится
         здесь, иначе отрисовка обязана уметь две. */
      latest: String(pick(p, ['latest'], (p && p.upstream && p.upstream.version) || '')),
      latestAt: String(pick(p, ['latestAt'], (p && p.upstream && p.upstream.at) || '')),
      staged: isStaged(
        String(pick(p, ['built', 'builtVersion', 'version'], '')),
        String(pick(p, ['active', 'activeVersion'], ''))
      ),
      missing: Array.isArray(p && p.missing) ? p.missing : [],
    }));
  }

  /* ---------- Новости ---------- */

  /* Заметка адресуется тройкой scope + gameId + slug. В индексе сервер
     кладёт `id` и `slug` одинаковыми, но обращаться надо именно по
     slug: `id` — это его собственное поле, а не адрес. */
  function news(raw, gameId) {
    const game = String(gameId || '');
    return items(raw).map((n) => ({
      slug: String(pick(n, ['slug', 'id'], '')),
      title: String(pick(n, ['title'], 'Без заголовка')),
      summary: String(pick(n, ['summary'], '')),
      game: game || String(pick(n, ['game', 'gameId'], '')),
      scope: game || pick(n, ['gameId'], '') ? 'game' : 'launcher',
      coverUrl: String(pick(n, ['coverUrl'], '')),
      at: pick(n, ['at', 'createdAt', 'date'], ''),
      published: pick(n, ['published'], pick(n, ['state'], '') === 'published') === true,
    }));
  }

  /* ---------- Обращения ---------- */

  function inbox(raw) {
    return items(raw).map((f) => ({
      id: String(pick(f, ['id'], '')),
      type: String(pick(f, ['type'], 'other')),
      name: String(pick(f, ['name'], '')),
      contact: String(pick(f, ['contact'], '')),
      comment: String(pick(f, ['comment', 'text'], '')),
      at: pick(f, ['createdAt', 'at'], ''),
      status: String(pick(f, ['status'], 'new')),
      important: Boolean(f && f.important),
      logBytes: num(pick(f, ['logBytes'], 0), 0),

      /* Диагностика: версия клиента, система, место на диске. В списке
         сервер её не присылает — она приходит с одним обращением. Без
         неё «у меня не качается» остаётся без единой зацепки. */
      system: (f && f.system) || null,
    }));
  }

  /** Фильтр списка обращений — тот же набор, что был на вкладке 1.0. */
  function filterInbox(list, f) {
    const flt = f || {};
    return (list || []).filter((x) => {
      if (flt.type && x.type !== flt.type) return false;
      if (flt.status && x.status !== flt.status) return false;
      if (flt.important === true && !x.important) return false;
      if (flt.query) {
        const q = String(flt.query).toLowerCase();
        const hay = (x.comment + ' ' + x.name + ' ' + x.contact).toLowerCase();
        if (!hay.includes(q)) return false;
      }
      if (flt.from && String(x.at) < String(flt.from)) return false;
      if (flt.to && String(x.at) > String(flt.to)) return false;
      return true;
    });
  }

  /* ---------- Технические работы ---------- */

  function maintenance(raw) {
    const r = raw || {};
    const blocks = r.blocks || {};
    return {
      on: r.enabled === true,
      reason: String(r.reason || ''),
      /* Окно работ: сервер понимает RFC3339 и умеет закончить работы
         сам. Без него выключать приходится руками, а забытые включённые
         работы — это тихо не работающий лаунчер у всех сразу. */
      startsAt: String(r.startsAt || ''),
      endsAt: String(r.endsAt || ''),
      blocks: {
        install: blocks.install !== false,
        update: blocks.update !== false,
        launch: blocks.launch === true,
      },
    };
  }

  /* ---------- Метрики ---------- */

  function metrics(raw) {
    /* Сервер зовёт их `byDay`; `days` — форма снимка. Читаем обе, иначе
       раздел молча считает пустой список за «событий не было». */
    const src = (raw && (raw.byDay || raw.days)) || raw;
    return items(src).map((d) => ({
      date: String(pick(d, ['date'], '')),
      starts: num(pick(d, ['launcherStarts', 'starts'], 0), 0),
      installs: num(pick(d, ['installs'], 0), 0),
      updates: num(pick(d, ['updates'], 0), 0),
      launches: num(pick(d, ['gameLaunches', 'launches'], 0), 0),
      errors: num(pick(d, ['errors'], 0), 0),
    }));
  }

  /* ОТКУДА БЕРУТСЯ КОДЫ ОШИБОК. Из сводки (`topErrors`), а не из
     `metrics/errors`: та ручка отвечает событиями ОДНОГО кода и без
     параметра `code` возвращает 400. Раздел же спрашивает обратное —
     какие коды вообще встречаются и как часто. */
  /* Код ошибки сам по себе не говорит ничего тому, кто его не писал.
     Названия здесь, а не на сервере: сервер отдаёт события, а объяснять
     их — работа того, кто показывает. */
  const WHAT = {
    download_reset: 'связь оборвалась на середине закачки',
    download_failed: 'файл не скачался целиком',
    hash_mismatch: 'скачанный файл не сошёлся по контрольной сумме',
    disk_full: 'на диске игрока кончилось место',
    launch_failed: 'игра не запустилась',
    steam_missing: 'Steam не нашёлся там, где его ждали',
    manifest_error: 'манифест не прочитался',
    update_failed: 'обновление не доехало',
  };

  function errors(raw) {
    const src = (raw && (raw.topErrors || raw.items)) || raw;
    const list = items(src).map((e) => ({
      code: String(pick(e, ['key', 'code', 'errorCode'], '')),
      n: num(pick(e, ['count', 'n'], 0), 0),
      what: String(pick(e, ['what', 'message'], WHAT[String(pick(e, ['key', 'code'], ''))] || '')),
      where: String(pick(e, ['where', 'game'], '')),
    }));
    const total = list.reduce((a, e) => a + e.n, 0);
    // Долю считаем здесь, а не на сервере: она обязана сходиться с тем
    // списком, который человек видит на экране, включая фильтры.
    return list.map((e) => Object.assign({}, e, { share: total ? e.n / total : 0 }));
  }

  /* ---------- Диск ---------- */

  const disk = (raw) => ({
    free: num(pick(raw, ['freeBytes', 'free'], 0), 0),
    total: num(pick(raw, ['totalBytes', 'total'], 0), 0),
  });

  const cache = (raw) => ({
    files: num(pick(raw, ['files', 'count'], 0), 0),
    bytes: num(pick(raw, ['bytes', 'size'], 0), 0),
    oldest: pick(raw, ['oldest'], ''),
  });

  /* ---------- Первый экран ---------- */

  /**
   * Что ждёт решения человека. Только необратимое и только то, где без
   * него не обойтись.
   */
  function decisions(d) {
    const s = d || {};
    const out = [];

    const l = s.launcher;
    if (l && l.pending) {
      const v = l.uploaded[0];
      out.push({
        id: 'launcher',
        title: 'Отдать игрокам лаунчер ' + v.version,
        detail: 'Игроки получают ' + l.active + '. Пока не активируешь, новая версия лежит на сервере и никому не отдаётся.',
        action: 'launcher.activate',
        args: { version: v.version },
        href: '#launcher',
      });
    }

    for (const p of s.packs || []) {
      if (p.deprecated) {
        out.push({
          id: 'pack:' + p.gameId,
          title: 'Заменить модпак: ' + p.title,
          detail: 'Автор объявил пакет устаревшим. Пересборка возьмёт последнюю доступную версию, но подобрать замену придётся руками.',
          href: '#packs',
          args: { gameId: p.gameId },
        });
      } else if (p.behind) {
        out.push({
          id: 'pack:' + p.gameId,
          title: 'Пересобрать модпак: ' + p.title,
          detail: 'Собрано ' + (p.built || '—') + ', на Thunderstore ' + (p.latest || '—') + '.',
          href: '#packs',
          args: { gameId: p.gameId },
        });
      } else if (p.built && p.active && p.built !== p.active) {
        out.push({
          id: 'pack:' + p.gameId,
          title: 'Отдать игрокам сборку ' + p.built + ': ' + p.title,
          detail: 'Собрано, но не активировано. Игроки пока получают ' + p.active + '.',
          action: 'mods.activate',
          args: { gameId: p.gameId, version: p.built },
          href: '#packs',
        });
      }
    }

    return out;
  }

  /** За чем следят. Это не решения, и путать их нельзя. */
  function watch(d) {
    const s = d || {};
    const out = [];

    const unread = (s.inbox || []).filter((f) => f.status === 'new');
    const important = unread.filter((f) => f.important);
    out.push({
      id: 'inbox',
      label: 'Обращения',
      value: String(unread.length),
      note: important.length ? important.length + ' помечено важным' : 'новых',
      tone: unread.length ? 'accent' : 'ok',
      href: '#inbox',
    });

    const drafts = (s.news || []).filter((n) => !n.published).length;
    out.push({
      id: 'drafts',
      label: 'Черновики',
      value: String(drafts),
      note: 'не опубликованы',
      tone: drafts ? 'warn' : '',
      href: '#news',
    });

    const m = s.maintenance;
    out.push({
      id: 'maint',
      label: 'Техработы',
      value: m && m.on ? 'включены' : 'выключены',
      note: m && m.on ? 'игроки видят заглушку' : 'сервис отдаёт всё',
      tone: m && m.on ? 'bad' : 'ok',
      href: '#maint',
    });

    const days = s.metrics || [];
    const today = days.length ? days[days.length - 1] : null;
    out.push({
      id: 'errors',
      label: 'Ошибок за сутки',
      value: String(today ? today.errors : 0),
      note: today ? 'при ' + today.updates + ' обновлениях' : 'данных нет',
      tone: today && today.errors > 0 ? 'warn' : 'ok',
      href: '#errors',
    });

    if (s.disk) {
      out.push({
        id: 'disk',
        label: 'Свободно',
        value: '',
        bytes: s.disk.free,
        note: 'на диске с контентом',
        tone: s.disk.total && s.disk.free / s.disk.total < 0.1 ? 'bad' : '',
        href: '#disk',
      });
    }

    return out;
  }

  /* ---------- Загрузчики ---------- */

  /** Имя раздела -> как его прочитать. Ровно то, что уходит в хранилище. */
  const LOADERS = {
    overview: (api) => api.summary(),
    launcher: (api) => api.launcherVersions().then(launcher),
    games: (api) => api.games().then(games),
    /* `mods/list` отвечает про ОДНУ игру и без `gameId` даёт 400,
       поэтому сначала реестр, потом по запросу на игру с включёнными
       модами. Игры без модпака не спрашиваем вовсе: у них эта ручка
       отвечает «у игры не включены моды», тоже четырёхсотым. */
    packs: async (api) => {
      const list = games(await api.games()).filter((g) => g.modsEnabled);
      if (!list.length) return [];

      const answers = await Promise.all(
        list.map((g) =>
          api
            .modsList(g.gameId)
            .then((r) => packRow(r, g))
            .catch(() => null)
        )
      );
      const rows = answers.filter(Boolean);

      /* Одна упавшая игра из пяти — не повод прятать остальные. Но если
         не ответила НИ ОДНА, это не «сборок нет», а «сервер не ответил»,
         и разница здесь принципиальная: в первом случае человек заводит
         сборку, во втором — идёт чинить сервер. Пустой список молчит об
         этом, отказ — нет. */
      if (!rows.length) throw new Error('ни одна игра не ответила про свои сборки');
      return rows;
    },
    /* У каждой игры своя лента, и у лаунчера своя. Спрашивать только
       про лаунчер значит не показать в панели половину написанного:
       новости игр были бы не видны и не правились вовсе. */
    news: async (api) => {
      const list = games(await api.games());
      const feeds = await Promise.all(
        [{ gameId: '' }].concat(list).map((g) =>
          api
            .newsList(g.gameId ? 'game' : 'launcher', g.gameId)
            .then((r) => news(r, g.gameId))
            .catch(() => [])
        )
      );
      return feeds.flat();
    },
    inbox: (api) => api.feedbackList().then(inbox),
    /* Ответ админской ручки — это {state, effective, path}: сохранённое
       состояние и то, что из него следует прямо сейчас. Читать надо
       state, иначе окно работ, ещё не наступившее, выглядит выключенным. */
    maint: (api) => api.maintenanceGet().then((r) => maintenance((r && (r.state || r)) || r)),
    metrics: (api) => api.metricsSummary().then(metrics),
    errors: (api) => api.metricsSummary().then(errors),
    disk: (api) => api.freeSpace().then(disk),
    cache: (api) => api.modsCache().then(cache),
  };

  return {
    items, pick,
    launcher, games, packs, packRow, isStaged, news, inbox, filterInbox,
    maintenance, metrics, errors, disk, cache,
    decisions, watch,
    LOADERS,
  };
});
