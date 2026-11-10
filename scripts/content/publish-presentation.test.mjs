import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { publishPresentation } from './publish-presentation.mjs';

const sample = { description:'Исследуйте остров', genre:'Кооператив',modpackDescription:'',modpackVersion:'',modpackRevision:'',heroUrl:'https://example.com/hero.jpg',highlights:['Друзья'],media:[{kind:'video',url:'https://example.com/trailer.mp4',posterUrl:'',caption:'Трейлер'},{kind:'image',url:'https://example.com/1.jpg',posterUrl:'',caption:'Скриншот'},{kind:'image',url:'https://example.com/2.jpg',posterUrl:'',caption:'Скриншот'}],mods:[],modBenefits:[] };

async function fixture(t, options={}) {
  const output=await mkdtemp(path.join(os.tmpdir(),'chillhub-presentation-'));
  const state={posts:0,items:[{gameId:'hidden',unpublished:true,order:0},{gameId:'test',title:'Test',order:1,exeRelativePath:'Test.exe',mods:{enabled:false},pinned:true}],etag:'"'+'a'.repeat(64)+'"'};
  const server=http.createServer(async(req,res)=>{
    res.setHeader('Content-Type','application/json');
    if(req.url==='/admin/api/games') {
      assert.equal(req.headers.cookie,'access_token=test; csrf_token=csrf');
      if(!options.oldAPI)res.setHeader('ETag',state.etag);
      res.end(JSON.stringify({items:state.items}));
    } else if(req.url==='/api/games') res.end(JSON.stringify({items:[{...state.items[1],latestVersion:options.changedVersion?'2.0':'1.0'}]}));
    else if(req.url==='/assets/media/file.jpg') {res.setHeader('Content-Length','12');res.end();}
    else if(req.url==='/admin/api/games/save') {
      state.posts++;
      assert.equal(req.headers['x-csrf-token'],'csrf');
      assert.equal(req.headers['if-match'],state.etag);
      let body='';for await(const chunk of req)body+=chunk;
      if(options.concurrentEdit){res.writeHead(412);res.end('{}');return;}
      state.items=JSON.parse(body).items;res.end(JSON.stringify({items:state.items}));
    }else{res.writeHead(404);res.end('{}');}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(async()=>{await new Promise(resolve=>server.close(resolve));await rm(output,{recursive:true,force:true});});
  const base=`http://127.0.0.1:${server.address().port}/`;
  return {state,output,args:{base,cookie:'access_token=test; csrf_token=csrf',seed:{test:structuredClone(sample)},provenance:{games:{test:{gameVersion:'1.0',packages:[]}}},output,allowLoopbackForTests:true,mediaPlan:{files:[{url:new URL('/assets/media/file.jpg',base).href,bytes:12}]}}};
}
test('dry run stores a complete backup and proposed registry without POST',async t=>{
  const f=await fixture(t,{oldAPI:true});const result=await publishPresentation(f.args);
  assert.equal(f.state.posts,0);assert.equal(result.serverWrites,0);assert.equal(result.checks.optimisticConcurrency,false);
  const backup=JSON.parse(await readFile(path.join(f.output,'registry-before.json')));
  assert.equal(backup.items[0].unpublished,true);assert.equal(backup.items[1].pinned,true);
  assert.equal(JSON.stringify(result).includes('access_token'),false);
});
test('apply requires new API and matching published versions before any POST',async t=>{
  for(const options of [{oldAPI:true},{changedVersion:true}]){
    await t.test(JSON.stringify(options),async t=>{const f=await fixture(t,options);await assert.rejects(publishPresentation({...f.args,apply:true}));assert.equal(f.state.posts,0);});
  }
});
test('apply preserves hidden games and launch settings, then verifies public metadata',async t=>{
  const f=await fixture(t);const result=await publishPresentation({...f.args,apply:true});
  assert.equal(f.state.posts,1);assert.equal(result.checks.publicReadback,true);
  assert.equal(f.state.items[0].unpublished,true);assert.equal(f.state.items[1].exeRelativePath,'Test.exe');assert.equal(f.state.items[1].pinned,true);
  assert.deepEqual(f.state.items[1].presentation,sample);
});
test('a concurrent edit refuses the migration instead of retrying a stale snapshot',async t=>{
  const f=await fixture(t,{concurrentEdit:true});await assert.rejects(publishPresentation({...f.args,apply:true}),/412/);
  assert.equal(f.state.posts,1);assert.equal(f.state.items[1].presentation,undefined);
  const report=JSON.parse(await readFile(path.join(f.output,'publication-result.json')));assert.equal(report.writeAttempted,true);
});
