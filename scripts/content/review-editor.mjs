// Headless review of the actual admin page with current editorial data.
// Every API request is served by a disposable localhost fixture.
import http from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const require = createRequire(import.meta.url);
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.LOCALAPPDATA,'agent-tools/playwright/node_modules/playwright'));
const directory = path.dirname(fileURLToPath(import.meta.url)), root = path.resolve(directory,'../..');
const seed = JSON.parse(await readFile(path.join(root,'launcher/ChillHub.Native/Data/presentation-seed.json'),'utf8'));
const output=path.join(directory,'.cache/admin-review');await mkdir(output,{recursive:true});
let saved=null;
const fixtures={games:{items:[{gameId:'peak',title:'PEAK',exeRelativePath:'PEAK.exe',order:0,mods:{enabled:true,steamAppId:'3527290',exeNames:['PEAK.exe']},presentation:seed.peak}]},summary:{launcher:{},mods:[]},'maintenance/get':{state:{enabled:false},effective:{enabled:false,blocks:{}}},'system/free':{bytes:10000000000,total:20000000000},'metrics/summary':{totals:{},byDay:[],topErrors:[]}};
const server=http.createServer(async(req,res)=>{
  try{
    const url=new URL(req.url,'http://localhost');
    if(url.pathname.startsWith('/admin/api/')){
      const key=url.pathname.slice('/admin/api/'.length);
      if(key==='games/save'){let body='';for await(const chunk of req)body+=chunk;saved=JSON.parse(body);fixtures.games=saved;}
      res.setHeader('Content-Type','application/json');res.end(JSON.stringify(fixtures[key]||{items:[]}));return;
    }
    const relative=url.pathname==='/admin/'?'index.html':url.pathname.replace(/^\/admin\/ui\//,'');
    assert(!relative.includes('..')&&!relative.startsWith('/'));
    const full=path.join(root,'server/admin_ui',relative);
    const type={'.js':'text/javascript','.css':'text/css','.html':'text/html','.svg':'image/svg+xml','.woff2':'font/woff2'}[path.extname(full)]||'application/octet-stream';
    res.setHeader('Content-Type',type);res.end(await readFile(full));
  }catch{res.writeHead(404);res.end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const base=`http://127.0.0.1:${server.address().port}`;
const browser=await chromium.launch({headless:true});
try{
  const page=await browser.newPage({viewport:{width:1440,height:1000},deviceScaleFactor:1});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(base+'/admin/#games');
  await page.locator('[data-tab="presentation"]').click();
  assert.equal(await page.locator('[data-p-row="mods"]').count(),233);
  assert.equal(await page.locator('[data-p-row="media"]').count(),5);
  assert.equal(await page.locator('[data-p-row="modBenefits"]').count(),3);
  assert.equal(await page.locator('[data-p-field="instructions"]').count(),0);
  await page.screenshot({path:path.join(output,'desktop-overview.png')});
  await page.locator('[data-p-row="modBenefits"]').first().scrollIntoViewIfNeeded();
  await page.screenshot({path:path.join(output,'desktop-benefits.png')});
  await page.locator('[data-p-row="media"]').nth(1).scrollIntoViewIfNeeded();
  await page.screenshot({path:path.join(output,'desktop-gameplay.png')});
  assert.equal(await page.locator('[data-p-row="media"]').nth(1).locator('[data-p-key="provider"]').inputValue(),'youtube');
  await page.locator('[data-p-row="modBenefits"]').first().locator('[data-p-key="title"]').fill('Команда друзей');
  await page.locator('[data-game-do="save"]').click();
  await page.waitForFunction(()=>document.body.textContent.includes('Сохранено'));
  await page.waitForLoadState('networkidle');
  assert(saved);assert.equal(saved.items[0].presentation.mods.length,233);
  assert.equal(saved.items[0].presentation.media.length,5);
  assert.equal(saved.items[0].presentation.modBenefits[0].title,'Команда друзей');
  assert.equal(saved.items[0].mods.steamAppId,'3527290');
  const layouts=[];
  for(const width of [1440,900]){
    await page.setViewportSize({width,height:1000});
    await page.locator('[data-p-field="description"]').scrollIntoViewIfNeeded();
    const size=await page.evaluate(()=>({viewport:innerWidth,content:document.documentElement.scrollWidth}));
    await page.screenshot({path:path.join(output,`width-${width}.png`)});
    const overflow=await page.evaluate(()=>[...document.querySelectorAll('body *')].filter(e=>e.getBoundingClientRect().right>innerWidth+1).slice(0,6).map(e=>({tag:e.tagName,class:e.className,right:e.getBoundingClientRect().right})));
    assert(size.content<=size.viewport,`Horizontal overflow at ${width}: ${size.content}; ${JSON.stringify(overflow)}`);layouts.push(size);
  }
  assert.deepEqual(errors,[]);
  const report={valid:true,serverWrites:0,localFixtureSaves:1,games:['peak'],mods:233,media:5,benefits:3,layouts,browserErrors:errors};
  const {writeFile}=await import('node:fs/promises');await writeFile(path.join(output,'result.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report));
}finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
