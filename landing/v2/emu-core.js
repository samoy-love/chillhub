// Логика копии главного экрана лаунчера — без единого обращения к DOM.
//
// ЗАЧЕМ ОТДЕЛЬНО ОТ ОТРИСОВКИ. Эта копия обязана вести себя как лаунчер:
// те же состояния кнопок, те же подписи, тот же порядок очереди. Проверить
// это можно только тестом, а тест невозможен, пока правила перемешаны с
// созданием элементов. Здесь правила, в emu.js — рисование.
//
// ОТКУДА ВЗЯТЫ ПРАВИЛА. Не выдуманы, а перенесены из исходников клиента:
//
//   Core/Home/ActionButtonState.cs  — какой кнопке быть на витрине
//   Core/Mods/LaunchButtons.cs      — сколько кнопок запуска и какая залита
//   Core/Home/HomeFormat.cs         — размеры и оставшееся время
//   Core/Home/SpaceHint.cs          — «Нужно: … (… доступно)»
//   Core/UI/GameStatusConverters.cs — подпись игры в списке
//
// Расхождение с продуктом здесь — это ошибка, и ловит её тест.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CHEmuCore = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const KB = 1024;
  const MB = KB * 1024;
  const GB = MB * 1024;

  /* ---------- Порт HomeFormat.cs ---------- */

  /* Дробная часть отделяется запятой: лаунчер форматирует по культуре
     системы и пишет «1,6 ГБ», а `toFixed` дал бы «1.6 ГБ». */
  const dec = (n) => n.toFixed(1).replace('.', ',');

  function formatSize(bytes) {
    if (!Number.isFinite(bytes) || bytes < 0) return '—';
    if (bytes >= GB) return dec(bytes / GB) + ' ГБ';
    if (bytes >= MB) return dec(bytes / MB) + ' МБ';
    if (bytes >= KB) return dec(bytes / KB) + ' КБ';
    return bytes + ' Б';
  }

  function pluralizeDayRu(n) {
    const n10 = n % 10;
    const n100 = n % 100;
    if (n10 === 1 && n100 !== 11) return 'день';
    if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return 'дня';
    return 'дней';
  }

  function formatEta(seconds) {
    if (!Number.isFinite(seconds)) return '—';
    const total = Math.max(0, Math.ceil(seconds));
    const d = Math.floor(total / 86400);
    const h = Math.floor((total % 86400) / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (d >= 1) return h > 0 ? d + ' ' + pluralizeDayRu(d) + ' ' + h + ' ч' : d + ' ' + pluralizeDayRu(d);
    if (total >= 3600) return h + ' ч ' + String(m).padStart(2, '0') + ' мин';
    if (total >= 60) return Math.ceil(total / 60) + ' мин';
    return s + ' с';
  }

  /* ---------- Порт ActionButtonState.cs ---------- */

  const MODE = {
    Checking: { text: 'Проверка…', on: false, look: 'checking' },
    Install: { text: 'Установить', on: true, look: 'install' },
    Update: { text: 'Обновить', on: true, look: 'update' },
    Play: { text: 'Играть', on: true, look: 'play' },
    Cancel: { text: 'Отмена', on: true, look: 'cancel' },
    Dequeue: { text: 'Убрать из очереди', on: true, look: 'dequeue' },
    Retry: { text: 'Повторить', on: true, look: 'retry' },
    Maintenance: { text: 'Технические работы', on: false, look: 'checking' },
    SteamOnly: { text: 'Нужна копия в Steam', on: false, look: 'checking' },
  };

  /**
   * Какой кнопке быть на витрине.
   *
   * Порядок ветвей повторяет ActionButtonState.Decide и важен целиком.
   * В частности: сборки нет — значит, нет и «Установить». Такая игра есть
   * только в Steam, а кнопка предлагала скачать несуществующий манифест,
   * и всё кончалось отказом очереди.
   */
  function decideMode(g) {
    if (!g) return 'Checking';
    if (g.error) return 'Retry';
    if (!g.hasServerBuild && !g.installed) return 'SteamOnly';
    // Незавершённое обновление — «Играть» не предлагаем, нужно докатить
    if (g.unfinished) return 'Update';
    if (g.installed && !g.needsUpdate) return 'Play';
    return g.installed ? 'Update' : 'Install';
  }

  /** Запрет техработ поверх режима. SteamOnly не трогаем: он не про сервер. */
  function blockedByMaintenance(mode, maint) {
    if (!maint || !maint.enabled) return false;
    const b = maint.blocks || {};
    if (mode === 'SteamOnly') return false;
    if (mode === 'Install') return Boolean(b.install);
    if (mode === 'Update' || mode === 'Retry') return Boolean(b.update);
    if (mode === 'Play') return Boolean(b.launch);
    return false;
  }

  /** Режим с учётом очереди и техработ — то, что видно на кнопке. */
  function effectiveMode(g, queue, maint) {
    const q = (queue || []).find((x) => x.gameId === (g && g.gameId));
    let mode = q ? (q.state === 'run' ? 'Cancel' : 'Dequeue') : decideMode(g);
    if (blockedByMaintenance(mode, maint)) mode = 'Maintenance';
    return mode;
  }

  const look = (mode) => MODE[mode] || MODE.Checking;

  /* ---------- Порт LaunchButtons.cs ---------- */

  /**
   * Кнопки запуска.
   *
   * Вне режима «Играть» сборки с сервера на витрине нет: её кнопка — это
   * «Установить»/«Обновить» слева, и второй такой же рядом быть не должно.
   * Залитая кнопка в ряду ровно одна: два акцента рядом не читаются как
   * «главный» и «запасной».
   */
  function launchButtons(g, mode) {
    if (mode !== 'Play') return [];
    if (!g || !g.mods || !g.mods.steamAppId) return [];
    return [
      { target: 'SteamModded', title: 'Steam', sub: 'с модами', accent: true },
      { target: 'LocalModded', title: 'Пиратка', sub: 'с модами', accent: false },
    ];
  }

  /* ---------- Подписи ---------- */

  /** Порт GameStatusConverters: подпись игры в списке слева. */
  function listSubtitle(g, queue) {
    const q = (queue || []).find((x) => x.gameId === (g && g.gameId));
    if (q) return q.state === 'run' ? 'Скачивание обновления…' : 'В очереди';
    if (!g) return '';
    if (g.needsUpdate) return 'Обновление';
    return g.installed ? 'Установлена' : 'Не установлена';
  }

  /** Цветовая метка подписи — она же признак состояния. */
  function listTone(g, queue) {
    const q = (queue || []).find((x) => x.gameId === (g && g.gameId));
    if (q) return 'busy';
    if (!g) return 'none';
    if (g.needsUpdate) return 'update';
    return g.installed ? 'ok' : 'none';
  }

  /**
   * Наигранное время.
   *
   * До часа считаем в минутах: первые запуски давали «0 ч в игре» — цифру,
   * которая выглядит как отсутствие данных, а не как восемь минут.
   */
  function playtime(minutes) {
    const m = Number(minutes);
    if (!Number.isFinite(m) || m <= 0) return 'ещё не запускали';
    if (m < 60) return m + ' мин в игре';
    return Math.floor(m / 60) + ' ч в игре';
  }

  /** Порт SpaceHint: подсказка о месте — только пока игру ещё качать. */
  function spaceHint(mode, needBytes, freeBytes) {
    if (mode !== 'Install' && mode !== 'Update') return '';
    return 'Нужно: ' + formatSize(needBytes) + ' (' + formatSize(freeBytes) + ' доступно)';
  }

  /** Строка версии и модпака под заголовком витрины. */
  function heroMeta(g) {
    if (!g) return [];
    const out = [playtime(g.playtimeMin), 'версия ' + (g.latestVersion || '—')];
    if (g.mods && g.mods.displayName) {
      out.push('моды: ' + g.mods.displayName + (g.mods.displayVersion ? ' ' + g.mods.displayVersion : ''));
    }
    return out;
  }

  /* ---------- Очередь ---------- */

  /**
   * Ставит игру в очередь. Качается ровно одна, остальные ждут: два потока
   * на один канал делят скорость и удлиняют обе загрузки.
   */
  function enqueue(queue, game) {
    const q = (queue || []).slice();
    if (q.some((x) => x.gameId === game.gameId)) return q;
    const busy = q.some((x) => x.state === 'run');
    q.push({ gameId: game.gameId, done: 0, total: game.bytes, speed: 0, state: busy ? 'wait' : 'run' });
    return q;
  }

  /** Убирает из очереди и передаёт эстафету следующему. */
  function dequeue(queue, gameId) {
    const q = (queue || []).filter((x) => x.gameId !== gameId);
    if (q.length && !q.some((x) => x.state === 'run')) q[0] = Object.assign({}, q[0], { state: 'run' });
    return q;
  }

  /**
   * Переставляет ожидающего. Первая позиция занята качающимся, и меняться
   * с ним местами нельзя: это оборвало бы начатую загрузку.
   */
  function move(queue, gameId, dir) {
    const q = (queue || []).slice();
    const i = q.findIndex((x) => x.gameId === gameId);
    const j = i + dir;
    if (i < 1 || j < 1 || j >= q.length) return q;
    const t = q[i];
    q[i] = q[j];
    q[j] = t;
    return q;
  }

  /** Подпись позиции: качающийся — без номера, ждущие нумеруются с двух. */
  function queueLabel(item, index) {
    if (item.state === 'run') return 'Скачивание обновления…';
    return 'В очереди · ' + (index + 1) + '-я';
  }

  /** Шапка дока. Счётчик появляется только когда есть из чего выбирать. */
  function dockTitle(queue) {
    const q = queue || [];
    const running = q.filter((x) => x.state === 'run').length;
    if (running && q.length > 1) return 'Очередь загрузок · качается 1 из ' + q.length;
    return 'Очередь загрузок';
  }

  /** Числа справа от полосы: сколько скачано и сколько осталось. */
  function progressText(item) {
    const left = item.speed > 0 ? formatEta((item.total - item.done) / item.speed) : '—';
    return {
      percent: Math.round((item.done / item.total) * 100),
      size: formatSize(item.done) + ' / ' + formatSize(item.total),
      rate: formatSize(item.speed) + '/с · осталось ' + left,
    };
  }

  return {
    KB, MB, GB,
    MODE, look,
    formatSize, formatEta, pluralizeDayRu,
    decideMode, blockedByMaintenance, effectiveMode, launchButtons,
    listSubtitle, listTone, playtime, spaceHint, heroMeta,
    enqueue, dequeue, move, queueLabel, dockTitle, progressText,
  };
});
