// Разбор ссылок и ответов Thunderstore.
//
// ПОЧЕМУ ССЫЛКУ ВООБЩЕ ПРИХОДИТСЯ РАЗБИРАТЬ. Половина модпаков в раздел
// «Modpacks» не проставлена и в каталоге не находится вовсе — их
// подставляют ссылкой на страницу пакета. Сервер такую ссылку разбирает
// сам (`ParsePackageURL` в mods/catalog.go), но отказ он выдаёт уже
// после нажатия, а панель может сказать сразу, что ссылка не та.
//
// Правило здесь то же, что на сервере, и это единственный способ не
// разойтись: две разные проверки одного и того же расходятся молча.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Mods = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  /* Две формы адреса: нынешняя с сообществом (`/c/<игра>/p/...`) и
     старая без него (`/package/...`). Обе живут в чужих закладках. */
  const PACKAGE_URL = /^https?:\/\/[^/]*thunderstore\.io\/(?:c\/([a-z0-9-]+)\/p|package)\/([A-Za-z0-9_]+)\/([A-Za-z0-9_]+)\/?/;

  /** Разбирает ссылку на страницу пакета. Не та ссылка — null, а не догадка. */
  function parsePackageUrl(raw) {
    const m = PACKAGE_URL.exec(String(raw || '').trim());
    if (!m) return null;
    return { community: m[1] || '', namespace: m[2], name: m[3] };
  }

  /** Полное имя пакета так, как его пишет Thunderstore. */
  const fullName = (ns, name) => String(ns || '') + '/' + String(name || '');

  /**
   * Одна строка каталога, приведённая к одному виду.
   *
   * Thunderstore зовёт поля по-разному в разных ответах: у пакета есть
   * `versions[0]`, а у результата поиска — плоские `version_number` и
   * `download_count`.
   */
  function entry(raw) {
    const r = raw || {};
    const v = (Array.isArray(r.versions) && r.versions[0]) || {};
    return {
      namespace: String(r.owner || r.namespace || ''),
      name: String(r.name || ''),
      version: String(r.version_number || v.version_number || r.latest_version_number || ''),
      downloads: Number(r.download_count || v.download_count || r.downloads || 0),
      updated: String(r.date_updated || r.updated || ''),
      description: String(r.description || v.description || ''),
      deprecated: Boolean(r.is_deprecated || r.deprecated),
      pinned: Boolean(r.is_pinned || r.pinned),
    };
  }

  /** Список каталога в одном виде. */
  const entries = (raw) => {
    const list = (raw && (raw.results || raw.items)) || (Array.isArray(raw) ? raw : []);
    return list.map(entry);
  };

  /**
   * Что сказать про место перед сборкой.
   *
   * Сервер считает, сколько уже лежит в кэше, и это не мелочь: разница
   * между «скачать 2 ГБ» и «скачать 200 МБ» решает, ждать минуту или
   * двадцать.
   */
  function planSpace(plan, format) {
    const p = plan || {};
    const f = format || (typeof window !== 'undefined' && window.CH2Format);
    const bytes = (n) => (f ? f.bytes(n) : String(n));
    const total = Number(p.totalBytes || 0);
    const cached = Number(p.cachedBytes || 0);
    const need = Math.max(0, total - cached);

    if (!total) return { text: 'Размер неизвестен', tone: '' };
    if (p.spaceOk === false) {
      return { text: p.spaceNote || 'Места не хватит: нужно ' + bytes(need), tone: 'bad' };
    }
    if (cached > 0) {
      return { text: 'Скачать ' + bytes(need) + ' из ' + bytes(total) + ' — остальное уже в кэше', tone: 'ok' };
    }
    return { text: 'Скачать ' + bytes(total), tone: '' };
  }

  /**
   * Можно ли собирать по этому плану.
   *
   * Пропавшие пакеты сами по себе не запрет: сервер умеет собрать без
   * них, если попросить. Запрет — только нехватка места.
   */
  function planProblem(plan) {
    const p = plan || {};
    if (p.spaceOk === false) return p.spaceNote || 'Не хватает места на диске';
    if (!Number(p.packages || 0)) return 'В сборке нет ни одного пакета';
    return '';
  }

  return { PACKAGE_URL, parsePackageUrl, fullName, entry, entries, planSpace, planProblem };
});
