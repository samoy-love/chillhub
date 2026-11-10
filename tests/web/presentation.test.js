const test = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');
const P = require('../../server/admin_ui/presentation.js');
const V = require('../../server/admin_ui/views.js');
const R = require('../../server/admin_ui/registry.js');
const D = require('../../server/admin_ui/registry-diff.js');

const sample = () => ({ description: 'Вместе на вершину', modpackDescription: 'Позволяет играть большой компанией', modpackVersion: 'Lart_Iste-PeakFriendsEdition-2.0.15', modpackRevision: '0123456789abcdef0123456789abcdef', genre: 'Кооператив', heroUrl: '/assets/hero.jpg', highlights: ['Четыре игрока'], media: [{ kind: 'video', url: 'https://cdn.example.com/trailer.mp4', posterUrl: '/assets/poster.jpg', caption: 'Трейлер' }], mods: [{ id: 'more', name: 'More Players', russianName: 'Больше игроков', description: 'Снимает лимит группы', category: 'Кооператив', version: '1.0', url: 'https://thunderstore.io/c/peak/' }] });

test('editor round-trips every presentation field without executing markup', () => {
  const data = sample(); data.description = '<img src=x onerror=alert(1)>';
  const doc = new JSDOM(V.presentationForm(data)).window.document;
  assert.equal(doc.querySelector('img'), null);
  assert.deepEqual(P.read(doc.querySelector('[data-presentation]')), P.normalize(data));
  const labels = [...doc.querySelectorAll('label[for]')];
  assert.ok(labels.every((label) => doc.getElementById(label.htmlFor)), 'all inputs need accessible labels');
});

test('media and mods can be added, reordered and removed without mutating saved data', () => {
  const original = sample();
  let changed = P.change(original, 'media', 'add');
  changed.media[1].url = '/assets/second.jpg';
  changed = P.change(changed, 'media', 'up', 1);
  assert.equal(changed.media[0].url, '/assets/second.jpg');
  changed = P.change(changed, 'media', 'remove', 0);
  assert.deepEqual(changed.media, P.normalize(original).media);
  assert.equal(original.media.length, 1);
  assert.equal(P.change(original, 'mods', 'add').mods.length, 2);
});

test('external videos and mod benefits survive edit, reorder and validation', () => {
  const p = sample();
  p.media.push({kind:'video',url:'https://www.youtube.com/watch?v=GWuF23poTf4',provider:'youtube',role:'gameplay',sourceUrl:'https://www.youtube.com/watch?v=GWuF23poTf4',author:'Marmok',publishedAt:'2025-03-07',width:0,height:0,posterUrl:'/assets/repo.jpg',caption:'Как играют друзья'});
  p.modBenefits = [{title:'Большая команда',description:'Отправляйтесь за добычей вместе с друзьями',icon:'users'}];
  const doc = new JSDOM(V.presentationForm(p)).window.document;
  assert.deepEqual(P.read(doc.querySelector('[data-presentation]')), P.normalize(p));
  assert.deepEqual(P.problems(p), []);
  assert.equal(P.change(p,'modBenefits','add').modBenefits.length,2);
  for(const url of ['https://evil.example/watch?v=GWuF23poTf4','https://www.youtube.com/redirect?q=https://evil.example','https://youtu.be/not-valid','https://rutube.ru/video/abcdef']){
    p.media[1].url=url;assert.ok(P.problems(p).some(e=>e.field==='presentation.media.1.url'),url);
  }
  p.modBenefits[0].icon='script';assert.ok(P.problems(p).some(e=>e.field==='presentation.modBenefits.0.icon'));
});

test('validation rejects unsafe links, duplicate mod IDs and oversized fields', () => {
  assert.deepEqual(P.problems(sample()), []);
  for (const url of ['javascript:alert(1)', '//example.com/a', '/assets/%2e%2e/secret', 'https://localhost/a', 'http://example.com/a', 'https://user:pass@example.com/a', 'file:///C:/a']) {
    const p = sample(); p.media[0].url = url; assert.ok(P.problems(p).length, url);
  }
  const p = sample(); p.mods.push({ ...p.mods[0], id: 'MORE' }); p.description = 'я'.repeat(12001); p.modpackDescription = 'я'.repeat(2001);
  assert.ok(P.problems(p).some((e) => e.field === 'presentation.description'));
  assert.ok(P.problems(p).some((e) => e.field === 'presentation.modpackDescription'));
  assert.ok(P.problems(p).some((e) => e.field === 'presentation.mods.1.id'));
});

test('empty editor explicitly clears presentation and missing presentation remains optional', () => {
  const doc = new JSDOM(V.presentationForm(null)).window.document;
  assert.equal(P.read(doc.querySelector('[data-presentation]')), null);
  assert.deepEqual(P.problems(undefined), []);
});

test('mod list binding validates revision without requiring editorial data for installs', () => {
  const p = sample(); p.modpackRevision = 'a'.repeat(32);
  assert.deepEqual(P.problems(p), []);
  p.modpackRevision = 'not-a-revision'; p.modpackVersion = 'x'.repeat(201);
  assert.ok(P.problems(p).some((e) => e.field === 'presentation.modpackRevision'));
  assert.ok(P.problems(p).some((e) => e.field === 'presentation.modpackVersion'));
});

test('mods-only registry entry can be edited without a local executable', () => {
  const row = { gameId: 'peak', title: 'PEAK', mods: { enabled: true, steamAppId: '3527290', exeNames: ['PEAK.exe'] } };
  assert.deepEqual(R.problems([row]), []);
  assert.ok(R.problems([{ ...row, mods: { ...row.mods, steamAppId: '' } }]).some((e) => e.field === 'exeRelativePath'));
});

test('presentation changes appear in the unsaved registry count', () => {
  const old = { gameId: 'peak', presentation: sample() };
  const next = { ...old, presentation: { ...old.presentation, description: 'Новое' } };
  assert.equal(D.countRegistryChanges([old], [next]).changed, 1);
  assert.equal(D.countRegistryChanges([old], [{ ...old, presentation: JSON.parse(JSON.stringify(old.presentation)) }]).total, 0);
});
