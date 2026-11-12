const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const audioSource = readFileSync('src/SecurityCameraViewer.Web/wwwroot/live-media.js', 'utf8');
const Controller = runInNewContext(audioSource + '\nLiveCameraAudioController');

class TestPlayer {
  constructor(changed) { Object.assign(this, { changed, channel: null, muted: false, status: 'stopped', hasAudio: null, message: '', starts: 0, stops: 0, plays: 0 }); }
  start(channel, tile, compatibility) { this.starts++; this.channel = channel; this.tile = tile; this.compatibility = compatibility; this.status = 'connecting'; this.changed(); }
  stop() { this.stops++; this.channel = null; this.hasAudio = null; this.status = 'stopped'; this.message = 'Choose Listen on a camera.'; this.changed(); }
  play() { this.plays++; this.status = 'playing'; this.changed(); }
  setMuted(value) { this.muted = value; this.changed(); }
  setVolume(value) { this.volume = value; }
  setTalkbackActive(value) { this.talking = value; }
  notify(status, message) { this.status = status; this.message = message; this.changed(); }
  audio(value) { this.hasAudio = value; this.status = value ? 'playing' : 'no-audio'; this.message = value ? 'Listening.' : 'This camera stream has no audio.'; this.changed(); }
  fail(message) { this.stop(); this.status = 'error'; this.message = message; this.changed(); }
}
function controller() { return new Controller(() => {}, callback => new TestPlayer(callback)); }
const tile = channel => ({ name: `Camera ${channel}` });

test('compatibility preference persists per browser and restarts active listeners preserving their choices', () => {
  const saved = new Map();
  const storage = { getItem: key => saved.get(key), setItem: (key, value) => saved.set(key, value) };
  const make = () => new Controller(() => {}, callback => new TestPlayer(callback), storage);
  const c = make(); c.setVolume(.3);
  c.listen(1, tile(1)); c.listen(2, tile(2)); c.listen(3, tile(3), true); c.players.get(3).stop();
  c.setCompatibility(true);
  for (const channel of [1, 2]) {
    const p = c.players.get(channel);
    assert.equal(p.starts, 2); assert.equal(p.channel, channel); assert.equal(p.compatibility, true);
    assert.equal(p.muted, channel === 1); assert.equal(p.volume, .3);
  }
  assert.equal(c.players.get(3).starts, 1);
  c.setCompatibility(true); assert.equal(c.players.get(1).starts, 2);
  assert.equal(make().compatibility, true);
  c.setCompatibility(false); assert.equal(make().compatibility, false);
  c.listen(4, tile(4)); assert.equal(c.players.get(4).compatibility, false);
});

test('unavailable browser storage does not prevent compatibility playback', () => {
  const c = new Controller(() => {}, callback => new TestPlayer(callback), {
    getItem() { throw new Error('Storage denied'); }, setItem() { throw new Error('Storage denied'); }
  });
  assert.equal(c.compatibility, false); c.setCompatibility(true); c.listen(1, tile(1));
  assert.equal(c.players.get(1).compatibility, true);
});

test('original and compatibility players use separate same-origin endpoints on every reconnect', () => {
  const urls = [];
  const Player = runInNewContext(audioSource + '\nLiveCameraPlayer', {
    CameraFrameClock: class {}, location: { protocol: 'https:', host: 'camera.example' },
    WebSocket: class { constructor(url) { urls.push(url); } }
  });
  const p = new Player(() => {}); p.channel = 8; p.version = 1;
  p.compatibility = true; p.connect(1); p.connect(1);
  p.compatibility = false; p.connect(1);
  assert.deepEqual(urls, ['wss://camera.example/api/cameras/8/live?compatibility=true',
    'wss://camera.example/api/cameras/8/live?compatibility=true', 'wss://camera.example/api/cameras/8/live']);
});

test('ordinary Listen mutes other active cameras without stopping their media sessions', () => {
  const c = controller();
  c.listen(1, tile(1)); c.listen(2, tile(2)); c.listen(3, tile(3));
  assert.equal(c.active, true); assert.equal(c.players.size, 3);
  for (const channel of [1, 2, 3]) {
    const p = c.players.get(channel);
    assert.equal(p.channel, channel); assert.equal(p.muted, channel !== 3); assert.equal(p.stops, 0);
  }
});

test('Ctrl+click adds audio, including a previously muted camera, without muting peers', () => {
  const c = controller();
  c.listen(1, tile(1)); c.listen(2, tile(2), true); c.listen(3, tile(3));
  c.listen(1, tile(1), true); c.listen(2, tile(2), true);
  assert.ok([...c.players.values()].every(p => !p.muted && p.starts === 1));
});

test('ordinary Listen on a muted camera restores it and mutes every other session', () => {
  const c = controller();
  c.listen(1, tile(1)); c.listen(2, tile(2), true); c.listen(3, tile(3), true);
  c.setMuted(1, true); c.listen(1, tile(1));
  assert.equal(c.players.get(1).muted, false); assert.equal(c.players.get(1).plays, 1);
  assert.equal(c.players.get(2).muted, true); assert.equal(c.players.get(3).muted, true);
});

test('Stop listening and Mute/Unmute apply only to the selected camera', () => {
  const c = controller(); c.listen(1, tile(1)); c.listen(2, tile(2), true);
  c.setMuted(1, true); c.setMuted(1, false);
  assert.equal(c.players.get(2).muted, false);
  c.listen(1, tile(1), true);
  assert.equal(c.players.get(1).channel, null); assert.equal(c.players.get(2).channel, 2);
  c.listen(1, tile(1), true); assert.equal(c.players.get(1).starts, 2);
});

test('Resume audio retries blocked playback during the gesture and honors Ctrl', () => {
  for (const ctrl of [false, true]) {
    const c = controller(); c.listen(1, tile(1)); c.listen(2, tile(2), true);
    c.players.get(2).status = 'blocked'; c.listen(2, tile(2), ctrl);
    assert.equal(c.players.get(2).plays, 1); assert.equal(c.players.get(2).stops, 0);
    assert.equal(c.players.get(1).muted, !ctrl);
  }
});

test('unavailable audio and missing tiles do not create sessions or disturb audible cameras', () => {
  const c = controller(); c.listen(1, tile(1)); c.listen(2, tile(2), true);
  c.players.get(2).audio(false); c.listen(2, tile(2)); c.listen(3, undefined);
  assert.equal(c.available(2), false); assert.equal(c.players.get(1).muted, false);
  assert.equal(c.players.size, 2); assert.equal(c.players.get(2).starts, 1); assert.equal(c.players.get(2).channel, null);
});

test('unsupported playback and stream failures preserve peers and can be retried', () => {
  const c = controller(); c.listen(1, tile(1));
  const unsupported = new TestPlayer(() => {}); unsupported.start = () => { unsupported.status = 'unsupported'; };
  c.players.set(2, unsupported); c.listen(2, tile(2));
  assert.equal(c.players.get(1).muted, false);
  c.players.get(1).fail('Decoder failed'); c.listen(1, tile(1));
  assert.equal(c.players.get(1).channel, 1); assert.equal(c.players.get(1).starts, 2);
});

test('global volume reaches existing and future players and is bounded by media element requirements', () => {
  const c = controller(); c.listen(1, tile(1)); c.setVolume(.35); c.listen(2, tile(2), true);
  assert.equal(c.volume, .35); assert.ok([...c.players.values()].every(p => p.volume === .35));
  c.setVolume(2); assert.equal(c.volume, 1); c.setVolume(-1); assert.equal(c.volume, 0);
});

test('talkback suppresses every session and preserves individual mute choices', () => {
  const c = controller(); c.listen(1, tile(1)); c.listen(2, tile(2)); c.setTalkbackActive(true);
  c.listen(3, tile(3), true); assert.ok([...c.players.values()].every(p => p.talking));
  c.setTalkbackActive(false);
  assert.equal(c.players.get(1).muted, true); assert.equal(c.players.get(2).muted, false);
  assert.ok([...c.players.values()].every(p => !p.talking));
});

test('stop releases all players, clears stale availability and tolerates repeated cleanup', () => {
  const c = controller(); c.listen(1, tile(1)); c.listen(2, tile(2), true);
  const players = [...c.players.values()]; players[1].audio(false);
  c.stop(); c.stop(); assert.equal(c.active, false); assert.equal(c.players.size, 0);
  assert.equal(c.available(2), true); assert.equal(players[0].stops, 1); assert.equal(players[1].stops, 2);
  c.listen(2, tile(2)); assert.equal(c.players.get(2).channel, 2);
});

function viewer() {
  const ids = new Map(), events = new Map();
  function element() {
    const children = new Map(), classes = new Set(), attributes = new Map();
    return { hidden: false, textContent: '', elements: [], dataset: {}, clientWidth: 1000, clientHeight: 700,
      classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value), toggle: (value, on) => on ? classes.add(value) : classes.delete(value) },
      setAttribute: (name, value) => attributes.set(name, value), getAttribute: name => attributes.get(name),
      append() {}, add() {}, replaceChildren() {}, removeAttribute() {}, focus() {},
      addEventListener(name, handler) { this[name] = handler; },
      querySelector(selector) { if (!children.has(selector)) children.set(selector, element()); return children.get(selector); } };
  }
  const document = { getElementById(id) { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); },
    createElement: element, addEventListener() {}, querySelectorAll: () => [] };
  const zoom = class { refresh() {} setFocused(value) { this.focused = value; } dispose() {} };
  const context = { document, window: { innerWidth: 1000, addEventListener: (name, handler) => events.set(name, handler) },
    LiveCameraAudioController: class extends Controller { constructor(changed) { super(changed, callback => new TestPlayer(callback)); } },
    PushToTalk: class { channel = null; message = ''; bind() {} stop() { this.channel = null; } static available() { return true; } },
    CameraLayoutView: class { configure() {} }, CameraZoomView: zoom, CameraEnhancementView: zoom, CameraEnhancementPreferences: class {},
    CameraDetectionOverlay: class { setPlaybackTime() {} setAvailable() {} dispose() {} },
    CameraPlayback: class { method = 'server'; suspend() {} updateMethodRetry() {} stop() {} },
    DetectionOverlayController: class { refresh() {} }, localStorage: {}, Option: class {},
    location: { protocol: 'http:', host: 'localhost' }, WebSocket: class {},
    fetch: () => new Promise(() => {}), performance: { now: () => 1000 },
    AbortSignal, setTimeout() {}, setInterval() {}, queueMicrotask() {}, URL: { revokeObjectURL() {} } };
  const appSource = ['camera-playback-policy.js', 'camera-collection.js', 'app.js'].map(file => readFileSync('src/SecurityCameraViewer.Web/wwwroot/' + file, 'utf8')).join('\n');
  const runtime = runInNewContext(appSource + `\n({
    initialize() { state = { connected: true, settings: { configurationId: 'test', sources: [1, 2, 3].map(channel => ({ channel, name: 'Camera ' + channel })) }, cameras: [1, 2, 3].map(channel => ({ channel, live: true, hasAudio: true, audioCodec: 'AAC' })) }; serverOnline = true; createTiles(); },
    tiles, liveAudio, renderLiveAudio, arrange, renderStatus,
    metadata(channel, hasAudio, audioCodec) { Object.assign(state.cameras.find(c => c.channel === channel), { hasAudio, audioCodec }); renderLiveAudio(); },
    focus(channel) { focus = channel; arrange(); },
    busy(value) { busy = value; renderLiveAudio(); },
    rewind(value) { liveRewind = { historical: value, players: new Map() }; renderLiveAudio(); },
    offline() { serverOnline = false; renderStatus(); },
    disconnect() { state.connected = false; renderStatus(); },
    rebuild: createTiles, demo() { state.demo = true; renderLiveAudio(); }
  })`, context);
  runtime.initialize(); return { ...runtime, ids, events };
}

test('every audio grid camera has one footer toggle after its codec, with Audio OFF before clicking', () => {
  const v = viewer();
  for (const t of v.tiles.values()) {
    assert.equal(t.sound.hidden, true); assert.equal(t.listen.hidden, false); assert.equal(t.listen.disabled, false);
    assert.equal(t.audioLabel.textContent, 'AAC Audio'); assert.equal(t.listen.textContent, 'Audio OFF');
    assert.equal(t.listen.getAttribute('aria-pressed'), 'false'); assert.equal(t.mute, undefined);
  }
  v.tiles.get(2).listen.onclick({});
  assert.equal(v.liveAudio.players.get(2).channel, 2); assert.equal(v.tiles.get(2).listen.textContent, 'Audio ON');
  assert.equal(v.tiles.get(2).listen.getAttribute('aria-pressed'), 'true');
  v.tiles.get(2).listen.onclick({}); assert.equal(v.liveAudio.players.get(2).channel, null);
  assert.equal(v.tiles.get(2).listen.textContent, 'Audio OFF');
});

test('tile events preserve Ctrl+click and ordinary clicks mute all other cameras', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({}); v.tiles.get(2).listen.onclick({ ctrlKey: true });
  assert.equal(v.liveAudio.players.get(1).muted, false);
  v.tiles.get(3).listen.onclick({});
  for (const channel of [1, 2]) {
    assert.equal(v.tiles.get(channel).listen.textContent, 'Audio OFF');
    assert.equal(v.tiles.get(channel).listen.getAttribute('aria-pressed'), 'false');
  }
  v.tiles.get(1).listen.onclick({ ctrlKey: true }); assert.equal(v.liveAudio.players.get(3).muted, false);
  v.tiles.get(3).listen.onclick({}); assert.equal(v.liveAudio.players.get(3).channel, null);
  assert.equal(v.liveAudio.players.get(1).muted, false);
});

test('focus changes, return to grid and rearranging preserve selected sessions', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({}); v.tiles.get(2).listen.onclick({ ctrlKey: true });
  v.focus(1); v.focus(3); v.focus(null); v.arrange();
  assert.ok([...v.liveAudio.players.values()].every(p => p.channel !== null && p.stops === 0 && !p.muted));
  assert.equal(v.ids.get('live-volume').disabled, false);
});

test('menu uses the same ON/OFF toggle for the focused camera and volume reaches all cameras', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({}); v.focus(2);
  assert.equal(v.ids.get('live-listen').textContent, 'Audio OFF');
  v.ids.get('live-listen').onclick({ ctrlKey: true });
  assert.equal(v.ids.get('live-listen').textContent, 'Audio ON');
  v.ids.get('live-listen').onclick({});
  assert.equal(v.liveAudio.players.get(1).muted, false); assert.equal(v.liveAudio.players.get(2).channel, null);
  v.ids.get('live-volume').oninput({ target: { value: '25' } });
  assert.ok([...v.liveAudio.players.values()].every(p => p.volume === .25));
});

test('upfront No Audio and unknown metadata never require a click or disturb audible cameras', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({});
  v.metadata(2, false, null); v.metadata(3, null, null);
  assert.equal(v.tiles.get(2).audioLabel.textContent, 'No Audio'); assert.equal(v.tiles.get(2).listen.hidden, true);
  assert.equal(v.tiles.get(3).audioLabel.textContent, 'Checking audio…'); assert.equal(v.tiles.get(3).listen.hidden, true);
  v.tiles.get(2).listen.onclick({}); v.tiles.get(3).listen.onclick({});
  assert.equal(v.liveAudio.players.size, 1); assert.equal(v.liveAudio.players.get(1).muted, false);
  v.metadata(3, true, null); assert.equal(v.tiles.get(3).audioLabel.textContent, 'Audio'); assert.equal(v.tiles.get(3).listen.hidden, false);
  v.focus(2); assert.equal(v.ids.get('live-listen').disabled, true);
});

test('actual stream audio overrides source metadata and a silent stream removes its toggle', () => {
  const v = viewer(); v.metadata(1, true, 'PCMU'); assert.equal(v.tiles.get(1).audioLabel.textContent, 'PCMU Audio');
  v.tiles.get(1).listen.onclick({}); const p = v.liveAudio.players.get(1);
  p.mimeType = 'video/mp4; codecs="avc1.42001F,mp4a.40.2"'; p.audio(true);
  assert.equal(v.tiles.get(1).audioLabel.textContent, 'AAC Audio');
  p.audio(false); assert.equal(v.tiles.get(1).listen.hidden, true); assert.equal(v.tiles.get(1).audioLabel.textContent, 'No Audio');
  assert.equal(v.tiles.get(1).sound.hidden, true); assert.equal(v.tiles.get(2).listen.hidden, false);
  v.demo(); assert.equal(v.tiles.get(2).listen.hidden, true); assert.equal(v.tiles.get(2).audioLabel.textContent, 'No Audio');
});

test('blocked playback and failures keep OFF retry controls and accessible camera-specific feedback', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({}); const p = v.liveAudio.players.get(1);
  p.status = 'blocked'; p.message = 'Browser blocked audio'; v.renderLiveAudio();
  assert.equal(v.tiles.get(1).listen.textContent, 'Audio OFF'); assert.match(v.tiles.get(1).listen.title, /Click to retry/);
  assert.equal(v.tiles.get(1).soundStatus.textContent, p.message); assert.equal(v.tiles.get(2).soundStatus.textContent, '');
  v.tiles.get(1).listen.onclick({}); assert.equal(p.plays, 1);
  p.fail('Decode failed'); assert.equal(v.tiles.get(1).listen.textContent, 'Audio OFF');
  assert.equal(v.tiles.get(1).soundStatus.textContent, 'Decode failed'); assert.match(v.tiles.get(1).listen.title, /Decode failed/);
});

test('busy and rewind disable every tile and prevent stale click handlers from starting audio', () => {
  const v = viewer();
  for (const set of [value => v.busy(value), value => v.rewind(value)]) {
    set(true); assert.ok([...v.tiles.values()].every(t => t.listen.disabled));
    v.tiles.get(1).listen.onclick({}); assert.equal(v.liveAudio.active, false); set(false);
  }
});

test('offline, disconnect, page exit and tile rebuild stop all independent sessions', () => {
  for (const end of [v => v.offline(), v => v.disconnect(), v => v.events.get('pagehide')(), v => v.rebuild()]) {
    const v = viewer(); v.tiles.get(1).listen.onclick({}); v.tiles.get(2).listen.onclick({ ctrlKey: true });
    const players = [...v.liveAudio.players.values()]; end(v);
    assert.equal(v.liveAudio.active, false); assert.ok(players.every(p => p.channel === null && p.stops === 1));
  }
});

test('talkback remains focused with its own overlay and never restores floating audio controls', () => {
  const v = viewer(), t = v.tiles.get(1);
  t.talkback = { supported: true, canTalk: true }; v.renderLiveAudio(); assert.equal(t.talk.hidden, true);
  v.focus(1); assert.equal(t.talk.hidden, false); assert.equal(t.sound.hidden, false);
  v.focus(null); assert.equal(t.talk.hidden, true); assert.equal(t.sound.hidden, true); assert.equal(t.listen.hidden, false);
});

test('server loss clears learned no-audio availability even when no listening sessions remain', () => {
  const v = viewer(); v.tiles.get(1).listen.onclick({}); v.liveAudio.players.get(1).audio(false);
  assert.equal(v.liveAudio.active, false); assert.equal(v.liveAudio.available(1), false);
  v.offline(); assert.equal(v.liveAudio.available(1), true); assert.equal(v.liveAudio.players.size, 0);
});
