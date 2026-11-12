const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-collection.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');

function viewer(timeout) {
  if (arguments.length === 0) timeout = 300;
  const ids = new Map(), revoked = [], intervals = [], blobs = [];
  let now = 1000, nextUrl = 0, decodeDelay = 0;
  function element() {
    const classes = new Set(), children = new Map();
    return { hidden: false, textContent: '', elements: [],
      classList: { add: value => classes.add(value), remove: value => classes.delete(value),
        contains: value => classes.has(value), toggle: (value, on) => on ? classes.add(value) : classes.delete(value) },
      setAttribute() {}, removeAttribute(name) { delete this[name]; }, addEventListener() {},
      querySelector(selector) { if (!children.has(selector)) children.set(selector, element()); return children.get(selector); } };
  }
  const document = { getElementById(id) { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); },
    addEventListener() {}, querySelectorAll: () => [] };
  class Socket {
    static OPEN = 1;
    constructor() { this.readyState = 1; }
    send() {}
  }
  const context = { document, window: { addEventListener() {} },
    LiveCameraAudioController: class { players = new Map(); audioAvailability = new Map(); active = false; available() { return true; } stop() {} setTalkbackActive() {} },
    PushToTalk: class { channel = null; stop() {} static available() { return false; } },
    CameraLayoutView: class {}, localStorage: {}, location: { protocol: 'http:', host: 'localhost' },
    DetectionOverlayController: class { refresh() {} },
    fetch: () => new Promise(() => {}), AbortSignal, WebSocket: Socket,
    performance: { now: () => now }, setTimeout() {}, setInterval: callback => intervals.push(callback),
    URL: { createObjectURL(blob) { blobs.push(blob); return `blob:${++nextUrl}`; }, revokeObjectURL: url => revoked.push(url) },
    Blob, Image: class { async decode() { now += decodeDelay; } } };
  const runtime = runInNewContext(`${source}\n({
    initialize(timeout) {
      state = { settings: { videoTimeoutSeconds: timeout }, cameras: [], connected: true };
      generation = 1; serverOnline = true; socketOnline = true;
    },
    add(channel, tile, status) { tiles.set(channel, tile); state.cameras.push({ channel, status, live: status === 'live' }); },
    renderStatus, receive: data => socket.onmessage({ data }),
    offline() { serverOnline = false; socketOnline = false; },
    socketOffline() { socketOnline = false; }
  });`, context);
  runtime.initialize(timeout);
  const add = (channel = 1, status = 'waiting') => {
    const tile = { root: element(), img: element(), badge: element(), check: element(),
      message: element(), detail: element(), stale: element(), url: null, lastFrame: 0,
      name: `Camera ${channel}`, sound: element(), listen: element(), audioLabel: element(),
      soundStatus: element(), talk: element(), talkStatus: element(), detections: { setAvailable() {} },
      playback: { method: 'server', suspended: false, suspend(value) { this.suspended = value; }, frame() {}, updateMethodRetry() {},
        expire() { tile.root.classList.remove('grid-media-ready'); } } };
    runtime.add(channel, tile, status); return tile;
  };
  const frame = async (channel = 1, age = 0, frameGeneration = 1) => {
    const data = new ArrayBuffer(20), header = new DataView(data);
    header.setInt32(0, channel, true); header.setInt32(4, frameGeneration, true); header.setFloat64(8, age, true);
    new Uint8Array(data).set([0xff, 0xd8, 0xff, 0xd9], 16);
    runtime.receive(data); await new Promise(resolve => setImmediate(resolve));
  };
  return { ...runtime, add, frame, revoked, blobs, intervals,
    advance(milliseconds) { now += milliseconds; }, delayDecode(milliseconds) { decodeDelay = milliseconds; } };
}

test('300 seconds without fresh video clears the image and restores the waiting screen', async () => {
  const v = viewer(), tile = v.add(); await v.frame();
  v.advance(299999); v.renderStatus();
  assert.ok(tile.root.classList.contains('has-frame'));
  v.advance(1); v.renderStatus();
  assert.equal(tile.url, null); assert.equal(tile.img.src, undefined);
  assert.equal(tile.root.classList.contains('has-frame'), false);
  assert.equal(tile.message.textContent, 'Waiting for activity');
  assert.equal(tile.badge.textContent, 'WAITING'); assert.equal(tile.stale.hidden, true);
  assert.deepEqual(v.revoked, ['blob:1']);
});

test('zero and absent timeout preserve the last picture indefinitely', async () => {
  for (const timeout of [0, undefined]) {
    const v = viewer(timeout), tile = v.add(); await v.frame();
    v.advance(90000000); v.renderStatus();
    assert.ok(tile.url); assert.ok(tile.root.classList.contains('has-frame'));
    assert.equal(v.revoked.length, 0);
  }
});

test('fresh video restores an expired tile and resets its countdown independently of other cameras', async () => {
  const v = viewer(), first = v.add(), second = v.add(2, 'connecting');
  await v.frame(); await v.frame(2);
  v.advance(300000); v.renderStatus();
  assert.equal(first.url, null); assert.equal(second.url, null);
  assert.equal(second.message.textContent, 'Waiting for camera');
  await v.frame(); assert.ok(first.root.classList.contains('has-frame'));
  v.advance(299999); v.renderStatus(); assert.ok(first.url); assert.equal(second.url, null);
  v.advance(1); v.renderStatus(); assert.equal(first.url, null);
});

test('opening or reconnecting a browser preserves the original age of a cached picture', async () => {
  const v = viewer(), tile = v.add(); await v.frame(1, 299000);
  assert.ok(tile.url); v.advance(1000); v.renderStatus(); assert.equal(tile.url, null);
  await v.frame(1, 300000); assert.equal(tile.url, null);
  assert.deepEqual(v.revoked, ['blob:1', 'blob:2']);
  assert.deepEqual([...new Uint8Array(await v.blobs[0].arrayBuffer())], [0xff, 0xd8, 0xff, 0xd9]);
});

test('time spent decoding counts toward expiry and outdated configurations are ignored', async () => {
  const v = viewer(1), tile = v.add(); v.delayDecode(1000); await v.frame();
  assert.equal(tile.url, null); assert.deepEqual(v.revoked, ['blob:1']);
  await v.frame(1, 0, 2); await v.frame(1, NaN); await v.frame(1, -1);
  assert.equal(tile.url, null); assert.equal(v.blobs.length, 1);
});

test('expiry continues while server polling is blocked or the video socket is disconnected', async () => {
  for (const offline of ['offline', 'socketOffline']) {
    const v = viewer(1), tile = v.add(); await v.frame(); v[offline](); v.advance(1000);
    v.intervals.forEach(callback => callback());
    assert.equal(tile.url, null); assert.equal(tile.root.classList.contains('has-frame'), false);
    if (offline === 'offline') assert.equal(tile.message.textContent, 'Server unavailable');
  }
});

for (const method of ['native', 'browser']) {
  test(`${method} pictures expire at the configured timeout and show a stale overlay while retained`, () => {
    const v = viewer(5), tile = v.add(257, 'live'); tile.playback.method = method;
    tile.root.classList.add('grid-media-ready'); tile.lastFrame = 1000;
    v.advance(4500); v.renderStatus();
    assert.equal(tile.stale.hidden, false); assert.equal(tile.badge.textContent, '\u25cf STALE');
    v.advance(500); v.renderStatus();
    assert.equal(tile.root.classList.contains('grid-media-ready'), false); assert.equal(tile.stale.hidden, true);
  });
  test(`${method} pictures remain when the timeout is zero`, () => {
    const v = viewer(0), tile = v.add(1, 'live'); tile.playback.method = method;
    tile.root.classList.add('grid-media-ready'); tile.lastFrame = 1000;
    v.advance(90000000); v.renderStatus(); assert.equal(tile.root.classList.contains('grid-media-ready'), true);
  });
}
test('modern JPEG transport preserves channel numbers above 255 and skips cameras using encoded video', async () => {
  const v = viewer(0), tile = v.add(257, 'live'); await v.frame(257); assert.ok(tile.url);
  tile.playback.method = 'browser'; await v.frame(257); assert.equal(v.blobs.length, 1);
});
