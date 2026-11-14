import assert from 'node:assert/strict';

export function gameplayPresentationEntry(gameplay) {
  if (!gameplay) return null;
  return {
    kind:'video',provider:'file',role:'gameplay',url:gameplay.url,sourceUrl:'',
    variants:gameplay.variants||[],posterUrl:gameplay.posterUrl,
    caption:gameplay.caption,author:gameplay.author,width:gameplay.width,height:gameplay.height,
  };
}

export function validateGameplayPresentation(presentation, provenance) {
  const gameplay = presentation.media.find(media=>media.role==='gameplay');
  assert.equal(Boolean(gameplay),Boolean(provenance.gameplay));
  if (!gameplay) return;
  assert.equal(gameplay.provider,'file');
  assert(gameplay.url.startsWith('https://launcher.samoy.love/assets/'));
  assert(gameplay.posterUrl.startsWith('https://launcher.samoy.love/assets/'));
  assert.equal(gameplay.sourceUrl,'');
  assert.equal(gameplay.author,provenance.gameplay.author);
  assert.equal(gameplay.caption,provenance.gameplay.caption);
  assert(gameplay.variants.length>0);
}

export function bundledPosterPath(gameId, media, index, hasGameplay) {
  const name = media.role === 'gameplay' ? 'gameplay-poster.jpg'
    : media.role === 'trailer' ? 'trailer-poster.jpg'
      : `screenshot-${index - (hasGameplay ? 1 : 0)}.jpg`;
  return `Assets/Media/${gameId}/${name}`;
}
