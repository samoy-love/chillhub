import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const data=path.join(root,'launcher','ChillHub.Native','Data');
const [seed,bundled]=await Promise.all([
  readFile(path.join(data,'presentation-seed.json'),'utf8').then(JSON.parse),
  readFile(path.join(data,'games.json'),'utf8').then(JSON.parse),
]);
const assetPrefix='https://launcher.samoy.love/assets/';
const expectedHeights=[1080,720,480];

function assertAssetUrl(url,label){
  assert.equal(typeof url,'string',`${label} URL is required`);
  assert(url.startsWith(assetPrefix),`${label} must use the launcher assets host`);
  const parsed=new URL(url);
  assert.equal(parsed.protocol,'https:');
  assert.equal(parsed.origin,'https://launcher.samoy.love');
  assert(parsed.pathname.startsWith('/assets/'));
}

function assertVideo(media,label,urls){
  assert.equal(media.provider,'file',`${label} must use the file provider`);
  assert(['trailer','gameplay'].includes(media.role),`${label} has an unsupported video role`);
  assertAssetUrl(media.url,`${label} playback`);
  assertAssetUrl(media.posterUrl,`${label} poster`);
  assert(Array.isArray(media.variants),`${label} variants are required`);
  assert.deepEqual(media.variants.map(v=>v.height).sort((a,b)=>b-a),expectedHeights,`${label} must ship 1080p, 720p, and 480p`);
  const primary=media.variants.find(v=>v.height===1080);
  assert.equal(media.url,primary.url,`${label} playback URL must select its 1080p variant`);
  for(const variant of media.variants){
    assertAssetUrl(variant.url,`${label} ${variant.height}p`);
    assert(Number.isSafeInteger(variant.width)&&variant.width>0,`${label} ${variant.height}p width must be positive`);
    assert(Number.isSafeInteger(variant.bytes)&&variant.bytes>0,`${label} ${variant.height}p bytes must be positive`);
    assert(!urls.has(variant.url),`Duplicate video variant URL: ${variant.url}`);
    urls.add(variant.url);
  }
}

test('final bundled games ship trailer and gameplay renditions for every presentation',()=>{
  const gameIds=Object.keys(seed);
  const bundledIds=bundled.map(game=>game.Id);
  assert(gameIds.length>0,'Presentation seed must not be empty');
  assert.equal(new Set(bundledIds).size,bundledIds.length,'Bundled games have duplicate ids');
  assert.deepEqual(bundledIds.toSorted(),gameIds.toSorted(),'Bundled game ids must match the presentation seed');

  const urls=new Set();
  for(const gameId of gameIds){
    const presentation=seed[gameId];
    const bundle=bundled.find(game=>game.Id===gameId);
    assert(bundle,`Missing bundled game: ${gameId}`);
    const videos=presentation.media.filter(media=>media.kind==='video');
    assert.equal(videos.length,2,`${gameId} must have exactly trailer and gameplay videos`);
    assert.deepEqual(videos.map(media=>media.role).toSorted(),['gameplay','trailer'],`${gameId} video roles are incomplete or duplicated`);
    const images=presentation.media.filter(media=>media.kind==='image');
    assert.equal(images.length,3,`${gameId} must have exactly three images`);
    for(const image of images)assertAssetUrl(image.url,`${gameId} image`);

    const gameplay=videos.find(media=>media.role==='gameplay');
    assert(gameplay,`${gameId} gameplay media is required`);
    assert.equal(gameplay.sourceUrl,'',`${gameId} gameplay must not expose its source URL`);
    assert(typeof gameplay.author==='string'&&gameplay.author.trim(),`${gameId} gameplay author is required`);
    assert(typeof gameplay.caption==='string'&&/[\p{Script=Cyrillic}]/u.test(gameplay.caption),`${gameId} gameplay caption must be Russian`);
    for(const video of videos)assertVideo(video,`${gameId}/${video.role}`,urls);
    const gameplayBytes=expectedHeights.map(height=>gameplay.variants.find(v=>v.height===height).bytes);
    assert(gameplayBytes[0]>gameplayBytes[1]&&gameplayBytes[1]>gameplayBytes[2],`${gameId} gameplay sizes must decrease from 1080p to 720p to 480p`);

    assert.equal(bundle.Media.length,5,`${gameId} bundled media must contain two videos and three images`);
    const bundledVideos=bundle.Media.filter(media=>media.Kind==='video');
    assert.equal(bundledVideos.length,2,`${gameId} bundle must contain two videos`);
    assert.deepEqual(bundledVideos.map(media=>media.Role).toSorted(),['gameplay','trailer'],`${gameId} bundle roles differ from its presentation`);
    for(const video of bundledVideos){
      assert.equal(video.Provider,'file',`${gameId}/${video.Role} bundle provider must be file`);
      assertAssetUrl(video.Url,`${gameId}/${video.Role} bundle playback`);
      const sourceVideo=videos.find(media=>media.role===video.Role);
      assert(sourceVideo,`${gameId}/${video.Role} is missing from presentation seed`);
      assert.equal(video.Url,sourceVideo.url,`${gameId}/${video.Role} bundle URL differs from seed`);
      assert.deepEqual(video.Variants,sourceVideo.variants,`${gameId}/${video.Role} bundle variants differ from seed`);
    }
    const bundledImages=bundle.Media.filter(media=>media.Kind==='image');
    assert.equal(bundledImages.length,3,`${gameId} bundle must contain three images`);
    for(const image of bundledImages)assertAssetUrl(image.Url,`${gameId} bundled image`);
    assert.equal(bundledVideos.find(media=>media.Role==='gameplay').Author,gameplay.author,`${gameId} gameplay author differs between seed and bundle`);
    assert.equal(bundledVideos.find(media=>media.Role==='gameplay').Caption,gameplay.caption,`${gameId} gameplay caption differs between seed and bundle`);
  }
});
