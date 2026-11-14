import assert from 'node:assert/strict';
import {copyFile, link, stat} from 'node:fs/promises';
import path from 'node:path';
import {fileHash} from './media-history.mjs';

const LINK_FALLBACK_CODES = new Set(['EACCES', 'EMLINK', 'ENOTSUP', 'EOPNOTSUPP', 'EPERM', 'EXDEV']);

export function canHardlinkMedia(source, entry) {
  if (!entry.path.endsWith('.mp4')) return false;
  const basename = path.basename(source);
  if (/^[a-f0-9]{64}$/i.test(basename)) return basename.toLowerCase() === entry.sha256;
  const match = basename.match(/-([a-f0-9]{16})\.mp4$/i);
  return Boolean(match && entry.sha256.startsWith(match[1].toLowerCase()));
}

export async function stageMediaFile({
  source, destination, entry, hardlinkEnabled = false,
  linkFile = link, copy = copyFile,
}) {
  let method = 'copied';
  if (hardlinkEnabled && canHardlinkMedia(source, entry)) {
    try {
      await linkFile(source, destination);
      method = 'linked';
    } catch (error) {
      if (!LINK_FALLBACK_CODES.has(error.code)) throw error;
      await copy(source, destination);
    }
  } else {
    await copy(source, destination);
  }

  assert.equal((await stat(destination)).size, entry.bytes, 'Staged media size changed');
  assert.equal(await fileHash(destination), entry.sha256, 'Staged media checksum changed');
  return {method, bytes: entry.bytes};
}
