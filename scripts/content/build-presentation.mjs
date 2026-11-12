import { readFile, writeFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createHash } from 'node:crypto';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, '../..');
const dataDirectory = path.join(root, 'launcher/ChillHub.Native/Data');
const read = async filename => JSON.parse(await readFile(filename, 'utf8'));
const harvest = await read(path.join(directory, '.cache/harvest.json'));
const editorial = await read(path.join(directory, 'games-ru.json'));
const benefitSources = await read(path.join(directory, 'benefit-sources.json'));
const prepared = await read(path.join(directory, '.cache/media-upload-plan.json'));
const oldSeed = await read(path.join(dataDirectory, 'games.json'));
const translations = {};
for (const filename of (await readdir(directory)).filter(name => /^mods-ru-part\d+\.json$/.test(name)).sort()) {
  for (const [id, value] of Object.entries(await read(path.join(directory, filename)))) {
    if (translations[id]) throw new Error(`Duplicate editorial package: ${id}`);
    translations[id] = value;
  }
}
const presentations = {};
const provenance = { harvestedAt: harvest.fetchedAt, registry: 'https://launcher.samoy.love/api/games', games: {} };
const output = [];
for (const game of harvest.registry.items) {
  const id = game.gameId;
  const original = oldSeed.find(g => g.Id === id);
  if (original) delete original.Instructions;
  const text = editorial[id];
  const store = harvest.steam[id];
  if (!original || !text || !store?.value) throw new Error(`Missing game metadata: ${id}`);
  const movies = [...(store.value.movies || [])];
  movies.sort((a, b) => moviePriority(a.name) - moviePriority(b.name));
  const movie = movies.find(m => movieUrl(m));
  if (!movie) throw new Error(`No official playable trailer: ${id}`);
  const screenshots = (store.value.screenshots || []).slice(0, 3);
  if (screenshots.length < 2) throw new Error(`Too few official screenshots: ${id}`);
  const ready = prepared.games[id];
  if (!ready || ready.trailerId !== movie.id) throw new Error(`Missing verified prepared media: ${id}`);
  const media = [
    { kind: 'video', provider:'file', role:'trailer', url: ready.trailer.url, variants:ready.trailer.variants || [], posterUrl: ready.poster.url, caption: 'Официальный трейлер', sourceUrl:ready.source, author:(store.value.developers||[]).join(', '), width:1920,height:1080 },
    ...ready.screenshots.map(shot => ({ kind: 'image', provider:'file',url: shot.url, posterUrl: shot.url, caption: shot.caption,sourceUrl:shot.sourceUrl,width:shot.width,height:shot.height })),
  ];
  const mods = [];
  const source = harvest.sources[id];
  if (game.mods?.hasLatest) {
    if (!source || source.version !== game.mods.version) throw new Error(`Unverified modpack version: ${id}`);
    for (const fullName of source.tree) {
      const sourcePackage = harvest.packages[fullName];
      const localized = translations[fullName];
      if (!sourcePackage || sourcePackage.unavailable || !localized) throw new Error(`Missing verified translation: ${fullName}`);
      for (const field of ['russianName', 'description', 'category']) {
        if (typeof localized[field] !== 'string' || !/[а-яё]/i.test(localized[field])) throw new Error(`Missing Russian ${field}: ${fullName}`);
      }
      mods.push({
        id: `${sourcePackage.namespace}-${sourcePackage.name}`,
        name: sourcePackage.name.replaceAll('_', ' '),
        russianName: localized.russianName,
        description: localized.description,
        category: localized.category,
        version: sourcePackage.version_number || sourcePackage.version,
        url: `https://thunderstore.io/c/${game.mods.community}/p/${encodeURIComponent(sourcePackage.namespace)}/${encodeURIComponent(sourcePackage.name)}/`,
      });
    }
  }
  const gallery = harvest.galleries[id];
  const heroUrl = gallery?.value?.cover ? new URL(encodeURIComponent(gallery.value.cover), gallery.url).href : screenshots[0].path_full;
  presentations[id] = { ...text, modpackVersion: game.mods?.version || '', modpackRevision: game.mods?.revision || '', heroUrl, media, mods };
  output.push({
    ...original, Description: text.description, Genre: text.genre, Highlights: text.highlights,
    GameVersion: game.latestVersion, HasMods: Boolean(game.mods?.hasLatest), LocalSupported: Boolean(game.hasLatest),
    SteamSupported: Boolean(game.mods?.steamAppId), SteamAppId: game.mods?.steamAppId || '',
    ModpackName: game.mods?.displayName || '', ModpackVersion: game.mods?.displayVersion || '', ModpackRevision: game.mods?.revision || '',
    ModpackDescription: text.modpackDescription, ModBenefits: text.modBenefits || [], MetadataNote: '',
    Media: media.map((m, index) => ({
      Kind: m.kind,
      Variants: m.variants || [],
      Path: `Assets/Media/${id}/${index === 0 ? 'trailer-poster.jpg' : `screenshot-${index}.jpg`}`,
      Caption: m.caption, Url: m.url, Provider:m.provider,Role:m.role||'',SourceUrl:m.sourceUrl||'', Author:m.author||'',PublishedAt:m.publishedAt||'',Width:m.width||0,Height:m.height||0,
      Source: m.sourceUrl || `https://store.steampowered.com/app/${store.appId}/`,
    })),
    Mods: mods.map(m => Object.fromEntries(Object.entries(m).map(([key,value]) => [key[0].toUpperCase() + key.slice(1), value]))),
  });
  provenance.games[id] = {
    gameVersion: game.latestVersion, modpackVersion: game.mods?.version || '', modpackRevision: game.mods?.revision || '',
    steamAppId: store.appId, steamSource: `https://store.steampowered.com/api/appdetails?appids=${store.appId}&l=russian&cc=us`,
    trailerName: movie.name, trailerId: movie.id, trailerFormat: 'mp4-h264-aac-1080p', trailer:publicAsset(ready.trailer),
    screenshots:ready.screenshots.map(publicAsset),
    modBenefits:(text.modBenefits || []).map((benefit,index)=>{
      const packages=benefitSources[id]?.[index];
      if(!packages?.length || packages.some(p=>!source.tree.includes(p.id))) throw new Error(`Unverified mod benefit: ${id} ${index}`);
      return {title:benefit.title,packages};
    }),
    packageSource: source?.url || '', sourceTreeDigest: source?.treeDigest || '',
    packages: (source?.tree || []).map(fullName => ({ id: fullName, source: translations[fullName]?.source || harvest.packages[fullName].source, descriptionSha256: createHash('sha256').update(harvest.packages[fullName].description || '').digest('hex') })),
  };
}
await writeFile(path.join(dataDirectory, 'games.json'), JSON.stringify(output, null, 2) + '\n');
await writeFile(path.join(dataDirectory, 'presentation-seed.json'), JSON.stringify(presentations, null, 2) + '\n');
await writeFile(path.join(directory, 'metadata-provenance.json'), JSON.stringify(provenance, null, 2) + '\n');
console.log(JSON.stringify({ games: output.length, mods: output.reduce((n,g) => n + g.Mods.length, 0), counts: Object.fromEntries(output.map(g => [g.Id, g.Mods.length])) }, null, 2));

function moviePriority(name = '') {
  if (/launch trailer|release trailer|available now|official trailer/i.test(name)) return 0;
  if (/gameplay/i.test(name)) return 1;
  if (/update|reveal|release date/i.test(name)) return 3;
  return 2;
}
function movieUrl(movie) { return movie.mp4?.max || movie.webm?.max || movie.mp4?.['480'] || movie.webm?.['480'] || movie.hls_h264 || movie.dash_h264 || ''; }
function publicAsset(asset) { const {localPath, ...value}=asset; return value; }
