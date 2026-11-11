// Build a verified artifact for deploy-kit. This script never connects to a server.
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, mkdir, copyFile, lstat, realpath, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import {mergeMediaManifest, validateMediaManifest} from './media-history.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const cache = path.join(root, 'scripts/content/.cache');
const sourceRoot = await realpath(path.join(cache, 'release-media'));
const plan = JSON.parse(await readFile(path.join(cache, 'media-upload-plan.json'), 'utf8'));
assert.equal(Object.keys(plan.games).length, 9, 'Expected all nine games');
assert(plan.files.length >= 45 && plan.files.length <= 100, 'Unexpected media inventory');
const release = JSON.parse(await readFile(path.join(root, 'scripts/content/media-release.json'), 'utf8'));
assert.deepEqual(plan.files.map(({localPath, ...asset}) => asset), release.files,
  'Prepared media differs from the reviewed release; record and review the new checksums before deployment');
const historyRoot = path.join(cache, 'published-media');
assert(!(await lstat(historyRoot)).isSymbolicLink(), 'History cache cannot be a link');
const history = JSON.parse(await readFile(path.join(historyRoot, 'snapshot.json'), 'utf8'));
assert.equal(history.base, 'https://launcher.samoy.love', 'History belongs to another server');
const age = Date.now() - Date.parse(history.preparedAt);
assert(Number.isFinite(age) && age >= 0 && age < 24 * 60 * 60 * 1000, 'Refresh published media history before staging');
assert(history.manifest.files.length || history.firstPublication === true, 'Empty history requires explicit first-publication receipt');
validateMediaManifest(history.manifest);
const output = path.join(root, 'stage-media');
const existing = await lstat(output).catch(error => { if (error.code !== 'ENOENT') throw error; });
assert(!existing?.isSymbolicLink(), 'Artifact directory must not be a link');
const verified = [];
const sources = new Map();
const destinations = new Set();
for (const entry of plan.files) {
  assert(/^media\/\d{4}-\d{2}\/[a-z0-9-]+\/[a-z0-9.-]+\.(?:mp4|jpg)$/.test(entry.relativePath), 'Unexpected media path');
  assert(!destinations.has(entry.relativePath), 'Duplicate media destination');
  destinations.add(entry.relativePath);
  // The artifact may be encoded on Windows and staged by Linux deploy-kit.
  // Derive its local location from the reviewed portable inventory, not a drive letter.
  assert(/^[a-z0-9-]+$/.test(entry.gameId) && /^[a-z0-9.-]+\.(?:mp4|jpg)$/.test(entry.name), 'Unsafe local media name');
  assert(entry.relativePath.endsWith(`/${entry.gameId}/${entry.name}`), 'Media inventory path mismatch');
  const source = await realpath(path.join(sourceRoot, entry.gameId, entry.name));
  const relative = path.relative(sourceRoot, source);
  assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'Source outside prepared media');
  assert.equal((await lstat(source)).size, entry.bytes, 'Media size changed');
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(source)) hash.update(chunk);
  assert.equal(hash.digest('hex'), entry.sha256, 'Media checksum changed');
  verified.push({ source, entry });
  sources.set(entry.relativePath, source);
}
const current = { files: verified.map(({ entry }) => ({ path: entry.relativePath, sha256: entry.sha256, bytes: entry.bytes,
  width: entry.width, height: entry.height, source: entry.sourceUrl })) };
const manifest = mergeMediaManifest(history.manifest, current);
const historicalRoot = await realpath(path.join(historyRoot, 'blobs'));
for (const entry of manifest.files) {
  if (sources.has(entry.path)) continue;
  const candidate = path.join(historicalRoot, entry.sha256);
  assert(!(await lstat(candidate)).isSymbolicLink(), 'Archived media cannot be a link');
  const source = await realpath(candidate);
  assert.equal(path.dirname(source), historicalRoot, 'Archived media escaped its cache');
  assert.equal((await lstat(source)).size, entry.bytes, 'Archived media size changed');
  const hash = createHash('sha256'); for await (const chunk of createReadStream(source)) hash.update(chunk);
  assert.equal(hash.digest('hex'), entry.sha256, 'Archived media checksum changed');
  sources.set(entry.path, source);
}
// Only this fixed build-output directory is replaced, after every source passed.
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
for (const entry of manifest.files) {
  const destination = path.join(output, entry.path);
  await mkdir(path.dirname(destination), { recursive: true });
  await copyFile(sources.get(entry.path), destination);
}
await writeFile(path.join(output, 'media-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
console.log(JSON.stringify({ files: manifest.files.length, bytes: manifest.files.reduce((sum, entry) => sum + entry.bytes, 0),
  currentFiles: current.files.length, inheritedFiles: manifest.files.length - current.files.length }));
