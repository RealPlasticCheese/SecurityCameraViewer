const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function updatePictureInPicture() {');
const end = source.indexOf('function cameraAudio(', start);

function harness() {
  const tiles = new Map(), liveAudio = { players: new Map() }, liveRewind = { historical: false, players: new Map() };
  const context = { tiles, liveAudio, liveRewind, cameraPictureInPicture: undefined };
  const update = runInNewContext(source.slice(start, end) + '\nupdatePictureInPicture;', context);
  function add(channel, classes = ['grid-media-ready']) {
    const values = new Set(classes);
    const tile = { root: { hidden: false, classList: { contains: name => values.has(name) } },
      playback: { player: { video: { readyState: 2 } } } };
    tiles.set(channel, tile);
    return { tile, classes: values, grid: tile.playback.player.video };
  }
  return { tiles, liveAudio, liveRewind, update, add, withoutRewind() { context.liveRewind = undefined; }, composite() { context.cameraPictureInPicture = { video: {} }; } };
}

test('only the displayed native video is eligible for automatic PiP', () => {
  const h = harness(), first = h.add(1), second = h.add(2);
  h.update();
  assert.equal(first.grid.disablePictureInPicture, false);
  assert.equal(second.grid.disablePictureInPicture, false);
  second.tile.root.hidden = true; h.update();
  assert.equal(first.grid.disablePictureInPicture, false);
  assert.equal(second.grid.disablePictureInPicture, true);
  second.tile.root.hidden = false; h.update();
  assert.equal(second.grid.disablePictureInPicture, false);
});

test('audio video takes PiP eligibility from the covered grid player and returns it when listening stops', () => {
  const h = harness(), { grid, classes } = h.add(1);
  const audio = { readyState: 2 }; h.liveAudio.players.set(1, { video: audio });
  h.update(); assert.equal(grid.disablePictureInPicture, false); assert.equal(audio.disablePictureInPicture, true);
  classes.add('live-media-ready'); h.update();
  assert.equal(grid.disablePictureInPicture, true); assert.equal(audio.disablePictureInPicture, false);
  classes.delete('live-media-ready'); h.update();
  assert.equal(grid.disablePictureInPicture, false); assert.equal(audio.disablePictureInPicture, true);
});

test('loading, expired and invisible audio players cannot supply a blank PiP window', () => {
  const h = harness(), { tile, grid, classes } = h.add(1);
  grid.readyState = 1; h.update(); assert.equal(grid.disablePictureInPicture, true);
  grid.readyState = 2; classes.clear(); h.update(); assert.equal(grid.disablePictureInPicture, true);
  const audio = { readyState: 1 }; h.liveAudio.players.set(1, { video: audio });
  classes.add('live-media-ready'); h.update(); assert.equal(audio.disablePictureInPicture, true);
  audio.readyState = 2; tile.root.hidden = true; h.update(); assert.equal(audio.disablePictureInPicture, true);
  tile.root.hidden = false; h.update(); assert.equal(audio.disablePictureInPicture, false);
});

test('rewind disables the live players and offers only its displayed video', () => {
  const h = harness(), { grid, classes } = h.add(1);
  const audio = { readyState: 2 }, rewind = { readyState: 2 };
  h.liveAudio.players.set(1, { video: audio }); h.liveRewind.players.set(1, { video: rewind });
  h.liveRewind.historical = true; classes.add('live-media-ready'); h.update();
  assert.equal(grid.disablePictureInPicture, true); assert.equal(audio.disablePictureInPicture, true);
  assert.equal(rewind.disablePictureInPicture, false);
  classes.delete('live-media-ready'); h.update(); assert.equal(rewind.disablePictureInPicture, true);
});

test('JPEG, software, empty and unsupported-browser views need no PiP API', () => {
  const h = harness(); h.update();
  h.tiles.set(1, { root: { classList: { contains: () => true } } });
  h.tiles.set(2, { root: { classList: { contains: () => false } }, playback: { player: { canvas: {} } } });
  h.update();
  const { grid } = h.add(3); h.withoutRewind(); h.update();
  assert.equal(grid.disablePictureInPicture, false);
});


test('the composed fullscreen grid owns PiP eligibility instead of its input videos', () => {
  const h = harness(), { grid, classes } = h.add(1);
  h.add(2);
  const audio = { readyState: 2 }; h.liveAudio.players.set(1, { video: audio }); classes.add('live-media-ready');
  h.composite(); h.update(); assert.equal(grid.disablePictureInPicture, true); assert.equal(audio.disablePictureInPicture, true);
});

test('focusing a native camera keeps its displayed source eligible alongside the captured view', () => {
  const h = harness(), first = h.add(1), second = h.add(2);
  h.composite(); h.update(); assert.equal(first.grid.disablePictureInPicture, true);
  second.tile.root.hidden = true; h.update();
  assert.equal(first.grid.disablePictureInPicture, false); assert.equal(second.grid.disablePictureInPicture, true);
  const audio = { readyState: 2 }; h.liveAudio.players.set(1, { video: audio }); first.classes.add('live-media-ready');
  h.update(); assert.equal(first.grid.disablePictureInPicture, true); assert.equal(audio.disablePictureInPicture, false);
  audio.readyState = 1; h.update(); assert.equal(audio.disablePictureInPicture, true);
  first.classes.delete('live-media-ready'); h.update(); assert.equal(first.grid.disablePictureInPicture, false);
  second.tile.root.hidden = false; h.update(); assert.equal(first.grid.disablePictureInPicture, true);
});

test('a one-camera configuration and a focused JPEG/software view retain their correct PiP candidates', () => {
  const h = harness(), first = h.add(1); h.composite(); h.update();
  assert.equal(first.grid.disablePictureInPicture, false);
  first.classes.clear(); h.update(); assert.equal(first.grid.disablePictureInPicture, true);
  first.tile.playback.player = { canvas: {} }; h.update();
  first.tile.root.hidden = true; h.update();
});
