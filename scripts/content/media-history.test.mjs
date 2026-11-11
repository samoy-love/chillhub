import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {createHash} from 'node:crypto';
import {mkdtemp,readFile,writeFile,rm,access} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {prepareMediaHistory,mergeMediaManifest,validateMediaManifest} from './media-history.mjs';
const bytes=Buffer.from('official-media-fixture');
const file={path:'media/2026-10/peak/screenshot-1.jpg',bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')};
async function fixture(t){
  const cache=await mkdtemp(path.join(os.tmpdir(),'chillhub-history-'));
  const state={status:200,manifest:{files:[file]},media:bytes,downloads:0};
  const server=http.createServer((req,res)=>{
    if(req.url==='/assets/media/manifest.json'){res.writeHead(state.status,{'Content-Type':'application/json'});res.end(JSON.stringify(state.manifest));}
    else if(req.url==='/assets/'+file.path){state.downloads++;res.end(state.media);}
    else{res.writeHead(404);res.end();}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(async()=>{await new Promise(resolve=>server.close(resolve));await rm(cache,{recursive:true,force:true});});
  return{state,cache,args:{base:`http://127.0.0.1:${server.address().port}/`,cache,allowLoopbackForTests:true}};
}
test('published history is downloaded, checksum verified and reused without losing older paths',async t=>{
  const f=await fixture(t);const first=await prepareMediaHistory(f.args);
  assert.deepEqual(first.manifest.files,[file]);assert.equal(f.state.downloads,1);
  await prepareMediaHistory(f.args);assert.equal(f.state.downloads,1);
  const next={...file,path:'media/2026-11/peak/screenshot-2.jpg'};
  assert.deepEqual(mergeMediaManifest(first.manifest,{files:[next]}).files.map(f=>f.path),[file.path,next.path]);
  assert.equal(mergeMediaManifest(first.manifest,{files:[file]}).files.length,1);
  assert.throws(()=>mergeMediaManifest(first.manifest,{files:[{...file,sha256:'a'.repeat(64)}]}),/Immutable/);
});
test('first publication requires explicit 404 consent and can never erase known history',async t=>{
  const f=await fixture(t);f.state.status=404;
  await assert.rejects(prepareMediaHistory(f.args),/first publication/);
  await assert.rejects(access(path.join(f.cache,'snapshot.json')));
  const first=await prepareMediaHistory({...f.args,firstPublication:true});assert.equal(first.firstPublication,true);
  f.state.status=200;await prepareMediaHistory(f.args);f.state.status=404;
  await assert.rejects(prepareMediaHistory({...f.args,firstPublication:true}),/previously present/);
});
test('network failure and altered media refuse a new snapshot instead of empty history',async t=>{
  const f=await fixture(t);await prepareMediaHistory(f.args);
  const before=await readFile(path.join(f.cache,'snapshot.json'),'utf8');
  f.state.status=503;await assert.rejects(prepareMediaHistory({...f.args,firstPublication:true}),/503/);
  assert.equal(await readFile(path.join(f.cache,'snapshot.json'),'utf8'),before);
  f.state.status=200;await writeFile(path.join(f.cache,'blobs',file.sha256),'broken');f.state.media=Buffer.alloc(bytes.length,1);
  await assert.rejects(prepareMediaHistory(f.args),/checksum changed/);
  assert.equal(await readFile(path.join(f.cache,'snapshot.json'),'utf8'),before);
});
test('media manifest cannot escape local cache or request unbounded downloads',()=>{
  for(const bad of [{...file,path:'../outside.mp4'},{...file,path:'media/2026-11/peak/../../bad.mp4'},{...file,bytes:10*1024*1024*1024+1},{...file,sha256:'invalid'}])assert.throws(()=>validateMediaManifest({files:[bad]}));
  assert.throws(()=>validateMediaManifest({files:[file,file]}),/Duplicate/);
});
