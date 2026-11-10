import {createHash} from 'node:crypto';
import {createReadStream} from 'node:fs';
import {mkdir, readFile, writeFile, open, rename, rm, lstat} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';

export function validateMediaManifest(manifest) {
  assert(manifest && Array.isArray(manifest.files) && manifest.files.length <= 4000, 'Invalid media manifest');
  const paths=new Set();let total=0;
  for(const file of manifest.files){
    assert(/^media\/\d{4}-(?:0[1-9]|1[0-2])\/[a-z0-9-]+\/[a-z0-9.-]+\.(?:mp4|jpg)$/.test(file.path), 'Unsafe media path');
    assert(!paths.has(file.path), 'Duplicate media path');paths.add(file.path);
    assert(/^[a-f0-9]{64}$/.test(file.sha256), 'Invalid media checksum');
    assert(Number.isSafeInteger(file.bytes)&&file.bytes>0&&file.bytes<=512*1024*1024, 'Invalid media size');
    total+=file.bytes;
  }
  assert(total<=20*1024*1024*1024,'Media history exceeds 20 GiB');
  return manifest;
}
export async function fileHash(filename){const hash=createHash('sha256');for await(const chunk of createReadStream(filename))hash.update(chunk);return hash.digest('hex');}
async function jsonResponse(response){
  assert(response.ok,`Media manifest unavailable (HTTP ${response.status})`);
  const chunks=[];let size=0;
  for await(const chunk of response.body){size+=chunk.length;assert(size<=4*1024*1024,'Media manifest exceeds 4 MiB');chunks.push(chunk);}
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}
export async function prepareMediaHistory({base,cache,firstPublication=false,allowLoopbackForTests=false}){
  const origin=new URL(base);assert(origin.pathname==='/'&&!origin.username&&!origin.password&&!origin.search&&!origin.hash);
  assert(origin.protocol==='https:'||(allowLoopbackForTests&&origin.protocol==='http:'&&origin.hostname==='127.0.0.1'));
  const info=await lstat(cache).catch(e=>{if(e.code!=='ENOENT')throw e;});assert(!info?.isSymbolicLink(),'History cache cannot be a link');
  await mkdir(path.join(cache,'blobs'),{recursive:true});
  const request=async(route,timeout=20000)=>{
    for(let i=0;i<2;i++)try{return await fetch(new URL(route,origin),{redirect:'error',signal:AbortSignal.timeout(timeout)});}catch(error){if(i)throw error;await new Promise(r=>setTimeout(r,150));}
  };
  const response=await request('/assets/media/manifest.json');
  const missing=response.status===404;
  if(missing){
    await response.body?.cancel();
    assert(firstPublication,'Missing published media manifest; first publication requires --first-publication');
    const previous=await readFile(path.join(cache,'snapshot.json'),'utf8').then(JSON.parse).catch(e=>{if(e.code==='ENOENT')return null;throw e;});
    assert(!previous?.manifest?.files?.length,'Published history was previously present; refusing to reset it');
  }
  const manifest=validateMediaManifest(missing?{files:[]}:await jsonResponse(response));
  for(const file of manifest.files){
    const target=path.join(cache,'blobs',file.sha256);
    const existing=await lstat(target).catch(e=>{if(e.code!=='ENOENT')throw e;});
    assert(!existing?.isSymbolicLink(),'Cached media cannot be a link');
    if(existing?.isFile()&&existing.size===file.bytes&&await fileHash(target)===file.sha256)continue;
    const remote=await request('/assets/'+file.path,120000);
    assert(remote.ok,`Missing archived media: ${file.path} (HTTP ${remote.status})`);
    const length=remote.headers.get('content-length');if(length!==null)assert.equal(Number(length),file.bytes,'Archived media size changed');
    const temporary=target+'.part';const handle=await open(temporary,'w');let bytes=0;const hash=createHash('sha256');
    try{
      for await(const chunk of remote.body){bytes+=chunk.length;assert(bytes<=file.bytes,'Archived media exceeds declared size');hash.update(chunk);await handle.writeFile(chunk);}
      assert.equal(bytes,file.bytes,'Archived media is incomplete');assert.equal(hash.digest('hex'),file.sha256,'Archived media checksum changed');
      await handle.sync();
    }catch(error){await handle.close();await rm(temporary,{force:true});throw error;}
    await handle.close();await rename(temporary,target);
  }
  const snapshot={base:origin.origin,preparedAt:new Date().toISOString(),firstPublication:missing,manifest};
  const temporary=path.join(cache,'snapshot.json.part');await writeFile(temporary,JSON.stringify(snapshot,null,2)+'\n');await rename(temporary,path.join(cache,'snapshot.json'));
  return snapshot;
}

export function mergeMediaManifest(previous,current){
  validateMediaManifest(previous);validateMediaManifest(current);
  const entries=new Map(previous.files.map(file=>[file.path,file]));
  for(const file of current.files){
    const old=entries.get(file.path);
    assert(!old||(old.sha256===file.sha256&&old.bytes===file.bytes),`Immutable media path changed: ${file.path}; use a new path`);
    entries.set(file.path,file);
  }
  return validateMediaManifest({files:[...entries.values()].sort((a,b)=>a.path.localeCompare(b.path))});
}
