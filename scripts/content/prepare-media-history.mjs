// Public GETs only. Prepares verified immutable history before deploy-kit builds.
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {prepareMediaHistory} from './media-history.mjs';
const directory=path.dirname(fileURLToPath(import.meta.url));
const snapshot=await prepareMediaHistory({base:process.env.CHILLHUB_BASE_URL||'https://launcher.samoy.love/',
  cache:path.join(directory,'.cache/published-media'),firstPublication:process.argv.includes('--first-publication')});
console.log(JSON.stringify({files:snapshot.manifest.files.length,bytes:snapshot.manifest.files.reduce((n,f)=>n+f.bytes,0),firstPublication:snapshot.firstPublication,serverWrites:0}));
