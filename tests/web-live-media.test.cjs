const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const source = ['camera-frame-clock.js', 'live-media.js'].map(file => readFileSync('src/SecurityCameraViewer.Web/wwwroot/' + file, 'utf8')).join('\n');

function setup() {
  const callbacks = new Map(), times = [], classes = new Set(['live-media-ready']); let next = 0;
  const schedule = callback => { const id = ++next; callbacks.set(id, (...args) => { callbacks.delete(id); callback(...args); }); return id; };
  const video = { paused: false, readyState: 2, currentTime: .8, buffered: { length: 1, start: () => 0, end: () => 1 },
    requestVideoFrameCallback: schedule, cancelVideoFrameCallback(id) { callbacks.delete(id); },
    pause() { this.paused = true; }, removeAttribute() {}, load() {} };
  const sandbox = { performance: { now: () => 1000 }, clearTimeout() {}, clearInterval() {},
    cancelAnimationFrame: id => callbacks.delete(id), requestAnimationFrame: schedule,
    URL: { revokeObjectURL() {} }, WebSocket: { OPEN: 1 } };
  const Player = runInNewContext(source + '\nLiveCameraPlayer', sandbox);
  const player = new Player(() => {}); player.video = video;
  player.tile = { root: { classList: { contains: value => classes.has(value), remove: value => classes.delete(value) } },
    detections: { setPlaybackTime: time => times.push(time) } };
  player.version = 1; player.socket = { readyState: 1 }; player.status = 'playing';
  player.buffer = { buffered: { length: 0 } }; player.update = async (_, action) => action(); player.buffer.appendBuffer = () => {};
  return { player, video, times, callbacks, classes, sandbox };
}

test('focused audio video pairs fragment timing with bytes and selects compositor frame time', async () => {
  const s = setup();
  await s.player.receive(JSON.stringify({ type: 'fragment', streamId: 'audio', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' }), 1, s.player.socket);
  await s.player.receive(new ArrayBuffer(8), 1, s.player.socket);
  s.callbacks.get(s.player.callback)(1000, { mediaTime: .3 });
  assert.equal(s.times.at(-1), Date.parse('2026-10-10T00:00:00.300Z'));
  const oldPaint = s.callbacks.get(s.player.callback);
  s.player.resetMedia(); assert.equal(s.times.at(-1), null); assert.equal(s.callbacks.size, 0);
  s.video.paused = false; s.classes.add('live-media-ready'); s.player.paint();
  const before = s.callbacks.size; oldPaint(1000, { mediaTime: .5 }); assert.equal(s.callbacks.size, before);
});

test('held focused sound resumes without a new fragment and releases animation callbacks on reset', () => {
  const s = setup(); let now = 1000, compositorCalls = 0;
  s.sandbox.performance.now = () => now;
  const scheduleCompositor = s.video.requestVideoFrameCallback;
  s.video.requestVideoFrameCallback = callback => { compositorCalls++; return scheduleCompositor(callback); };
  s.player.play = () => { s.video.paused = false; };
  s.video.currentTime = .5;
  s.player.clock.update({ streamId: 'audio', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-11T11:00:01Z' });
  s.player.paint(); assert.equal(s.video.paused, true); assert.equal(compositorCalls, 0);
  now += 500; s.callbacks.get(s.player.callback)();
  assert.equal(s.video.paused, false); assert.equal(s.video.currentTime, .5); assert.equal(compositorCalls, 1);
  s.player.resetMedia(); assert.equal(s.callbacks.size, 0);
  s.player.clock.update({ streamId: 'next', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-11T11:00:03Z' });
  s.video.currentTime = .5; s.player.paint(); assert.equal(s.video.paused, true);
  s.player.resetMedia(); assert.equal(s.callbacks.size, 0);
});

test('a final paused focused compositor frame updates overlays while animation polling leaves them held', () => {
  const s = setup();
  s.player.clock.update({ streamId: 'audio', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  s.player.synchronize = () => false; s.player.paint();
  const compositor = s.callbacks.get(s.player.callback);
  s.video.pause(); compositor(1000, { mediaTime: .2 });
  assert.equal(s.times.at(-1), Date.parse('2026-10-10T00:00:00.200Z'));
  s.video.currentTime = .9; s.callbacks.get(s.player.callback)(1016);
  assert.equal(s.times.at(-1), Date.parse('2026-10-10T00:00:00.200Z'));
  s.player.resetMedia(); assert.equal(s.callbacks.size, 0);
});

test('focused playback without timing keeps overlays hidden and animation fallback uses currentTime', () => {
  const s = setup(); s.video.requestVideoFrameCallback = undefined;
  s.player.paint(); assert.equal(s.times.at(-1), null);
  s.player.clock.update({ streamId: 'audio', startSeconds: 0, endSeconds: 1, producedAt: '2026-10-10T00:00:01Z' });
  s.callbacks.get(s.player.callback)();
  assert.equal(s.times.at(-1), Date.parse('2026-10-10T00:00:00.800Z'));
  s.player.resetMedia(); assert.equal(s.callbacks.size, 0);
});


test('initialization reports discovered audio immediately without clearing a blocked playback gesture', async () => {
  for (const audio of [true, false]) {
    const s = setup(), changes = [];
    s.player.changed = player => changes.push({ hasAudio: player.hasAudio, status: player.status });
    const media = { addSourceBuffer: () => ({}) };
    s.player.Media = { isTypeSupported: () => true }; s.player.media = media;
    s.player.sourceReady = Promise.resolve(media); s.player.status = 'blocked';
    await s.player.receive(JSON.stringify({ type: 'init', mimeType: 'video/mp4', hasAudio: audio }), 1, s.player.socket);
    assert.equal(s.player.hasAudio, audio); assert.equal(s.video.muted, !audio);
    assert.equal(changes.length, 1); assert.equal(changes[0].hasAudio, audio);
    assert.equal(changes[0].status, audio ? 'blocked' : 'no-audio');
  }
});

test('an obsolete initialization cannot restore audio availability after the stream changes', async () => {
  const s = setup(), changes = []; let opened;
  s.player.changed = player => changes.push(player.hasAudio);
  s.player.Media = { isTypeSupported: () => true }; s.player.media = {};
  s.player.sourceReady = new Promise(resolve => { opened = resolve; });
  const init = s.player.receive(JSON.stringify({ type: 'init', mimeType: 'video/mp4', hasAudio: true }), 1, s.player.socket);
  s.player.version++; opened({ addSourceBuffer() { throw new Error('Stale initialization'); } }); await init;
  assert.equal(s.player.hasAudio, undefined); assert.equal(changes.length, 0);
});

test('talkback restores each actual media element mute setting when the speaker session ends', () => {
  const first = setup(), second = setup();
  first.player.setMuted(true); second.player.setMuted(false);
  for (const s of [first, second]) { s.player.setTalkbackActive(true); assert.equal(s.video.muted, true); }
  first.player.setTalkbackActive(false); second.player.setTalkbackActive(false);
  assert.equal(first.video.muted, true); assert.equal(second.video.muted, false);
  second.player.hasAudio = false; second.player.setMuted(false); assert.equal(second.video.muted, true);
});
