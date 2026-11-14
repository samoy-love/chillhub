import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {prepareGameplay,runCli} from './prepare-gameplay.mjs';
import {gameplayPresentationEntry,bundledPosterPath,validateGameplayPresentation} from './gameplay-presentation.mjs';
import {validatePresentation} from './presentation-tools.mjs';

test('imports local gameplay, keeps other plan media, and replaces only prior gameplay', async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'chillhub-gameplay-'));
  try{
    const input=path.join(root,'recording.mp4'), poster=path.join(root,'poster.jpg');
    await writeFile(input,'source'); await writeFile(poster,Buffer.from([0xff,0xd8,0xff,0xd9]));
    const trailer={gameId:'sample',role:'trailer',url:'https://launcher.samoy.love/assets/media/2026-11/sample/trailer.mp4'};
    const screenshot={gameId:'sample',url:'https://launcher.samoy.love/assets/media/2026-11/sample/screenshot-1.jpg'};
    const other={gameId:'other',role:'gameplay',url:'https://launcher.samoy.love/assets/media/2026-11/other/gameplay.mp4'};
    const plan={games:{sample:{trailer:{url:trailer.url},gameplay:{url:'https://launcher.samoy.love/assets/old.mp4',posterUrl:'https://launcher.samoy.love/assets/old.jpg',variants:[{url:'https://launcher.samoy.love/assets/old.mp4'}]}},other:{gameplay:{url:other.url}}},files:[trailer,screenshot,other,{gameId:'sample',role:'gameplay',url:'https://launcher.samoy.love/assets/old.mp4'},{gameId:'sample',role:'gameplay-poster',url:'https://launcher.samoy.love/assets/old.jpg'}]};
    let encode=0;
    const prepareVideo=async({output,name})=>{
      const digest=encode++?'b':'a';
      const localPath=path.join(output,`${name}-720p-${digest.repeat(16)}.mp4`);
      await writeFile(localPath,'encoded');
      return {variants:[{name:path.basename(localPath),localPath,width:1280,height:720,bytes:7,sha256:digest.repeat(64)}]};
    };
    const result=await prepareGameplay({gameId:'sample',input,poster,author:'Автор канала',caption:'Запись прохождения',plan,output:path.join(root,'output'),month:'2026-11',prepareVideo});
    assert.equal(result.gameplay.provider,'file'); assert.equal(result.gameplay.role,'gameplay');
    assert.equal(result.gameplay.author,'Автор канала'); assert.equal(result.gameplay.caption,'Запись прохождения');
    assert.equal(result.gameplay.height,720); assert.equal(result.gameplay.variants.length,1);
    assert(result.gameplay.url.startsWith('https://launcher.samoy.love/assets/'));
    assert(result.gameplay.variants.every(v=>v.url.startsWith('https://launcher.samoy.love/assets/')));
    assert(!/youtube|youtu\.be/i.test(JSON.stringify(result.gameplay)));
    assert(result.gameplay.posterUrl.startsWith('https://launcher.samoy.love/assets/'));
    assert(!JSON.stringify(result.gameplay).includes('recording.mp4'));
    assert(plan.files.includes(trailer)); assert(plan.files.includes(screenshot)); assert(plan.files.includes(other));
    assert.equal(plan.files.filter(f=>f.gameId==='sample'&&f.role==='gameplay').length,2); // old immutable file retained
    assert.equal(plan.files.filter(f=>f.gameId==='sample'&&f.role==='gameplay-poster').length,2);
    const entry=gameplayPresentationEntry(result.gameplay);
    const presentation={description:'Описание игры',genre:'Приключения',modpackDescription:'',modpackVersion:'',modpackRevision:'',heroUrl:'https://launcher.samoy.love/assets/hero.jpg',highlights:['Хорошая игра'],mods:[],media:[
      {kind:'video',provider:'file',role:'trailer',url:trailer.url,posterUrl:trailer.url,caption:'Официальный трейлер',width:1920,height:1080,variants:[]},
      entry,
      ...['screenshot-1','screenshot-2','screenshot-3'].map((name,index)=>({kind:'image',url:`https://launcher.samoy.love/assets/${name}.jpg`,posterUrl:`https://launcher.samoy.love/assets/${name}.jpg`,caption:`Скриншот ${index+1}`,width:1280,height:720,variants:[]})),
    ]};
    validatePresentation(presentation,[]);
    validateGameplayPresentation(presentation,{gameplay:{author:'Автор канала',caption:'Запись прохождения'}});
    assert.throws(()=>validateGameplayPresentation(presentation,{gameplay:{author:'',caption:'Запись прохождения'}}));
    assert.equal(bundledPosterPath('sample',entry,1,true),'Assets/Media/sample/gameplay-poster.jpg');
    assert.equal(bundledPosterPath('sample',presentation.media[2],2,true),'Assets/Media/sample/screenshot-1.jpg');
    assert.equal(bundledPosterPath('sample',{kind:'image'},1,false),'Assets/Media/sample/screenshot-1.jpg');
    const replacement=await prepareGameplay({gameId:'sample',input,poster,author:'Автор 2',caption:'Новая запись',plan,output:path.join(root,'output'),month:'2026-11',prepareVideo});
    assert.equal(plan.files.filter(f=>f.gameId==='sample'&&f.role==='gameplay').length,3);
    assert.equal(plan.files.filter(f=>f.gameId==='sample'&&f.role==='gameplay-poster').length,2); // same poster URL deduplicated
    assert(plan.files.some(f=>f.url===result.gameplay.url),'old immutable URL remains available');
    assert.notEqual(replacement.gameplay.url,result.gameplay.url);
    assert.equal(plan.games.sample.gameplay.url,replacement.gameplay.url,'the current card points at the replacement');
    assert.equal(replacement.gameplay.author,'Автор 2');
    assert.deepEqual(await readFile(poster),Buffer.from([0xff,0xd8,0xff,0xd9]));
  }finally{await rm(root,{recursive:true,force:true});}
});

test('rejects invalid rendition names, dimensions, duplicate heights, and unsafe game ids', async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'chillhub-gameplay-invalid-'));
  try{
    const input=path.join(root,'input.mp4'),poster=path.join(root,'poster.jpg');
    await writeFile(input,'source');await writeFile(poster,Buffer.from([0xff,0xd8,0xff,0xd9]));
    const run=async(gameId,variants)=>prepareGameplay({gameId,input,poster,author:'Автор',caption:'Подпись',plan:{games:{sample:{}},files:[]},output:path.join(root,'out'),month:'2026-11',prepareVideo:async()=>({variants:variants.map((v,i)=>{
      const name=v.name||`gameplay-${i}.mp4`;const localPath=path.join(root,name);return {...v,name,localPath,bytes:1,sha256:'c'.repeat(64)};
    })})});
    const valid={name:'gameplay-720p-cccccccccccccccc.mp4',width:1280,height:720};
    await assert.rejects(run('../sample',[valid]),/Invalid gameId/);
    await assert.rejects(run('sample',[{...valid,name:'../escape.mp4'}]),/Unsafe rendition name/);
    await assert.rejects(run('sample',[{...valid,width:0}]),/Invalid rendition dimensions/);
    await assert.rejects(run('sample',[valid,{...valid,name:'gameplay-720p-dddddddddddddddd.mp4'}]),/Duplicate rendition heights/);
    const sorted=await run('sample',[{...valid,name:'gameplay-480p-eeeeeeeeeeeeeeee.mp4',width:854,height:480},
      {...valid,name:'gameplay-1080p-ffffffffffffffff.mp4',width:1920,height:1080},valid]);
    assert.deepEqual(sorted.gameplay.variants.map(v=>v.height),[1080,720,480]);
    await assert.rejects(()=>prepareGameplay({gameId:'sample',input,poster,author:'Автор',caption:'Gameplay video',plan:{games:{sample:{}},files:[]},output:path.join(root,'out'),prepareVideo:async()=>({variants:[valid]})}),/Caption must be Russian/);
  }finally{await rm(root,{recursive:true,force:true});}
});

test('rejects external source inputs that would create external public asset URLs', async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'chillhub-gameplay-'));
  try{
    const poster=path.join(root,'poster.jpg'); await writeFile(poster,Buffer.from([0xff,0xd8,0xff,0xd9]));
    const plan={games:{sample:{}},files:[]};
    await assert.rejects(()=>prepareGameplay({gameId:'sample',input:'https://example.com/video.mp4',poster,author:'Автор',caption:'Подпись',plan,output:path.join(root,'out'),prepareVideo:async()=>({variants:[{name:'x.mp4',localPath:'x',width:1280,height:720,bytes:1}]})}),/Invalid local gameplay video|ENOENT|no such file/i);
  }finally{await rm(root,{recursive:true,force:true});}
});

test('CLI workflow writes a JSON-serializable updated media plan', async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'chillhub-gameplay-cli-'));
  try{
    const input=path.join(root,'capture.mp4'),poster=path.join(root,'poster.jpg'),planPath=path.join(root,'media-upload-plan.json');
    await writeFile(input,'local video');await writeFile(poster,Buffer.from([0xff,0xd8,0xff,0xd9]));
    await writeFile(planPath,JSON.stringify({games:{sample:{trailer:{url:'https://launcher.samoy.love/assets/trailer.mp4'}}},files:[]}));
    const prepareVideo=async({output,name})=>{
      const bytes=Buffer.from('fixture mp4'),sha256=(await import('node:crypto')).createHash('sha256').update(bytes).digest('hex');
      const filename=`${name}-720p-${sha256.slice(0,16)}.mp4`,localPath=path.join(output,filename);
      await writeFile(localPath,bytes);
      return {variants:[{name:filename,localPath,width:1280,height:720,bytes:bytes.length,sha256}]};
    };
    const result=await runCli(['node','prepare-gameplay.mjs','--game-id','sample','--input',input,'--poster',poster,
      '--author','Автор канала','--caption','Запись прохождения','--plan',planPath,'--output',path.join(root,'release-media')],{prepareVideo});
    const saved=JSON.parse(await readFile(planPath,'utf8'));
    assert.equal(saved.games.sample.gameplay.author,'Автор канала');
    assert.equal(saved.games.sample.gameplay.caption,'Запись прохождения');
    assert.equal(saved.games.sample.gameplay.variants[0].height,720);
    assert.equal(saved.files.length,2);
    assert.equal(result.serverWrites,0);
    assert.equal(await readFile(planPath+'.part','utf8').catch(()=>''),'');
  }finally{await rm(root,{recursive:true,force:true});}
});
