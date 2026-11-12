const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-pip.js'), 'utf8');
const CameraZoom = runInNewContext(readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-zoom.js'), 'utf8') + '\nCameraZoom;');

function harness(options = {}) {
  const callbacks = new Map(), draws = [], text = [], failures = [], videos = [], tracks = [], order = [];
  let next = 0, tiles = [], failDraw = false, viewportWidth = options.width || 640;
  const frameModes = [];
  const context = { fillRect() {}, save() {}, restore() {}, beginPath() {}, rect() {}, clip() {},
    fillText: value => text.push(value), drawImage: (...args) => { if (failDraw) throw new Error('Canvas draw failed'); draws.push(args); } };
  const document = Object.assign(new EventTarget(), { hidden: false, documentElement: { requestFullscreen() { order.push('fullscreen'); return options.fullscreen ? options.fullscreen() : Promise.resolve(); } }, body: { prepend(video) { video.attached = true; } }, createElement(tag) {
    if (tag === 'canvas') return { getContext: () => options.noContext ? null : context,
      captureStream: options.noCapture ? undefined : rate => {
        assert.equal(rate, 0, 'Frames follow the video compositor rather than a hidden-page timer.');
        const track = { requested: 0, stopped: false, stop() { this.stopped = true; },
          requestFrame: options.noRequestFrame ? undefined : function () { this.requested++; } };
        tracks.push(track); return { getVideoTracks: () => [track], getTracks: () => [track] };
      } };
    const video = { setAttribute() {}, paused: true, pause() { this.paused = true; }, remove() { this.removed = true; },
      play() { order.push('play'); this.paused = false; return options.play ? options.play() : Promise.resolve(); },
      requestFullscreen() { order.push('fullscreen'); return options.fullscreen ? options.fullscreen() : Promise.resolve(); },
      requestVideoFrameCallback: options.noFrameCallback ? undefined : callback => { frameModes.push('video'); callbacks.set(++next, callback); return next; },
      cancelVideoFrameCallback: id => callbacks.delete(id) };
    videos.push(video); return video;
  } });
  const window = new EventTarget();
  const animation = callback => { frameModes.push('animation'); callbacks.set(++next, callback); return next; };
  const View = runInNewContext(source + '\nCameraPictureInPictureView;', { document, window, AbortController, requestAnimationFrame: animation, cancelAnimationFrame: id => callbacks.delete(id) });
  const view = new View(() => tiles, () => ({ width: viewportWidth, scale: options.scale || 1 }), error => failures.push(error));
  const add = (tag = 'VIDEO', classes = ['grid-media-ready'], width = 320, height = 180) => {
    const values = new Set(classes), media = { tagName: tag, width, height, videoWidth: tag === 'VIDEO' ? width : 0,
      videoHeight: tag === 'VIDEO' ? height : 0, readyState: 2, complete: true };
    const surface = { querySelector: selector => selector === '.grid-camera-video' && tag === 'VIDEO' ||
      selector === '.grid-camera-canvas' && tag === 'CANVAS' || selector === '.live-camera-video' && values.has('live-media-ready') ? media : null };
    const tile = { name: 'Camera ' + (tiles.length + 1), img: media, message: { textContent: 'Waiting for activity' },
      root: { classList: { contains: name => values.has(name) }, querySelector: () => surface } };
    tiles.push(tile); return { tile, media, classes: values };
  };
  const paint = () => { const id = view.frame, callback = callbacks.get(id); callbacks.delete(id); callback(); };
  return { view, callbacks, draws, text, failures, videos, tracks, order, frameModes, document, add, paint,
    hide(hidden = true) { document.hidden = hidden; document.dispatchEvent(new Event('visibilitychange')); },
    persistent(active = true) { viewportWidth = active ? 0 : 640; window.dispatchEvent(new Event('resize')); },
    failDraw() { failDraw = true; }, clearTiles() { tiles = []; } };
}

test('four cameras form a single 2x2 native video with source pixels and labels in each slot', async () => {
  const h = harness(); for (let i = 0; i < 4; i++) h.add();
  const video = h.view.start(); await h.view.playing;
  assert.deepEqual([h.view.canvas.width, h.view.canvas.height], [640, 360]);
  assert.equal(video.srcObject, h.view.stream); assert.equal(video.muted, true); assert.equal(video.playsInline, true);
  assert.deepEqual(h.draws.map(draw => draw.slice(1)), [[0, 0, 320, 180], [320, 0, 320, 180], [0, 180, 320, 180], [320, 180, 320, 180]]);
  assert.deepEqual(h.text, ['Camera 1', 'Camera 2', 'Camera 3', 'Camera 4']);
  assert.equal(h.callbacks.size, 1); h.paint(); assert.equal(h.draws.length, 8); assert.equal(h.callbacks.size, 1);
  assert.equal(h.tracks[0].requested, 2); h.view.stop();
});

test('single cameras and collections above sixteen use the available viewport pixels without camera caps', () => {
  for (const count of [1, 5, 25]) {
    const h = harness({ scale: 2 }); for (let i = 0; i < count; i++) h.add(); h.view.start();
    assert.equal(h.draws.length, count); assert.equal(h.view.canvas.width, 1280);
    assert.equal(h.view.canvas.height, Math.round(1280 * Math.ceil(count / Math.ceil(Math.sqrt(count))) / (Math.ceil(Math.sqrt(count)) * 16 / 9)));
    h.view.stop();
  }
});

test('mixed native, software canvas and JPEG playback fit their original aspect ratios into the grid', () => {
  const h = harness(); const native = h.add('VIDEO'), software = h.add('CANVAS', ['grid-media-ready'], 320, 240), jpeg = h.add('IMG', ['has-frame']);
  h.view.start(); assert.deepEqual(h.draws.map(draw => draw[0]), [native.media, software.media, jpeg.media]);
  assert.deepEqual(h.draws[1].slice(1), [360, 0, 240, 180]); h.view.stop();
});

test('live sound video owns the slot and enhancements do not replace its original source', () => {
  const h = harness(), camera = h.add('VIDEO', ['grid-media-ready', 'live-media-ready', 'enhancement-ready']);
  h.view.start(); assert.equal(h.draws[0][0], camera.media); h.view.stop();
});

test('PiP uses the selected display ratio for canvas sizing and every camera rectangle', () => {
  const h = harness(), a = h.add('VIDEO', ['grid-media-ready'], 640, 480), b = h.add('CANVAS', ['grid-media-ready'], 640, 480);
  for (const [entry, ratio] of [[a, '16:9'], [b, '4:3']]) {
    entry.tile.zoom = { bounds: (media, viewport) => CameraZoom.picture(viewport.width, viewport.height, media.width, media.height, ratio) };
  }
  h.view.start(); assert.equal(h.view.canvas.width / h.view.canvas.height, 640 / 180);
  const first = h.draws[0], second = h.draws[1];
  assert.equal(first[3] / first[4], 16 / 9); assert.equal(second[3] / second[4], 4 / 3);
  assert.equal(first[1], 0); assert.equal(second[1], 360); h.view.stop();
});

test('waiting, loading and expired cameras retain their named slots and recover on fresh footage', () => {
  const h = harness(), first = h.add(), second = h.add(), third = h.add('IMG', ['has-frame']);
  first.classes.clear(); second.media.readyState = 1; third.media.complete = false;
  h.view.start(); assert.equal(h.draws.length, 0); assert.equal(h.text.filter(text => text === 'Waiting for activity').length, 3);
  first.classes.add('grid-media-ready'); second.media.readyState = 2; third.media.complete = true; h.paint();
  assert.equal(h.draws.length, 3); h.view.stop();
});

test('media without decoded dimensions does not supply black or invalid source pixels', () => {
  const h = harness(); h.add('CANVAS', ['grid-media-ready'], 0, 0); h.view.start();
  assert.equal(h.draws.length, 0); assert.ok(h.text.includes('Waiting for activity')); h.view.stop();
});

test('fullscreen is requested in the gesture before the media play promise resolves', async () => {
  let release;
  const h = harness({ play: () => new Promise(resolve => { release = resolve; }) }); h.add();
  const entering = h.view.enterFullscreen(); assert.deepEqual(h.order, ['play', 'fullscreen']);
  release(); await entering; h.view.stop();
});

for (const stage of ['play', 'fullscreen']) {
  test(stage + ' rejection releases capture tracks, video and pending compositor callbacks', async () => {
    const h = harness({ [stage]: () => Promise.reject(new Error(stage + ' denied')) }); h.add();
    await assert.rejects(h.view.enterFullscreen(), new RegExp(stage + ' denied'));
    assert.equal(h.view.video, null); assert.equal(h.tracks[0].stopped, true); assert.equal(h.callbacks.size, 0);
    assert.equal(h.videos[0].srcObject, null); assert.equal(h.videos[0].removed, true);
  });
}

for (const feature of ['noContext', 'noCapture', 'noFrameCallback', 'noRequestFrame']) {
  test('unsupported ' + feature + ' reports the actual browser requirement and releases partial setup', () => {
    const h = harness({ [feature]: true }); h.add(); assert.throws(() => h.view.start(), /browser cannot/);
    assert.equal(h.view.video, null); assert.equal(h.callbacks.size, 0); assert.ok(h.tracks.every(track => track.stopped));
  });
}

test('empty camera views fail without allocating media', () => {
  const h = harness(); assert.throws(() => h.view.start(), /no cameras/); assert.equal(h.videos.length, 0);
});

test('draw and native video failures stop capture and report one actionable failure', () => {
  for (const event of ['draw', 'video', 'play']) {
    const h = harness(); h.add(); h.view.start();
    if (event === 'video') h.view.video.onerror();
    else { h.failDraw(); event === 'play' ? h.view.video.onplay() : h.paint(); }
    assert.equal(h.failures.length, 1); assert.equal(h.view.video, null); assert.equal(h.tracks[0].stopped, true);
    assert.equal(h.callbacks.size, 0);
  }
});

test('initial draw failure disposes the newly captured stream before propagating the error', () => {
  const h = harness(); h.add(); h.failDraw(); assert.throws(() => h.view.start(), /draw failed/);
  assert.equal(h.tracks[0].stopped, true); assert.equal(h.videos[0].removed, true); assert.equal(h.callbacks.size, 0);
});

test('repeated exit, restart and late media callbacks cannot revive or stop another capture', () => {
  const h = harness(); h.add(); h.view.start();
  const oldPaint = h.callbacks.get(h.view.frame), oldError = h.view.video.onerror, oldPlay = h.view.video.onplay;
  h.view.stop(); h.view.stop(); h.view.start(); const current = h.view.video;
  oldPaint(); oldError(); oldPlay(); assert.equal(h.view.video, current); assert.equal(h.failures.length, 0); assert.equal(h.callbacks.size, 1);
  h.clearTiles(); h.paint(); assert.equal(h.callbacks.size, 1); h.view.stop();
  assert.ok(h.tracks.every(track => track.stopped));
});


test('foreground capture runs behind the viewer and hidden/PiP capture switches to compositor callbacks', () => {
  const h = harness(); h.add(); const video = h.view.start();
  assert.equal(h.frameModes.at(-1), 'animation'); assert.equal(h.view.isPresented(), false);
  assert.equal(video.attached, true); assert.equal(video.controls, undefined);
  h.hide(); assert.equal(h.frameModes.at(-1), 'video'); assert.equal(h.callbacks.size, 1);
  h.paint(); assert.equal(h.frameModes.at(-1), 'video');
  h.hide(false); assert.equal(h.frameModes.at(-1), 'animation');
  h.persistent(); assert.equal(h.view.isPresented(), true); assert.equal(h.frameModes.at(-1), 'video');
  h.persistent(false); assert.equal(h.view.isPresented(), false); assert.equal(h.frameModes.at(-1), 'animation');
  h.document.pictureInPictureElement = video; h.view.refresh(); assert.equal(h.frameModes.at(-1), 'video');
  h.view.stop(); h.hide(); h.persistent(); assert.equal(h.callbacks.size, 0);
});

test('mode-change drawing failures release callbacks and subscriptions without reviving on another resize', () => {
  const h = harness(); h.add(); h.view.start(); h.failDraw(); h.hide();
  assert.equal(h.failures.length, 1); assert.equal(h.callbacks.size, 0); h.persistent();
  assert.equal(h.failures.length, 1); assert.equal(h.view.video, null);
});
