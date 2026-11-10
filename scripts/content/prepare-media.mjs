// Prepare official Steam promotional media locally. This never uploads or
// downloads third-party gameplay videos; external playback stays on its source.
import { readFile, writeFile, mkdir, stat } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const directory = path.dirname(fileURLToPath(import.meta.url));
const harvest = JSON.parse(await readFile(path.join(directory, '.cache/harvest.json'), 'utf8'));
const curation = JSON.parse(await readFile(path.join(directory, 'media-curation.json'), 'utf8'));
const output = path.join(directory, '.cache/release-media');
const ffmpeg = process.env.FFMPEG || 'ffmpeg';
const ffprobe = process.env.FFPROBE || 'ffprobe';
await mkdir(output, { recursive: true });
const run = (exe, args) => new Promise((resolve, reject) => {
  const child = spawn(exe, args, { windowsHide: true });
  let stdout = '', stderr = '';
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-12000); });
  child.on('error', reject);
  child.on('close', code => code === 0 ? resolve(stdout) : reject(new Error(`${exe}: ${code}\n${stderr}`)));
});
const report = { createdAt: new Date().toISOString(), serverWrites: 0, root: output, serverRoot:'/var/www/chillhub-media/current', deployment:{method:'deploy-kit',target:'chillhub-media',root:'/var/www/chillhub-media',urlPrefix:'/assets/media/',currentAlias:'/var/www/chillhub-media/current/media/',immutablePolicy:'Refuse to replace an existing relative path with different SHA-256; use a new revision directory'}, files: [], games: {} };
for (const game of harvest.registry.items) {
  const id = game.gameId;
  const store = harvest.steam[id];
  const movie = [...store.value.movies].sort((a,b) => priority(a.name)-priority(b.name)).find(m => m.hls_h264);
  assert(movie, `Missing official HLS: ${id}`);
  const playlist = await (await fetch(movie.hls_h264)).text();
  const variants = [...playlist.matchAll(/#EXT-X-STREAM-INF:([^\n]+)\r?\n([^\r\n]+)/g)];
  const index = variants.findIndex(v => /RESOLUTION=1920x1080(?:,|$)/.test(v[1]));
  assert(index >= 0, `No true 1080p source; do not upscale ${id}`);
  const gameDir = path.join(output, id);
  await mkdir(gameDir, { recursive: true });
  const filename = `trailer-${movie.id}-1080p.mp4`;
  const fullPath = path.join(gameDir, filename);
  if (!(await stat(fullPath).catch(() => null))) {
    await run(ffmpeg, ['-hide_banner', '-nostdin', '-loglevel', 'warning', '-i', movie.hls_h264, '-map', `0:v:${index}`, '-map', '0:a:0', '-c', 'copy', '-movflags', '+faststart', '-y', fullPath + '.part.mp4']);
    const { rename } = await import('node:fs/promises');
    await rename(fullPath + '.part.mp4', fullPath);
  }
  const probe = JSON.parse(await run(ffprobe, ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', fullPath]));
  const video = probe.streams.find(s => s.codec_type === 'video');
  assert(video?.width === 1920 && video?.height === 1080 && video.codec_name === 'h264', `Unexpected trailer format: ${id}`);
  assert(probe.streams.some(s => s.codec_type === 'audio'), `Missing trailer audio: ${id}`);
  const assets = [];
  for (const [index, selection] of curation[id].screenshots.entries()) {
    const screenshot = store.value.screenshots[selection.index];
    const shotName = `screenshot-${index+1}.jpg`;
    const response = await fetch(screenshot.path_full); assert(response.ok);
    const data = Buffer.from(await response.arrayBuffer()); assert(data.length <= 12*1024*1024);
    const shotPath = path.join(gameDir, shotName);
    await writeFile(shotPath, data);
    const shotProbe = JSON.parse(await run(ffprobe, ['-v','error','-show_streams','-of','json',shotPath]));
    const dimensions = shotProbe.streams[0]; assert(dimensions.width >= 1280 && dimensions.height >= 720);
    assets.push({ name: shotName, sourceUrl: screenshot.path_full, caption:selection.caption, width: dimensions.width, height: dimensions.height });
  }
  // The movie poster is a publisher supplied still, not a copied creator thumbnail.
  const poster = await fetch(movie.thumbnail); assert(poster.ok);
  await writeFile(path.join(gameDir,'trailer-poster.jpg'), Buffer.from(await poster.arrayBuffer()));
  assets.push({ name: 'trailer-poster.jpg', sourceUrl: movie.thumbnail });
  assets.unshift({ name: filename, sourceUrl: movie.hls_h264, width: 1920, height: 1080, durationSeconds: Number(probe.format.duration), codec: video.codec_name });
  const versionPath = `media/2026-11/${id}`;
  for (const asset of assets) {
    const bytes = await readFile(path.join(gameDir, asset.name));
    Object.assign(asset, { gameId:id, localPath:path.join(gameDir,asset.name), relativePath:`${versionPath}/${asset.name}`, url:`https://launcher.samoy.love/assets/${versionPath}/${asset.name}`, bytes:bytes.length, sha256:createHash('sha256').update(bytes).digest('hex') });
    report.files.push(asset);
  }
  report.games[id] = { trailer: assets[0], screenshots: assets.slice(1,4), poster: assets.at(-1), source:`https://store.steampowered.com/app/${store.appId}/`, trailerName:movie.name, trailerId:movie.id };
  await writeFile(path.join(directory,'.cache/media-upload-plan.json'), JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({ gameId:id, trailer:filename, bytes:assets[0].bytes, width:video.width, height:video.height }));
}
console.log(JSON.stringify({games:Object.keys(report.games).length,files:report.files.length,bytes:report.files.reduce((n,f)=>n+f.bytes,0),serverWrites:0}));
if(process.argv.includes('--record-release')){
  await writeFile(path.join(directory,'media-release.json'),JSON.stringify({files:report.files.map(({localPath,...asset})=>asset)},null,2)+'\n');
}

function priority(name='') {
  if (/launch trailer|release trailer|available now|official trailer/i.test(name)) return 0;
  if (/gameplay/i.test(name)) return 1;
  if (/update|reveal|release date/i.test(name)) return 3;
  return 2;
}
