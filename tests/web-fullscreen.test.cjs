const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
const functionStart = source.indexOf('async function fullscreen()');
const functionEnd = source.indexOf("$('fullscreen').onclick", functionStart);
const listenerStart = source.indexOf("document.addEventListener('fullscreenchange'");
const listenerEnd = source.indexOf('async function poll()', listenerStart);

function harness(fail = false, userAgent = 'Desktop Chrome', historical = false, pending = null) {
  const tiles = new Map(), views = [], viewport = { clientWidth: 1000 };
  const document = new EventTarget();
  const icons = { expand: { hidden: false }, shrink: { hidden: true } };
  for (const icon of Object.values(icons)) icon.toggleAttribute = (name, enabled) => {
    assert.equal(name, 'hidden');
    icon.hidden = enabled;
  };
  const button = { attributes: {}, setAttribute(name, value) { this.attributes[name] = value; },
    querySelector(selector) { return icons[selector.includes('expand') ? 'expand' : 'shrink']; } };
  const notice = {};
  let menuOpen = false;
  document.fullscreenElement = null;
  const update = active => { document.fullscreenElement = active === true ? document.documentElement : active || null; document.dispatchEvent(new Event('fullscreenchange')); };
  document.documentElement = { async requestFullscreen() { if (fail) throw new Error('Fullscreen unavailable'); update(true); } };
  document.exitFullscreen = async () => { if (fail) throw new Error('Fullscreen unavailable'); update(false); };
  const fullscreen = runInNewContext(source.slice(functionStart, functionEnd) + source.slice(listenerStart, listenerEnd) + '\nfullscreen;', {
    document, navigator: { userAgent }, tiles, liveRewind: historical ? { historical: true } : undefined, cameraPictureInPicture: undefined, fullscreenPending: false, window: { devicePixelRatio: 2 }, updatePictureInPicture() {},
    CameraPictureInPictureView: class {
      constructor(select, size) { this.select = select; this.size = size; this.stops = 0; views.push(this); }
      async enterFullscreen() { this.video = {}; this.cameras = this.select(); this.viewport = this.size(); if (fail) throw new Error('Grid capture unavailable'); update(true); if (pending) await pending; }
      stop() { this.stops++; this.video = null; }
    }, $: id => id === 'fullscreen' ? button : id === 'cameras' ? viewport : notice, setMenu: open => { menuOpen = open; }
  });
  return { fullscreen, document, button, icons, notice, update, tiles, views, get menuOpen() { return menuOpen; } };
}

test('entering and exiting fullscreen switch icons, labels, tooltips and pressed state', async () => {
  const f = harness();
  await f.fullscreen();
  assert.equal(f.icons.expand.hidden, true);
  assert.equal(f.icons.shrink.hidden, false);
  assert.equal(f.button.attributes['aria-label'], 'Exit full screen');
  assert.equal(f.button.attributes['aria-pressed'], 'true');
  assert.equal(f.button.title, 'Exit full screen (F11)');
  await f.fullscreen();
  assert.equal(f.icons.expand.hidden, false);
  assert.equal(f.icons.shrink.hidden, true);
  assert.equal(f.button.attributes['aria-label'], 'Full screen');
  assert.equal(f.button.attributes['aria-pressed'], 'false');
  assert.equal(f.button.title, 'Full screen (F11)');
});

test('fullscreen changes outside the menu also update its icons', () => {
  const f = harness();
  f.update(true);
  assert.equal(f.icons.shrink.hidden, false);
  f.update(false);
  assert.equal(f.icons.expand.hidden, false);
});

for (const active of [false, true]) {
  test('failed fullscreen ' + (active ? 'exit' : 'entry') + ' keeps the actual icon state and shows browser guidance', async () => {
    const f = harness(true);
    f.update(active);
    await f.fullscreen();
    assert.equal(f.icons.expand.hidden, active);
    assert.equal(f.icons.shrink.hidden, !active);
    assert.equal(f.menuOpen, true);
    assert.match(f.notice.textContent, /browser.*fullscreen control/);
  });
}


test('Android keeps the viewer fullscreen with every camera in its background PiP source and releases it on exit', async () => {
  const f = harness(false, 'Mozilla/5.0 (Linux; Android 16) Chrome');
  for (let i = 1; i <= 4; i++) f.tiles.set(i, { name: 'Camera ' + i, root: { hidden: false } });
  await f.fullscreen();
  assert.equal(f.views.length, 1); assert.equal(f.views[0].cameras.length, 4);
  assert.equal(f.document.fullscreenElement, f.document.documentElement);
  assert.equal(f.document.fullscreenElement === f.views[0].video, false);
  assert.equal(f.views[0].viewport.width, 1000); assert.equal(f.views[0].viewport.scale, 2);
  await f.fullscreen(); assert.equal(f.views[0].video, null); assert.equal(f.views[0].stops, 1);
  assert.equal(f.document.fullscreenElement, null);
});

test('Android focused fullscreen captures the focused view and reuses the controller without retaining its prior video', async () => {
  const f = harness(false, 'Android');
  for (let i = 1; i <= 4; i++) f.tiles.set(i, { name: 'Camera ' + i, root: { hidden: i !== 2 } });
  await f.fullscreen(); assert.deepEqual(Array.from(f.views[0].cameras, tile => tile.name), ['Camera 2']);
  const first = f.views[0].video; await f.fullscreen(); await f.fullscreen();
  assert.equal(f.views.length, 1); assert.notEqual(f.views[0].video, first); await f.fullscreen();
});

test('desktop camera fullscreen retains the full viewer and creates no captured video', async () => {
  const f = harness(); f.tiles.set(1, { root: { hidden: false } }); await f.fullscreen();
  assert.equal(f.document.fullscreenElement, f.document.documentElement); assert.equal(f.views.length, 0);
});

test('Android with no cameras retains the full viewer without allocating a capture', async () => {
  const f = harness(false, 'Android'); await f.fullscreen();
  assert.equal(f.document.fullscreenElement, f.document.documentElement); assert.equal(f.views.length, 0);
});

test('Android grid entry failures stop partial capture and retain useful fullscreen guidance', async () => {
  const f = harness(true, 'Android'); f.tiles.set(1, { root: { hidden: false } }); await f.fullscreen();
  assert.equal(f.document.fullscreenElement, null); assert.equal(f.views[0].video, null);
  assert.ok(f.views[0].stops > 0); assert.equal(f.menuOpen, true); assert.match(f.notice.textContent, /Grid capture unavailable/);
});


test('Android historical playback retains its existing full viewer path', async () => {
  const f = harness(false, 'Android', true); f.tiles.set(1, { root: { hidden: false } }); await f.fullscreen();
  assert.equal(f.document.fullscreenElement, f.document.documentElement); assert.equal(f.views.length, 0);
});


test('concurrent fullscreen gestures cannot restart capture or dispose an in-flight request', async () => {
  let release; const pending = new Promise(resolve => { release = resolve; });
  const f = harness(false, 'Android', false, pending); f.tiles.set(1, { root: { hidden: false } });
  const entering = f.fullscreen(); await f.fullscreen(); assert.equal(f.views.length, 1); assert.equal(f.views[0].stops, 0);
  release(); await entering; await f.fullscreen(); assert.equal(f.views[0].stops, 1);
});

test('a standard fullscreen-to-PiP transition keeps capture until PiP closes', async () => {
  const f = harness(false, 'Android'); f.tiles.set(1, { root: { hidden: false } }); await f.fullscreen();
  f.document.pictureInPictureElement = f.views[0].video; await f.document.exitFullscreen();
  assert.equal(f.views[0].stops, 0);
  f.document.pictureInPictureElement = null; f.document.dispatchEvent(new Event('leavepictureinpicture'));
  assert.equal(f.views[0].stops, 1);
});
