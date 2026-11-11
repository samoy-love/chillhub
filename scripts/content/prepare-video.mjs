import {parseArgs} from 'node:util';
import {writeFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import {prepareVideo} from './video-renditions.mjs';

const {values} = parseArgs({options:{input:{type:'string'},output:{type:'string'},name:{type:'string'},'url-prefix':{type:'string'}}});
assert(values.input && values.output && values['url-prefix'], 'Usage: node prepare-video.mjs --input source.mp4 --output directory --name gameplay --url-prefix https://launcher.samoy.love/assets/media/YYYY-MM/game/');
const base = new URL(values['url-prefix']);
assert(base.protocol === 'https:' && base.pathname.endsWith('/') && !base.username && !base.password && !base.search && !base.hash, 'Use an HTTPS directory URL');
const report = await prepareVideo(values);
report.variants = report.variants.map(v => ({...v,url:new URL(v.name,base).href}));
await writeFile(path.join(values.output, `${values.name || 'video'}-renditions.json`), JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify(report,null,2));
