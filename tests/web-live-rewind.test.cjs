const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/playback-controls.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/live-rewind.js'), 'utf8');
const classes = runInNewContext(`${source}\n({ LiveRewindClock, RewindWindow, RewindCameraPlayer, LiveRewindController });`, { TextDecoder });

function windowBytes(playlist, encode = fragment => new Uint8Array([fragment.mediaStartSeconds + 1]), overrides = {}) {
  const initialization = new Uint8Array([0]), fragments = playlist.fragments.map(encode);
  const metadata = Buffer.from(JSON.stringify({ playlist, initializationLength: initialization.length, fragmentLengths: fragments.map(bytes => bytes.length), ...overrides }));
  const bytes = new Uint8Array(4 + metadata.length + initialization.length + fragments.reduce((sum, fragment) => sum + fragment.length, 0));
  new DataView(bytes.buffer).setUint32(0, metadata.length, true); bytes.set(metadata, 4);
  let at = 4 + metadata.length; bytes.set(initialization, at); at += initialization.length;
  for (const fragment of fragments) { bytes.set(fragment, at); at += fragment.length; }
  return bytes;
}

test('one clock advances all cameras at the selected speed and preserves pause across seeks', () => {
  let now = 0;
  const clock = new classes.LiveRewindClock(() => now);
  assert.equal(clock.current(), null);
  clock.seek(10000); now = 1000; assert.equal(clock.current(), 11000);
  clock.speed(2); now = 2000; assert.equal(clock.current(), 13000);
  clock.pause(true); now = 9000; assert.equal(clock.current(), 13000);
  clock.seek(5000); now = 15000; assert.equal(clock.current(), 5000);
  clock.speed(0.5); clock.pause(false); now = 17000; assert.equal(clock.current(), 6000);
  clock.speed(-1); clock.seek(NaN); now = 19000; assert.equal(clock.current(), 4000);
  clock.speed(3.75); now = 20000; assert.equal(clock.current(), 7750);
  clock.live(); assert.equal(clock.current(), null); assert.equal(clock.rate, 1); assert.equal(clock.paused, false);
});

test('coverage selection preserves reconnect gaps and exact range boundaries', () => {
  const ranges = [{ epoch: 'old', start: 1000, end: 2000 }, { epoch: 'new', start: 3000, end: 5000 }];
  assert.equal(classes.LiveRewindClock.rangeAt(ranges, 1000).epoch, 'old');
  assert.equal(classes.LiveRewindClock.rangeAt(ranges, 1999).epoch, 'old');
  for (const at of [0, 2000, 2500, 5000]) assert.equal(classes.LiveRewindClock.rangeAt(ranges, at), undefined);
  assert.equal(classes.LiveRewindClock.rangeAt(ranges, 3000).epoch, 'new');
});

test('three historical cameras seek the same server UTC instant across independent media origins and measured source paces', async () => {
  const h = harness();
  const players = [];
  for (const [index, clockRate] of [1, 1.2, .95].entries()) {
    const mediaStart = 30 + index * 40;
    const playlist = { ...h.playlist, fragments: h.playlist.fragments.map((item, i) => ({ ...item,
      start: 100000 + i * clockRate * 1000, end: 100000 + (i + 1) * clockRate * 1000,
      mediaStartSeconds: mediaStart + i, clockRate })) };
    const request = async () => windowBytes(playlist);
    const player = new h.runtime.RewindCameraPlayer(index + 1, h.tile, request, () => {});
    await player.synchronize(h.range, 105400, true, 1);
    assert.ok(Math.abs(player.video.currentTime * clockRate - 5.4) < .001);
    assert.equal(player.video.paused, true);
    await player.synchronize(h.range, 105400, false, 2);
    assert.equal(player.video.playbackRate, 2 / clockRate);
    await player.synchronize(h.range, 104200, false, -1);
    assert.ok(Math.abs(player.video.currentTime * clockRate - 4.2) < .001);
    assert.equal(player.video.paused, true); assert.equal(player.video.muted, true);
    players.push(player);
  }
  for (const player of players) player.dispose();
});

test('rewind uses individual fragment mapping across a source pause and leaves the missing interval empty', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  for (const item of h.playlist.fragments) if (item.sequence > 6) item.start += 4000;
  await player.synchronize(h.range, 111500, true, 1);
  assert.equal(player.video.currentTime, 7.5);
  await player.synchronize(undefined, 108000, true, 1);
  assert.equal(player.epoch, null); assert.match(player.message, /No buffered footage/);
  player.dispose();
});

test('malformed fragment time or pace clears historical playback instead of inventing a seek target', async () => {
  for (const invalid of [{ clockRate: 0 }, { clockRate: -1 }, { clockRate: NaN }, { clockRate: Infinity },
    { start: NaN }, { mediaStartSeconds: Infinity }, { durationSeconds: 0 }, { end: 99999 }]) {
    const h = harness(), playlist = { ...h.playlist, fragments: h.playlist.fragments.map(item => ({ ...item, ...invalid })) };
    const request = async () => windowBytes(playlist);
    const player = new h.runtime.RewindCameraPlayer(1, h.tile, request, () => {});
    await player.synchronize(h.range, 103500, true, 1);
    assert.match(player.message, /Invalid buffered timing/); assert.equal(player.epoch, null);
    assert.equal(player.video.paused, true); assert.equal(player.timingFragments.size, 0);
    player.dispose();
  }
});

test('refined neighboring metadata is applied to already downloaded fragments without re-appending media', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 110500, true, 1);
  const appended = player.buffer.times.length;
  // A new sample removes 200 ms of flushing latency from retained metadata.
  for (const item of h.playlist.fragments) item.start -= 200;
  await player.synchronize(h.range, 110500, true, 1);
  assert.equal(player.video.currentTime, 10.7);
  assert.equal(player.buffer.times.length, appended);
  player.dispose();
});

test('continuous rewind timing across short production gaps stays playable when paused, playing or scanning backwards', async () => {
  const h = harness();
  // Public playlist timing covers the available media continuously despite
  // 1-73 ms production-clock discontinuities at compressed fragment boundaries.
  const boundaries = [100000, 101000, 102000, 103000, 104073, 105073, 106100, 107100, 108101, 109128, 110128, 111200, 112200];
  h.range.end = boundaries.at(-1);
  h.playlist.fragments = h.playlist.fragments.map((fragment, index) => ({ ...fragment,
    start: boundaries[index], end: boundaries[index + 1], clockRate: (boundaries[index + 1] - boundaries[index]) / 1000 }));
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  for (const [paused, rate] of [[true, 1], [false, 1], [false, -1]]) {
    const positions = boundaries.slice(1, -1).flatMap(boundary => [boundary - 74, boundary - 1, boundary, boundary + 1]);
    if (rate < 0) positions.reverse();
    for (const at of positions) {
      await player.synchronize(h.range, at, paused, rate);
      const fragment = h.playlist.fragments.find(item => item.start <= at && at < item.end);
      const expected = fragment.mediaStartSeconds - 30 + (at - fragment.start) / (fragment.clockRate * 1000);
      assert.ok(Math.abs(player.video.currentTime - expected) <= (paused || rate < 0 ? .04 : .1) / fragment.clockRate);
      assert.equal(player.message, ''); assert.equal(player.epoch, 'first');
      assert.equal(h.tile.root.classList.contains('rewind-gap'), false);
      assert.equal(player.video.paused, paused || rate < 0);
    }
  }
  assert.equal(h.sourceCount(), 1, 'Timing boundaries reuse the decoded media instead of flickering through reloads.');
  player.dispose();
});

function harness(timers = { setTimeout, clearTimeout }) {
  let now = 0, urlNumber = 0, sourceMedia;
  const revoked = [], calls = [], elements = new Map();
  class Element extends EventTarget {
    constructor() {
      super(); this.classes = new Set(); this.children = [];
      this.classList = { add: (...names) => names.forEach(name => this.classes.add(name)), remove: (...names) => names.forEach(name => this.classes.delete(name)), contains: name => this.classes.has(name),
        toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name) };
      this.paused = true; this.currentTime = 0; this.volume = 1; this.muted = true; this.attributes = {}; this.playCalls = 0;
      this.readyState = 2;
      this.style = { setProperty() {} };
    }
    setAttribute(key, value) { this.attributes[key] = value; }
    removeAttribute(key) { delete this[key]; }
    append(child) { this.children.push(child); }
    remove() { this.removed = true; }
    load() {}
    pause() { this.paused = true; }
    play() { this.paused = false; this.playCalls++; return Promise.resolve(); }
    querySelector() { return this.surface ||= new Element(); }
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { top: 0, bottom: 600, width: 1000, height: 600 }; }
    contains(element) { return element === this || this.children.includes(element); }
    matches() { return false; }
    set src(value) { this._src = value; this.sourceMedia = sourceMedia; queueMicrotask(() => this.sourceMedia.dispatchEvent(new Event('sourceopen'))); }
    get buffered() { return this.sourceMedia?.buffer.buffered || { length: 0 }; }
  }
  class Media extends EventTarget {
    static isTypeSupported = () => true;
    constructor() { super(); sourceMedia = this; this.buffer = new Buffer(); }
    addSourceBuffer() { return this.buffer; }
  }
  class Buffer extends EventTarget {
    constructor() { super(); this.times = []; this.timestampOffset = 0; }
    get buffered() { return { length: this.times.length ? 1 : 0, start: () => this.times[0], end: () => this.times.at(-1) + 1 }; }
    appendBuffer(bytes) {
      if (bytes[0]) this.times.push(bytes[0] - 1 + this.timestampOffset);
      queueMicrotask(() => this.dispatchEvent(new Event('updateend')));
    }
    remove(begin, end) { this.times = this.times.filter(time => time >= end); queueMicrotask(() => this.dispatchEvent(new Event('updateend'))); }
  }
  const document = { hidden: false, querySelector: () => null, createElement: () => new Element(), addEventListener() {}, getElementById(id) { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); } };
  class Observer { observe() {} disconnect() {} }
  const context = { document, window: { innerHeight: 600, MediaSource: Media, addEventListener() {} }, Event, AbortController, AbortSignal, DOMException,
    ResizeObserver: Observer, MutationObserver: Observer, requestAnimationFrame() {}, cancelAnimationFrame() {},
    performance: { now: () => now }, setInterval() {}, ...timers, queueMicrotask, TextDecoder,
    URL: { createObjectURL() { return `blob:${++urlNumber}`; }, revokeObjectURL(url) { revoked.push(url); } },
    fetch: () => new Promise(() => {}) };
  const runtime = runInNewContext(`${source}\n({ LiveRewindClock, RewindWindow, RewindCameraPlayer, LiveRewindController });`, context);
  const range = { epoch: 'first', start: 100000, end: 120000 };
  const playlist = { epoch: 'first', mimeType: 'video/mp4', hasAudio: true, fragments: Array.from({ length: 12 }, (_, index) => ({ sequence: index + 1, start: 100000 + index * 1000, mediaStartSeconds: index + 30, durationSeconds: 1 })) };
  const request = async (url, signal, binary) => {
    calls.push(url);
    if (signal?.aborted) throw new DOMException('', 'AbortError');
    if (binary) return windowBytes(playlist);
    return playlist;
  };
  const tile = { name: 'Test', root: new Element(), zoom: { refresh() {} } };
  return { runtime, context, document, elements, range, playlist, tile, request, calls, revoked,
    sourceCount: () => urlNumber,
    advance(milliseconds) { now += milliseconds; }, flush: () => new Promise(resolve => setImmediate(resolve)) };
}

function controllerHarness() {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, connected: true, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] };
  controller.online = true; controller.clock.pause(true); controller.seek(110000);
  return { ...h, controller, enter(rate) { controller.speed.value = String(rate); controller.speed.onchange(); } };
}

test('atomic window decoding rejects truncated, malformed and inconsistent envelopes without allocating advertised payload lengths', () => {
  const h = harness(), valid = windowBytes(h.playlist);
  const padded = new Uint8Array(valid.length + 6); padded.set(valid, 3);
  const decoded = classes.RewindWindow.read(padded.subarray(3, 3 + valid.length));
  assert.equal(decoded.epoch, 'first'); assert.equal(decoded.initialization[0], 0); assert.equal(decoded.media.get(1)[0], 31);
  const excessive = valid.slice(); new DataView(excessive.buffer).setUint32(0, excessive.length + 1, true);
  const badUtf8 = valid.slice(); badUtf8[4] = 255;
  for (const invalid of [new Uint8Array(0), new Uint8Array(3), new Uint8Array(4), valid.slice(0, -1),
    Uint8Array.from([...valid, 0]), excessive, badUtf8,
    windowBytes({ ...h.playlist, fragments: [] }), windowBytes({ ...h.playlist, epoch: '' }),
    windowBytes({ ...h.playlist, mimeType: '' }), windowBytes({ ...h.playlist, hasAudio: 'true' }),
    ...[0, -1, .5, Number.MAX_SAFE_INTEGER, null].map(initializationLength => windowBytes(h.playlist, undefined, { initializationLength })),
    ...[[], 'invalid', [0], h.playlist.fragments.map(() => -1), h.playlist.fragments.map(() => .5),
      h.playlist.fragments.map(() => Number.MAX_SAFE_INTEGER)].map(fragmentLengths => windowBytes(h.playlist, undefined, { fragmentLengths })),
    windowBytes({ ...h.playlist, fragments: [h.playlist.fragments[0], h.playlist.fragments[0]] }),
    windowBytes({ ...h.playlist, fragments: [{ ...h.playlist.fragments[0], sequence: 0 }] })])
    assert.throws(() => classes.RewindWindow.read(invalid), /Invalid buffered playback window/);
});

test('an expired optional read-ahead keeps the selected decoded history and refreshes coverage', async () => {
  const h = harness(); let refreshes = 0;
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {}, () => {}, () => refreshes++);
  await player.synchronize(h.range, 108000, true, 1);
  player.request = async () => { throw Object.assign(new Error('Footage has left the rewind buffer.'), { status: 410 }); };
  await player.synchronize(h.range, 108500, true, 1);
  assert.equal(player.epoch, 'first'); assert.equal(player.video.currentTime, 8.5); assert.equal(player.video.paused, true);
  assert.equal(player.message, ''); assert.equal(h.tile.root.classList.contains('live-media-ready'), true);
  assert.equal(h.tile.root.classList.contains('rewind-gap'), false); assert.equal(refreshes, 1); assert.equal(h.sourceCount(), 1);
  player.dispose();
});

test('a filling buffer retains an admitted window when the latest target advances beyond it and extends without reloading', async () => {
  const h = harness(); h.range.end = 104000; h.playlist.fragments = h.playlist.fragments.slice(0, 4);
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 102000, false, 1);
  assert.equal(h.calls.length, 1, 'Read-ahead waits for new coverage when a filling window already contains its tail.');
  const admitted = windowBytes(h.playlist); let release;
  player.request = () => new Promise(resolve => { release = resolve; });
  const range = { ...h.range, end: 106000 };
  const loading = player.synchronize(range, 103500, false, 1);
  await player.synchronize(range, 104500, false, 1);
  release(admitted); await loading;
  assert.equal(player.epoch, 'first'); assert.equal(h.sourceCount(), 1); assert.equal(player.waiting, true);
  assert.equal(player.video.paused, true); assert.equal(h.tile.root.classList.contains('live-media-ready'), false);
  player.video.dispatchEvent(new Event('loadeddata'));
  assert.equal(h.tile.root.classList.contains('live-media-ready'), false, 'An old decoder event cannot reveal a picture while the requested time is missing.');
  h.playlist.fragments.push(...[4, 5].map(index => ({ sequence: index + 1, start: 100000 + index * 1000, mediaStartSeconds: 30 + index, durationSeconds: 1 })));
  player.request = h.request;
  await player.synchronize(range, 104500, false, 1);
  assert.equal(player.video.currentTime, 4.5); assert.equal(player.video.paused, false); assert.equal(player.waiting, false);
  assert.equal(h.sourceCount(), 1); assert.equal(h.tile.root.classList.contains('live-media-ready'), true); player.dispose();
});

test('fresh coverage retries an evicted oldest point immediately and cannot substitute a different epoch', async () => {
  const h = harness(); let refreshes = 0;
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, async () => {
    throw Object.assign(new Error('Footage has left the rewind buffer.'), { status: 410 });
  }, () => {}, () => {}, () => refreshes++);
  await player.synchronize(h.range, h.range.start, false, 1); assert.equal(refreshes, 1); assert.ok(player.retryAt > 0);
  player.request = h.request;
  await player.synchronize({ ...h.range, start: 101000 }, 101000, false, 1);
  assert.equal(player.epoch, 'first'); assert.equal(player.message, ''); assert.equal(player.video.currentTime, 1);
  player.request = async () => windowBytes({ ...h.playlist, epoch: 'another' });
  await player.synchronize({ ...h.range, epoch: 'second' }, 101000, true, 1);
  assert.equal(player.epoch, null); assert.match(player.message, /another stream/); player.dispose();
});

test('leaving a genuine gap resumes available history immediately instead of retaining the missing-footage cooldown', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 102000, false, 1);
  await player.synchronize(undefined, 122000, false, 1);
  assert.match(player.message, /No buffered footage/);
  h.advance(100);
  await player.synchronize(h.range, 103000, false, 1);
  assert.equal(player.epoch, 'first'); assert.equal(player.message, '');
  assert.equal(player.video.currentTime, 3); assert.equal(player.video.paused, false);
  assert.equal(h.tile.root.classList.contains('rewind-gap'), false); player.dispose();
});

test('manual signed speed selects transport, presets and magnitude-based animal highlights', () => {
  const h = controllerHarness(), c = h.controller;
  for (const rate of [-64, -8, -4, -2, -1, -0.5, -0.25, -0.001, 0, 0.001, 0.25, 0.5, 1, 2, 4, 8, 64, 3.75, -3.75]) {
    h.enter(rate);
    assert.equal(c.signedRate, rate);
    assert.equal(c.speed.value, String(rate));
    assert.equal(c.pauseButton.attributes['aria-pressed'], String(rate === 0));
    assert.equal(c.reverseButton.attributes['aria-pressed'], String(rate < 0));
    assert.equal(c.playButton.attributes['aria-pressed'], String(rate > 0));
    assert.equal(c.slowerButton.attributes['aria-pressed'], String(Math.abs(rate) > 0 && Math.abs(rate) < 0.5));
    assert.equal(c.fasterButton.attributes['aria-pressed'], String(Math.abs(rate) > 4));
    for (const preset of c.presets) assert.equal(preset.button.attributes['aria-pressed'], String(Math.abs(rate) === preset.rate));
  }
});

test('presets, snail and rabbit preserve direction, including resuming a paused reverse cursor', () => {
  const h = controllerHarness(), c = h.controller;
  for (const direction of [-1, 1]) {
    h.enter(direction);
    for (const preset of c.presets) {
      preset.button.onclick(); assert.equal(c.signedRate, direction * preset.rate);
    }
    c.slowerButton.onclick(); assert.equal(c.signedRate, direction * 2);
    c.fasterButton.onclick(); assert.equal(c.signedRate, direction * 4);
    c.pauseButton.onclick(); assert.equal(c.signedRate, 0); assert.equal(c.speed.value, '0');
    c.slowerButton.onclick(); assert.equal(c.signedRate, direction * 2);
    c.pauseButton.onclick(); c.presets[0].button.onclick(); assert.equal(c.signedRate, direction * 0.5);
    c.pauseButton.onclick(); c.fasterButton.onclick(); assert.equal(c.signedRate, direction);
  }
});

test('switching direction or leaving pause starts at 1x; active play advances then doubles', () => {
  const h = controllerHarness(), c = h.controller;
  for (const forwardRate of [0.25, 2, 8]) {
    h.enter(forwardRate); c.reverseButton.onclick(); assert.equal(c.signedRate, -1);
    for (const magnitude of [2, 4, 8, 16, 32]) { c.reverseButton.onclick(); assert.equal(c.signedRate, -magnitude); }
    c.playButton.onclick(); assert.equal(c.signedRate, 1);
    for (const magnitude of [2, 4, 8, 16, 32]) { c.playButton.onclick(); assert.equal(c.signedRate, magnitude); }
    c.pauseButton.onclick(); c.reverseButton.onclick(); assert.equal(c.signedRate, -1);
    c.pauseButton.onclick(); c.playButton.onclick(); assert.equal(c.signedRate, 1);
  }
  for (const [rate, expected] of [[0.1, 0.5], [0.5, 1], [1.5, 2], [3.75, 4], [5, 10], [-0.25, -0.5], [-3, -4], [-5, -10]]) {
    h.enter(rate); (rate < 0 ? c.reverseButton : c.playButton).onclick(); assert.equal(c.signedRate, expected);
  }
});

test('live transport enters reverse at -1 and forward at +1 without instantly catching up', () => {
  const h = controllerHarness(), c = h.controller;
  c.goLive(); c.reverseButton.onclick(); assert.equal(c.signedRate, -1); assert.equal(c.historical, true);
  assert.equal(c.clock.current(), c.latest()); h.advance(1000); c.tick(); assert.equal(c.clock.current(), c.latest() - 1000);
  c.goLive(); c.playButton.onclick(); assert.equal(c.signedRate, 1); assert.equal(c.historical, true);
  c.goLive(); c.pauseButton.onclick(); h.enter(2); assert.equal(c.historical, true); assert.equal(c.signedRate, 2);
});

test('ten-second seeks and timeline preserve signed speed and pause until a history boundary', () => {
  const h = controllerHarness(), c = h.controller;
  h.range.end = 160000;
  for (const rate of [-2, 2, 0]) {
    h.enter(rate); c.clock.seek(130000);
    h.elements.get('playback-back').onclick(); assert.equal(c.clock.current(), 120000); assert.equal(c.signedRate, rate);
    h.elements.get('playback-forward').onclick(); assert.equal(c.clock.current(), 130000); assert.equal(c.signedRate, rate);
    c.slider.value = '125000'; c.slider.oninput(); assert.equal(c.clock.current(), 125000); assert.equal(c.signedRate, rate);
  }
});

test('reverse pauses at the oldest footage, including expiry, while forward catches up to live', () => {
  const h = controllerHarness(), c = h.controller;
  h.enter(-4); h.advance(3000); c.tick(); assert.equal(c.clock.current(), h.range.start);
  assert.equal(c.signedRate, 0); assert.equal(c.speed.value, '0'); assert.match(c.status, /Start.*paused/);
  h.advance(5000); c.tick(); assert.equal(c.clock.current(), h.range.start);
  c.clock.seek(110000); h.enter(-2); h.range.start = 112000; c.tick();
  assert.equal(c.signedRate, 0); assert.equal(c.clock.current(), 112000);
  c.clock.seek(115000); h.enter(4); h.advance(2000); c.tick(); assert.equal(c.historical, false); assert.equal(c.speed.value, '1');
});

test('automatic boundaries update a submitted speed but preserve an unsubmitted edit', () => {
  const h = controllerHarness(), c = h.controller;
  h.enter(-4); h.document.activeElement = c.speed;
  h.advance(3000); c.tick(); assert.equal(c.speed.value, '0');
  c.clock.seek(110000); h.enter(-4); c.speed.value = '-2';
  h.advance(3000); c.tick(); assert.equal(c.signedRate, 0); assert.equal(c.speed.value, '-2');
  c.clock.seek(110000); h.enter(4); c.speed.value = '2';
  h.advance(3000); c.tick(); assert.equal(c.historical, false); assert.equal(c.speed.value, '2');
});

test('losing all retained footage releases historical state in forward, reverse and pause', () => {
  for (const rate of [-2, 2, 0]) {
    const h = controllerHarness(), c = h.controller;
    h.enter(rate); c.snapshot.cameras = []; c.tick();
    assert.equal(c.historical, false); assert.equal(c.signedRate, 1); assert.equal(c.speed.value, '1');
    assert.equal(c.controls.compactLive.hidden, true); assert.match(c.label.textContent, /Filling/);
  }
});

test('focused reverse disables and mutes Sound while retaining its forward playback preference', () => {
  const h = harness(); let focus = 1;
  const c = new h.runtime.LiveRewindController(() => ({ tiles: new Map([[1, h.tile]]), focus, generation: 1, volume: 0.4 }), () => {}, () => {});
  c.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] }; c.online = true;
  c.clock.pause(true); c.seek(110000); c.sound = true; c.setRate(-2);
  assert.equal(c.soundButton.hidden, false); assert.equal(c.soundButton.disabled, true);
  assert.equal(c.soundButton.attributes['aria-pressed'], 'false'); assert.equal(c.sound, true);
  assert.equal(c.players.get(1).video.muted, true);
  c.setRate(1); assert.equal(c.soundButton.disabled, false); assert.equal(c.soundButton.attributes['aria-pressed'], 'true');
  focus = null; c.render(); assert.equal(c.soundButton.hidden, true); c.goLive();
});

test('browser-rejected or clamped forward rates retain the actual speed and selection; reverse is independent', async () => {
  for (const mode of ['throw', 'clamp']) {
    const h = controllerHarness(), c = h.controller;
    let nativeRate = 1;
    Object.defineProperty(c.rateProbe, 'playbackRate', {
      get: () => nativeRate,
      set(rate) {
        if (rate < 0.0625 || rate > 16) { if (mode === 'throw') throw new Error('Unsupported rate'); nativeRate = 16; }
        else nativeRate = rate;
      }
    });
    h.enter(16); const position = c.clock.current();
    for (const action of [() => c.playButton.onclick(), () => c.fasterButton.onclick(), () => h.enter(32), () => h.enter(0.001)]) {
      action(); assert.equal(c.signedRate, 16); assert.equal(c.speed.value, '16'); assert.equal(c.clock.current(), position);
      assert.equal(c.fasterButton.attributes['aria-pressed'], 'true'); assert.match(c.label.textContent, /does not support.*remains 16/);
    }
    c.request = async () => c.snapshot; await c.poll(); assert.match(c.label.textContent, /does not support/);
    h.enter(-32); assert.equal(c.signedRate, -32); assert.equal(c.speedError, '');
    c.pauseButton.onclick(); h.enter(32); assert.equal(c.signedRate, 0); assert.equal(c.pauseButton.attributes['aria-pressed'], 'true');
    c.reverseButton.onclick(); assert.equal(c.signedRate, -1);
    h.enter(-0.001); assert.equal(c.signedRate, -0.001); assert.equal(c.slowerButton.attributes['aria-pressed'], 'true');
  }
});

test('empty/nonfinite input and doubling overflow leave playback intact; zero always pauses', () => {
  const h = controllerHarness(), c = h.controller;
  h.enter(-2);
  for (const input of ['', ' ', 'NaN', 'Infinity', '-Infinity', '1e999', 'hello']) {
    h.enter(input); assert.equal(c.signedRate, -2); assert.equal(c.speed.value, '-2'); assert.match(c.speedError, /finite/);
  }
  h.enter('-0'); assert.equal(c.signedRate, 0); assert.equal(c.speedError, '');
  c.pauseButton.onclick(); assert.equal(c.signedRate, 0);
  h.enter(-Number.MAX_VALUE); c.fasterButton.onclick(); assert.equal(c.signedRate, -Number.MAX_VALUE); assert.ok(c.speedError);
});

test('signed clock ignores invalid and zero rates and never advances for a backwards wall clock', () => {
  let now = 100;
  const c = new classes.LiveRewindClock(() => now);
  c.seek(10000); c.speed(-2);
  for (const rate of [0, NaN, Infinity, -Infinity]) { c.speed(rate); assert.equal(c.rate, -2); }
  now = 50; assert.equal(c.current(), 10000);
  now = 1050; assert.equal(c.current(), 8000);
});

test('reverse camera scanning seeks paused muted video, retains nearby media and restores forward audio', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 105000, false, 2); player.sound(true, 0.4);
  const calls = h.calls.length;
  await player.synchronize(h.range, 104750, false, -1);
  assert.equal(player.video.currentTime, 4.75); assert.equal(player.video.paused, true); assert.equal(player.video.muted, true);
  assert.equal(player.video.playbackRate, 2, 'No negative rate is assigned to the browser.');
  player.sound(true, 0.4); assert.equal(player.video.paused, true); assert.equal(player.video.muted, true);
  await player.synchronize(h.range, 104500, false, -32); assert.equal(player.video.currentTime, 4.5);
  assert.equal(h.calls.length, calls, 'Reverse inside the media window needs no extra download.');
  await player.synchronize(h.range, 104500, false, 1); player.sound(true, 0.4);
  assert.equal(player.video.paused, false); assert.equal(player.video.muted, false); player.dispose();
});

test('reverse windows look behind the cursor and include the current fragment when fragments are short', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  const early = { ...h.playlist, fragments: h.playlist.fragments.slice(0, 3) };
  const current = { ...h.playlist, fragments: h.playlist.fragments.slice(2) };
  const positions = [];
  player.request = async url => { positions.push(Number(url.split('at=')[1])); return windowBytes(positions.length === 1 ? early : current); };
  const playlist = await player.playlist(h.range, 111000, -2, new AbortController().signal);
  assert.deepEqual(positions, [101000, 111000]);
  assert.deepEqual(Array.from(playlist.fragments, f => f.sequence), Array.from({ length: 12 }, (_, i) => i + 1));
  player.dispose();
});

test('pausing or reversing stops forward video immediately during an outstanding media fetch', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 102000, false, 1);
  let release;
  player.request = () => new Promise(resolve => { release = resolve; });
  const work = player.synchronize(h.range, 108000, false, 1);
  assert.equal(player.busy, true);
  await player.synchronize(h.range, 107000, false, -1);
  assert.equal(player.video.paused, true); assert.equal(player.video.muted, true);
  release(windowBytes(h.playlist)); await work;
  assert.equal(player.video.currentTime, 7); assert.equal(player.video.paused, true); player.dispose();
});

test('reverse crosses reconnect gaps without mixing epochs and survives failure/disposal', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 105000, false, -1);
  await player.synchronize(undefined, 99000, false, -1); assert.equal(player.epoch, null); assert.ok(player.message);
  h.advance(2001); h.range.epoch = h.playlist.epoch = 'earlier';
  await player.synchronize(h.range, 105000, false, -1); assert.equal(player.epoch, 'earlier'); assert.equal(player.video.paused, true);
  player.request = async () => { throw new Error('Footage has left the rewind buffer.'); };
  await player.synchronize(h.range, 119000, false, -1); assert.match(player.message, /left the rewind buffer/);
  player.dispose(); assert.equal(h.tile.root.classList.contains('rewind-active'), false);
});

test('historical pause retains incoming media, seeks away from live and does not resume when sound is selected', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 103500, true, 1);
  assert.equal(player.video.currentTime, 3.5); assert.equal(player.video.paused, true);
  assert.equal(player.video.buffered.end(0), 12);
  player.sound(true, 0.4); assert.equal(player.video.paused, true); assert.equal(player.video.volume, 0.4);
  await player.synchronize(h.range, 102000, false, 2);
  assert.equal(player.video.currentTime, 2); assert.equal(player.video.paused, false); assert.equal(player.video.playbackRate, 2);
  assert.equal(h.sourceCount(), 1);
  player.dispose(); assert.equal(player.video.removed, true); assert.equal(h.tile.root.classList.contains('rewind-active'), false);
  assert.deepEqual(h.revoked, ['blob:1']);
});

test('browser audio permission rejection keeps video playing muted and leaves footage available', async () => {
  const h = harness(); let blocked = 0;
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {}, () => blocked++);
  await player.synchronize(h.range, 103500, true, 1);
  player.video.play = function () {
    this.playCalls++;
    if (!this.muted) return Promise.reject(new DOMException('Audio requires a gesture', 'NotAllowedError'));
    this.paused = false; return Promise.resolve();
  };
  player.sound(true, 1);
  await player.synchronize(h.range, 103500, false, 1); await h.flush();
  assert.equal(blocked, 1); assert.equal(player.video.paused, false); assert.equal(player.video.muted, true);
  assert.equal(player.message, ''); assert.equal(h.tile.root.classList.contains('rewind-gap'), false);
  player.dispose();
});

test('a late play rejection cannot change sound state after player disposal', async () => {
  const h = harness(); let blocked = 0, reject;
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {}, () => blocked++);
  await player.synchronize(h.range, 103500, true, 1);
  player.video.play = () => new Promise((_, fail) => { reject = fail; });
  player.sound(true, 1);
  await player.synchronize(h.range, 103500, false, 1);
  player.dispose(); reject(new DOMException('Late rejection', 'NotAllowedError')); await h.flush();
  assert.equal(blocked, 0); assert.equal(player.video.removed, true);
});

test('gaps hide current imagery and discard the old stream before reconnect initialization', async () => {
  const h = harness(), player = new h.runtime.RewindCameraPlayer(1, h.tile, h.request, () => {});
  await player.synchronize(h.range, 102000, false, 1);
  await player.synchronize(undefined, 122000, false, 1);
  assert.equal(player.epoch, null); assert.equal(h.tile.root.classList.contains('rewind-gap'), true);
  assert.match(player.message, /No buffered footage/); assert.deepEqual(h.revoked, ['blob:1']);
  h.advance(2001); h.range.epoch = h.playlist.epoch = 'second';
  await player.synchronize(h.range, 102000, true, 1);
  assert.equal(player.epoch, 'second'); assert.equal(h.tile.root.classList.contains('rewind-gap'), false);
  assert.ok(h.calls.some(url => url.includes('/second/window?'))); player.dispose();
});

test('expired fragments and unsupported decoders report a gap without replaying live video', async () => {
  for (const unsupported of [false, true]) {
    const h = harness();
    if (unsupported) h.context.window.MediaSource.isTypeSupported = () => false;
    const request = async (...args) => { if (!unsupported) throw new Error('Footage has left the rewind buffer.'); return h.request(...args); };
    const player = new h.runtime.RewindCameraPlayer(1, h.tile, request, () => {});
    await player.synchronize(h.range, 101000, false, 1);
    assert.equal(player.epoch, null); assert.equal(h.tile.root.classList.contains('rewind-gap'), true);
    assert.match(player.message, unsupported ? /unsupported/ : /left the rewind buffer/);
    const count = h.calls.length; await player.synchronize(h.range, 101000, false, 1); assert.equal(h.calls.length, count);
    player.dispose();
  }
});

test('a pending fetch cannot resurrect a disposed player or append media from a previous seek', async () => {
  const h = harness(); let release;
  const pending = new Promise(resolve => { release = resolve; });
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, async () => pending, () => {});
  const work = player.synchronize(h.range, 101000, false, 1);
  player.dispose(); release(windowBytes(h.playlist)); await work;
  assert.equal(player.epoch, null); assert.equal(h.tile.root.classList.contains('rewind-active'), false); assert.equal(player.video.removed, true);
});

test('grid/focus switches preserve time, speed and pause while releasing invisible players', () => {
  const h = harness(), tiles = new Map([[1, h.tile], [2, { ...h.tile, root: new h.tile.root.constructor() }]]);
  let focus = null, entered = 0;
  const controller = new h.runtime.LiveRewindController(() => ({ tiles, focus, generation: 1, volume: 1 }), () => entered++, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [1, 2].map(channel => ({ channel, ranges: [h.range] })) };
  controller.online = true; controller.clock.pause(true); controller.seek(105000); controller.clock.speed(0.5);
  assert.equal(controller.players.size, 2); assert.equal(entered, 1);
  focus = 2; controller.viewChanged(); assert.equal(controller.players.size, 1); assert.ok(controller.players.has(2));
  focus = null; controller.viewChanged(); assert.equal(controller.players.size, 2);
  assert.equal(controller.clock.current(), 105000); assert.equal(controller.clock.paused, true); assert.equal(controller.clock.rate, 0.5);
  controller.goLive(); assert.equal(controller.players.size, 0); assert.equal(controller.historical, false);
});

test('late network failure cannot restore gap overlays on a disposed camera', async () => {
  const h = harness(); let reject;
  const pending = new Promise((_, fail) => { reject = fail; });
  const player = new h.runtime.RewindCameraPlayer(1, h.tile, () => pending, () => {});
  const work = player.synchronize(h.range, 101000, false, 1);
  player.dispose(); reject(new Error('Late network failure')); await work;
  assert.equal(h.tile.root.classList.contains('rewind-gap'), false); assert.equal(player.epoch, null);
});

test('the deferred script initializes playback after app.js declares its initially empty controller binding', () => {
  const h = harness();
  Object.assign(h.context, { tiles: new Map(), focus: null, generation: 0, liveRewind: undefined,
    liveAudio: { volume: 1, stop() {} }, pushToTalk: { stop() {} }, renderStatus() {} });
  const context = { ...h.context };
  runInNewContext(source, context);
  assert.ok(context.liveRewind); assert.equal(context.liveRewind.historical, false);
});

test('pause at live creates a paused history cursor; catching up returns to live', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] }; controller.online = true;
  controller.pauseButton.onclick(); assert.equal(controller.historical, true); assert.equal(controller.clock.paused, true);
  h.advance(10000); assert.equal(controller.clock.current(), 118800);
  controller.seek(110000); controller.clock.pause(false); controller.clock.speed(2);
  h.advance(5000); controller.tick(); assert.equal(controller.historical, false);
});

test('expired paused positions clamp to retained history without restarting playback', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] }; controller.online = true;
  controller.clock.pause(true); controller.seek(101000); h.range.start = 105000; controller.tick();
  assert.equal(controller.clock.current(), 105000); assert.equal(controller.clock.paused, true); assert.match(controller.status, /expired/);
});

test('timeline updates preserve speed input while editing and accept signed rates', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] }; controller.online = true;
  controller.clock.pause(true); controller.seek(105000);
  h.document.activeElement = controller.speed; controller.speed.value = '3.75'; controller.tick();
  assert.equal(controller.speed.value, '3.75'); assert.equal(controller.clock.rate, 1);
  controller.speed.onchange(); assert.equal(controller.clock.rate, 3.75);
  controller.speed.value = '-1'; controller.speed.onchange(); assert.equal(controller.clock.rate, -1);
  h.document.activeElement = null; controller.render(); assert.equal(controller.speed.value, '-1');
});

test('closing the playback drawer preserves rewind position, pause, speed, sound and players', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map([[1, h.tile]]), focus: 1, generation: 1, volume: 0.4 }), () => {}, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] };
  controller.online = true; controller.clock.pause(true); controller.seek(105000); controller.clock.speed(0.5);
  controller.sound = true; controller.applySound(); controller.render();
  const player = controller.players.get(1);
  controller.controls.show(); controller.controls.close();
  assert.equal(controller.controls.open, false); assert.equal(controller.historical, true);
  assert.equal(controller.clock.current(), 105000); assert.equal(controller.clock.paused, true); assert.equal(controller.clock.rate, 0.5);
  assert.equal(controller.sound, true); assert.equal(controller.players.get(1), player);
  assert.equal(controller.controls.compactStatus.textContent, 'PAUSED · 0× · 14s behind live');
  controller.goLive();
});

test('compact Go live remains usable after a playback failure and disabled buffers hide the drawer', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] };
  controller.online = true; controller.clock.pause(true); controller.seek(105000);
  controller.online = false; controller.status = 'Playback server unavailable.'; controller.render();
  assert.equal(controller.controls.compactLive.hidden, false);
  assert.equal(controller.liveButton.disabled, false);
  controller.controls.compactLive.dispatchEvent(new Event('click'));
  assert.equal(controller.historical, false); assert.equal(controller.controls.compactLive.hidden, true);
  controller.controls.show(); assert.equal(controller.controls.open, true);
  controller.snapshot.settings.enabled = false; controller.goLive();
  assert.equal(controller.controls.root.hidden, true); assert.equal(controller.controls.open, false);
});


test('expanded, compact and accessible timeline delays use the same minutes and seconds', () => {
  const h = harness();
  const controller = new h.runtime.LiveRewindController(() => ({ tiles: new Map(), focus: null, generation: 1, volume: 1 }), () => {}, () => {});
  h.range.end = 500000;
  controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [{ channel: 1, ranges: [h.range] }] };
  controller.online = true;
  for (const paused of [false, true]) {
    controller.clock.pause(paused);
    for (const [seconds, expected] of [[247, '4m 7s'], [60, '1m 0s'], [59.5, '1m 0s'], [59, '59s'], [0, '0s']]) {
      controller.clock.seek(controller.latest() - seconds * 1000);
      controller.render();
      assert.ok(controller.label.textContent.endsWith(` · ${expected} behind live`));
      assert.equal(controller.slider.attributes['aria-valuetext'], controller.label.textContent);
      assert.equal(controller.controls.compactStatus.textContent, `${paused ? 'PAUSED · 0×' : 'PLAYBACK · 1×'} · ${expected} behind live`);
    }
  }
  controller.status = 'Playback server unavailable.';
  controller.render();
  assert.equal(controller.label.textContent, controller.status);
  assert.equal(controller.slider.attributes['aria-valuetext'], controller.status);
  controller.status = '';
  controller.goLive();
  assert.equal(controller.label.textContent, 'LIVE');
  assert.equal(controller.controls.compactStatus.textContent, '');
});

function saveHarness(timers) {
  const h = harness(timers);
  let focus = null;
  const tiles = new Map([[1, h.tile], [2, { ...h.tile, root: new h.tile.root.constructor() }]]);
  const controller = new h.runtime.LiveRewindController(() => ({ tiles, focus, generation: 1, volume: 1 }), () => {}, () => {});
  controller.snapshot = { generation: 1, connected: true, settings: { enabled: true },
    cameras: [1, 2].map(channel => ({ channel, ranges: [h.range] })) };
  controller.online = true;
  controller.clock.pause(true); controller.seek(105000);
  return { ...h, controller, focus(channel) { focus = channel; controller.viewChanged(); } };
}

test('save is disabled live and start selection pauses with an accessible prompt for visible cameras', async () => {
  const h = saveHarness(), c = h.controller;
  c.goLive(); assert.equal(c.saveButton.disabled, true); await c.saveRecording(); assert.equal(c.selection, null);
  c.clock.pause(false); c.seek(105000); await c.saveRecording();
  assert.equal(c.clock.paused, true); assert.equal(c.selection.start, 105000);
  assert.deepEqual(Array.from(c.selection.channels), [1, 2]);
  assert.equal(c.saveButton.textContent, 'Save end'); assert.match(c.saveLabel.textContent, /slider.*playback buttons.*Earlier end/);
  assert.equal(c.cancelSaveButton.hidden, false);
  c.cancelSaveButton.onclick(); assert.equal(c.selection, null);
  h.focus(2); await c.saveRecording(); assert.deepEqual(Array.from(c.selection.channels), [2]); c.goLive();
});

test('save end accepts either ordering and uses slider and transport times without changing the selected start', async () => {
  for (const earlier of [false, true]) {
    const h = saveHarness(), c = h.controller, requests = [];
    h.context.fetch = async (url, options) => { requests.push({ url, ...options }); return { ok: true, json: async () => [{ id: 'saved' }] }; };
    await c.saveRecording();
    c.slider.value = earlier ? '102000' : '108000'; c.slider.oninput();
    assert.equal(c.selection.start, 105000); await c.saveRecording();
    assert.equal(requests.length, 1); assert.equal(requests[0].url, '/api/playback/recordings');
    assert.deepEqual(JSON.parse(requests[0].body), { generation: 1, start: 105000, channels: [1, 2], end: earlier ? 102000 : 108000 });
    assert.equal(c.selection, null); assert.match(c.saveLabel.textContent, /1 recording saved.*View recordings/);
    c.goLive();
  }
});

test('same-time end does not submit; failures preserve selection for retry and duplicate clicks do not submit twice', async () => {
  const h = saveHarness(), c = h.controller;
  await c.saveRecording(); await c.saveRecording(); assert.match(c.saveMessage, /different end time/);
  c.seek(108000);
  h.context.fetch = async () => ({ ok: false, status: 409, json: async () => ({ message: 'Footage expired.' }) });
  await c.saveRecording(); assert.match(c.saveMessage, /expired/); assert.equal(c.selection.start, 105000); assert.equal(c.saving, false);
  let finish, calls = 0;
  h.context.fetch = () => { calls++; return new Promise(resolve => { finish = resolve; }); };
  const pending = c.saveRecording(); assert.equal(c.saveButton.disabled, true); await c.saveRecording(); assert.equal(calls, 1);
  finish({ ok: true, json: async () => [{}, {}] }); await pending; assert.equal(c.selection, null); assert.match(c.saveMessage, /2 recordings saved/);
  c.goLive();
});

test('view changes, reset and go-live clear selection and late replies cannot restore it', async () => {
  const h = saveHarness(), c = h.controller;
  await c.saveRecording(); h.focus(1); assert.equal(c.selection, null); assert.match(c.saveMessage, /view changed/);
  await c.saveRecording(); c.goLive(); assert.equal(c.selection, null); assert.equal(c.saveButton.disabled, true);
  c.clock.pause(true); c.seek(105000); await c.saveRecording(); c.seek(108000);
  let finish;
  h.context.fetch = () => new Promise(resolve => { finish = resolve; });
  const pending = c.saveRecording(); c.reset(); finish({ ok: true, json: async () => [{}] }); await pending;
  assert.equal(c.selection, null); assert.equal(c.saveMessage, ''); assert.equal(c.historical, false);
});

test('network and non-JSON failures leave the chosen start available and save unavailable offline', async () => {
  const h = saveHarness(), c = h.controller;
  await c.saveRecording(); c.seek(108000);
  for (const fetch of [async () => { throw new Error('Network lost'); },
    async () => ({ json: async () => { throw new Error('Invalid response'); } })]) {
    h.context.fetch = fetch; await c.saveRecording(); assert.ok(c.saveMessage); assert.equal(c.selection.start, 105000);
  }
  c.online = false; c.render(); assert.equal(c.saveButton.disabled, true); c.goLive();
});


function timedSaveHarness() {
  let now = 0, sequence = 0;
  const timers = new Map();
  const h = saveHarness({
    setTimeout(callback, milliseconds) { const id = ++sequence; timers.set(id, { callback, at: now + milliseconds }); return id; },
    clearTimeout(id) { timers.delete(id); }
  });
  h.context.fetch = async () => ({ ok: true, json: async () => [{}] });
  return { ...h, timers, advanceSaveTime(milliseconds) {
    now += milliseconds;
    for (const [id, timer] of [...timers]) if (timer.at <= now && timers.delete(id)) timer.callback();
  } };
}

async function saveTimedClip(h) {
  await h.controller.saveRecording();
  h.controller.seek(h.controller.clock.current() === 105000 ? 108000 : 105000);
  await h.controller.saveRecording();
}

test('successful save confirmation hides at ten seconds and preserves playback and players', async () => {
  const h = timedSaveHarness(), c = h.controller;
  await saveTimedClip(h);
  const position = c.clock.current(), players = [...c.players.values()];
  assert.equal(c.saveLabel.hidden, false); assert.match(c.saveLabel.textContent, /recording saved/);
  c.request = async () => c.snapshot; await c.poll();
  h.advanceSaveTime(9999); c.tick(); assert.equal(c.saveLabel.hidden, false);
  h.advanceSaveTime(1);
  assert.equal(c.saveLabel.hidden, true); assert.equal(c.saveLabel.textContent, ''); assert.equal(c.saveMessage, '');
  assert.equal(c.saveMessageTimer, null); assert.equal(h.timers.size, 0);
  assert.equal(c.saveButton.textContent, 'Save video'); assert.equal(c.cancelSaveButton.hidden, true);
  assert.equal(c.clock.current(), position); assert.equal(c.clock.paused, true); assert.equal(c.historical, true);
  assert.deepEqual([...c.players.values()], players); c.goLive();
});

test('a new selection cancels the earlier confirmation expiry and keeps its instructions', async () => {
  const h = timedSaveHarness(), c = h.controller;
  await saveTimedClip(h); h.advanceSaveTime(5000); await c.saveRecording();
  assert.equal(h.timers.size, 0); assert.equal(c.saveMessageTimer, null);
  h.advanceSaveTime(10000);
  assert.equal(c.selection.start, 108000); assert.equal(c.saveLabel.hidden, false);
  assert.match(c.saveLabel.textContent, /choose the end/); assert.equal(c.saveButton.textContent, 'Save end');
  c.cancelSaveButton.onclick(); assert.equal(c.saveLabel.hidden, true); c.goLive();
});

test('each successive successful save receives a full ten seconds for its confirmation', async () => {
  const h = timedSaveHarness(), c = h.controller;
  await saveTimedClip(h); h.advanceSaveTime(5000); await saveTimedClip(h);
  h.advanceSaveTime(5000); assert.equal(c.saveLabel.hidden, false);
  h.advanceSaveTime(4999); assert.equal(c.saveLabel.hidden, false);
  h.advanceSaveTime(1); assert.equal(c.saveLabel.hidden, true); c.goLive();
});

test('expired confirmation cannot erase pending save progress or a failed save', async () => {
  for (const fail of [false, true]) {
    const h = timedSaveHarness(), c = h.controller;
    await saveTimedClip(h); h.advanceSaveTime(5000);
    await c.saveRecording(); c.seek(105000);
    let finish;
    h.context.fetch = () => new Promise(resolve => { finish = resolve; });
    const pending = c.saveRecording();
    h.advanceSaveTime(10000);
    assert.equal(c.saving, true); assert.match(c.saveLabel.textContent, /Saving video/);
    finish({ ok: !fail, json: async () => fail ? { message: 'Disk is full.' } : [{}] }); await pending;
    h.advanceSaveTime(10000);
    assert.equal(c.saveLabel.hidden, !fail);
    if (fail) {
      assert.equal(c.saveMessage, 'Disk is full.'); assert.equal(c.selection.start, 108000);
      assert.equal(c.saveButton.textContent, 'Save end');
    }
    c.goLive();
  }
});

test('go-live and reset cancel confirmation timers and hide their status', async () => {
  for (const reset of [false, true]) {
    const h = timedSaveHarness(), c = h.controller;
    await saveTimedClip(h);
    if (reset) c.reset(); else c.goLive();
    assert.equal(h.timers.size, 0); assert.equal(c.saveMessageTimer, null); assert.equal(c.saveLabel.hidden, true);
    h.advanceSaveTime(10000); assert.equal(c.saveMessage, ''); assert.equal(c.historical, false);
  }
});

test('a late successful save after reset starts no confirmation timer', async () => {
  const h = timedSaveHarness(), c = h.controller;
  await c.saveRecording(); c.seek(108000);
  let finish;
  h.context.fetch = () => new Promise(resolve => { finish = resolve; });
  const pending = c.saveRecording(); c.reset();
  finish({ ok: true, json: async () => [{}] }); await pending;
  assert.equal(h.timers.size, 0); assert.equal(c.saveMessageTimer, null); assert.equal(c.saveLabel.hidden, true);
});
