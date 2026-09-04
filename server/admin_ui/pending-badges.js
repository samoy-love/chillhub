// Значки «здесь ждут действия» на вкладках «Лаунчер» и «Моды».
//
// Две вещи в панели решает человек и только человек: какую сборку лаунчера
// сделать активной и когда пересобрать модпак под вышедшее обновление. Обе
// узнавались одинаково — открыть вкладку, выбрать игру, сравнить таблицу
// глазами, — то есть узнать о них можно было только случайно.
//
// Вынесено отдельным CommonJS-модулем по той же причине, что ndjson.js и
// feedback-logs.js: только его c8 связывает с исходником построчно.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  // describeLauncher решает, что показать на вкладке «Лаунчер».
  //
  // Значок загорается только когда есть И активная версия, И более свежая
  // загруженная. Пустой ответ (лаунчер ещё ни разу не публиковали) — это не
  // «требует внимания», а «здесь пока нечего решать».
  function describeLauncher(l) {
    if (!l || !l.pending || !l.newest || !l.active) {
      return { show: false, text: '', title: '' };
    }
    return {
      show: true,
      text: l.newest,
      title: 'Загружена версия ' + l.newest + ', игроки получают ' + l.active
        + '. Сделайте новую активной, когда проверите её.',
    };
  }

  // describeMods решает, что показать на вкладке «Моды».
  //
  // В сводке теперь строка на КАЖДУЮ игру с модами, включая свежие: значок
  // считает не длину списка, а те строки, с которыми надо что-то делать. Считай
  // он по-прежнему все, — горел бы всегда и не значил бы ничего.
  function describeMods(games) {
    const list = Array.isArray(games) ? games : [];
    const behind = list.filter(function (g) { return g && (g.behind || g.deprecated); });
    if (behind.length === 0) {
      return { show: false, text: '', title: '' };
    }
    const lines = behind.map(function (g) {
      const name = (g.title || g.gameId);
      // Устаревший пакет той же версии — это не «вышло обновление», и звать
      // пересобирать его бесполезно: решать, чем его заменить, придётся человеку.
      return g.behind
        ? name + ': ' + (g.latest || '?')
        : name + ': пакет объявлен устаревшим';
    });
    return {
      show: true,
      text: String(behind.length),
      title: 'Модпаки требуют внимания — ' + lines.join('; ')
        + '. Пересоберите пакет и активируйте новую версию.',
      // Игра, с которой начинать. Значок говорил «где-то есть обновление», а
      // искать, в какой именно игре, оператор шёл в подсказку и потом в
      // выпадающий список: вкладка открывалась на той игре, что стояла первой.
      gameId: (behind[0] && behind[0].gameId) || '',
    };
  }

  // applyBadge рисует один значок. Пустое состояние прячет его целиком, а не
  // оставляет ноль: «0» на вкладке читается как «что-то есть».
  function applyBadge(el, view) {
    if (!el) return;
    if (!view || !view.show) {
      el.style.display = 'none';
      el.textContent = '';
      el.removeAttribute('title');
      el.removeAttribute('data-game-id');
      return;
    }
    el.style.display = '';
    el.textContent = view.text;
    el.title = view.title;
    // Игру кладём на сам значок: вкладка читает её при открытии и показывает
    // сразу ту, где ждут действия.
    if (view.gameId) el.setAttribute('data-game-id', view.gameId);
    else el.removeAttribute('data-game-id');
  }

  // refreshPendingBadges тянет сводку и раскладывает её по вкладкам.
  //
  // Ошибку глотает намеренно: панель, которая не смогла нарисовать значки,
  // обязана нарисовать всё остальное.
  // opts.force — спросить Thunderstore заново, не дожидаясь, пока стухнет
  // серверный кеш сводки. Нужно ровно после действия оператора: он только что
  // активировал новый модпак, а значок ещё десять минут показывал бы прежний
  // ответ — то есть висел бы над уже сделанной работой.
  async function refreshPendingBadges(doc, fetchImpl, opts) {
    const d = doc || (typeof document !== 'undefined' ? document : null);
    const f = fetchImpl || (typeof fetch === 'function' ? fetch : null);
    if (!d || !f) return null;

    let data = null;
    try {
      const res = await f('/admin/summary' + (opts && opts.force ? '?force=1' : ''));
      if (!res || !res.ok) return null;
      data = await res.json();
    } catch (_) {
      return null;
    }

    applyBadge(d.getElementById('launcher_pending_badge'), describeLauncher(data && data.launcher));
    applyBadge(d.getElementById('mods_pending_badge'), describeMods(data && data.mods));
    return data;
  }

  return { describeLauncher, describeMods, applyBadge, refreshPendingBadges };
});
