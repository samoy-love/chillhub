import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdtemp, mkdir, readFile, rm, stat, writeFile, copyFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {canHardlinkMedia, stageMediaFile} from './stage-media-files.mjs';

const data = Buffer.from('verified immutable video bytes');
const sha256 = createHash('sha256').update(data).digest('hex');
const entry = (mediaPath = 'media/2026-11/game/gameplay-1080p.mp4', digest = sha256) => ({path: mediaPath, bytes: data.length, sha256: digest});

async function temp(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'chillhub-stage-media-'));
  t.after(() => rm(root, {recursive: true, force: true}));
  const sourceDirectory = path.join(root, 'source');
  const destinationDirectory = path.join(root, 'stage');
  await mkdir(sourceDirectory);
  await mkdir(destinationDirectory);
  return {root, sourceDirectory, destinationDirectory};
}

test('hardlinks are opt-in and limited to MP4 sources named for their content hash', async t => {
  const f = await temp(t);
  const shortName = `gameplay-demo-1080p-${sha256.slice(0, 16)}.mp4`;
  const fullName = sha256;
  const imageName = `poster-${sha256.slice(0, 16)}.jpg`;
  assert.equal(canHardlinkMedia(path.join(f.sourceDirectory, shortName), entry()), true);
  assert.equal(canHardlinkMedia(path.join(f.sourceDirectory, fullName), entry()), true);
  assert.equal(canHardlinkMedia(path.join(f.sourceDirectory, imageName), entry('media/2026-11/game/poster.jpg')), false);
  assert.equal(canHardlinkMedia(path.join(f.sourceDirectory, `gameplay-demo-${'0'.repeat(16)}.mp4`), entry()), false);
});

test('default staging copies files independently', async t => {
  const f = await temp(t);
  const source = path.join(f.sourceDirectory, `gameplay-demo-${sha256.slice(0, 16)}.mp4`);
  const destination = path.join(f.destinationDirectory, 'gameplay.mp4');
  await writeFile(source, data);
  const result = await stageMediaFile({source, destination, entry: entry()});
  assert.deepEqual(result, {method: 'copied', bytes: data.length});
  await writeFile(source, Buffer.from('changed source after staging'));
  assert.deepEqual(await readFile(destination), data);
});

test('opt-in staging hardlinks eligible MP4 and verifies destination size and hash', async t => {
  const f = await temp(t);
  const source = path.join(f.sourceDirectory, `gameplay-demo-1080p-${sha256.slice(0, 16)}.mp4`);
  const destination = path.join(f.destinationDirectory, 'gameplay.mp4');
  await writeFile(source, data);
  const result = await stageMediaFile({source, destination, entry: entry(), hardlinkEnabled: true});
  assert.deepEqual(result, {method: 'linked', bytes: data.length});
  assert.equal((await stat(source)).ino, (await stat(destination)).ino);
});

test('opt-in staging still copies images and MP4 without a matching hash name', async t => {
  const f = await temp(t);
  const cases = [
    {name: 'poster.jpg', mediaPath: 'media/2026-11/game/poster.jpg'},
    {name: 'gameplay-unhashed.mp4', mediaPath: 'media/2026-11/game/gameplay-unhashed.mp4'},
    {name: `gameplay-wrong-${'0'.repeat(16)}.mp4`, mediaPath: 'media/2026-11/game/gameplay-wrong.mp4'},
  ];
  let linkCalls = 0;
  for (const item of cases) {
    const source = path.join(f.sourceDirectory, item.name);
    const destination = path.join(f.destinationDirectory, item.name);
    await writeFile(source, data);
    const result = await stageMediaFile({source, destination, entry: entry(item.mediaPath), hardlinkEnabled: true,
      linkFile: async () => { linkCalls++; throw new Error('ineligible files must not be linked'); }});
    assert.deepEqual(result, {method: 'copied', bytes: data.length});
    await writeFile(source, Buffer.from('changed source after staging'));
    assert.deepEqual(await readFile(destination), data);
  }
  assert.equal(linkCalls, 0);
});

test('expected hardlink errors fall back to a verified copy', async t => {
  const f = await temp(t);
  const source = path.join(f.sourceDirectory, sha256);
  const destination = path.join(f.destinationDirectory, 'gameplay.mp4');
  await writeFile(source, data);
  const result = await stageMediaFile({source, destination, entry: entry(), hardlinkEnabled: true,
    linkFile: async () => { throw Object.assign(new Error('different devices'), {code: 'EXDEV'}); }});
  assert.deepEqual(result, {method: 'copied', bytes: data.length});
  assert.deepEqual(await readFile(destination), data);
});

test('unexpected hardlink errors fail instead of silently copying', async t => {
  const f = await temp(t);
  const source = path.join(f.sourceDirectory, sha256);
  const destination = path.join(f.destinationDirectory, 'gameplay.mp4');
  await writeFile(source, data);
  for (const code of ['EIO', 'EEXIST']) {
    await assert.rejects(stageMediaFile({source, destination, entry: entry(), hardlinkEnabled: true,
      linkFile: async () => { throw Object.assign(new Error(code), {code}); }}), new RegExp(code));
  }
});

test('destination hash mismatch fails after copy', async t => {
  const f = await temp(t);
  const source = path.join(f.sourceDirectory, 'poster.jpg');
  const destination = path.join(f.destinationDirectory, 'poster.jpg');
  await writeFile(source, data);
  const copyWrongBytes = async (from, to) => copyFile(from, to).then(() => writeFile(to, Buffer.alloc(data.length, 0)));
  await assert.rejects(stageMediaFile({source, destination,
    entry: entry('media/2026-11/game/poster.jpg'), copy: copyWrongBytes}), /Staged media checksum changed/);
});
