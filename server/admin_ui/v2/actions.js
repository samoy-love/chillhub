// Реестр пишущих действий панели.
//
// ЗАЧЕМ ОН НУЖЕН. В панели 1.0 каждая кнопка сама решала три вопроса:
// спрашивать ли подтверждение, что написать в случае успеха и что после
// себя перечитать. Решала по-разному. «Удалить версию» спрашивало, а
// «Удалить игру и все версии» — нет; часть кнопок молчала об успехе,
// часть показывала «ok».
//
// Здесь это описано таблицей: у действия есть имя объекта, вопрос перед
// необратимым шагом, глагол для сообщения и список разделов, которые
// после него устарели. Кнопка не решает ничего — она называет действие.
//
// ПРАВИЛО ПОДТВЕРЖДЕНИЯ. Спрашиваем только там, где отменить нельзя, и
// вопрос обязан назвать объект: «Удалить версию 1.6.24?» вместо
// «Вы уверены?». Спрашивать про всё подряд — тот же вред: человек
// привыкает жать «да» не читая.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Actions = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /**
   * Описания действий.
   *
   * `danger` — необратимо для игроков, спрашиваем.
   * `ask` — как звучит вопрос; получает те же аргументы, что и `run`.
   * `done` — что сказать после успеха.
   * `after` — какие разделы перечитать.
   */
  const ACTIONS = {
    /* --- лаунчер --- */
    'launcher.activate': {
      danger: true,
      ask: (a) => ({
        title: 'Отдать версию ' + a.version + ' игрокам?',
        body: 'Она уедет всем сразу и заменит то, что они получают сейчас. Вернуть можно только активацией предыдущей версии.',
        ok: 'Отдать игрокам',
      }),
      run: (api, a) => api.launcherActivate(a.version),
      done: (a) => 'Игроки получают версию ' + a.version,
      after: ['launcher', 'overview'],
    },
    'launcher.delete': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить версию ' + a.version + '?',
        body: 'Файлы версии будут стёрты с сервера. Вернуть нельзя.',
        ok: 'Удалить',
      }),
      run: (api, a) => api.launcherDelete(a.version),
      done: (a) => 'Версия ' + a.version + ' удалена',
      after: ['launcher'],
    },
    'launcher.prune': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить старые версии?',
        body: 'Останутся ' + a.keep + ' самых свежих, остальные будут стёрты. Вернуть нельзя.',
        ok: 'Удалить старые',
      }),
      run: (api, a) => api.launcherPrune(a.keep),
      done: () => 'Старые версии удалены',
      after: ['launcher'],
    },

    /* --- игры --- */
    'games.save': {
      run: (api, a) => api.gamesSave(a.items),
      done: () => 'Реестр сохранён',
      after: ['games', 'overview'],
    },
    'games.scan': {
      run: (api) => api.gamesScan(),
      done: () => 'Каталог просканирован',
      after: ['games'],
    },
    'games.purge': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить игру ' + a.title + ' и все её версии?',
        body: 'С сервера уйдут все сборки этой игры и её контент. У игроков она пропадёт из списка. Вернуть нельзя.',
        ok: 'Удалить игру',
      }),
      run: (api, a) => api.gamesPurge(a.gameId),
      done: (a) => 'Игра ' + a.title + ' удалена',
      after: ['games', 'overview'],
    },

    /* --- галерея --- */
    'gallery.mkdir': {
      run: (api, a) => api.galleryMkdir(a.gameId, a.dir),
      done: (a) => 'Папка ' + a.dir + ' создана',
      after: ['gallery'],
    },
    'gallery.rename': {
      run: (api, a) => api.galleryRename(a.gameId, a.from, a.to),
      done: (a) => 'Переименовано в ' + a.to,
      after: ['gallery'],
    },
    'gallery.delete': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить ' + a.path + '?',
        body: 'Файл уйдёт из галереи игры. Если он был обложкой, витрина останется с градиентом.',
        ok: 'Удалить',
      }),
      run: (api, a) => api.galleryDelete(a.gameId, a.path),
      done: () => 'Удалено',
      after: ['gallery'],
    },
    'gallery.caption': {
      run: (api, a) => api.gallerySetCaption(a.gameId, a.file, a.caption),
      done: () => 'Подпись сохранена',
      after: ['gallery'],
    },
    'gallery.cover': {
      run: (api, a) => api.gallerySetCover(a.gameId, a.file),
      done: () => 'Обложка выбрана',
      after: ['gallery', 'games'],
    },

    /* --- сборки модов --- */
    'mods.activate': {
      danger: true,
      ask: (a) => ({
        title: 'Отдать сборку ' + a.version + ' игрокам?',
        body: 'Игроки получат её при следующем запуске лаунчера и скачают разницу. Вернуть можно только активацией предыдущей.',
        ok: 'Отдать игрокам',
      }),
      run: (api, a) => api.modsActivate(a.gameId, a.version),
      done: (a) => 'Игроки получают сборку ' + a.version,
      after: ['packs', 'overview'],
    },
    'mods.delete': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить сборку ' + a.version + '?',
        body: 'Файлы сборки будут стёрты. Активную удалить нельзя: клиенты, которые её докачивают, потеряют файлы на середине.',
        ok: 'Удалить',
      }),
      run: (api, a) => api.modsDelete(a.gameId, a.version),
      done: (a) => 'Сборка ' + a.version + ' удалена',
      after: ['packs'],
    },

    /* --- новости --- */
    'news.save': {
      run: (api, a) => api.newsSave(a.payload),
      done: () => 'Сохранено',
      after: ['news'],
    },
    'news.publish': {
      danger: true,
      ask: (a) => ({
        title: a.published ? 'Опубликовать «' + a.title + '»?' : 'Снять с публикации «' + a.title + '»?',
        body: a.published
          ? 'Заметка появится на главном экране лаунчера у всех игроков.'
          : 'Заметка пропадёт с главного экрана у всех игроков.',
        ok: a.published ? 'Опубликовать' : 'Снять',
      }),
      run: (api, a) => api.newsPublish(a.id, a.published),
      done: (a) => (a.published ? 'Опубликовано' : 'Снято с публикации'),
      after: ['news', 'overview'],
    },
    'news.delete': {
      danger: true,
      ask: (a) => ({
        title: 'Удалить «' + a.title + '»?',
        body: 'Заметка и её вложения будут стёрты. Вернуть нельзя.',
        ok: 'Удалить',
      }),
      run: (api, a) => api.newsDelete(a.id),
      done: () => 'Заметка удалена',
      after: ['news', 'overview'],
    },
    'news.rebuild': {
      run: (api) => api.newsRebuild(),
      done: () => 'Индекс новостей пересобран',
      after: ['news'],
    },

    /* --- обращения --- */
    'inbox.important': {
      run: (api, a) => api.feedbackImportant(a.id, a.important),
      done: (a) => (a.important ? 'Помечено важным' : 'Метка снята'),
      after: ['inbox'],
    },
    'inbox.read': {
      run: (api, a) => (a.read ? api.feedbackRead(a.id) : api.feedbackUnread(a.id)),
      done: (a) => (a.read ? 'Отмечено прочитанным' : 'Возвращено в новые'),
      after: ['inbox', 'overview'],
    },
    'inbox.delete': {
      danger: true,
      ask: () => ({
        title: 'Удалить обращение?',
        body: 'Текст и приложенные журналы будут стёрты. Вернуть нельзя.',
        ok: 'Удалить',
      }),
      run: (api, a) => api.feedbackDelete(a.id),
      done: () => 'Обращение удалено',
      after: ['inbox', 'overview'],
    },
    'inbox.clear': {
      danger: true,
      ask: (a) => ({
        title: 'Очистить все обращения?',
        body: 'Будут стёрты все ' + a.count + ' обращений вместе с журналами. Вернуть нельзя.',
        ok: 'Очистить всё',
      }),
      run: (api) => api.feedbackClear(),
      done: () => 'Обращения очищены',
      after: ['inbox', 'overview'],
    },

    /* --- технические работы --- */
    'maint.on': {
      danger: true,
      ask: () => ({
        title: 'Включить технические работы?',
        body: 'Все игроки сразу увидят заглушку вместо каталога, сборки перестанут отдаваться, самообновление лаунчера замолчит. Уже скачанное продолжит запускаться.',
        ok: 'Включить работы',
      }),
      run: (api, a) => api.maintenanceSet(a.payload),
      done: () => 'Технические работы включены',
      after: ['maint', 'overview'],
    },
    'maint.off': {
      run: (api) => api.maintenanceClear(),
      done: () => 'Технические работы выключены',
      after: ['maint', 'overview'],
    },

    /* --- метрики --- */
    'metrics.clear': {
      danger: true,
      ask: () => ({
        title: 'Удалить все метрики?',
        body: 'История запусков, установок и ошибок будет стёрта целиком. Восстановить нельзя, накопится заново только со следующих событий.',
        ok: 'Удалить метрики',
      }),
      run: (api) => api.metricsClear(),
      done: () => 'Метрики удалены',
      after: ['errors', 'overview'],
    },
  };

  /** Есть ли такое действие. */
  const has = (id) => Object.prototype.hasOwnProperty.call(ACTIONS, id);

  /** Нужно ли спрашивать перед этим действием. */
  function needsConfirm(id) {
    return has(id) && ACTIONS[id].danger === true;
  }

  /**
   * Вопрос перед необратимым действием.
   * Возвращает null там, где спрашивать не о чем.
   */
  function question(id, args) {
    if (!needsConfirm(id)) return null;
    const q = ACTIONS[id].ask(args || {});
    return { title: q.title, body: q.body, ok: q.ok, cancel: 'Отмена' };
  }

  /** Что сказать после успеха. */
  function success(id, args) {
    if (!has(id)) return '';
    return ACTIONS[id].done(args || {});
  }

  /** Какие разделы устарели после действия. */
  function stale(id) {
    if (!has(id)) return [];
    return ACTIONS[id].after.slice();
  }

  /**
   * Выполняет действие.
   *
   * `confirm` — функция, задающая вопрос; должна вернуть true/false.
   * Отказ — это не ошибка: возвращаем `{ ok: false, cancelled: true }`,
   * чтобы вызывающий код не показывал «не удалось» там, где человек
   * просто передумал.
   */
  async function run(id, args, deps) {
    const d = deps || {};
    if (!has(id)) throw new Error('нет такого действия: ' + id);

    if (needsConfirm(id)) {
      const agreed = d.confirm ? await d.confirm(question(id, args)) : false;
      if (!agreed) return { ok: false, cancelled: true };
    }

    try {
      const result = await ACTIONS[id].run(d.api, args || {});
      return { ok: true, result: result, message: success(id, args), stale: stale(id) };
    } catch (e) {
      return { ok: false, error: e, message: (e && e.message) || 'не получилось' };
    }
  }

  return { ACTIONS: ACTIONS, has: has, needsConfirm: needsConfirm, question: question, success: success, stale: stale, run: run };
});
