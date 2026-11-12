import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync('src/SecurityCameraViewer.Web/wwwroot/talkback.js', 'utf8');
const workletSource = fs.readFileSync('src/SecurityCameraViewer.Web/wwwroot/talkback-worklet.js', 'utf8');
const liveSource = fs.readFileSync('src/SecurityCameraViewer.Web/wwwroot/camera-frame-clock.js', 'utf8') + '\n' + fs.readFileSync('src/SecurityCameraViewer.Web/wwwroot/live-media.js', 'utf8');
const tick = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };

function environment({ delayedPermission = false, delayedSocket = false, secure = true } = {}) {
  const handlers = new Map(), timers = new Map(), sockets = [], contexts = [];
  let permit, requests = 0;
  const track = { stops: 0, stop() { this.stops++; }, onended: null };
  const stream = { getTracks: () => [track] };
  const eventTarget = scope => ({ addEventListener(name, handler) { handlers.set(scope + name, handler); } });
  class Context {
    constructor() { this.closed = 0; this.audioWorklet = { addModule: async () => {} }; this.destination = {}; contexts.push(this); }
    resume() { return Promise.resolve(); }
    close() { this.closed++; return Promise.resolve(); }
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
    createGain() { return { gain: {}, connect() {}, disconnect() {} }; }
  }
  class Node {
    constructor() { this.port = { postMessage() {}, close() {} }; }
    connect() {} disconnect() {}
  }
  class Socket {
    static OPEN = 1;
    constructor() {
      this.readyState = 0; this.bufferedAmount = 0; this.sent = []; sockets.push(this);
      if (!delayedSocket) queueMicrotask(() => this.ready());
    }
    ready() { this.readyState = 1; this.onmessage?.({ data: JSON.stringify({ type: 'ready', sampleRate: 8000, channels: 1, frameSamples: 160, encoding: 'pcm_s16le' }) }); }
    send(bytes) { this.sent.push(bytes); }
    close() { this.readyState = 3; }
  }
  const sandbox = { window: { ...eventTarget('window:'), isSecureContext: secure, AudioContext: Context, AudioWorkletNode: Node },
    document: { ...eventTarget('document:'), hidden: false }, navigator: { mediaDevices: { getUserMedia: () => {
      requests++; return delayedPermission ? new Promise(resolve => { permit = resolve; }) : Promise.resolve(stream);
    } } }, AudioContext: Context, AudioWorkletNode: Node, WebSocket: Socket, ArrayBuffer,
    location: { protocol: 'https:', host: 'fixture.example' }, setTimeout(callback, ms) { const id = Symbol(); timers.set(id, { callback, ms }); return id; },
    clearTimeout(id) { timers.delete(id); }, console };
  vm.createContext(sandbox); vm.runInContext(source + '\nthis.Player = PushToTalk;', sandbox);
  const player = new sandbox.Player(() => {});
  return { player, track, sockets, contexts, handlers, timers, sandbox, permission: () => permit(stream), requests: () => requests };
}
{
  const sandbox = { console }; vm.createContext(sandbox);
  vm.runInContext(liveSource + '\nthis.Player = LiveCameraPlayer;', sandbox);
  const player = new sandbox.Player(() => {}); player.video = { muted: false };
  player.setTalkbackActive(true); assert.equal(player.video.muted, true); assert.equal(player.muted, false);
  player.setTalkbackActive(false); assert.equal(player.video.muted, false);
  player.setMuted(true); player.setTalkbackActive(true); player.setTalkbackActive(false);
  assert.equal(player.video.muted, true); assert.equal(player.muted, true);
  player.setTalkbackActive(true); player.setMuted(false); assert.equal(player.video.muted, true);
  player.setTalkbackActive(false); assert.equal(player.video.muted, false);
  console.log('PASS: Talking suppresses live feedback without losing the user\'s mute preference');
}

{
  const test = environment({ delayedPermission: true });
  const opening = test.player.start(1, 'Fixture'); test.player.stop(); test.permission(); await opening;
  assert.equal(test.track.stops, 1); assert.equal(test.contexts[0].closed, 1); assert.equal(test.sockets.length, 0);
  console.log('PASS: Releasing during microphone permission stops late tracks without opening a speaker');
}
{
  const test = environment(); await test.player.start(1, 'Fixture');
  assert.match(test.player.message, /Talking/);
  test.player.node.port.onmessage({ data: new ArrayBuffer(320) });
  assert.equal(test.sockets[0].sent.length, 1);
  test.handlers.get('window:blur')();
  assert.equal(test.track.stops, 1); assert.equal(test.sockets[0].readyState, 3); assert.equal(test.player.channel, null);
  console.log('PASS: Hold sends PCM; focus loss closes the speaker and stops microphone capture');
}
{
  const test = environment({ delayedSocket: true }); const opening = test.player.start(1, 'Fixture'); await tick();
  assert.equal(test.sockets.length, 1); test.player.stop(); await opening;
  assert.equal(test.track.stops, 1); assert.equal(test.player.channel, null);
  console.log('PASS: Releasing during the WebSocket handshake cannot revive a cancelled press');
}
{
  const test = environment(); await test.player.start(1, 'Fixture');
  test.sockets[0].bufferedAmount = 3000; test.player.node.port.onmessage({ data: new ArrayBuffer(320) });
  assert.equal(test.track.stops, 1); assert.equal(test.sockets[0].sent.length, 0);
  console.log('PASS: Network backpressure stops speech instead of buffering stale microphone audio');
}
{
  const test = environment(); await test.player.start(1, 'Fixture');
  [...test.timers.values()].find(timer => timer.ms === 30000).callback();
  assert.equal(test.track.stops, 1); assert.equal(test.player.channel, null);
  console.log('PASS: The maximum press timer releases microphone capture');
}
{
  const test = environment(); const button = { disabled: false, focus() {}, setPointerCapture() {} };
  test.player.bind(button, 1, 'Fixture'); button.onkeydown({ code: 'Space', repeat: false, preventDefault() {} }); await tick();
  assert.equal(test.player.channel, 1); test.handlers.get('document:keyup')({ code: 'Space' });
  assert.equal(test.track.stops, 1);
  button.onpointerdown({ button: 0, isPrimary: true, pointerId: 7, preventDefault() {} }); await tick();
  button.onlostpointercapture({ pointerId: 7 }); assert.equal(test.player.channel, null);
  console.log('PASS: Keyboard release and lost pointer capture stop push-to-talk');
}
{
  const test = environment({ secure: false }); await test.player.start(1, 'Fixture');
  assert.equal(test.requests(), 0);
  console.log('PASS: Insecure contexts never request microphone permission');
}
for (const rate of [48000, 44100]) {
  let Processor; const frames = [];
  const sandbox = { sampleRate: rate, AudioWorkletProcessor: class { constructor() { this.port = { postMessage(frame) {
    frames.push(frame); queueMicrotask(() => processor.port.onmessage({ data: 'ack' }));
  } }; } }, registerProcessor(_, type) { Processor = type; }, ArrayBuffer, DataView };
  vm.createContext(sandbox); vm.runInContext(workletSource, sandbox);
  const processor = new Processor();
  for (let offset = 0; offset < rate / 10; offset += 128) {
    const input = new Float32Array(Math.min(128, rate / 10 - offset)).fill(.25), output = new Float32Array(input.length).fill(1);
    assert.equal(processor.process([[input]], [[output]]), true); assert.ok(output.every(value => value === 0));
    await tick();
  }
  assert.equal(frames.length, 5); assert.ok(frames.every(frame => frame.byteLength === 320 && new DataView(frame).getInt16(0, true) === 8192));
  console.log(`PASS: Actual worklet downsamples ${rate} Hz to fixed 8 kHz PCM without local microphone feedback`);
}
{
  let Processor; const messages = [];
  const sandbox = { sampleRate: 8000, AudioWorkletProcessor: class { constructor() { this.port = { postMessage: value => messages.push(value) }; } },
    registerProcessor(_, type) { Processor = type; }, ArrayBuffer, DataView };
  vm.createContext(sandbox); vm.runInContext(workletSource, sandbox);
  const processor = new Processor();
  assert.equal(processor.process([[new Float32Array(480)]], [[new Float32Array(480)]]), false);
  assert.equal(messages.length, 3); assert.equal(messages[2].type, 'overflow');
  console.log('PASS: A stalled worklet consumer retains at most two PCM frames and stops');
}
