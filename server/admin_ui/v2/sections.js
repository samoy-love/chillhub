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
    return items(raw).map((g) => ({
      gameId: String(pick(g, ['gameId', 'id'], '')),
      title: String(pick(g, ['title', 'name'], pick(g, ['gameId', 'id'], ''))),
      exe: String(pick(g, ['exeRelativePath', 'exe'], '')),
      iconUrl: String(pick(g, ['iconUrl', 'icon'], '')),
      steamId: String(pick(g, ['steamAppId', 'steamId'], '')),
      published: pick(g, ['published'], true) !== false,
      order: num(pick(g, ['order'], 0), 0),
    }));
  }

  /* ---------- Сборки модов ---------- */

  /**
   * Модпаки. `behind` — на Thunderstore вышло новее собранного,
   * `deprecated` — автор объявил пакет устаревшим. Это два разных повода,
   * и советы по ним противоположные: первый пересобирают, второму ищут
   * замену. Сливать их в «требует внимания» нельзя.
   */
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
      latest: String(pick(p, ['latest'], (p && p.upstream && p.upstream.version) || '')),
    }));
  }

  /* ---------- Новости ---------- */

  function news(raw) {
    return items(raw).map((n) => ({
      id: String(pick(n, ['id'], '')),
      title: String(pick(n, ['title'], 'Без заголовка')),
      game: String(pick(n, ['game', 'gameId'], '')),
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
      blocks: {
        install: blocks.install !== false,
        update: blocks.update !== false,
        launch: blocks.launch === true,
      },
    };
  }

  /* ---------- Метрики ---------- */

  function metrics(raw) {
    const src = raw && raw.days ? raw.days : raw;
    return items(src).map((d) => ({
      date: String(pick(d, ['date'], '')),
      starts: num(pick(d, ['launcherStarts', 'starts'], 0), 0),
      installs: num(pick(d, ['installs'], 0), 0),
      updates: num(pick(d, ['updates'], 0), 0),
      launches: num(pick(d, ['gameLaunches', 'launches'], 0), 0),
      errors: num(pick(d, ['errors'], 0), 0),
    }));
  }

  function errors(raw) {
    const list = items(raw).map((e) => ({
      code: String(pick(e, ['code', 'errorCode'], '')),
      n: num(pick(e, ['n', 'count'], 0), 0),
      what: String(pick(e, ['what', 'message'], '')),
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
    packs: (api) => api.modsList().then(packs),
    news: (api) => api.newsList().then(news),
    inbox: (api) => api.feedbackList().then(inbox),
    maint: (api) => api.maintenanceGet().then(maintenance),
    metrics: (api) => api.metricsSummary().then(metrics),
    errors: (api) => api.metricsErrors().then(errors),
    disk: (api) => api.freeSpace().then(disk),
    cache: (api) => api.modsCache().then(cache),
  };

  return {
    items, pick,
    launcher, games, packs, news, inbox, filterInbox,
    maintenance, metrics, errors, disk, cache,
    decisions, watch,
    LOADERS,
  };
});
