const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
function harness() {
  const sockets = [], workers = [], events = [], frames = new Map(), timers = new Map(); let next = 0, time = 1000;
  class Element extends EventTarget {
    constructor(tag) { super(); this.tagName = tag.toUpperCase(); this.children = []; this.style = {}; this.attrs = {}; this.readyState = 2; this.paused = true; this.currentTime = 0; this.videoWidth = this.width = 64; this.videoHeight = this.height = 48;
      const values = new Set(); this.classList = { add: value => values.add(value), remove: value => values.delete(value), contains: value => values.has(value), toggle: (value, on) => on ? values.add(value) : values.delete(value) };
      this.buffered = { length: 1, start: () => 0, end: () => 1 }; }
    append(child) { this.children.push(child); }
    remove() { this.removed = true; }
    setAttribute(name, value) { this.attrs[name] = value; }
    removeAttribute(name) { delete this.attrs[name]; }
    pause() { this.paused = true; }
    // A play promise normally resolves only AFTER the first media fragment, never during initialization.
    play() { this.paused = false; return new Promise(() => {}); }
    load() {}
    transferControlToOffscreen() { return {}; }
    getContext() { return { drawImage() {} }; }
    querySelector() { return this.surface; }
  }
  class Buffer extends EventTarget {
    constructor() { super(); this.appended = []; this.updating = false; this.buffered = { length: 0 }; }
    appendBuffer(data) { this.appended.push(data); queueMicrotask(() => this.dispatchEvent(new Event('updateend'))); }
  }
  class Media extends EventTarget {
    static isTypeSupported = () => true;
    addSourceBuffer() { return this.buffer = new Buffer(); }
  }
  class Socket { static OPEN = 1; constructor(url) { this.url = url; this.readyState = 1; this.sent = []; sockets.push(this); } send(value) { this.sent.push(value); } close() { this.closed = true; this.readyState = 3; } }
  class Worker { constructor() { this.messages = []; workers.push(this); } postMessage(message) { this.messages.push(message); } terminate() { this.stopped = true; } }
  const window = Object.assign(new EventTarget(), { MediaSource: Media });
  const document = Object.assign(new EventTarget(), { createElement: tag => new Element(tag), hidden: false });
  const dispatch = document.dispatchEvent.bind(document);
  document.dispatchEvent = event => { events.push(event); return event instanceof Event ? dispatch(event) : true; };
  const context = { window, document, AbortController, Math: Object.assign(Object.create(Math), { random: () => 0 }),
    WebSocket: Socket, Worker, WebAssembly, OffscreenCanvas: Element, HTMLCanvasElement: Element, location: { host: 'fixture', protocol: 'http:' },
    performance: { now: () => time, getEntriesByType: () => [{ nextHopProtocol: 'h2' }] }, Event, EventTarget, CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    setTimeout: (fn, milliseconds) => { timers.set(++next, { fn, at: time + milliseconds }); return next; }, clearTimeout: id => timers.delete(id), setInterval: () => 1, clearInterval() {}, requestAnimationFrame: callback => { frames.set(++next, callback); return next; }, cancelAnimationFrame: id => frames.delete(id),
    URL: { createObjectURL: media => { queueMicrotask(() => media.dispatchEvent(new Event('sourceopen'))); return 'blob:fixture'; }, revokeObjectURL() {} }, console };
  const source = ['camera-frame-clock.js', 'camera-playback-policy.js', 'camera-video.js', 'live-media.js'].map(file => readFileSync('src/SecurityCameraViewer.Web/wwwroot/' + file, 'utf8')).join('\n');
  const classes = runInNewContext(source + '\n({ CameraVideoRecovery, CameraVideoTransport, NativeCameraVideo, SoftwareCameraVideo, CameraPlayback, LiveCameraPlayer, LiveCameraClock })', context);
  const tile = { name: 'Fixture', root: new Element('article'), playbackStatus: new Element('span') }; tile.root.surface = new Element('div');
  const advance = milliseconds => {
    const end = time + milliseconds;
    while (true) {
      const entry = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!entry) break;
      time = entry[1].at; timers.delete(entry[0]); entry[1].fn();
    }
    time = end;
  };
  return { ...classes, tile, sockets, workers, events, frames, timers, advance, context };
}
const tick = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

test('retry policy follows the configured delays indefinitely, adds jitter and resets only after healthy decoded progress', () => {
  const h = harness(); let now = 0;
  const recovery = new h.CameraVideoRecovery('1, 3, 5', () => now, () => .5);
  for (const seconds of [1, 3, 5, 5, 5]) {
    recovery.failed(); assert.equal(recovery.retryAt - now, seconds * 1100);
    const retryAt = recovery.retryAt; recovery.failed(); assert.equal(recovery.retryAt, retryAt);
    now = retryAt; recovery.start(); recovery.frame(); now += 1000;
  }
  for (let i = 0; i < 30; i++) { recovery.frame(); now += 1000; }
  assert.equal(recovery.failures, 0); recovery.failed(); assert.equal(recovery.retryAt - now, 1100);
  recovery.start(); recovery.frame(); now += 31000; recovery.frame();
  assert.equal(recovery.failures, 1, 'A long pause is not thirty seconds of healthy playback.');
  for (const schedule of ['0', '-1', '1,', '1.5', '2147483648']) assert.throws(() => new h.CameraVideoRecovery(schedule), /schedule/);
});

test('transport repeats its final delay, cancels reconnects on stop, and chunks platform-sized delays', () => {
  const h = harness(), states = [];
  const transport = new h.CameraVideoTransport(1, { reset() {} }, error => assert.fail(error), state => states.push(state),
    { maximumQueuedBytes: 100, retryDelaysSeconds: '2, 5' });
  for (const seconds of [2, 5, 5]) {
    const count = h.sockets.length; h.sockets.at(-1).onclose({ code: 1006 });
    assert.equal(states.at(-1).retryInSeconds, seconds); h.advance(seconds * 1000 - 1);
    assert.equal(h.sockets.length, count); h.advance(1); assert.equal(h.sockets.length, count + 1);
  }
  transport.stop(); assert.equal(h.timers.size, 0);
  const long = new h.CameraVideoTransport(2, { reset() {} }, error => assert.fail(error), () => {},
    { maximumQueuedBytes: 100, retryDelaysSeconds: '2147483647' });
  h.sockets.at(-1).onclose(); assert.ok([...h.timers.values()].every(timer => timer.at - 13000 <= 1000));
  long.stop(); assert.equal(h.timers.size, 0);
});

test('consumer timeout reconnects the same method and ignores late work from the expired socket', async () => {
  const h = harness(), states = [], failures = []; let finish;
  const transport = new h.CameraVideoTransport(1, { fragment: () => new Promise(resolve => finish = resolve), reset() {} },
    error => failures.push(error), state => states.push(state), { maximumQueuedBytes: 100, acknowledgementTimeoutSeconds: 2 });
  const socket = h.sockets[0]; socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
  h.advance(2000); assert.equal(states.at(-1).reason, 'consumer-timeout'); assert.equal(socket.closed, true);
  h.advance(1000); finish(); await tick(); assert.equal(socket.sent.length, 0); assert.equal(failures.length, 0);
  assert.equal(transport.snapshot().pendingAcknowledgementMilliseconds, 2000);
  assert.equal(transport.snapshot().pageProtocol, 'h2'); transport.stop();
});

test('an acknowledgement send exception is a recoverable connection failure, not a decoder failure', async () => {
  const h = harness(), failures = [], states = [];
  const transport = new h.CameraVideoTransport(1, { fragment: async () => {}, reset() {} },
    error => failures.push(error), state => states.push(state), { maximumQueuedBytes: 100 });
  const socket = h.sockets[0]; socket.send = () => { throw new Error('Socket closed during send'); };
  socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
  assert.equal(failures.length, 0); assert.equal(states.at(-1).reason, 'send-error');
  assert.equal(socket.closed, true); transport.stop(); assert.equal(h.timers.size, 0);
});

test('a synchronous socket connection failure retries without selecting another decoder', () => {
  const h = harness(), states = [], Socket = h.context.WebSocket;
  h.context.WebSocket = class { constructor() { throw new Error('Network unavailable'); } };
  const transport = new h.CameraVideoTransport(1, { reset() {} }, error => assert.fail(error), state => states.push(state), { maximumQueuedBytes: 100 });
  assert.equal(states.at(-1).reason, 'connection-error');
  h.context.WebSocket = Socket; h.advance(1000);
  assert.equal(h.sockets.length, 1); transport.stop(); assert.equal(h.timers.size, 0);
});

test('foreground and resume events preserve healthy PiP, refresh stalled playback, and release event handlers on stop', () => {
  const h = harness(), consumer = { reset() {} };
  const transport = new h.CameraVideoTransport(1, consumer, error => assert.fail(error), () => {}, { maximumQueuedBytes: 100 });
  h.context.document.hidden = true; h.context.document.dispatchEvent(new Event('visibilitychange'));
  assert.equal(h.sockets[0].closed, undefined);
  transport.frame(); h.advance(1000); h.context.document.hidden = false;
  h.context.document.dispatchEvent(new Event('visibilitychange')); assert.equal(h.sockets[0].closed, undefined);
  h.advance(4000); h.context.document.dispatchEvent(new Event('resume'));
  assert.equal(h.sockets[0].closed, true); h.advance(1000); assert.equal(h.sockets.length, 2);
  h.context.window.dispatchEvent(new Event('online')); assert.equal(h.sockets[1].closed, true);
  transport.stop(); h.context.window.dispatchEvent(new Event('online')); h.advance(10000);
  assert.equal(h.sockets.length, 2); assert.equal(h.timers.size, 0);
});

test('persisted page restoration reconnects and new-page events do not disrupt a healthy connection', () => {
  const h = harness();
  const transport = new h.CameraVideoTransport(1, { reset() {} }, error => assert.fail(error), () => {}, { maximumQueuedBytes: 100 });
  h.context.window.dispatchEvent(new Event('pageshow')); assert.equal(h.sockets[0].closed, undefined);
  const restored = new Event('pageshow'); restored.persisted = true;
  h.context.window.dispatchEvent(restored); assert.equal(h.sockets[0].closed, true); transport.stop();
});

test('genuine unsupported streams retain Automatic fallback and forced native failure', () => {
  for (const preference of ['native', 'automatic']) {
    const h = harness(), playback = new h.CameraPlayback(1, h.tile, preference, { maximumQueuedBytes: 100 }, () => {});
    h.sockets[0].onmessage({ data: JSON.stringify({ type: 'state', status: 'unsupported', fallback: true, message: 'Unsupported stream.' }) });
    assert.equal(playback.method, preference === 'automatic' ? 'server' : null);
    assert.equal(playback.failed.size, 2); playback.stop(); assert.equal(h.timers.size, 0);
  }
});

test('late software worker pictures cannot clear a reconnect countdown or reset healthy progress', () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'browser', { maximumQueuedBytes: 100 }, () => {});
  const worker = h.workers[0];
  const picture = () => worker.onmessage({ data: { type: 'frame', width: 64, height: 48, decodeMilliseconds: 1, mediaTime: 1 } });
  picture(); const frames = playback.stats.frames, lastFrame = h.tile.lastFrame;
  h.sockets[0].onmessage({ data: JSON.stringify({ type: 'state', status: 'retrying', reason: 'queue-overflow' }) });
  h.advance(100); picture();
  assert.equal(playback.reconnecting, true); assert.equal(playback.confirmed, false);
  assert.equal(playback.stats.frames, frames); assert.equal(h.tile.lastFrame, lastFrame);
  playback.stop();
});

for (const preference of ['native', 'automatic']) {
  test(preference + ' recovers from server queue overflow without losing native playback or its last picture', async () => {
    const h = harness(), playback = new h.CameraPlayback(1, h.tile, preference,
      { maximumQueuedBytes: 1024, retryDelaysSeconds: '1, 3' }, () => {});
    const send = async socket => {
      socket.onmessage({ data: JSON.stringify({ type: 'init', mimeType: 'video/mp4; codecs="avc1.4D001E"' }) });
      socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
      socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
    };
    await send(h.sockets[0]);
    const player = playback.player;
    h.sockets[0].onmessage({ data: JSON.stringify({ type: 'state', status: 'retrying', fallback: true,
      reason: 'queue-overflow', message: 'Live playback fell behind. Reconnecting…',
      queue: { queuedPackets: 8, queuedBytes: 800 }, serverProtocol: 'HTTP/1.1' }) });
    assert.equal(playback.method, 'native'); assert.equal(playback.failed.size, 0);
    assert.ok(player.frozen); assert.match(playback.connectionMessage, /fell behind.*1 second/i);
    const before = playback.stats.frames;
    h.advance(1000); await send(h.sockets[1]);
    assert.equal(playback.player, player); assert.equal(player.frozen, null);
    assert.ok(playback.stats.frames > before); assert.equal(playback.confirmed, true);
    assert.equal(playback.connectionMessage, ''); playback.stop(); assert.equal(h.timers.size, 0);
  });
}

test('a held native camera resumes on animation ticks without new media or compositor callbacks and cancels the correct callback', async () => {
  const h = harness(); let now = 1000, videoCallbacks = 0;
  h.context.performance.now = () => now;
  const player = new h.NativeCameraVideo(h.tile, () => {});
  player.video.requestVideoFrameCallback = callback => { videoCallbacks++; h.frames.set(1000, callback); return 1000; };
  player.video.cancelVideoFrameCallback = id => h.frames.delete(id);
  await player.initialize({ mimeType: 'video/mp4' });
  player.video.currentTime = .5;
  await player.fragment(new ArrayBuffer(8), { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-11T11:00:01Z' });
  assert.equal(player.video.paused, true); assert.equal(videoCallbacks, 0);
  const animation = player.callback; now += 500;
  const tickAnimation = h.frames.get(animation); h.frames.delete(animation); tickAnimation(now);
  assert.equal(player.video.paused, false); assert.equal(player.video.currentTime, .5);
  assert.ok(videoCallbacks > 0);
  player.reset(); assert.equal(h.frames.size, 0);
  player.video.currentTime = .5;
  player.clock.update({ streamId: 'next', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-11T11:00:03Z' });
  player.paint(); assert.equal(player.video.paused, true);
  player.stop(); assert.equal(h.frames.size, 0);
});

test('native and software players share live timing, send it to the worker and release membership on reset or focus', async () => {
  const h = harness(), native = new h.NativeCameraVideo(h.tile, () => {}), software = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  await native.initialize({ mimeType: 'video/mp4' });
  const packet = { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-11T11:00:01Z' };
  await native.fragment(new ArrayBuffer(8), packet);
  const pending = software.fragment(new ArrayBuffer(8), { ...packet, startSeconds: 100, endSeconds: 101 });
  const message = h.workers[0].messages.at(-1);
  assert.equal(message.timing.position, Date.parse('2026-10-11T11:00:00Z'));
  assert.equal(message.timing.rate, 1);
  assert.equal(message.timing.samples[0].start, 100); assert.equal(message.timing.sentAt, 1000);
  assert.equal(h.LiveCameraClock.shared.members.size, 2);
  h.workers[0].onmessage({ data: { type: 'done', id: message.id } }); await pending;
  software.reset(); assert.equal(h.LiveCameraClock.shared.members.size, 1);
  h.tile.root.classList.add('live-media-ready'); native.synchronize();
  assert.equal(h.LiveCameraClock.shared.members.size, 0);
  native.stop(); software.stop();
});

test('software worker schedules calibrated pictures on the shared clock across different window and worker time origins', async () => {
  let time = 10000, available = 1, pts = 1000000;
  const messages = [], waits = [], memory = new ArrayBuffer(64), captureOrigin = Date.parse('2026-10-11T11:00:00Z');
  const decoder = { HEAPU8: new Uint8Array(memory), _malloc: () => 4, _free() {}, _camera_push: () => 0,
    _camera_next: () => available-- > 0 ? 1 : 0, _camera_pts: () => pts,
    _camera_width: () => 2, _camera_height: () => 2, _camera_pixels: () => 16, rendered: 0 };
  const context = { importScripts() {}, self: {}, CameraMp4: { packets: () => [{ data: new Uint8Array(1), pts }] },
    performance: { now: () => time, timeOrigin: 100000 }, postMessage: message => messages.push(message),
    ImageData: class {}, setTimeout: (fn, milliseconds) => { waits.push(milliseconds); time += milliseconds; fn(); } };
  const worker = runInNewContext(readFileSync('src/SecurityCameraViewer.Web/wwwroot/camera-decoder-worker.js', 'utf8') +
    '\n({ fragment, prepare(m) { module = m; decoder = 1; video = {}; canvas = { width: 2, height: 2 }; context = { putImageData() { ++m.rendered; } }; } })', context);
  worker.prepare(decoder);
  const timing = { samples: [{ start: 0, end: 3, origin: captureOrigin, rate: 1.2 }],
    sentAt: 110000, position: captureOrigin + 1000, toleranceMilliseconds: 100 };
  await worker.fragment(new ArrayBuffer(8), timing);
  assert.deepEqual(waits, [200]); assert.equal(decoder.rendered, 1); assert.equal(messages.at(-1).mediaTime, 1);
  time = 10500; available = 1;
  await worker.fragment(new ArrayBuffer(8), timing);
  assert.equal(decoder.rendered, 1); assert.equal(messages.at(-1).type, 'drop');
  time = 11000; available = 1; pts = 2000000;
  await worker.fragment(new ArrayBuffer(8), { ...timing, sentAt: 111000, position: captureOrigin + 1800 });
  assert.equal(decoder.rendered, 2); assert.equal(time, 11600); assert.equal(messages.at(-1).type, 'frame');
  time = 12000; available = 1; pts = 3000000;
  await worker.fragment(new ArrayBuffer(8), { ...timing, sentAt: 112000, position: captureOrigin + 3000, rate: 1.25 });
  assert.ok(Math.abs(waits.at(-1) - 480) < 1e-6, 'Software display follows the shared catch-up speed as well as the calibrated source pace.');
  assert.equal(decoder.rendered, 3);
  time = 12600; available = 1;
  await worker.fragment(new ArrayBuffer(8), { ...timing, sentAt: 112000, position: captureOrigin + 3000, rate: 1.25 });
  assert.equal(decoder.rendered, 3); assert.equal(messages.at(-1).type, 'drop');
});
test('native initialization does not await autoplay before receiving its first media fragment', async () => {
  const h = harness(), player = new h.NativeCameraVideo(h.tile, () => {});
  await player.initialize({ mimeType: 'video/mp4; codecs="avc1.640028"' });
  await player.append(new ArrayBuffer(12)); await player.fragment(new ArrayBuffer(8));
  assert.equal(player.buffer.appended.length, 2); assert.ok(h.tile.root.classList.contains('grid-media-ready'));
  player.stop(); assert.equal(h.frames.size, 0); assert.ok(player.video.removed);
});
test('stopping native playback releases pending initialization and ignores late video errors', async () => {
  const h = harness(); h.context.URL.createObjectURL = () => 'blob:delayed';
  const player = new h.NativeCameraVideo(h.tile, () => {}); let failures = 0; player.failure = () => failures++;
  const ready = player.initialize({ mimeType: 'video/mp4' }); player.stop(); await ready;
  assert.equal(player.buffer, null); assert.equal(failures, 0);
});
test('transport acknowledges only after a fragment finishes decoding and cancels stale completions', async () => {
  const h = harness(); let finish, resets = 0; const failures = [];
  const consumer = { initialize: async () => {}, fragment: () => new Promise(resolve => finish = resolve), reset: () => resets++ };
  const transport = new h.CameraVideoTransport(257, consumer, error => failures.push(error), () => {}, { maximumQueuedBytes: 100 });
  const socket = h.sockets[0]; assert.match(socket.url, /257\/video/);
  socket.onmessage({ data: JSON.stringify({ type: 'init', mimeType: 'video/mp4' }) }); socket.onmessage({ data: new ArrayBuffer(20) }); await tick();
  assert.deepEqual(socket.sent, ['ok']); socket.onmessage({ data: new ArrayBuffer(30) }); await tick(); assert.equal(socket.sent.length, 1);
  transport.stop(); finish(); await tick(); assert.equal(socket.sent.length, 1); assert.ok(socket.closed); assert.equal(failures.length, 0);
});
test('malformed commands and resource overruns reconnect without disqualifying a decoder or retaining compressed data', () => {
  for (const data of ['not json', new ArrayBuffer(6)]) {
    const h = harness(), failures = [], states = [], consumer = { reset() {} };
    const transport = new h.CameraVideoTransport(1, consumer, error => failures.push(error), state => states.push(state), { maximumQueuedBytes: 5 });
    h.sockets[0].onmessage({ data });
    assert.equal(failures.length, 0); assert.equal(h.sockets[0].closed, true);
    assert.equal(states.at(-1).reason, typeof data === 'string' ? 'protocol-error' : 'client-backpressure');
    transport.stop(); assert.equal(h.timers.size, 0);
  }
});
test('software worker failures reject requests, and disposal releases pending work and the canvas', async () => {
  const h = harness(), player = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  const pending = player.initialize({}, new ArrayBuffer(4));
  const id = h.workers[0].messages.at(-1).id;
  h.workers[0].onmessage({ data: { type: 'error', id, message: 'Decoder unavailable' } });
  await assert.rejects(pending, /unavailable/);
  const cancelled = player.fragment(new ArrayBuffer(4)); player.stop(); await cancelled;
  assert.ok(h.workers[0].stopped); assert.ok(player.canvas.removed); assert.equal(player.requests.size, 0);
});
test('automatic and forced modes handle failures differently, and suspension disposes all decoding', () => {
  const h = harness(); let changed = 0;
  const automatic = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => changed++);
  automatic.fail('Native unsupported'); assert.equal(automatic.method, 'browser'); assert.ok(h.sockets[0].closed);
  automatic.fail('Software unsupported'); assert.equal(automatic.method, 'server'); assert.match(automatic.message, /server CPU/);
  automatic.suspend(true); assert.equal(automatic.player, null); automatic.stop();
  const forced = new h.CameraPlayback(2, h.tile, 'native', { maximumQueuedBytes: 100 }, () => {}); forced.fail('Codec unsupported');
  assert.equal(forced.method, null); assert.match(forced.message, /unavailable/); forced.stop(); assert.ok(changed >= 3);
});

for (const method of ['native', 'browser']) {
  test(`forced ${method} creates fresh decoders on the feed retry schedule and resets after healthy progress`, async () => {
    const h = harness(), playback = new h.CameraPlayback(1, h.tile, method,
      { maximumQueuedBytes: 100, retryDelaysSeconds: '2, 5' }, () => {});
    const fail = () => method === 'native' ? playback.player.failure('Decoder failed') : playback.player.worker.onerror();
    const start = async () => {
      if (method === 'native') await playback.player.initialize({ mimeType: 'video/mp4' }, new ArrayBuffer(8));
    };
    const picture = mediaTime => {
      if (method === 'native') {
        playback.player.video.currentTime = mediaTime;
        playback.player.video.ontimeupdate();
      } else playback.player.worker.onmessage({ data: { type: 'frame', width: 64, height: 48, mediaTime, decodeMilliseconds: 1 } });
    };
    for (const [index, seconds] of [2, 5, 5].entries()) {
      const old = playback.player;
      fail();
      assert.equal(playback.method, null);
      assert.match(playback.connectionMessage, new RegExp(`in ${seconds} seconds`));
      assert.ok(h.sockets[index].closed);
      h.advance(seconds * 1000 - 1);
      assert.equal(playback.player, null);
      h.advance(1);
      assert.equal(playback.method, method);
      assert.notEqual(playback.player, old);
    }
    await start();
    for (let second = 1; second <= 31; second++) { picture(second); h.advance(1000); }
    assert.equal(playback.confirmed, true);
    fail();
    assert.match(playback.connectionMessage, /in 2 seconds/);
    playback.stop();
    h.advance(60000);
    assert.equal(h.sockets.length, 4);
    assert.equal(h.timers.size, 0);
  });
}

function jpegPicture(h, playback) {
  h.tile.lastFrame = h.context.performance.now();
  playback.frame(1);
}

function advanceJpeg(h, playback, seconds) {
  for (let second = 0; second < seconds; second++) {
    jpegPicture(h, playback);
    h.advance(1000);
  }
}

test('Automatic keeps JPEG active while probing native, then promotes the already playing decoder', async () => {
  const h = harness(), changes = [], overlayTimes = [];
  h.tile.detections = { setPlaybackTime: time => overlayTimes.push(time) };
  const playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => changes.push(h.tile.playback?.method));
  h.tile.playback = playback;
  playback.fail('Native decoder failed');
  playback.fail('Software decoder failed');
  const calls = changes.length, overlayCalls = overlayTimes.length;
  advanceJpeg(h, playback, 29);
  assert.equal(h.sockets.length, 2);
  advanceJpeg(h, playback, 1);
  const probe = playback.probe.player;
  assert.equal(probe.video.className, 'camera-decoder-probe');
  assert.equal(probe.video.disablePictureInPicture, true);
  assert.equal(playback.player, null);
  await probe.initialize({ mimeType: 'video/mp4; codecs="avc1.42001F"' }, new ArrayBuffer(8));
  probe.video.currentTime = .5; probe.video.ontimeupdate();
  advanceJpeg(h, playback, 2);
  probe.video.ontimeupdate();
  assert.equal(changes.length, calls);
  assert.equal(overlayTimes.length, overlayCalls);
  assert.equal(playback.method, 'server');
  assert.equal(h.tile.root.classList.contains('grid-media-ready'), false);
  probe.video.currentTime = 2.5; probe.video.ontimeupdate();
  assert.equal(playback.method, 'native');
  assert.equal(playback.player, probe);
  assert.equal(probe.video.className, 'grid-camera-video');
  assert.equal(h.tile.root.classList.contains('grid-media-ready'), true);
  assert.equal(changes.at(-1), 'native');
  assert.equal(h.CameraPlayback.reports.get(1).videoCodec, 'H.264');
  assert.equal(h.timers.size, 0, 'A lower-ranked failed decoder must not be probed after native recovers.');
  playback.stop();
});

test('failed and timed-out probes back off through 30, 60, 120 and repeated 300 second delays', () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  playback.fail('Native decoder failed');
  playback.fail('Software decoder failed');
  for (const [index, seconds] of [30, 60, 120, 300, 300].entries()) {
    const before = h.sockets.length;
    advanceJpeg(h, playback, seconds - 1);
    assert.equal(h.sockets.length, before);
    advanceJpeg(h, playback, 1);
    assert.equal(h.sockets.length, before + 1);
    const probe = playback.probe.player;
    assert.equal(playback.probe.method, index % 2 ? 'browser' : 'native');
    if (index % 2) probe.worker.onerror();
    else advanceJpeg(h, playback, 12);
    assert.equal(playback.probe, null);
    assert.equal(playback.method, 'server');
    assert.ok(h.sockets.at(-1).closed);
    assert.ok((probe.video || probe.canvas).removed);
  }
  playback.stop();
  assert.equal(h.timers.size, 0);
});

test('a software probe can replace JPEG and still leaves native eligible for a later upgrade', () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  playback.fail('Native decoder failed');
  playback.fail('Software decoder failed');
  advanceJpeg(h, playback, 30);
  playback.probe.player.failure('Native still fails');
  advanceJpeg(h, playback, 60);
  const probe = playback.probe.player;
  const picture = mediaTime => probe.worker.onmessage({ data: { type: 'frame', width: 64, height: 48, mediaTime, decodeMilliseconds: 1 } });
  picture(1); advanceJpeg(h, playback, 2); picture(3);
  assert.equal(playback.method, 'browser');
  assert.equal(playback.player, probe);
  assert.equal(playback.retryCandidate(), 'native');
  assert.equal(probe.canvas.className, 'grid-camera-canvas');
  playback.stop();
});

test('losing fresh fallback pictures uses the configured blank-tile delay', () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic',
    { maximumQueuedBytes: 100, retryDelaysSeconds: '2, 5' }, () => {});
  playback.fail('Native decoder failed');
  playback.fail('Software decoder failed');
  jpegPicture(h, playback);
  h.advance(4000);
  playback.updateMethodRetry();
  h.advance(1999);
  assert.equal(playback.probe, null);
  h.advance(1);
  assert.equal(playback.probe.method, 'native');
  playback.stop();
  assert.equal(h.timers.size, 0);
});

test('a failed native probe leaves active software video alone and suspension cancels all pending work', async () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  playback.fail('Native decoder failed');
  const fallback = playback.player, fallbackSocket = h.sockets.at(-1);
  const picture = () => fallback.worker.onmessage({ data: { type: 'frame', width: 64, height: 48, decodeMilliseconds: 1 } });
  for (let second = 0; second < 30; second++) { picture(); h.advance(1000); }
  const probe = playback.probe.player, fail = probe.failure;
  await probe.initialize({ mimeType: 'video/mp4' }, new ArrayBuffer(8));
  const lastFrame = h.tile.lastFrame;
  probe.video.currentTime = 1; probe.video.ontimeupdate();
  assert.equal(h.tile.lastFrame, lastFrame);
  fail('Test failure');
  assert.equal(playback.player, fallback);
  assert.equal(fallbackSocket.closed, undefined);
  assert.equal(h.tile.root.classList.contains('grid-media-ready'), true);
  for (let second = 0; second < 60; second++) { picture(); h.advance(1000); }
  const secondProbe = playback.probe.player;
  const lateFailure = secondProbe.failure;
  playback.suspend(true);
  lateFailure('Late failure from abandoned probe');
  h.advance(600000);
  assert.equal(playback.player, null);
  assert.equal(playback.probe, null);
  assert.ok(secondProbe.video.removed);
  assert.ok(fallback.worker.stopped);
  assert.equal(h.timers.size, 0);
  playback.stop();
});

test('native MIME rejection stops timed retries until the active software stream changes codec', async () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  const mimeType = 'video/mp4; codecs="hvc1.1.6.L93.B0"';
  h.context.window.MediaSource.isTypeSupported = mime => mime !== mimeType;
  h.sockets[0].onmessage({ data: JSON.stringify({ type: 'init', mimeType }) });
  h.sockets[0].onmessage({ data: new ArrayBuffer(8) }); await tick();
  assert.equal(playback.method, 'browser');
  assert.equal(playback.retryCandidate(), undefined);
  assert.equal(h.timers.size, 0);
  const fallback = playback.player;
  const init = fallback.initialize({ mimeType: 'video/mp4; codecs="avc1.42001F"' }, new ArrayBuffer(8));
  const id = fallback.worker.messages.at(-1).id;
  fallback.worker.onmessage({ data: { type: 'done', id } }); await init;
  assert.equal(playback.retryCandidate(), 'native');
  playback.stop();
});

test('native buffer cleanup batches an existing retention window instead of blocking every fragment on eviction', async () => {
  const h = harness(), player = new h.NativeCameraVideo(h.tile, () => {}), removals = [];
  await player.initialize({ mimeType: 'video/mp4' });
  player.synchronize = () => false;
  let start = 0;
  player.buffer.buffered = { length: 1, start: () => start, end: () => 40 };
  player.video.buffered = player.buffer.buffered;
  player.buffer.remove = (from, to) => {
    removals.push([from, to]); start = to;
    queueMicrotask(() => player.buffer.dispatchEvent(new Event('updateend')));
  };
  for (const time of [10, 11, 12, 13, 14, 15]) { player.video.currentTime = time; await player.fragment(new ArrayBuffer(8)); }
  assert.equal(removals.length, 0, 'Ordinary appends must not each wait for slow native buffer eviction.');
  player.video.currentTime = 16; await player.fragment(new ArrayBuffer(8));
  assert.deepEqual(removals, [[0, 8]]);
  start = 0; // A long GOP can keep older keyframe dependencies after an eviction request.
  for (const time of [17, 18, 19, 20, 21, 22, 23]) { player.video.currentTime = time; await player.fragment(new ArrayBuffer(8)); }
  assert.equal(removals.length, 1);
  player.video.currentTime = 24; await player.fragment(new ArrayBuffer(8));
  assert.deepEqual(removals, [[0, 8], [0, 16]]); player.reset();
  assert.equal(player.prunedThrough, undefined); player.stop();
});

test('native reconnects retain a canvas picture until the timeout or a fresh frame', async () => {
  const h = harness(), player = new h.NativeCameraVideo(h.tile, () => {});
  await player.initialize({ mimeType: 'video/mp4' }); await player.fragment(new ArrayBuffer(8));
  player.reset(); assert.ok(player.frozen); assert.ok(h.tile.root.classList.contains('grid-media-ready'));
  player.expire(); assert.equal(player.frozen, null); assert.equal(h.tile.root.classList.contains('grid-media-ready'), false); player.stop();
});
test('software reconnect keeps its last picture without retaining its decoder on suspension', () => {
  const h = harness(), player = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  h.workers[0].onmessage({ data: { type: 'frame', width: 640, height: 360, decodeMilliseconds: 2 } });
  player.reset(); assert.ok(h.tile.root.classList.contains('grid-media-ready'));
  assert.equal(player.stop(true), player.canvas); assert.ok(h.workers[0].stopped); assert.ok(!player.canvas.removed);
  player.expire(); assert.equal(h.tile.root.classList.contains('grid-media-ready'), false);
});

test('software presentation clock recovers after a network pause instead of dropping every future picture', async () => {
  let time = 0, available = 1, pts = 0, rendered = 0;
  const messages = [], memory = new ArrayBuffer(64);
  const decoder = { HEAPU8: new Uint8Array(memory), _malloc: () => 4, _free() {}, _camera_push: () => 0,
    _camera_next: () => available-- > 0 ? 1 : 0, _camera_pts: () => pts,
    _camera_width: () => 2, _camera_height: () => 2, _camera_pixels: () => 16 };
  const context = { importScripts() {}, self: {}, CameraMp4: { packets: () => [{ data: new Uint8Array(1), pts }] },
    performance: { now: () => time }, postMessage: message => messages.push(message),
    ImageData: class {}, setTimeout: (fn, milliseconds) => { time += milliseconds; fn(); } };
  const worker = runInNewContext(readFileSync('src/SecurityCameraViewer.Web/wwwroot/camera-decoder-worker.js', 'utf8') +
    '\n({ fragment, prepare(m) { module = m; decoder = 1; video = {}; canvas = { width: 2, height: 2 }; context = { putImageData() { ++m.rendered; } }; } })', context);
  decoder.rendered = 0; worker.prepare(decoder);
  await worker.fragment(new ArrayBuffer(8)); assert.equal(decoder.rendered, 1);
  pts = 100000; time = 5000; available = 1;
  await worker.fragment(new ArrayBuffer(8)); assert.equal(decoder.rendered, 1); assert.equal(messages[0].type, 'frame'); assert.equal(messages.at(-1).type, 'drop');
  pts = 200000; available = 1;
  await worker.fragment(new ArrayBuffer(8)); assert.equal(decoder.rendered, 2); assert.equal(messages.at(-1).type, 'frame');
});

test('native overlays use compositor presentation time and reset before reconnect footage', async () => {
  const h = harness(), times = [];
  h.tile.detections = { setPlaybackTime: time => times.push(time) };
  const player = new h.NativeCameraVideo(h.tile, () => {});
  player.video.requestVideoFrameCallback = callback => { h.frames.set(1, callback); return 1; };
  player.video.cancelVideoFrameCallback = id => h.frames.delete(id);
  await player.initialize({ mimeType: 'video/mp4' });
  await player.fragment(new ArrayBuffer(8), { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  player.video.currentTime = .5;
  h.frames.get(1)(1000, { mediaTime: .2 });
  assert.equal(times.at(-1), Date.parse('2026-10-10T00:00:00.200Z'));
  player.reset(); assert.equal(times.at(-1), null); assert.equal(player.clock.time(.2), null);
  player.stop();
});

test('a final paused native compositor frame updates overlays while animation polling leaves them held', async () => {
  const h = harness(), times = [];
  h.tile.detections = { setPlaybackTime: time => times.push(time) };
  const player = new h.NativeCameraVideo(h.tile, () => {});
  player.video.requestVideoFrameCallback = callback => { h.frames.set(1000, callback); return 1000; };
  player.video.cancelVideoFrameCallback = id => h.frames.delete(id);
  await player.initialize({ mimeType: 'video/mp4' });
  await player.fragment(new ArrayBuffer(8), { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  player.synchronize = () => false;
  const compositor = h.frames.get(player.callback); h.frames.delete(player.callback);
  player.video.pause(); player.video.currentTime = .5;
  compositor(1000, { mediaTime: .2 });
  assert.equal(times.at(-1), Date.parse('2026-10-10T00:00:00.200Z'));
  const animation = h.frames.get(player.callback); h.frames.delete(player.callback);
  player.video.currentTime = .6; animation(1016);
  assert.equal(times.at(-1), Date.parse('2026-10-10T00:00:00.200Z'));
  player.stop(); assert.equal(h.frames.size, 0);
});

test('software overlays use decoded presentation timestamps rather than fragment receipt', async () => {
  const h = harness(), times = []; h.tile.detections = { setPlaybackTime: time => times.push(time) };
  const player = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  const pending = player.fragment(new ArrayBuffer(8), { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  h.workers[0].onmessage({ data: { type: 'frame', width: 640, height: 360, decodeMilliseconds: 2, mediaTime: .4 } });
  assert.equal(times.at(-1), Date.parse('2026-10-10T00:00:00.400Z'));
  player.reset(); assert.equal(times.at(-1), null);
  player.stop(); await pending;
});

test('fragment metadata travels with its binary payload and acknowledgements remain single-flight', async () => {
  const h = harness(), observed = [];
  const transport = new h.CameraVideoTransport(1, { fragment: async (bytes, timing) => observed.push([bytes.byteLength, timing]), reset() {} },
    error => assert.fail(error), () => {}, { maximumQueuedBytes: 100 });
  const metadata = { type: 'fragment', streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' };
  const socket = h.sockets[0]; socket.onmessage({ data: JSON.stringify(metadata) });
  assert.equal(socket.sent.length, 0);
  socket.onmessage({ data: new ArrayBuffer(20) }); await tick();
  assert.equal(observed[0][0], 20); assert.deepEqual(JSON.parse(JSON.stringify(observed[0][1])), metadata); assert.deepEqual(socket.sent, ['ok']);
  transport.stop();
});

test('focused audio video keeps ownership of overlay timing while grid playback resets or falls back', async () => {
  const h = harness(), times = [];
  h.tile.detections = { setPlaybackTime: time => times.push(time) };
  h.tile.root.classList.add('live-media-ready');
  const native = new h.NativeCameraVideo(h.tile, () => {});
  await native.initialize({ mimeType: 'video/mp4' }); await native.fragment(new ArrayBuffer(8));
  native.reset(); native.stop();
  const software = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  h.workers[0].onmessage({ data: { type: 'frame', width: 640, height: 360, decodeMilliseconds: 2, mediaTime: .4 } });
  software.reset(); software.stop();
  const playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  playback.fail('Native unavailable'); playback.fail('Browser unavailable'); playback.stop();
  assert.equal(times.length, 0);
});

test('native grid labels and benchmark reports show the actual stream codec and profile', async () => {
  for (const [codec, label] of [['avc1.640028', 'H.264'], ['hvc1.1.6.L93.B0', 'H.265']]) {
    const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
    const socket = h.sockets[0], mimeType = `video/mp4; codecs="${codec}"`;
    assert.equal(h.tile.playbackStatus.textContent, 'Auto: Checking Native video');
    socket.onmessage({ data: JSON.stringify({ type: 'init', mimeType }) });
    socket.onmessage({ data: new ArrayBuffer(12) }); await tick();
    assert.equal(h.tile.playbackStatus.textContent, `Auto: Checking Native video · ${label}`);
    socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
    assert.equal(h.tile.playbackStatus.textContent, `Auto: Native video · ${label}`);
    assert.ok(h.tile.playbackStatus.title.includes(mimeType));
    const report = h.CameraPlayback.reports.get(1);
    assert.equal(report.mimeType, mimeType); assert.equal(report.videoCodec, label); assert.equal(report.audioCodec, null);
    playback.stop();
  }
});

test('software decoding keeps its codec and CPU warning, and JPEG fallback clears old encoded metadata', async () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'automatic', { maximumQueuedBytes: 100 }, () => {});
  playback.fail('Native unavailable');
  const init = playback.player.initialize({ mimeType: 'video/mp4; codecs="hev1.1.6.L93.B0"' }, new ArrayBuffer(8));
  const id = h.workers[0].messages.at(-1).id;
  h.workers[0].onmessage({ data: { type: 'done', id } }); await init;
  h.workers[0].onmessage({ data: { type: 'frame', width: 640, height: 360, decodeMilliseconds: 2 } });
  assert.equal(h.tile.playbackStatus.textContent, 'Auto: Browser software decoding · H.265 · client CPU');
  playback.fail('Software unavailable');
  assert.equal(h.tile.playbackStatus.textContent, 'Auto: Server JPEG previews · server CPU');
  assert.equal(h.CameraPlayback.reports.get(1).mimeType, null);
  assert.equal(h.CameraPlayback.reports.get(1).videoCodec, 'JPEG');
  assert.equal(h.tile.playbackStatus.title.includes('hev1'), false); playback.stop();
});

test('grid reconnection and camera waiting clear stale codecs before accepting a changed stream', async () => {
  for (const state of ['connecting', 'waiting', 'retrying', 'close']) {
    const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'native', { maximumQueuedBytes: 100 }, () => {});
    await playback.player.initialize({ mimeType: 'video/mp4; codecs="hvc1.1.6.L93.B0"' }, new ArrayBuffer(8));
    const socket = h.sockets[0];
    if (state === 'close') socket.onclose();
    else socket.onmessage({ data: JSON.stringify({ type: 'state', status: state, message: 'Waiting' }) });
    assert.match(h.tile.playbackStatus.textContent, /^Checking Native video/);
    if (state === 'retrying' || state === 'close') assert.match(h.tile.playbackStatus.textContent, state === 'close' ? /Reconnecting in 1 second/ : /Waiting/);
    assert.equal(h.CameraPlayback.reports.get(1).videoCodec, null);
    await playback.player.initialize({ mimeType: 'video/mp4; codecs="avc3.640028"' }, new ArrayBuffer(8));
    assert.match(h.tile.playbackStatus.textContent, /^Checking Native video · H\.264/); playback.stop();
  }
});

test('unrecognized or unsupported native codec metadata preserves clear playback status without guessing', async () => {
  const h = harness(), playback = new h.CameraPlayback(1, h.tile, 'native', { maximumQueuedBytes: 100 }, () => {});
  await playback.player.initialize({ mimeType: 'video/mp4; codecs="unknown"' }, new ArrayBuffer(8));
  assert.equal(h.tile.playbackStatus.textContent, 'Checking Native video');
  assert.equal(h.CameraPlayback.reports.get(1).videoCodec, null);
  h.context.window.MediaSource.isTypeSupported = () => false;
  const socket = h.sockets[0];
  socket.onmessage({ data: JSON.stringify({ type: 'init', mimeType: 'video/mp4; codecs="hvc1.1.6.L93.B0"' }) });
  socket.onmessage({ data: new ArrayBuffer(8) }); await tick();
  assert.equal(h.tile.playbackStatus.textContent, 'Playback unavailable');
  assert.equal(h.CameraPlayback.reports.get(1).mimeType, null); playback.stop();
});

test('Listen labels follow delivered video and audio instead of the hidden grid, then restore the grid codec', async () => {
  const h = harness();
  const playback = h.tile.playback = new h.CameraPlayback(1, h.tile, 'native', { maximumQueuedBytes: 100 }, () => {});
  await playback.player.initialize({ mimeType: 'video/mp4; codecs="hvc1.1.6.L93.B0"' }, new ArrayBuffer(8));
  const live = new h.LiveCameraPlayer(() => {}); live.start(1, h.tile); await tick();
  const mimeType = 'video/mp4; codecs="avc1.42001F,mp4a.40.2"';
  await live.receive(JSON.stringify({ type: 'init', mimeType, hasAudio: true }), live.version, live.socket);
  assert.equal(h.tile.playbackStatus.textContent, 'Checking Native video · H.265');
  live.video.onplaying();
  assert.equal(h.tile.playbackStatus.textContent, 'Native video · H.264');
  assert.ok(h.tile.playbackStatus.title.includes(mimeType));
  assert.equal(h.CameraPlayback.reports.get(1).videoCodec, 'H.264');
  assert.equal(h.CameraPlayback.reports.get(1).audioCodec, 'AAC');
  live.setMuted(true); assert.equal(h.tile.playbackStatus.textContent, 'Native video · H.264');
  const playing = live.video.onplaying;
  live.stop(); playing();
  assert.equal(h.tile.playbackStatus.textContent, 'Checking Native video · H.265');
  assert.equal(h.CameraPlayback.reports.get(1).audioCodec, null); playback.stop();
});

test('Listen reconnection, silent streams and failures clear stale audio descriptions', async () => {
  for (const ending of ['retrying', 'no-audio', 'unsupported', 'error']) {
    const h = harness();
    const playback = h.tile.playback = new h.CameraPlayback(1, h.tile, 'server', { maximumQueuedBytes: 100 }, () => {});
    const live = new h.LiveCameraPlayer(() => {}); live.start(1, h.tile); await tick();
    const init = { type: 'init', mimeType: 'video/mp4; codecs="avc1.42001F,mp4a.40.2"', hasAudio: true };
    await live.receive(JSON.stringify(init), live.version, live.socket); live.video.onplaying();
    assert.equal(h.tile.playbackStatus.textContent, 'Native video · H.264');
    if (ending === 'retrying') {
      await live.receive(JSON.stringify({ type: 'state', status: 'retrying', message: 'Retrying' }), live.version, live.socket);
      assert.equal(h.tile.playbackStatus.textContent, 'Server JPEG previews · server CPU');
    } else if (ending === 'no-audio') {
      await live.receive(JSON.stringify({ ...init, mimeType: 'video/mp4; codecs="hvc1.1.6.L93.B0"', hasAudio: false }), live.version, live.socket);
      live.video.onplaying();
      assert.equal(h.tile.playbackStatus.textContent, 'Native video · H.265');
    } else if (ending === 'unsupported') {
      h.context.window.MediaSource.isTypeSupported = () => false;
      await live.receive(JSON.stringify(init), live.version, live.socket);
      assert.equal(h.tile.playbackStatus.textContent, 'Server JPEG previews · server CPU');
    } else {
      live.video.onerror(); assert.equal(h.tile.playbackStatus.textContent, 'Server JPEG previews · server CPU');
    }
    assert.equal(h.CameraPlayback.reports.get(1).audioCodec, null); live.stop(); playback.stop();
  }
});

test('missing Listen codec metadata never borrows the hidden grid codec or invents an audio type', async () => {
  const h = harness();
  const playback = h.tile.playback = new h.CameraPlayback(1, h.tile, 'native', { maximumQueuedBytes: 100 }, () => {});
  await playback.player.initialize({ mimeType: 'video/mp4; codecs="hvc1.1.6.L93.B0"' }, new ArrayBuffer(8));
  for (const mimeType of [null, 'video/mp4', 'video/mp4; codecs="unknown"']) {
    playback.setLiveMedia({ mimeType, hasAudio: true });
    assert.equal(h.tile.playbackStatus.textContent, 'Native video');
    assert.equal(h.CameraPlayback.reports.get(1).videoCodec, null);
    assert.equal(h.CameraPlayback.reports.get(1).audioCodec, null);
    assert.equal(h.tile.playbackStatus.title.includes('hvc1'), false);
  }
  playback.setLiveMedia(null);
  assert.equal(h.tile.playbackStatus.textContent, 'Checking Native video · H.265'); playback.stop();
  playback.setLiveMedia({ mimeType: 'video/mp4; codecs="avc1.42001F,mp4a.40.2"', hasAudio: true });
  assert.equal(h.CameraPlayback.reports.has(1), false);
});

test('native time updates keep background video visible without inflating frame statistics', async () => {
  const h = harness(); let activities = 0, frames = 0;
  const player = new h.NativeCameraVideo(h.tile, () => frames++, () => activities++);
  assert.equal(player.video.disablePictureInPicture, true, 'Loading videos start ineligible for PiP.');
  await player.initialize({ mimeType: 'video/mp4' });
  player.video.currentTime = .5; player.video.ontimeupdate();
  assert.equal(activities, 1); assert.equal(frames, 0);
  assert.equal(h.tile.root.classList.contains('grid-media-ready'), true);
  player.video.ontimeupdate(); assert.equal(activities, 1, 'A stalled clock is not fresh video.');
  player.expire(); player.video.currentTime = 1; player.video.ontimeupdate();
  assert.equal(activities, 2); assert.equal(h.tile.root.classList.contains('grid-media-ready'), true);
  player.stop();
  player.video.paused = false; player.video.currentTime = 2; player.video.ontimeupdate();
  assert.equal(activities, 2, 'Late media events cannot revive a stopped player.');
});

for (const [name, state] of [['paused', { paused: true }], ['seeking', { seeking: true }], ['not decoded', { readyState: 1 }]]) {
  test('native time updates do not mark ' + name + ' footage as fresh', async () => {
    const h = harness(); let activities = 0;
    const player = new h.NativeCameraVideo(h.tile, () => {}, () => activities++);
    await player.initialize({ mimeType: 'video/mp4' });
    Object.assign(player.video, state); player.video.currentTime = .5; player.video.ontimeupdate();
    assert.equal(activities, 0); assert.equal(h.tile.root.classList.contains('grid-media-ready'), false);
    player.stop();
  });
}

test('native progress clears retained reconnect pixels and restarts after a media reset', async () => {
  const h = harness(); let activities = 0;
  const player = new h.NativeCameraVideo(h.tile, () => {}, () => activities++);
  await player.initialize({ mimeType: 'video/mp4' });
  player.video.currentTime = .5; player.video.ontimeupdate(); player.reset();
  const frozen = player.frozen;
  assert.ok(frozen); assert.equal(player.video.style.visibility, 'hidden');
  player.video.paused = false; player.video.ontimeupdate(); assert.equal(activities, 1);
  await player.initialize({ mimeType: 'video/mp4' });
  player.video.currentTime = .5; player.video.ontimeupdate();
  assert.equal(activities, 2); assert.equal(frozen.removed, true); assert.equal(player.frozen, null);
  assert.equal(player.video.style.visibility, ''); player.stop();
});

test('background native progress refreshes the camera timeout independently of compositor statistics', async () => {
  const h = harness(); let now = 1000; h.context.performance.now = () => now;
  const playback = new h.CameraPlayback(1, h.tile, 'native', { maximumQueuedBytes: 1024 }, () => {});
  await playback.player.initialize({ mimeType: 'video/mp4' }, new ArrayBuffer(8));
  const frames = playback.stats.snapshot().frames;
  now = 5000; playback.player.video.currentTime = .5; playback.player.video.ontimeupdate();
  assert.equal(h.tile.lastFrame, 5000); assert.equal(playback.stats.snapshot().frames, frames);
  now = 9000; playback.player.video.ontimeupdate(); assert.equal(h.tile.lastFrame, 5000);
  playback.stop();
});

test('software fragments carry the current shared catch-up pace into the decoder worker', async () => {
  const h = harness(), player = new h.SoftwareCameraVideo(h.tile, () => {}, () => {});
  h.LiveCameraClock.shared.update = () => 1000;
  h.LiveCameraClock.shared.rate = 1.1;
  const pending = player.fragment(new ArrayBuffer(4), { streamId: 'first', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  const message = h.workers[0].messages.at(-1);
  assert.equal(message.timing.rate, 1.1);
  h.workers[0].onmessage({ data: { type: 'done', id: message.id } }); await pending;
  player.stop();
});

test('software pictures refresh display geometry only when decoded dimensions change', () => {
  const h = harness(); let refreshes = 0;
  h.tile.zoom = { refresh() { refreshes++; } };
  const player = new h.SoftwareCameraVideo(h.tile, () => {}, () => {}), worker = h.workers[0];
  worker.onmessage({ data: { type: 'frame', width: 640, height: 480 } }); assert.equal(refreshes, 1);
  worker.onmessage({ data: { type: 'frame', width: 640, height: 480 } }); assert.equal(refreshes, 1);
  worker.onmessage({ data: { type: 'frame', width: 1280, height: 720 } }); assert.equal(refreshes, 2);
  player.stop();
});
