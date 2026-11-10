import assert from 'node:assert/strict';
import { isIP } from 'node:net';
import P from '../../server/admin_ui/presentation.js';

export function validateURL(value, optional = false) {
  if (optional && value === '') return;
  assert.equal(typeof value, 'string');
  assert(value.length <= 2048 && value === value.trim() && !/[\\\x00-\x20]/.test(value), `Invalid URL: ${value}`);
  if (value.startsWith('/')) {
    assert(/^\/(assets|gallery|manifests|content)\//.test(value), `Unexpected local media URL: ${value}`);
    assert(!decodeURIComponent(value).split('/').some(part => part === '..' || part === '.'), `Traversal: ${value}`);
    return;
  }
  const url = new URL(value);
  assert(url.protocol === 'https:' && !url.username && !url.password && !url.hash, `Untrusted URL: ${value}`);
  assert(!isIP(url.hostname) && !/(^|\.)(localhost|local)$/.test(url.hostname), `Local host: ${value}`);
  const originalPath = value.replace(/^https:\/\/[^/]+/, '').split('?')[0];
  assert(!decodeURIComponent(originalPath).split('/').some(part => part === '..' || part === '.'), `Traversal: ${value}`);
}

function text(value, limit, russian = false) {
  assert.equal(typeof value, 'string');
  assert([...value].length <= limit && !/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/.test(value), 'Invalid editorial text');
  if (russian) assert(/[а-яё]/i.test(value), `Missing Russian text: ${value}`);
}

export function validatePresentation(presentation, expectedPackages) {
  assert.deepEqual(P.problems(presentation), [], 'Admin and content schema disagree');
  text(presentation.description, 12000, true);
  text(presentation.genre, 120, true);
  text(presentation.modpackDescription, 2000);
  text(presentation.modpackVersion, 200);
  text(presentation.modpackRevision, 64);
  validateURL(presentation.heroUrl);
  for (const [field, count, length] of [['highlights',24,240]]) {
    assert(Array.isArray(presentation[field]) && presentation[field].length > 0 && presentation[field].length <= count);
    presentation[field].forEach(value => text(value, length, true));
  }
  assert(Array.isArray(presentation.media) && presentation.media.length >= 3 && presentation.media.length <= 24);
  assert(presentation.media.some(m => m.kind === 'video'), 'Missing trailer');
  assert(presentation.media.filter(m => m.kind === 'image').length >= 2, 'Missing screenshots');
  for (const media of presentation.media) {
    assert(['image','video'].includes(media.kind));
    validateURL(media.url); validateURL(media.posterUrl, true); text(media.caption,240,true);
    if (media.kind === 'video') {
      if (['youtube','rutube'].includes(media.provider)) assert(P.providerUrl(media.url,media.provider),'Invalid external provider');
      else assert(/\.(mp4|webm|m3u8|mpd)(\?|$)/i.test(media.url), `Trailer needs a direct playable URL: ${media.url}`);
    }
  }
  assert(Array.isArray(presentation.mods) && presentation.mods.length <= 256);
  const names = new Set(); const actual = [];
  for (const mod of presentation.mods) {
    text(mod.id,160);text(mod.name,160);text(mod.russianName,160,true);text(mod.description,2000,true);text(mod.category,80,true);text(mod.version,80);
    validateURL(mod.url);
    assert(mod.id && mod.name && mod.version && !names.has(mod.id.toLowerCase()), `Duplicate or empty package: ${mod.id}`);
    names.add(mod.id.toLowerCase()); actual.push(`${mod.id}-${mod.version}`);
  }
  if (expectedPackages) assert.deepEqual(actual.toSorted(), expectedPackages.toSorted(), 'Editorial list differs from the exact published modpack');
  assert.equal(Boolean(presentation.mods.length), Boolean(presentation.modpackDescription), 'Mod summary and package list disagree');
  if (presentation.mods.length) {
    assert(presentation.modpackVersion && /^[a-f0-9]{32}$/i.test(presentation.modpackRevision), 'Mod descriptions must bind to an exact published version and revision');
    if (presentation.modBenefits) {
      assert(presentation.modBenefits.length>=3&&presentation.modBenefits.length<=5,'Need 3–5 useful modpack benefits');
      for(const benefit of presentation.modBenefits){text(benefit.title,80,true);text(benefit.description,240,true);}
    }
  } else {
    assert.equal(presentation.modpackVersion, ''); assert.equal(presentation.modpackRevision, '');
  }
}

// The admin save endpoint replaces the whole registry. Always merge into a full
// fresh admin export; the public /api/games response is not an admin export.
export function mergeRegistry(registry, presentations) {
  assert(registry && Array.isArray(registry.items), 'Expected a full admin registry {items:[...]}');
  assert(!registry.items.some(item => 'hasLatest' in item || 'latestVersion' in item), 'A public catalog cannot be used to overwrite the admin registry');
  const ids = new Set(registry.items.map(item => item.gameId));
  for (const id of Object.keys(presentations)) assert(ids.has(id), `Game missing from admin registry: ${id}`);
  return { ...structuredClone(registry), items: registry.items.map(item => presentations[item.gameId] ? { ...structuredClone(item), presentation: structuredClone(presentations[item.gameId]) } : structuredClone(item)) };
}
