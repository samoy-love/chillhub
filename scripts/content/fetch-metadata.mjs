// Read-only metadata harvest. No archives, credentials or server mutations.
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createHash } from 'node:crypto';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, '../..');
const cache = path.join(directory, '.cache');
const base = 'https://launcher.samoy.love';
await mkdir(cache, { recursive: true });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
let requests = 0;
let nextRequestAt = 0;
let cooldownUntil = 0;
async function pace() {
  const start = Math.max(Date.now(), nextRequestAt, cooldownUntil);
  nextRequestAt = start + 350;
  await sleep(Math.max(0, start - Date.now()));
  await sleep(Math.max(0, cooldownUntil - Date.now()));
}

async function get(url, fresh = false) {
  const filename = path.join(cache, `${createHash('sha256').update(url).digest('hex')}.json`);
  if (!fresh) {
    try { const saved = JSON.parse(await readFile(filename, 'utf8')); return saved.value; } catch {}
  }
  for (let attempt = 0; attempt < 8; attempt++) {
    await pace();
    let response;
    try { response = await fetch(url, { signal: AbortSignal.timeout(30000), headers: { 'User-Agent': 'ChillHub-Metadata/1.0', Accept: 'application/json' } }); }
    catch (error) { if (attempt === 7) throw error; await sleep(1200 * (attempt + 1)); continue; }
    requests++;
    if (response.status === 404) return null;
    if (response.status === 429) { cooldownUntil = Math.max(cooldownUntil, Date.now() + Math.min(90000, 15000 * (attempt + 1))); continue; }
    if (response.status >= 500) { await sleep(1200 * (attempt + 1)); continue; }
    if (!response.ok) throw new Error(`${response.status} ${url}`);
    const body = await response.text();
    if (body.length > 8 * 1024 * 1024) throw new Error(`Oversized metadata: ${url}`);
    const value = JSON.parse(body);
    await writeFile(filename, JSON.stringify({ url, fetchedAt: new Date().toISOString(), value }));
    return value;
  }
  throw new Error(`Metadata unavailable after retries: ${url}`);
}

async function parallel(items, mapper, concurrency = 4) {
  const output = new Array(items.length); let next = 0;
  await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, async () => {
    while (next < items.length) { const index = next++; output[index] = await mapper(items[index], index); }
  }));
  return output;
}

const registry = await get(`${base}/api/games`, true);
const seed = JSON.parse(await readFile(path.join(root, 'launcher/ChillHub.Native/Data/games.json'), 'utf8'));
const sources = {};
for (const game of registry.items) {
  if (game.mods?.hasLatest) {
    const url = `${base}/manifests/_mods/${encodeURIComponent(game.gameId)}/sources/${encodeURIComponent(game.mods.version)}.json`;
    const value = await get(url, true);
    if (!value?.tree?.length || value.version !== game.mods.version) throw new Error(`No exact tree: ${game.gameId}`);
    sources[game.gameId] = { url, ...value };
  }
}
const dependencies = [...new Set(Object.values(sources).flatMap(source => source.tree))].sort();
const packages = {};
await parallel(dependencies, async (fullName, index) => {
  const parts = fullName.split('-'); const version = parts.pop();
  let entry = null;
  for (let cut = 1; cut < parts.length; cut++) {
    const namespace = parts.slice(0, cut).join('-'); const name = parts.slice(cut).join('-');
    const url = `https://thunderstore.io/api/experimental/package/${encodeURIComponent(namespace)}/${encodeURIComponent(name)}/${encodeURIComponent(version)}/`;
    const value = await get(url);
    if (value) { entry = { fullName, namespace, name, version, source: url, ...value }; break; }
  }
  if (!entry) entry = { fullName, version, unavailable: true };
  packages[fullName] = entry;
  if ((index + 1) % 25 === 0) console.log(`Packages ${index + 1}/${dependencies.length}`);
  await sleep(150);
});

const steam = {};
const galleries = {};
const editorialAppIds = { farfarwest: '3124540', 'drive-beyond-horizons': '2625420', 'machine-party': '4108000' };
for (const game of registry.items) {
  const bundled = seed.find(g => g.Id === game.gameId);
  const appId = game.mods?.steamAppId || bundled?.SteamAppId || editorialAppIds[game.gameId] || bundled?.Media?.map(m => m.Source?.match(/steampowered\.com\/app\/(\d+)/)?.[1]).find(Boolean);
  if (appId) {
    const value = await get(`https://store.steampowered.com/api/appdetails?appids=${appId}&l=russian&cc=us`);
    steam[game.gameId] = { appId, value: value?.[appId]?.data || null };
  }
  const url = `${base}/content/${game.gameId}/gallery/gallery.json`;
  const value = await get(url);
  if (value) galleries[game.gameId] = { url, value };
}
const harvest = { fetchedAt: new Date().toISOString(), registry, sources, packages, steam, galleries };
await writeFile(path.join(cache, 'harvest.json'), JSON.stringify(harvest, null, 2));
const compact = Object.values(packages).map(p => ({ id: p.fullName, name: p.name, description: p.description || '', dependencies: p.dependencies || [], unavailable: Boolean(p.unavailable) }));
await writeFile(path.join(cache, 'packages-editorial.json'), JSON.stringify(compact, null, 2));
console.log(JSON.stringify({ games: registry.items.length, packages: dependencies.length, sourceCounts: Object.fromEntries(Object.entries(sources).map(([id, source]) => [id, source.tree.length])), unavailable: compact.filter(p => p.unavailable).map(p => p.id), requests }, null, 2));
