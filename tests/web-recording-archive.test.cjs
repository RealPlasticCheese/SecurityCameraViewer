const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/recording-archive.js'), 'utf8');
function environment(request) {
  const nodes = new Map();
  class Element {
    constructor() { this.children = []; this.style = {}; this.listeners = {}; this.duration = 300; this.currentTime = 0; }
    replaceChildren(...children) { this.children = children; }
    append(child) { this.children.push(child); }
    setAttribute() {}
    addEventListener(name, listener) { this.listeners[name] = listener; }
    removeAttribute(name) { delete this[name]; }
    pause() { this.paused = true; }
    load() {}
    play() { this.paused = false; return Promise.resolve(); }
  }
  const el = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  const Player = runInNewContext(source + '\nRecordingArchivePlayer', {
    document: { getElementById: el, createElement: () => new Element() }, URLSearchParams
  });
  return { player: new Player(request), el };
}
const recording = (id, start, end) => ({ id, cameraName: 'Garage', mode: 'continuous', status: 'ready',
  startedAt: `2026-10-09T${start}:00Z`, endedAt: `2026-10-09T${end}:00Z` });

test('coverage shows playable segments and current captures; marker navigation seeks existing footage', async () => {
  const segment = recording('first', '02:00', '02:05');
  const env = environment(async path => path.startsWith('recordings/timeline')
    ? { recordings: [segment, { ...recording('active', '02:05', '02:10'), status: 'recording' }], truncated: false }
    : [{ source: 'continuous', timestamp: segment.startedAt }, { source: 'motion', timestamp: '2026-10-09T02:02:00Z', zones: [{ name: 'Door' }] }]);
  await env.player.coverage({ channel: 1, date: '2026-10-09', zones: [] }, 'default');
  const buttons = env.el('recording-timeline').children;
  assert.equal(buttons.length, 2); assert.equal(buttons[1].disabled, true);
  await env.player.play(segment);
  const markers = env.el('recording-markers').children;
  assert.equal(markers.length, 1); assert.match(markers[0].textContent, /Door/);
  markers[0].onclick(); assert.equal(env.el('recording-player').currentTime, 120);
  assert.equal(env.el('recording-player').src, '/api/recordings/first/video');
});

test('playback advances through consecutive segments and stops at gaps or event clips', async () => {
  const env = environment(async () => []);
  env.el('recording-play-next').checked = true;
  const segments = [recording('first', '02:00', '02:05'), recording('second', '02:05', '02:10'), recording('gap', '02:20', '02:25')];
  env.player.recordings = segments;
  await env.player.play(segments[0]); env.player.next();
  assert.equal(env.player.current.id, 'second');
  env.player.next(); assert.equal(env.player.current.id, 'second');
  assert.match(env.el('recording-timeline-status').textContent, /gap/);
  await env.player.play({ ...segments[0], mode: 'event' }); env.player.next();
  assert.equal(env.player.current.id, 'first');
});

test('an activity seek waits for media metadata when the clip is still loading', async () => {
  const env = environment(async () => [{ source: 'motion', timestamp: '2026-10-09T02:02:00Z', reason: 'Motion' }]);
  const video = env.el('recording-player'); video.duration = NaN;
  await env.player.play(recording('first', '02:00', '02:05'));
  env.el('recording-markers').children[0].onclick();
  assert.equal(video.currentTime, 0);
  video.duration = 300; video.onloadedmetadata();
  assert.equal(video.currentTime, 120);
});

test('closing playback and changing filters invalidate late coverage and marker responses', async () => {
  const pending = [];
  const env = environment(path => new Promise(resolve => pending.push({ path, resolve })));
  const coverage = env.player.coverage({ channel: 1, date: '2026-10-09', zones: [] }, 'default');
  const playback = env.player.play(recording('first', '02:00', '02:05'));
  env.player.clear();
  pending[0].resolve({ recordings: [recording('first', '02:00', '02:05')], truncated: false });
  pending[1].resolve([{ source: 'motion', timestamp: '2026-10-09T02:01:00Z', reason: 'Motion' }]);
  await Promise.all([coverage, playback]);
  assert.equal(env.player.recordings.length, 0);
  assert.equal(env.el('recording-markers').children.length, 0);
  assert.equal(env.el('recording-player-panel').hidden, true);
});

test('changing cameras clears old coverage while the next request is pending', async () => {
  let resolveNext;
  const env = environment(async () => ({ recordings: [recording('first', '02:00', '02:05')], truncated: false }));
  await env.player.coverage({ channel: 1, date: '2026-10-09', zones: [] }, 'default');
  assert.equal(env.el('recording-timeline').children.length, 1);
  env.player.request = () => new Promise(resolve => { resolveNext = resolve; });
  const next = env.player.coverage({ channel: 2, date: '2026-10-09', zones: [] }, 'default');
  assert.equal(env.el('recording-timeline').children.length, 0);
  resolveNext({ recordings: [], truncated: false });
  await next;
});

test('coverage forwards the replay-only filter and changing it clears the current recording', async () => {
  const paths = [], segment = recording('saved', '02:00', '02:05');
  const env = environment(async path => { paths.push(path); return { recordings: [segment], truncated: false }; });
  await env.player.coverage({ channel: 1, date: '2026-10-09', zones: [], savedReplayOnly: true }, 'default');
  assert.match(paths[0], /savedReplayOnly=true/);
  assert.equal(env.el('recording-timeline-panel').hidden, false);
  env.player.current = segment;
  await env.player.coverage({ channel: 1, date: '2026-10-09', zones: [] }, 'default');
  assert.doesNotMatch(paths[1], /savedReplayOnly/); assert.equal(env.player.current, null);
});
