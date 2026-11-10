import test from 'node:test';
import assert from 'node:assert/strict';
import { mergeRegistry, validateURL, validatePresentation } from './presentation-tools.mjs';

test('merging presentations preserves hidden games, order, mod configuration and unknown fields',()=>{
  const original={revision:42,items:[{gameId:'hidden',hidden:true},{gameId:'peak',order:5,mods:{enabled:true,steamAppId:'3527290'},futureField:{important:true}}]};
  const snapshot=structuredClone(original);
  const merged=mergeRegistry(original,{peak:{description:'Новое описание'}});
  assert.deepEqual(original,snapshot);assert.deepEqual(merged.items[0],snapshot.items[0]);assert.deepEqual(merged.items[1].mods,snapshot.items[1].mods);assert.deepEqual(merged.items[1].futureField,snapshot.items[1].futureField);assert.equal(merged.items[1].order,5);assert.equal(merged.revision,42);assert.equal(merged.items[1].presentation.description,'Новое описание');
});
test('public catalog and missing game cannot produce destructive registry replacement',()=>{
  assert.throws(()=>mergeRegistry({items:[{gameId:'peak',hasLatest:true}]},{peak:{}}));
  assert.throws(()=>mergeRegistry({items:[{gameId:'peak'}]},{repo:{}}));
});
test('media URLs reject credentials, local addresses and traversal',()=>{
  for(const url of ['http://example.com/a.mp4','https://user:pass@example.com/a.mp4','https://localhost/a.mp4','https://127.0.0.1/a.mp4','file:///C:/x','/content/../secret','https://example.com/content/%2e%2e/secret','//evil.example/a'])assert.throws(()=>validateURL(url),url);
  validateURL('https://video.akamai.steamstatic.com/store_trailers/game/hls_264_master.m3u8?t=1');
});
test('exact source version prevents silently publishing a newer unrelated mod list',()=>{
  const p={description:'Описание',genre:'Жанр',modpackDescription:'Сборка',modpackVersion:'Author-Pack-1.0.0',modpackRevision:'a'.repeat(32),heroUrl:'https://example.com/hero.jpg',highlights:['Игра'],media:[{kind:'video',url:'https://example.com/trailer.mp4',posterUrl:'',caption:'Трейлер'},{kind:'image',url:'https://example.com/1.jpg',posterUrl:'',caption:'Снимок'},{kind:'image',url:'https://example.com/2.jpg',posterUrl:'',caption:'Снимок'}],mods:[{id:'Author-Mod',name:'Mod',russianName:'Название',description:'Описание',category:'Контент',version:'1.0.0',url:'https://example.com/mod'}]};
  validatePresentation(p,['Author-Mod-1.0.0']);assert.throws(()=>validatePresentation(p,['Author-Mod-2.0.0']));
  assert.throws(()=>validatePresentation({...p,modpackRevision:''}));
});
