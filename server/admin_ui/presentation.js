/* global module */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.CH2Presentation = factory();
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';
  const text = (v) => typeof v === 'string' ? v : '';
  const MEDIA = ['kind', 'url', 'posterUrl', 'caption', 'provider', 'role', 'sourceUrl', 'author', 'publishedAt'];
  const BENEFITS = ['title', 'description', 'icon'];
  const MODS = ['id', 'name', 'russianName', 'description', 'category', 'version', 'url'];
  const HEIGHTS = [480, 720, 1080];
  const VARIANT_FIELDS = HEIGHTS.flatMap(h => ['url', 'width', 'bytes'].map(k => 'quality' + h + k));
  const variants = (items) => Array.isArray(items) ? items.map(v => ({url: text(v.url), width: Number(v.width), height: Number(v.height), bytes: Number(v.bytes)})).sort((a,b) => a.height-b.height) : [];
  const fields = (value, names) => Object.fromEntries(names.map((key) => [key, text(value && value[key])]));

  function normalize(value) {
    const p = value || {};
    return {
      description: text(p.description), modpackDescription: text(p.modpackDescription), genre: text(p.genre), heroUrl: text(p.heroUrl),
      modpackVersion: text(p.modpackVersion), modpackRevision: text(p.modpackRevision),
      highlights: Array.isArray(p.highlights) ? p.highlights.map(text) : [],
      media: Array.isArray(p.media) ? p.media.map((m) => ({ ...fields(m, MEDIA), width: Number(m.width ?? 0), height: Number(m.height ?? 0), variants: variants(m.variants) })) : [],
      modBenefits: Array.isArray(p.modBenefits) ? p.modBenefits.map((m) => fields(m, BENEFITS)) : [],
      mods: Array.isArray(p.mods) ? p.mods.map((m) => fields(m, MODS)) : [],
    };
  }

  function safeUrl(value, optional) {
    if (!value) return Boolean(optional);
    if (value.length > 2048 || value.trim() !== value || /[\\\u0000-\u0020]/.test(value)) return false;
    if (value.startsWith('//')) return false;
    try {
      // Check the original path: URL normalizes dot segments before exposing pathname.
      const decoded = decodeURIComponent(value.split('?')[0]);
      if (decoded.includes('\\') || decoded.split('/').some((p) => p === '.' || p === '..')) return false;
      const u = new URL(value, 'https://launcher.samoy.love');
      if (u.username || u.password || u.hash) return false;
      if (value.startsWith('/')) return /^\/(assets|gallery|manifests|content)\//.test(u.pathname);
      if (u.protocol !== 'https:' || !u.hostname) return false;
      const host = u.hostname.toLowerCase().replace(/\.$/, '');
      if (host === 'localhost' || /\.(localhost|local)$/.test(host)) return false;
      if (/^(127\.|10\.|192\.168\.|169\.254\.|0\.)/.test(host) || /^172\.(1[6-9]|2\d|3[01])\./.test(host)) return false;
      if (/^\[(::1|::|f[cd][0-9a-f]*:|fe80:)/i.test(host)) return false;
      return true;
    } catch { return false; }
  }

  function problems(value) {
    if (value === null || value === undefined) return [];
    const p = normalize(value), errors = [];
    const bad = (field, message) => errors.push({ field: 'presentation.' + field, message });
    const check = (field, v, max) => {
      if ([...v].length > max) bad(field, 'Слишком длинное поле: ' + field + ' (максимум ' + max + ' символов)');
      if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(v)) bad(field, 'Недопустимый символ: ' + field);
    };
    const link = (field, v, optional) => { if (!safeUrl(v, optional)) bad(field, 'Проверьте ссылку: ' + field + '. Нужна HTTPS-ссылка или путь /assets/…'); };
    check('description', p.description, 12000); check('modpackDescription', p.modpackDescription, 2000); check('genre', p.genre, 120); link('heroUrl', p.heroUrl, true);
    check('modpackVersion', p.modpackVersion, 200);
    if (p.modpackRevision && !/^[a-f0-9]{32}$/i.test(p.modpackRevision)) bad('modpackRevision', 'Ревизия состава должна содержать 32 шестнадцатеричных символа');
    for (const [name, limit, length] of [['highlights', 24, 240]]) {
      if (p[name].length > limit) bad(name, 'В разделе ' + name + ' допускается не больше ' + limit + ' пунктов');
      p[name].forEach((v, i) => { check(name + '.' + i, v, length); if (!v.trim()) bad(name + '.' + i, 'Заполните пункт ' + (i + 1)); });
    }
    if (p.media.length > 24) bad('media', 'Допускается не больше 24 скриншотов и трейлеров');
    p.media.forEach((m, i) => {
      if (!['image', 'video'].includes(m.kind)) bad('media.' + i + '.kind', 'Выберите тип файла');
      link('media.' + i + '.url', m.url, false); link('media.' + i + '.posterUrl', m.posterUrl, true); check('media.' + i + '.caption', m.caption, 240);
      link('media.' + i + '.sourceUrl', m.sourceUrl, true); check('media.' + i + '.author', m.author, 160);
      if (![m.width, m.height].every((v) => Number.isInteger(v) && v >= 0 && v <= 16384) || Boolean(m.width) !== Boolean(m.height)) bad('media.' + i, 'Укажите корректные ширину и высоту');
      if (m.publishedAt && (!/^\d{4}-\d{2}-\d{2}$/.test(m.publishedAt) || Number.isNaN(Date.parse(m.publishedAt)) || new Date(m.publishedAt).toISOString().slice(0, 10) !== m.publishedAt)) bad('media.' + i + '.publishedAt', 'Укажите дату ГГГГ-ММ-ДД');
      if (m.kind === 'image' && ((m.provider && m.provider !== 'file') || m.role)) bad('media.' + i, 'У скриншота не бывает видеопровайдера или роли');
      if (m.kind === 'video') {
        if (!['', 'trailer', 'gameplay'].includes(m.role)) bad('media.' + i + '.role', 'Выберите назначение видео');
        if (!['', 'file', 'youtube', 'rutube'].includes(m.provider)) bad('media.' + i + '.provider', 'Выберите способ воспроизведения');
        if (['youtube', 'rutube'].includes(m.provider) && !providerUrl(m.url, m.provider)) bad('media.' + i + '.url', 'Ссылка не соответствует выбранному видеосервису');
      }
      if (m.variants.length) {
        if (m.kind !== 'video' || !['', 'file'].includes(m.provider) || m.variants.length > 3) bad('media.' + i + '.variants', 'До трёх вариантов качества доступны только у видеофайла');
        const seen = new Set();
        m.variants.forEach((v, j) => {
          const key = 'media.' + i + '.variants.' + j;
          link(key + '.url', v.url, false);
          if (!HEIGHTS.includes(v.height) || seen.has(v.height) || !Number.isInteger(v.width) || v.width < 2 || v.width > 3840 || v.width % 2 || !Number.isSafeInteger(v.bytes) || v.bytes <= 0 || v.bytes > 10 * 1024 ** 3) bad(key, 'Проверьте разрешение и размер видео');
          seen.add(v.height);
        });
      }
    });
    if (p.modBenefits.length > 5) bad('modBenefits', 'Допускается не больше 5 преимуществ');
    p.modBenefits.forEach((b, i) => {
      check('modBenefits.' + i + '.title', b.title, 80); check('modBenefits.' + i + '.description', b.description, 240);
      if (!b.title.trim() || !b.description.trim()) bad('modBenefits.' + i, 'Заполните заголовок и описание преимущества');
      if (!['package', 'users', 'gamepad', 'sparkles', 'wrench', 'compass'].includes(b.icon)) bad('modBenefits.' + i + '.icon', 'Выберите значок преимущества');
    });
    const seen = new Set();
    if (p.mods.length > 256) bad('mods', 'Допускается не больше 256 модов');
    p.mods.forEach((m, i) => {
      const id = m.id.trim().toLowerCase();
      if (!id || seen.has(id)) bad('mods.' + i + '.id', 'У мода ' + (i + 1) + ' должен быть уникальный идентификатор');
      seen.add(id);
      if (!m.name.trim()) bad('mods.' + i + '.name', 'Укажите название мода ' + (i + 1));
      for (const [key, limit] of [['id', 160], ['name', 160], ['russianName', 160], ['description', 2000], ['category', 80], ['version', 80]]) check('mods.' + i + '.' + key, m[key], limit);
      link('mods.' + i + '.url', m.url, true);
    });
    return errors;
  }

  function change(value, group, action, index) {
    const p = normalize(value);
    if (!['media', 'mods', 'modBenefits'].includes(group)) return p;
    const list = p[group], max = group === 'media' ? 24 : group === 'modBenefits' ? 5 : 256;
    if (action === 'add' && list.length < max) list.push(group === 'media' ? { ...fields({}, MEDIA), kind: 'image', width: 0, height: 0, variants: [] } : group === 'modBenefits' ? { title: '', description: '', icon: 'package' } : fields({}, MODS));
    if (Number.isInteger(index) && index >= 0 && index < list.length) {
      if (action === 'remove') list.splice(index, 1);
      const to = action === 'up' ? index - 1 : action === 'down' ? index + 1 : index;
      if (to >= 0 && to < list.length && to !== index) [list[index], list[to]] = [list[to], list[index]];
    }
    return p;
  }

  function read(root) {
    const value = (name) => text(root.querySelector('[data-p-field="' + name + '"]')?.value).trim();
    const lines = (name) => value(name).split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
    const rows = (group, names) => Array.from(root.querySelectorAll('[data-p-row="' + group + '"]')).map((row) =>
      Object.fromEntries(names.map((name) => [name, text(row.querySelector('[data-p-key="' + name + '"]')?.value).trim()])));
    const media = rows('media', [...MEDIA, 'width', 'height', ...VARIANT_FIELDS]).map(m => ({
      ...fields(m, MEDIA), width: Number(m.width), height: Number(m.height),
      variants: HEIGHTS.filter(h => ['url', 'width', 'bytes'].some(k => m['quality' + h + k])).map(h => ({
        url: m['quality' + h + 'url'], width: Number(m['quality' + h + 'width']), height: h, bytes: Number(m['quality' + h + 'bytes'])
      }))
    }));
    const p = { description: value('description'), modpackDescription: value('modpackDescription'), modpackVersion: value('modpackVersion'), modpackRevision: value('modpackRevision'), genre: value('genre'), heroUrl: value('heroUrl'), highlights: lines('highlights'), media, mods: rows('mods', MODS), modBenefits: rows('modBenefits', BENEFITS) };
    return Object.values(p).every((v) => !v.length) ? null : p;
  }

  function providerUrl(value, provider) {
    if (!safeUrl(value, false)) return false;
    try {
      const u = new URL(value);
      if (u.port) return false;
      if (provider === 'youtube') return (u.hostname === 'youtu.be' && /^\/[A-Za-z0-9_-]{11}$/.test(u.pathname)) || (['youtube.com', 'www.youtube.com', 'm.youtube.com'].includes(u.hostname) && u.pathname === '/watch' && /^[A-Za-z0-9_-]{11}$/.test(u.searchParams.get('v') || ''));
      return ['rutube.ru', 'www.rutube.ru'].includes(u.hostname) && /^\/video\/[a-f0-9]{32}\/?$/i.test(u.pathname);
    } catch { return false; }
  }

  return { normalize, problems, safeUrl, providerUrl, change, read };
});
