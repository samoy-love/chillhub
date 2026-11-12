// Default: offline validation. --live checks public production state with GETs.
// --registry FILE --output FILE prepares a local merged admin export, never POSTs.
import { readFile, writeFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import {fileHash} from './media-history.mjs';
import { validatePresentation, mergeRegistry } from './presentation-tools.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, '../..');
const data = path.join(root,'launcher/ChillHub.Native/Data');
const read = async filename => JSON.parse(await readFile(filename,'utf8'));
const presentations = await read(path.join(data,'presentation-seed.json'));
const bundled = await read(path.join(data,'games.json'));
const provenance = await read(path.join(directory,'metadata-provenance.json'));
const prepared = process.argv.includes('--prepared') ? await read(path.join(directory,'.cache/media-upload-plan.json')) : null;
if(prepared){
  assert(prepared.files.length >= 45 && prepared.files.length <= 100);
  for(const file of prepared.files){
    assert.equal((await stat(file.localPath)).size,file.bytes);assert.equal(await fileHash(file.localPath),file.sha256);
  }
}
assert.equal(Object.keys(presentations).length, 9);
assert.deepEqual(bundled.map(g=>g.Id).toSorted(),Object.keys(presentations).toSorted());
const report = [];
for (const game of bundled) {
  const source = provenance.games[game.Id];
  assert(source,`Missing provenance: ${game.Id}`);
  const p = presentations[game.Id];
  validatePresentation(p,source.packages.map(p=>p.id));
  assert.equal(game.GameVersion,source.gameVersion);
  assert.equal(game.ModpackRevision,source.modpackRevision);
  assert.equal(p.modpackVersion,source.modpackVersion);
  assert.equal(p.modpackRevision,source.modpackRevision);
  assert.equal(game.Mods.length,p.mods.length);
  assert.deepEqual(game.Mods.map(m=>`${m.Id}-${m.Version}`).toSorted(),source.packages.map(p=>p.id).toSorted());
  assert.equal(game.ModpackDescription,p.modpackDescription);
  assert.equal(p.media.filter(m=>m.kind==='video').length,1);
  assert.equal(p.media.filter(m=>m.kind==='image').length,3);
  assert.deepEqual(game.ModBenefits,p.modBenefits);
  assert.equal(p.media.find(m=>m.role==='trailer').height,1080);
  for (const media of p.media) for (const variant of media.variants || []) {
    assert(prepared === null || prepared.files.some(f => f.url === variant.url && f.bytes === variant.bytes && f.height === variant.height && f.width === variant.width), `Unprepared video quality: ${game.Id} ${variant.height}`);
  }
  report.push({gameId:game.Id,gameVersion:game.GameVersion,modpackVersion:source.modpackVersion,modpackRevision:source.modpackRevision,packages:p.mods.length,media:p.media.length,trailer:source.trailerFormat});
}
let liveChecked=false;
if (process.argv.includes('--live')) {
  const request = async url => { const response=await fetch(url,{signal:AbortSignal.timeout(20000)});assert(response.ok,`${response.status} ${url}`);return response; };
  const live=await(await request('https://launcher.samoy.love/api/games')).json();
  assert.deepEqual(live.items.map(g=>g.gameId).toSorted(),Object.keys(presentations).toSorted());
  for(const game of live.items){
    const source=provenance.games[game.gameId];
    assert.equal(game.latestVersion,source.gameVersion,`Game version changed: ${game.gameId}`);
    assert.equal(game.mods?.version||'',source.modpackVersion,`Modpack changed: ${game.gameId}`);
    assert.equal(game.mods?.revision||'',source.modpackRevision,`Modpack rebuilt: ${game.gameId}`);
    if(source.packageSource){const tree=await(await request(source.packageSource)).json();assert.deepEqual(tree.tree.toSorted(),source.packages.map(p=>p.id).toSorted());}
    const hero=await fetch(presentations[game.gameId].heroUrl,{method:'HEAD',signal:AbortSignal.timeout(15000)});assert(hero.ok,`Unavailable hero: ${game.gameId}`);
    for(const media of presentations[game.gameId].media){
      if(prepared){assert(prepared.files.some(f=>f.url===media.url),`Unprepared media ${media.url}`);continue;}
      const response=await fetch(media.url,{method:'HEAD',signal:AbortSignal.timeout(15000)});assert(response.ok,`Unavailable media: ${game.gameId} ${response.status} ${media.url}`);
      if(media.kind==='video'&&media.url.includes('.m3u8')){
        const playlist=await(await request(media.url)).text();assert(playlist.startsWith('#EXTM3U'),`Not HLS: ${game.gameId}`);
        const child=playlist.split(/\r?\n/).find(line=>line&&!line.startsWith('#'));
        assert(child,`Empty HLS: ${game.gameId}`);
        const nested=await(await request(new URL(child,media.url))).text();assert(nested.startsWith('#EXTM3U'),`Invalid rendition: ${game.gameId}`);
      }
    }
  }
  liveChecked=true;
}
const registryIndex=process.argv.indexOf('--registry');const outputIndex=process.argv.indexOf('--output');
if(registryIndex>=0){
  const input=path.resolve(process.argv[registryIndex+1]||'');const output=path.resolve(process.argv[outputIndex+1]||'');
  assert(outputIndex>=0 && input!==output,'Use a separate --output file; the source registry is never modified');
  const merged=mergeRegistry(await read(input),presentations);
  await writeFile(output,JSON.stringify(merged,null,2)+'\n');
}
console.log(JSON.stringify({valid:true,mode:'dry-run',serverWrites:0,liveChecked,preparedMedia:prepared?{files:prepared.files.length,bytes:prepared.files.reduce((n,f)=>n+f.bytes,0),hashesVerified:true}:null,games:report},null,2));
