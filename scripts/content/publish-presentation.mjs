// Editorial migration through the admin API, never a deployment mechanism.
// Default is GET + local backup/review. --apply requires the upgraded API's ETag,
// matching published versions and already deployed immutable media assets.
// Credentials: CHILLHUB_ADMIN_COOKIE; no secrets are written to reports.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { pathToFileURL, fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import { mergeRegistry, validatePresentation } from './presentation-tools.mjs';
import Presentation from '../../server/admin_ui/presentation.js';

const directory = path.dirname(fileURLToPath(import.meta.url));
const canonical = value => JSON.stringify(value, (_, v) => v && !Array.isArray(v) && typeof v === 'object'
  ? Object.fromEntries(Object.entries(v).sort(([a], [b]) => a.localeCompare(b))) : v);
const digest = value => createHash('sha256').update(typeof value === 'string' ? value : canonical(value)).digest('hex');
const stripPresentation = row => { const copy = structuredClone(row); delete copy.presentation; return copy; };

async function boundedJson(response) {
  assert(response.ok, `HTTP ${response.status}: ${new URL(response.url).pathname}`);
  const chunks = []; let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length; assert(size <= 4 * 1024 * 1024, 'Response exceeds registry size limit'); chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

export async function publishPresentation({ base, cookie, seed, provenance, output, apply = false, mediaPlan = null, allowLoopbackForTests = false }) {
  const origin = new URL(base);
  assert(origin.pathname === '/' && !origin.username && !origin.password && !origin.search && !origin.hash);
  assert(origin.protocol === 'https:' || (allowLoopbackForTests && origin.hostname === '127.0.0.1' && origin.protocol === 'http:'));
  assert(cookie && !/[\r\n]/.test(cookie), 'Provide an authenticated session in CHILLHUB_ADMIN_COOKIE');
  const csrf = cookie.split(';').map(s => s.trim()).find(s => s.startsWith('csrf_token='))?.slice('csrf_token='.length);
  if (apply) assert(csrf, 'The session must include csrf_token before applying changes');
  for (const [gid, p] of Object.entries(seed)) {
    assert(provenance.games[gid], `Missing source proof: ${gid}`);
    validatePresentation(p, provenance.games[gid].packages.map(p => p.id));
  }
  const request = async (route, options = {}) => {
    const attempts = !options.method || options.method === 'HEAD' ? 2 : 1;
    for (let attempt = 0; attempt < attempts; attempt++) {
      try {
        return await fetch(new URL(route, origin), {
          redirect: 'error', signal: AbortSignal.timeout(20000), ...options,
          headers: { Accept: 'application/json', ...(route.startsWith('/admin/') ? { Cookie: cookie } : {}), ...options.headers },
        });
      } catch (cause) {
        if (attempt + 1 === attempts) throw new Error(`Request failed: ${options.method || 'GET'} ${route}`, { cause });
        await new Promise(resolve => setTimeout(resolve, 150));
      }
    }
  };
  await mkdir(output, { recursive: true });
  const response = await request('/admin/api/games');
  const before = await boundedJson(response);
  const etag = response.headers.get('etag');
  const merged = mergeRegistry(before, seed);
  assert.deepEqual(merged.items.map(stripPresentation), before.items.map(stripPresentation));
  const snapshot = JSON.stringify(before, null, 2) + '\n';
  await writeFile(path.join(output, 'registry-before.json'), snapshot, { flag: 'wx' });
  await writeFile(path.join(output, 'registry-proposed.json'), JSON.stringify(merged, null, 2) + '\n', { flag: 'wx' });
  const live = await boundedJson(await request('/api/games'));
  for (const [gid, p] of Object.entries(seed)) {
    const game = live.items.find(g => g.gameId === gid); assert(game, `Missing published game: ${gid}`);
    assert.equal(game.latestVersion, provenance.games[gid].gameVersion, `Game changed: ${gid}`);
    assert.equal(game.mods?.version || '', p.modpackVersion, `Modpack version changed: ${gid}`);
    assert.equal(game.mods?.revision || '', p.modpackRevision, `Modpack revision changed: ${gid}`);
  }
  const report = { createdAt: new Date().toISOString(), mode: apply ? 'apply' : 'dry-run', serverWrites: 0,
    base: origin.origin, snapshotSha256: digest(snapshot), seedSha256: digest(seed), etag,
    games: Object.entries(seed).map(([gid, p]) => ({ gameId: gid, mods: p.mods.length, media: p.media.length, benefits: p.modBenefits?.length || 0 })),
    backup: path.join(output, 'registry-before.json'), proposed: path.join(output, 'registry-proposed.json'),
    checks: { versions: true, preservesNonEditorialFields: true, optimisticConcurrency: Boolean(etag), publicReadback: false } };
  const saveReport = () => writeFile(path.join(output, 'publication-result.json'), JSON.stringify(report, null, 2) + '\n');
  await saveReport();
  if (!apply) return report;
  assert(/^"[a-f0-9]{64}"$/.test(etag || ''), 'Admin API lacks safe revision checks; deploy the new schema first');
  assert(mediaPlan?.files?.length, 'Applying requires the prepared immutable media plan');
  for (const file of mediaPlan.files) {
    const url = new URL(file.url); assert.equal(url.origin, origin.origin, 'Upload plan belongs to another server');
    const result = await request(url.pathname, { method: 'HEAD' });
    assert(result.ok, `Media is not deployed: ${url.pathname} (HTTP ${result.status})`);
    assert.equal(Number(result.headers.get('content-length')), file.bytes, `Media size mismatch: ${url.pathname}`);
  }
  report.writeAttempted = true; await saveReport();
  const result = await request('/admin/api/games/save', {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'If-Match': etag }, body: JSON.stringify(merged),
  });
  const written = await boundedJson(result);
  report.serverWrites = 1;
  await writeFile(path.join(output, 'registry-after.json'), JSON.stringify(written, null, 2) + '\n', { flag: 'wx' });
  await saveReport();
  assert.deepEqual(written.items.map(stripPresentation), before.items.map(stripPresentation), 'Non-editorial fields changed after save');
  const after = await boundedJson(await request('/admin/api/games'));
  const published = await boundedJson(await request('/api/games'));
  for (const [gid, p] of Object.entries(seed)) {
    assert.deepEqual(Presentation.normalize(after.items.find(g => g.gameId === gid)?.presentation), Presentation.normalize(p), `Admin readback differs: ${gid}`);
    assert.deepEqual(Presentation.normalize(published.items.find(g => g.gameId === gid)?.presentation), Presentation.normalize(p), `Public readback differs: ${gid}`);
  }
  report.checks.publicReadback = true; await saveReport(); return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const value = (flag, fallback) => process.argv.includes(flag) ? process.argv[process.argv.indexOf(flag) + 1] : fallback;
  const read = async file => JSON.parse(await readFile(file, 'utf8'));
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const result = await publishPresentation({
    base: process.env.CHILLHUB_BASE_URL || 'https://launcher.samoy.love/', cookie: process.env.CHILLHUB_ADMIN_COOKIE,
    seed: await read(path.join(directory, '../../launcher/ChillHub.Native/Data/presentation-seed.json')),
    provenance: await read(path.join(directory, 'metadata-provenance.json')),
    mediaPlan: await read(path.join(directory, '.cache/media-upload-plan.json')),
    output: path.resolve(value('--output', path.join(directory, '.cache/publication-' + stamp))), apply: process.argv.includes('--apply'),
  });
  console.log(JSON.stringify(result, null, 2));
}
