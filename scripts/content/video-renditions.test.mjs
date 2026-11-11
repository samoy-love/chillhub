import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, rm, stat} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {profiles, encodingArgs, sourceDetails, prepareVideo, run, probe} from './video-renditions.mjs';

test('rendition policy preserves frames, caps peaks and makes seekable H.264', () => {
  for (const profile of profiles) {
    const args = encodingArgs('source.mp4','out.mp4',profile);
    assert.equal(args[args.indexOf('-crf')+1], String(profile.crf));
    assert.equal(args[args.indexOf('-maxrate')+1], `${profile.maxrate}k`);
    assert.equal(args[args.indexOf('-bufsize')+1], `${profile.maxrate*2}k`);
    assert.equal(args[args.indexOf('-movflags')+1], '+faststart');
    assert.equal(args[args.indexOf('-c:v')+1], 'libx264');
    assert.equal(args[args.indexOf('-pix_fmt')+1], 'yuv420p');
    assert(!args.includes('-r'), 'do not discard frames');
  }
});

test('reject sources that need manual rotation or HDR treatment', () => {
  const source = {streams:[{codec_type:'video',width:1920,height:1080}], format:{duration:'30'}};
  assert.equal(sourceDetails(source).duration,30);
  for (const patch of [{height:360},{color_transfer:'smpte2084'},{side_data_list:[{rotation:90}]}]) {
    assert.throws(() => sourceDetails({...source,streams:[{...source.streams[0],...patch}]}));
  }
  for (const duration of ['NaN',0,14401]) assert.throws(() => sourceDetails({...source,format:{duration}}));
});

test('real encoder: no upscaling, audio and duration preserved, immutable output', {skip:!process.env.FFMPEG || !process.env.FFPROBE}, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(),'chillhub-video-'));
  try {
    const input = path.join(root,'source.mp4');
    await run(process.env.FFMPEG,['-hide_banner','-nostdin','-loglevel','error','-f','lavfi','-i','testsrc2=size=1280x720:rate=30','-f','lavfi','-i','sine=frequency=440','-t','1','-c:v','libx264','-preset','ultrafast','-crf','10','-c:a','aac','-y',input]);
    const report = await prepareVideo({input,output:path.join(root,'output')});
    assert.deepEqual(report.variants.map(v=>v.height),[720,480]);
    for (const v of report.variants) {
      assert.equal((await stat(v.localPath)).size,v.bytes);
      const info=await probe(v.localPath);
      assert.equal(info.streams[0].height,v.height);
      assert(info.streams.some(s=>s.codec_type==='audio'&&s.codec_name==='aac'));
      assert(Math.abs(Number(info.format.duration)-1)<0.1);
      assert.match(v.name,new RegExp(`^video-${v.height}p-[a-f0-9]{16}\\.mp4$`));
    }
    const second=await prepareVideo({input,output:path.join(root,'output')});
    assert.deepEqual(second,report);
  } finally { await rm(root,{recursive:true,force:true}); }
});
