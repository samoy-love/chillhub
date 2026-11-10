// Public watch-page and oEmbed metadata only. No video stream extraction/download.
import {readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const directory=path.dirname(fileURLToPath(import.meta.url));
const selected=JSON.parse(await readFile(path.join(directory,'gameplay-sources.json'),'utf8'));
const output={checkedAt:new Date().toISOString(),playback:'external',downloads:0,games:{}};
for(const [id,source] of Object.entries(selected)){
  const url=`https://www.youtube.com/watch?v=${source.videoId}`;
  const response=await fetch('https://www.youtube.com/oembed?format=json&url='+encodeURIComponent(url),{signal:AbortSignal.timeout(15000)});
  assert(response.ok,`Unavailable creator video ${id}`);
  const metadata=await response.json();assert.equal(metadata.author_name,source.author,`Creator mismatch ${id}`);
  let match;
  for(let attempt=0;attempt<3&&!match;attempt++){
    if(attempt)await new Promise(r=>setTimeout(r,1500));
    const page=await(await fetch(url+'&hl=en',{signal:AbortSignal.timeout(15000)})).text();
    match=page.match(/var ytInitialData = (.*?);<\/script>/s);
  }
  assert(match,`Missing public video metadata: ${id}`);
  let info;
  function walk(value){if(!value||typeof value!=='object'||info)return;if(value.videoPrimaryInfoRenderer){info=value.videoPrimaryInfoRenderer;return;}for(const v of Object.values(value))walk(v);}
  walk(JSON.parse(match[1]));
  const viewText=info?.viewCount?.videoViewCountRenderer?.viewCount?.simpleText||'';
  const views=Number(viewText.replace(/[^0-9]/g,''));assert(views>0,`Missing confirmed views: ${id}`);
  const dateText=info?.dateText?.simpleText||'';
  const date=new Date(dateText.replace(/^.*?(?=[A-Z][a-z]{2} \d)/,''));
  const publishedAt=Number.isNaN(date.getTime())?source.publishedAt:`${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
  assert(/^\d{4}-\d{2}-\d{2}$/.test(publishedAt),`Missing exact publication date: ${id}: ${dateText}`);
  output.games[id]={...source,url,provider:'youtube',role:'gameplay',publishedAt,title:metadata.title,authorUrl:metadata.author_url,posterUrl:metadata.thumbnail_url,views,viewText,dateText,checkedAt:output.checkedAt,sourceUrl:url,metadataSource:'https://www.youtube.com/oembed?format=json&url='+encodeURIComponent(url)};
  await writeFile(path.join(directory,'gameplay-verification.json'),JSON.stringify(output,null,2)+'\n');
  console.log(JSON.stringify({id,title:metadata.title,views,publishedAt}));
}
await writeFile(path.join(directory,'gameplay-verification.json'),JSON.stringify(output,null,2)+'\n');
