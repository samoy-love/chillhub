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
