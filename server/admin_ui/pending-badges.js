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
  function describeMods(games) {
    const list = Array.isArray(games) ? games : [];
    if (list.length === 0) {
      return { show: false, text: '', title: '' };
    }
    const lines = list.map(function (g) {
      return (g.title || g.gameId) + ': ' + (g.latest || '?');
    });
    return {
      show: true,
      text: String(list.length),
      title: 'Вышли обновления модпаков — ' + lines.join('; ')
        + '. Пересоберите пакет и активируйте новую версию.',
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
      return;
    }
    el.style.display = '';
    el.textContent = view.text;
    el.title = view.title;
  }

  // refreshPendingBadges тянет сводку и раскладывает её по вкладкам.
  //
  // Ошибку глотает намеренно: панель, которая не смогла нарисовать значки,
  // обязана нарисовать всё остальное.
  async function refreshPendingBadges(doc, fetchImpl) {
    const d = doc || (typeof document !== 'undefined' ? document : null);
    const f = fetchImpl || (typeof fetch === 'function' ? fetch : null);
    if (!d || !f) return null;

    let data = null;
    try {
      const res = await f('/admin/summary');
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
