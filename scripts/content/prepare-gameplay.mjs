// Import a locally supplied gameplay recording into the reviewed media plan.
// This tool never fetches media or publishes files.
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {copyFile, mkdir, readFile, rename, stat, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {parseArgs} from 'node:util';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {prepareVideo as defaultPrepareVideo} from './video-renditions.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const publicBase = 'https://launcher.samoy.love/assets/';

export async function prepareGameplay({gameId, input, poster, author, caption, plan, output, month = new Date().toISOString().slice(0, 7), prepareVideo = defaultPrepareVideo}) {
  assert(/^[a-z0-9-]+$/.test(gameId || ''), 'Invalid gameId');
  assert(typeof author === 'string' && author.trim() && [...author].length <= 160, 'Author is required (maximum 160 characters)');
  assert(typeof caption === 'string' && caption.trim() && [...caption].length <= 240 && /[а-яё]/i.test(caption), 'Caption must be Russian (maximum 240 characters)');
  assert(/^\d{4}-(?:0[1-9]|1[0-2])$/.test(month), 'Invalid media month');
  const inputInfo = await stat(input);
  assert(inputInfo.isFile() && inputInfo.size > 0 && inputInfo.size <= 10 * 1024 ** 3, 'Invalid local gameplay video');
  const posterInfo = await stat(poster);
  assert(posterInfo.isFile() && posterInfo.size > 0 && posterInfo.size <= 12 * 1024 * 1024, 'Invalid poster file');
  assert(['.jpg','.jpeg'].includes(path.extname(poster).toLowerCase()), 'Poster must be a local JPEG file');
  assert(plan?.games && Object.hasOwn(plan.games, gameId) && Array.isArray(plan.files), `Game is missing from media plan: ${gameId}`);

  const gameDir = path.resolve(output, gameId);
  await mkdir(gameDir, {recursive:true});
  const report = await prepareVideo({input, output:gameDir, name:`gameplay-${gameId}`});
  assert(Array.isArray(report.variants) && report.variants.length > 0, 'No gameplay renditions were prepared');
  const versionPath = `media/${month}/${gameId}`;
  const checkedVariants = report.variants.map(v => {
    assert(v && typeof v.name === 'string' && path.basename(v.name) === v.name && /^[a-z0-9][a-z0-9.-]*\.mp4$/.test(v.name), 'Unsafe rendition name');
    assert(path.basename(v.localPath || '') === v.name, 'Rendition path/name mismatch');
    assert([480,720,1080].includes(v.height) && Number.isSafeInteger(v.width) && v.width >= 2 && v.width <= 3840 && v.width % 2 === 0, 'Invalid rendition dimensions');
    assert(Number.isSafeInteger(v.bytes) && v.bytes > 0 && v.bytes <= 10 * 1024 ** 3 && /^[a-f0-9]{64}$/.test(v.sha256 || ''), 'Invalid rendition size or checksum');
    return v;
  }).toSorted((a,b)=>b.height-a.height);
  assert(new Set(checkedVariants.map(v=>v.height)).size === checkedVariants.length, 'Duplicate rendition heights');
  const variants = checkedVariants.map(v => {
    const relativePath = path.posix.join(versionPath, v.name);
    const url = new URL(relativePath, publicBase).href;
    assert(url.startsWith(publicBase) && new URL(url).origin === 'https://launcher.samoy.love' && new URL(url).pathname.startsWith('/assets/'), 'Unsafe public rendition URL');
    return {...v, gameId, role:'gameplay', relativePath, url};
  });

  const posterBytes = await readFile(poster);
  const posterSha256 = createHash('sha256').update(posterBytes).digest('hex');
  const posterName = `gameplay-poster-${posterSha256.slice(0,16)}.jpg`;
  const posterPath = path.join(gameDir, posterName);
  await copyFile(poster, posterPath);
  // The admin presentation's Path is the stable offline bundle filename.
  await copyFile(poster, path.join(gameDir,'gameplay-poster.jpg'));
  const posterRelativePath = path.posix.join(versionPath, posterName);
  const posterUrl = new URL(posterRelativePath, publicBase).href;
  assert(posterUrl.startsWith(publicBase) && new URL(posterUrl).origin === 'https://launcher.samoy.love' && new URL(posterUrl).pathname.startsWith('/assets/'), 'Unsafe public poster URL');
  const posterAsset = {name:posterName, gameId, role:'gameplay-poster', localPath:posterPath, relativePath:posterRelativePath,
    url:posterUrl, bytes:posterBytes.length, sha256:posterSha256};
  const gameplay = {provider:'file', role:'gameplay', author:author.trim(), caption:caption.trim(),
    url:variants[0].url, posterUrl:posterAsset.url,
    localPath:variants[0].localPath,
    sha256:variants[0].sha256,
    width:variants[0].width, height:variants[0].height,
    variants:variants.map(({url,width,height,bytes})=>({url,width,height,bytes})), poster:posterAsset};

  // Keep old immutable URLs available to existing clients; only the current
  // plan.games[gameId].gameplay pointer is replaced below.
  const byUrl = new Map(plan.files.map(file=>[file.url,file]));
  for(const file of [...variants,posterAsset]){
    const existing=byUrl.get(file.url);
    assert(!existing || (existing.sha256===file.sha256 && existing.bytes===file.bytes && existing.relativePath===file.relativePath), 'Immutable media URL collision');
    if(!existing)byUrl.set(file.url,file);
  }
  plan.files=[...byUrl.values()];
  plan.games[gameId].gameplay = gameplay;
  return {plan, gameplay, added: [...variants, posterAsset]};
}

async function main(args) {
  const {values:options}=parseArgs({args:args.slice(2),allowPositionals:false,options:{
    'game-id':{type:'string'},input:{type:'string'},poster:{type:'string'},author:{type:'string'},caption:{type:'string'},
    plan:{type:'string'},output:{type:'string'},
  }});
  for (const key of ['game-id','input','poster','author','caption']) assert(options[key], `Missing --${key}`);
  const planPath = options.plan || path.join(directory,'.cache/media-upload-plan.json');
  const plan = JSON.parse(await readFile(planPath,'utf8'));
  const output = options.output || path.join(directory,'.cache/release-media');
  const result = await prepareGameplay({gameId:options['game-id'],input:options.input,poster:options.poster,
    author:options.author,caption:options.caption,plan,output});
  const temp = planPath+'.part';
  await writeFile(temp,JSON.stringify(result.plan,null,2)+'\n');
  await rename(temp,planPath);
  console.log(JSON.stringify({gameId:options['game-id'],variants:result.gameplay.variants.length,serverWrites:0}));
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main(process.argv).catch(error=>{console.error(error.message);process.exitCode=1;});
}
