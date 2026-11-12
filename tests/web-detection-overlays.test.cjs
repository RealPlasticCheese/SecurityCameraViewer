const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/detection-overlays.js'), 'utf8');
const zoomSource = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-zoom.js'), 'utf8');
const clockSource = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-frame-clock.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const flush = () => new Promise(resolve => setImmediate(resolve));

function setup(stored = false, metadataStored) {
  let now = 0, sequence = 0;
  const timers = new Map(), calls = [], handlers = new Map();
  const document = { hidden: false, createElement: element, addEventListener: (name, fn) => handlers.set(name, fn), dispatchEvent() {} };
  function element() {
    return { ownerDocument: document, style: {}, children: [], attributes: {}, hidden: false, textContent: '',
      append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; }, remove() { this.removed = true; },
      setAttribute(name, value) { this.attributes[name] = value; }, addEventListener(name, fn) { this[name] = fn; }, focus() { this.focused = true; } };
  }
  const viewport = { width: 800, height: 600 }, surface = { ...element(), getBoundingClientRect: () => viewport };
  const root = { ...element(), querySelector: () => surface };
  const zoom = { crop: null, picture: () => ({ left: 0, top: 75, width: 800, height: 450 }) };
  const context = { configurationId: 'first', generation: 1, online: true };
  const toggle = element(), metadataToggle = element(), status = element();
  const storage = new Map([['scv.detection-overlays', String(stored)]]);
  if (metadataStored !== undefined) storage.set('scv.detection-overlay-metadata', String(metadataStored));
  const sandbox = { document, window: { addEventListener: (name, fn) => handlers.set(name, fn) }, AbortController, Event,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) }, performance: { now: () => now },
    ResizeObserver: class { observe() {} disconnect() { this.disconnected = true; } },
    setTimeout(fn, delay) { const id = ++sequence; timers.set(id, { fn, at: now + delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    fetch(url, options) { return new Promise(resolve => calls.push({ url, options, resolve })); }
  };
  const api = runInNewContext(`${zoomSource}\n${clockSource}\n${source}\n({ DetectionOverlay, CameraDetectionOverlay, DetectionOverlayController, CameraFrameClock })`, sandbox);
  const view = new api.CameraDetectionOverlay(root, zoom);
  view.setAvailable(true);
  const tiles = new Map([[1, { detections: view }]]);
  const controller = () => new api.DetectionOverlayController(toggle, metadataToggle, status, () => tiles, () => context);
  function advance(ms) {
    const until = now + ms;
    for (;;) {
      const next = [...timers].filter(([, job]) => job.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
      if (!next) break;
      timers.delete(next[0]); now = next[1].at; next[1].fn();
    }
    now = until;
  }
  return { ...api, view, viewport, zoom, root, context, toggle, metadataToggle, storage, tiles, status, calls, handlers, timers, controller, advance,
    hide(value) { document.hidden = value; handlers.get('visibilitychange')?.(); } };
}
const methods = [{ id: 'adaptive', name: 'Adaptive background subtraction', shortName: 'Motion' }, { id: 'ai', name: 'AI object detection', shortName: 'AI' }];
const models = [{ id: 'test-model', name: 'Test local model' }];
function observation() {
  return { bounds: { x: .25, y: .25, width: .25, height: .5 }, kind: 'object', confirmed: true,
    capturedAt: '2026-10-08T00:00:00Z', expiresAt: '2026-10-08T00:00:02Z', zones: [{ id: 'gate', name: 'Gate' }],
    sources: [{ method: 'adaptive', capturedAt: '2026-10-08T00:00:00Z', attributes: [{ name: 'changedArea', number: .4, unit: '%' }] },
      { method: 'ai', model: 'test-model', capturedAt: '2026-10-08T00:00:00Z', attributes: [
        { name: 'objectType', text: 'person' }, { name: 'confidence', number: .87, unit: 'ratio' },
        { name: 'device', text: 'cpu' }, { name: 'inferenceTime', number: 45.123, unit: 'ms' }] }] };
}
const entry = (value = observation(), seconds = 2) => ({ observation: value, remainingSeconds: seconds, ageSeconds: .1 });
const response = (configurationId, entries = [entry()]) => ({ configurationId, enabled: true, methods, models, cameras: [{ channel: 1, detections: entries }] });
const reply = (call, data) => call.resolve({ ok: true, status: 200, json: async () => data });

test('one renderer formats hybrid evidence and future method metadata without model-specific branches', () => {
  const s = setup(), object = observation();
  assert.equal(s.DetectionOverlay.label(object, methods), 'Person · 87% · Motion + AI');
  const rows = plain(s.DetectionOverlay.details(object, methods, models, .5));
  assert.ok(rows.some(([key, value]) => key === 'Model' && value === 'Test local model'));
  assert.ok(rows.some(([key, value]) => key === 'Changed Area' && value === '0.4%'));
  assert.ok(rows.some(([key, value]) => key === 'Inference Time' && value === '45.12 ms'));
  object.disposition = 'ignore';
  assert.match(s.DetectionOverlay.label(object, methods), /ignored$/);
  assert.match(s.DetectionOverlay.details(object, methods, models, 0)[0][1], /cannot trigger/);
  delete object.disposition;
  object.kind = 'motion'; object.sources = [{ method: 'future-detector', attributes: [{ name: 'customTemperature', number: 21, unit: 'C' }] }];
  object.confirmed = false;
  assert.equal(s.DetectionOverlay.label(object, methods), 'Movement · Future detector · candidate');
  assert.ok(s.DetectionOverlay.details(object, methods, models, 0).some(([key, value]) => key === 'Custom Temperature' && value === '21 C'));
});

test('bounds align with letterboxing, cropped zoom and clipping without accepting malformed geometry', () => {
  const s = setup(), box = observation().bounds, picture = s.zoom.picture();
  assert.deepEqual(plain(s.DetectionOverlay.geometry(box, picture, s.viewport, null)), { left: 200, top: 187.5, width: 200, height: 225 });
  const crop = { left: .25, top: .25, width: .25, height: .5 };
  const result = s.DetectionOverlay.geometry(box, picture, s.viewport, crop);
  assert.deepEqual(plain(result), { left: 0, top: 0, width: 800, height: 600 });
  assert.equal(s.DetectionOverlay.geometry({ ...box, x: NaN }, picture, s.viewport, null), null);
  assert.equal(s.DetectionOverlay.geometry({ ...box, width: 2 }, picture, s.viewport, null), null);
  assert.equal(s.DetectionOverlay.geometry({ x: 0, y: 0, width: .05, height: .05 }, picture, s.viewport, crop), null);
});

test('labels support click/keyboard details, keep markup as text, reuse nodes and expire without another response', () => {
  const s = setup(), object = observation(); object.zones[0].name = '<img src=x onerror=alert(1)>';
  s.view.setEnabled(true); s.view.update([entry(object)], methods, models, 100);
  const item = s.view.items[0]; assert.equal(s.view.layer.hidden, false);
  item.button.onclick(); assert.equal(s.view.panel.hidden, false);
  assert.ok(s.view.definition.children.some(value => value.textContent === object.zones[0].name));
  s.view.update([entry(object)], methods, models, 100); assert.equal(s.view.items[0].button, item.button);
  item.button.keydown({ key: 'Escape', stopPropagation() {} });
  assert.equal(s.view.panel.hidden, true); assert.equal(item.button.focused, true);
  s.advance(1911); assert.equal(s.view.layer.hidden, true); assert.equal(item.region.hidden, true);
  s.view.dispose(); assert.equal(s.view.layer.removed, true); assert.equal(s.view.observer.disconnected, true);
  assert.equal(s.timers.size, 0);
});

test('unavailable video and disabled overlays remove boxes and metadata immediately', () => {
  const s = setup(); s.view.setEnabled(true); s.view.update([entry()], methods, models, 0);
  s.view.items[0].button.onclick(); s.view.setAvailable(false);
  assert.equal(s.view.layer.hidden, true); assert.equal(s.view.panel.hidden, true);
  s.view.setAvailable(true); assert.equal(s.view.layer.hidden, false);
  s.view.setEnabled(false); assert.equal(s.view.entries.length, 0); assert.equal(s.view.layer.hidden, true);
});

test('metadata can be hidden and restored without removing boxes or reopening details', () => {
  const s = setup(); s.view.setEnabled(true); s.view.update([entry()], methods, models, 0);
  const item = s.view.items[0]; item.button.onclick(); assert.equal(s.view.panel.hidden, false);
  s.view.setMetadataVisible(false);
  assert.equal(s.view.panel.hidden, true); assert.equal(item.button.hidden, true);
  assert.equal(s.view.layer.hidden, false); assert.equal(item.region.hidden, false);
  s.zoom.crop = { left: .25, top: .25, width: .25, height: .5 };
  s.view.update([entry()], methods, models, 0);
  assert.equal(item.region.style.width, '800px'); assert.equal(item.button.hidden, true);
  s.view.setMetadataVisible(true);
  assert.equal(item.button.hidden, false); assert.equal(s.view.panel.hidden, true);
  assert.equal(s.view.items[0].button, item.button);
  item.button.onclick(); assert.equal(s.view.panel.hidden, false);
  s.advance(2011); assert.equal(s.view.layer.hidden, true); assert.equal(s.view.panel.hidden, true);
  s.view.dispose();
});

test('metadata preference persists and updates existing and new tiles without restarting polling', async () => {
  const s = setup(true), controller = s.controller();
  assert.equal(s.metadataToggle.checked, true); assert.equal(s.metadataToggle.disabled, false);
  reply(s.calls[0], response('first')); await flush();
  s.advance(500); const request = s.calls[1];
  s.metadataToggle.checked = false; s.metadataToggle.onchange();
  assert.equal(s.storage.get('scv.detection-overlay-metadata'), 'false');
  assert.equal(s.calls.length, 2); assert.equal(request.options.signal.aborted, false);
  assert.equal(s.view.items[0].button.hidden, true); assert.equal(s.view.layer.hidden, false);
  const added = new s.CameraDetectionOverlay(s.root, s.zoom); added.setAvailable(true);
  s.tiles.set(2, { detections: added }); controller.refresh();
  assert.equal(added.metadataVisible, false);
  reply(request, response('first')); await flush(); assert.equal(s.view.items[0].button.hidden, true);
  s.toggle.checked = false; s.toggle.onchange(); assert.equal(s.metadataToggle.disabled, true);
  controller.dispose(); const restored = s.controller();
  assert.equal(s.metadataToggle.checked, false); assert.equal(s.metadataToggle.disabled, true);
  s.toggle.checked = true; s.toggle.onchange(); assert.equal(s.view.metadataVisible, false);
  assert.equal(s.metadataToggle.disabled, false);
  s.metadataToggle.checked = true; s.metadataToggle.onchange();
  assert.equal(s.view.metadataVisible, true); assert.equal(s.storage.get('scv.detection-overlay-metadata'), 'true');
  restored.dispose(); s.view.dispose(); added.dispose();
});

test('polling is opt-in, single-flight, bounded to visible connected viewing and rejects late configurations', async () => {
  const s = setup(), controller = s.controller();
  assert.equal(s.calls.length, 0); assert.equal(s.timers.size, 0);
  s.toggle.checked = true; s.toggle.onchange(); assert.equal(s.calls.length, 1);
  controller.refresh(); s.advance(700); assert.equal(s.calls.length, 1);
  s.context.configurationId = 'second'; s.context.generation++; controller.refresh();
  assert.equal(s.calls[0].options.signal.aborted, true);
  reply(s.calls[0], response('first')); await flush(); assert.equal(s.view.entries.length, 0);
  s.advance(500); assert.equal(s.calls.length, 2);
  reply(s.calls[1], response('second')); await flush(); assert.equal(s.view.layer.hidden, false);
  s.hide(true); assert.equal(s.view.layer.hidden, true); s.advance(5000); assert.equal(s.calls.length, 2);
  s.hide(false); assert.equal(s.calls.length, 3);
  s.toggle.checked = false; s.toggle.onchange(); reply(s.calls[2], response('second')); await flush();
  assert.equal(s.view.entries.length, 0); assert.equal(s.timers.size, 0);
  controller.dispose(); assert.equal(controller.events.signal.aborted, true);
});

test('server errors clear results and disabled detection offers configuration advice', async () => {
  const s = setup(true), controller = s.controller();
  reply(s.calls[0], response('first')); await flush(); assert.equal(s.view.layer.hidden, false);
  s.advance(500); s.calls[1].resolve({ ok: false, status: 403 }); await flush();
  assert.equal(s.view.layer.hidden, true); assert.match(s.status.textContent, /unavailable/);
  s.advance(500); reply(s.calls[2], { ...response('first', []), enabled: false }); await flush();
  assert.match(s.status.textContent, /Enable detection/); controller.dispose(); assert.equal(s.timers.size, 0);
});

const frameStart = Date.parse('2026-10-08T00:00:00Z');
const dateAt = seconds => new Date(frameStart + seconds * 1000).toISOString();
const detectionFrame = (seconds, x, published = seconds) => ({ capturedAt: dateAt(seconds), publishedAt: dateAt(published),
  detections: x == null ? [] : [{ ...observation(), kind: 'motion', capturedAt: dateAt(seconds), expiresAt: dateAt(seconds + 2),
    bounds: { x, y: .25, width: .1, height: .2 } }] });
const historyResponse = frames => ({ ...response('first', []), generatedAt: dateAt(10), historySeconds: 30, maximumHistorySamples: 600,
  cameras: [{ channel: 1, detections: [], history: { id: 'first', frames } }] });
const historyUpdate = (s, frames, id = 'first', generated = 10, maximum = 600) =>
  s.view.updateHistory({ id, frames }, methods, models, dateAt(generated), 30, maximum);

test('a moving car box follows the displayed frame and never appears ahead of its arrival', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(frameStart - 1);
  historyUpdate(s, [detectionFrame(0, .1), detectionFrame(.5, .3), detectionFrame(1, .7)]);
  assert.equal(s.view.layer.hidden, true);
  s.view.setPlaybackTime(frameStart + 200);
  assert.equal(s.view.items[0].region.style.left, '80px');
  s.view.setPlaybackTime(frameStart + 700);
  assert.equal(s.view.items[0].region.style.left, '240px');
  s.view.setPlaybackTime(frameStart + 1100);
  assert.equal(s.view.items[0].region.style.left, '560px');
  s.view.setPlaybackTime(frameStart + 200); // Playback seek / correction.
  assert.equal(s.view.items[0].region.style.left, '80px');
  s.view.dispose();
});

test('expiry follows the buffered footage and an empty sample clears boxes at the corresponding frame', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(frameStart + 200);
  historyUpdate(s, [detectionFrame(0, .1), detectionFrame(1, null)], 'first', 10);
  assert.equal(s.view.layer.hidden, false); // Observation expired eight seconds ago in wall time.
  s.advance(5000); assert.equal(s.view.layer.hidden, false);
  s.view.setPlaybackTime(frameStart + 1000); assert.equal(s.view.layer.hidden, true);
  historyUpdate(s, [detectionFrame(1.1, .5)]);
  s.view.setPlaybackTime(frameStart + 1200); assert.equal(s.view.layer.hidden, false);
  s.view.setPlaybackTime(frameStart + 3100); assert.equal(s.view.layer.hidden, true);
  s.view.dispose();
});

test('incremental history deduplicates, rejects malformed times, honors budgets and changes epochs', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(frameStart + 750);
  historyUpdate(s, [detectionFrame(0, .1), detectionFrame(.5, .3)]);
  historyUpdate(s, [detectionFrame(.5, .3), detectionFrame(1, .7), { ...detectionFrame(2, .8), capturedAt: 'bad' }]);
  assert.equal(s.view.frames.length, 3); assert.equal(s.view.items[0].region.style.left, '240px');
  historyUpdate(s, [detectionFrame(1.5, .6)], 'first', 10, 2);
  assert.equal(s.view.frames.length, 2); assert.equal(s.view.layer.hidden, true); // No retained sample at displayed time.
  historyUpdate(s, [detectionFrame(2, .4)], 'second');
  assert.equal(s.view.frames.length, 1); assert.equal(s.view.layer.hidden, true);
  s.view.setPlaybackTime(frameStart + 2100); assert.equal(s.view.items[0].region.style.left, '320px');
  historyUpdate(s, [], 'second', 40); assert.equal(s.view.frames.length, 0);
  s.view.dispose();
});

test('missing playback timing hides encoded overlays, while independent cameras select their own history', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(null);
  historyUpdate(s, [detectionFrame(0, .1), detectionFrame(.5, .3)]);
  assert.equal(s.view.layer.hidden, true);
  s.view.setPlaybackTime(frameStart + 200); assert.equal(s.view.layer.hidden, false);
  const other = setup(); other.view.setEnabled(true); other.view.setPlaybackTime(frameStart + 700);
  historyUpdate(other, [detectionFrame(0, .1), detectionFrame(.5, .3)]);
  assert.equal(s.view.items[0].region.style.left, '80px');
  assert.equal(other.view.items[0].region.style.left, '240px');
  s.view.setPlaybackTime(null); assert.equal(s.view.layer.hidden, true);
  s.view.dispose(); other.view.dispose();
});

test('history selection skips repainting between detector samples and preserves source evidence age', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(frameStart + 200);
  const frame = detectionFrame(.1, .1); frame.detections[0].capturedAt = dateAt(0);
  historyUpdate(s, [frame]);
  let refreshed = 0; const refresh = s.view.refresh.bind(s.view); s.view.refresh = () => { ++refreshed; refresh(); };
  for (let i = 201; i < 300; i++) s.view.setPlaybackTime(frameStart + i);
  assert.equal(refreshed, 0);
  s.view.setPlaybackTime(frameStart + 2100); assert.equal(refreshed, 1); assert.equal(s.view.layer.hidden, true);
  s.view.dispose();
});

test('submillisecond publications remain distinct and missing timing avoids repeated hidden repaints', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(null);
  const first = detectionFrame(0, .1), second = detectionFrame(0, .2);
  first.publishedAt = '2026-10-08T00:00:00.0001000Z'; second.publishedAt = '2026-10-08T00:00:00.0002000Z';
  historyUpdate(s, [first, second]); historyUpdate(s, [second]);
  assert.equal(s.view.frames.length, 2);
  let refreshed = 0; const refresh = s.view.refresh.bind(s.view); s.view.refresh = () => { ++refreshed; refresh(); };
  for (let i = 0; i < 20; i++) s.view.setPlaybackTime(null);
  assert.equal(refreshed, 0); assert.equal(s.view.layer.hidden, true);
  s.view.dispose();
});

test('an absent history or camera clears previously retained observations immediately', () => {
  const s = setup(); s.view.setEnabled(true); s.view.setPlaybackTime(frameStart + 200);
  historyUpdate(s, [detectionFrame(0, .1)]); assert.equal(s.view.layer.hidden, false);
  s.view.update([], methods, models, 0);
  assert.equal(s.view.frames.length, 0); assert.equal(s.view.layer.hidden, true);
  s.view.update([{ observation: observation(), remainingSeconds: 2 }], methods, models, 0);
  assert.equal(s.view.layer.hidden, false); assert.equal(s.view.hasHistory, false);
  s.view.dispose();
});

test('healthy motion samples remain visible when paced source drift exceeds the detection history window', () => {
  const paced = setup(), normal = setup();
  for (const [camera, rate] of [[paced, 1.05], [normal, 1]]) {
    camera.view.setEnabled(true); camera.clock = new camera.CameraFrameClock();
    for (let second = 0; second < 720; second++) {
      const generated = (second + 1) * rate;
      camera.clock.update({ streamId: 'live', startSeconds: second, endSeconds: second + 1, producedAt: dateAt(generated) });
      camera.view.setPlaybackTime(camera.clock.time(second + .5));
      historyUpdate(camera, [detectionFrame((second + .5) * rate - .1, .2)], 'live', generated);
      assert.equal(camera.view.layer.hidden, false);
      assert.ok(camera.view.presentedFrame.captured <= camera.view.playbackTime);
    }
  }
  assert.ok(paced.view.frames.length < 32); assert.ok(normal.view.frames.length < 32);
  paced.view.dispose(); normal.view.dispose();
});

test('fresh results become visible after a source pause even when old detection history has expired', () => {
  const s = setup(), clock = new s.CameraFrameClock(); s.view.setEnabled(true);
  clock.update({ streamId: 'live', startSeconds: 0, endSeconds: 1, producedAt: dateAt(1) });
  historyUpdate(s, [detectionFrame(.4, .1)], 'live', 1);
  s.view.setPlaybackTime(clock.time(.5)); assert.equal(s.view.layer.hidden, false);
  clock.update({ streamId: 'live', startSeconds: 1, endSeconds: 2, producedAt: dateAt(42) });
  clock.update({ streamId: 'live', startSeconds: 2, endSeconds: 3, producedAt: dateAt(43) });
  historyUpdate(s, [detectionFrame(42.4, .4)], 'live', 43);
  s.view.setPlaybackTime(clock.time(2.5));
  assert.equal(s.view.frames.length, 1); assert.equal(s.view.layer.hidden, false);
  assert.equal(s.view.items[0].region.style.left, '320px'); s.view.dispose();
});

test('poll cursors request only new publications and reset after failures or configuration changes', async () => {
  const s = setup(true), controller = s.controller();
  s.view.setPlaybackTime(frameStart + 200);
  reply(s.calls[0], historyResponse([detectionFrame(0, .1)])); await flush();
  s.advance(500); assert.match(s.calls[1].url, /since=2026-10-08T00%3A00%3A10/);
  reply(s.calls[1], historyResponse([detectionFrame(.5, .3)])); await flush();
  assert.equal(s.view.frames.length, 2); assert.equal(s.view.items[0].region.style.left, '80px');
  s.advance(500); s.calls[2].resolve({ ok: false, status: 503 }); await flush();
  s.advance(500); assert.equal(s.calls[3].url, '/api/detections');
  reply(s.calls[3], historyResponse([detectionFrame(0, .1)])); await flush();
  s.context.generation++; controller.refresh();
  assert.equal(s.calls.at(-1).url, '/api/detections');
  controller.dispose(); s.view.dispose();
});
