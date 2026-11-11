// Add prepared renditions to the existing reviewed trailer plan. Never publishes.
import {readFile, writeFile, rename} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
import {prepareVideo} from './video-renditions.mjs';

const directory=path.dirname(fileURLToPath(import.meta.url));
const planPath=path.join(directory,'.cache/media-upload-plan.json');
const plan=JSON.parse(await readFile(planPath,'utf8'));
for (const [id,game] of Object.entries(plan.games)) {
  const source=game.trailer;
  assert(source.localPath && source.height === 1080,'A verified 1080p trailer is required');
  const report=await prepareVideo({input:source.localPath,output:path.dirname(source.localPath),name:`trailer-${game.trailerId}`});
  const variants=report.variants.map(v=>({...v,gameId:id,sourceUrl:source.sourceUrl,
    relativePath:path.posix.join(path.posix.dirname(source.relativePath),v.name),url:new URL(v.name,source.url).href}));
  game.trailer={...variants[0],variants:variants.map(({url,width,height,bytes})=>({url,width,height,bytes}))};
  const previous=new Set([source.url,...(source.variants||[]).map(v=>v.url)]);
  plan.files=plan.files.filter(f=>!previous.has(f.url));
  plan.files.push(...variants);
  // Keep a resumable checkpoint separate from the deploy plan until all games succeed.
  await writeFile(path.join(directory,'.cache/trailer-qualities-progress.json'),JSON.stringify(plan,null,2)+'\n');
  console.log(JSON.stringify({gameId:id,sourceBytes:report.sourceBytes,variants:variants.map(({height,bytes})=>({height,bytes}))}));
}
await writeFile(planPath+'.part',JSON.stringify(plan,null,2)+'\n');
await rename(planPath+'.part',planPath);
if(process.argv.includes('--record-release')) await writeFile(path.join(directory,'media-release.json'),JSON.stringify({files:plan.files.map(({localPath,...asset})=>asset)},null,2)+'\n');
