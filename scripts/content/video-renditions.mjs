// Local, authorized source files only. Publication remains a separate deploy-kit step.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdir, mkdtemp, stat, rename, rm, copyFile} from 'node:fs/promises';
import path from 'node:path';
import {fileHash} from './media-history.mjs';

export const profiles = [
  {height:1080, crf:20, maxrate:10000, audio:160},
  {height:720, crf:21, maxrate:5000, audio:128},
  {height:480, crf:22, maxrate:2200, audio:96},
];

export function run(exe, args, timeout = 3 * 60 * 60 * 1000) {
  return new Promise((resolve, reject) => {
    const child = spawn(exe, args, {windowsHide:true, timeout});
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout = (stdout + chunk).slice(-1024 * 1024); });
    child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-16000); });
    child.on('error', reject);
    child.on('close', code => code === 0 ? resolve(stdout) : reject(new Error(`${path.basename(exe)} exited ${code}: ${stderr}`)));
  });
}

export async function probe(filename, ffprobe = process.env.FFPROBE || 'ffprobe') {
  return JSON.parse(await run(ffprobe, ['-v','error','-show_streams','-show_format','-of','json',filename], 60000));
}

export function encodingArgs(input, output, profile) {
  return ['-hide_banner','-nostdin','-loglevel','warning','-i', input,
    '-map','0:v:0','-map','0:a:0?','-map_metadata','-1','-sn','-dn',
    '-vf',`scale=-2:${profile.height}:flags=lanczos,setsar=1`,
    '-c:v','libx264','-preset','slow','-crf',String(profile.crf),
    '-maxrate',`${profile.maxrate}k`,'-bufsize',`${profile.maxrate * 2}k`,
    '-pix_fmt','yuv420p','-threads','4','-g','120',
    '-c:a','aac','-b:a',`${profile.audio}k`,'-ac','2',
    '-movflags','+faststart','-y',output];
}

export function sourceDetails(info) {
  const video = info.streams.find(s => s.codec_type === 'video');
  const duration = Number(info.format.duration);
  assert(video && Number.isInteger(video.width) && video.width >= 2 && video.width <= 7680 && video.height >= 480 && video.height <= 4320, 'Source must contain video of at least 480p');
  assert(Number.isFinite(duration) && duration > 0 && duration <= 4 * 3600, 'Invalid video duration (maximum four hours)');
  assert(!video.side_data_list?.some(s => s.rotation), 'Rotate the source before preparing renditions');
  assert(!['smpte2084', 'arib-std-b67'].includes(video.color_transfer), 'HDR needs an explicitly reviewed SDR conversion');
  const audio = info.streams.find(s => s.codec_type === 'audio');
  return {video, audio, duration};
}

export async function prepareVideo({input, output, name = 'video', ffmpeg = process.env.FFMPEG || 'ffmpeg', ffprobe = process.env.FFPROBE || 'ffprobe'}) {
  assert(/^[a-z0-9-]+$/.test(name), 'Use a simple media name');
  input = path.resolve(input); output = path.resolve(output);
  const original = await stat(input);
  assert(original.isFile() && original.size > 0 && original.size <= 10 * 1024 ** 3, 'Invalid source file');
  const info = sourceDetails(await probe(input, ffprobe));
  await mkdir(output, {recursive:true});
  const temporary = await mkdtemp(path.join(output, '.encode-'));
  const variants = [];
  try {
    for (const profile of profiles.filter(p => p.height <= info.video.height)) {
      const encoded = path.join(temporary, `${profile.height}.mp4`);
      await run(ffmpeg, encodingArgs(input, encoded, profile));
      // A compact compatible original needs only a fast-start remux, not another generation of loss.
      if (info.video.height === profile.height && info.video.codec_name === 'h264' && info.video.pix_fmt === 'yuv420p' && (!info.audio || info.audio.codec_name === 'aac') && original.size < (await stat(encoded)).size) {
        await run(ffmpeg, ['-hide_banner','-nostdin','-loglevel','warning','-i',input,'-map','0:v:0','-map','0:a:0?','-c','copy','-map_metadata','-1','-movflags','+faststart','-y',encoded]);
      }
      const result = sourceDetails(await probe(encoded, ffprobe));
      assert(result.video.height === profile.height && result.video.width <= 3840 && result.video.width % 2 === 0 && result.video.codec_name === 'h264' && result.video.pix_fmt === 'yuv420p', 'Invalid rendition format');
      assert(Math.abs(result.duration - info.duration) < 0.25, 'Rendition duration changed');
      assert(Boolean(info.audio) === Boolean(result.audio), 'Rendition lost audio');
      const sha256 = await fileHash(encoded), bytes = (await stat(encoded)).size;
      assert(bytes > 0 && bytes <= 10 * 1024 ** 3, 'Rendition too large');
      const filename = `${name}-${profile.height}p-${sha256.slice(0,16)}.mp4`;
      const destination = path.join(output, filename);
      const existing = await stat(destination).catch(e => { if (e.code !== 'ENOENT') throw e; });
      if (existing) assert(await fileHash(destination) === sha256, 'Immutable media name collision');
      else { await copyFile(encoded, destination + '.part'); await rename(destination + '.part', destination); }
      variants.push({name:filename, localPath:destination, width:result.video.width, height:profile.height, bytes, sha256, durationSeconds:result.duration});
    }
    return {sourceBytes:original.size, sourceSha256:await fileHash(input), variants};
  } finally { await rm(temporary, {recursive:true, force:true}); }
}
