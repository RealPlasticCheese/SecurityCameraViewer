const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const webRoot = join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot');
const source = readFileSync(join(webRoot, 'camera-zoom.js'), 'utf8') + '\n' + readFileSync(join(webRoot, 'camera-enhancements.js'), 'utf8');
const { CameraZoom, CameraEnhancements, CameraEnhancementProcessor, CameraEnhancementView, CameraEnhancementPreferences } = runInNewContext(
  `${source}\n({ CameraZoom, CameraEnhancements, CameraEnhancementProcessor, CameraEnhancementView, CameraEnhancementPreferences })`, { AbortController, setTimeout, clearTimeout });
const plain = value => JSON.parse(JSON.stringify(value));
const settle = async () => { for (let i = 0; i < 6; i++) await Promise.resolve(); };

function node() {
  const classes = new Set(), handlers = new Map();
  return { children: [], style: {}, hidden: false, value: '', checked: false, textContent: '',
    classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value),
      toggle(value, on) { if (on) classes.add(value); else classes.delete(value); } },
    append(...children) { this.children.push(...children); }, setAttribute(name, value) { this[name] = value; },
    addEventListener(name, fn) { handlers.set(name, fn); }, emit(name, event = {}) { handlers.get(name)?.(event); },
    remove() { this.removed = true; } };
}

function viewer({ delayed = false, unsupported = false, preferences } = {}) {
  const frames = new Map(), processors = [], calls = [];
  let nextFrame = 0, loads = 0, resolve;
  const window = { ...node(), requestAnimationFrame(fn) { frames.set(++nextFrame, fn); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); } };
  const document = { ...node(), defaultView: window, hidden: false, createElement: node,
    createTextNode: text => ({ textContent: text }) };
  const img = { ...node(), tagName: 'IMG', complete: true, naturalWidth: 640, naturalHeight: 360, currentSrc: 'blob:1' };
  const video = { ...node(), tagName: 'VIDEO', videoWidth: 640, videoHeight: 360, readyState: 2, currentTime: 0 };
  const surface = { ...node(), querySelector: type => type === 'img' ? img : video,
    getBoundingClientRect: () => ({ width: 1000, height: 600 }) };
  const root = { ...node(), ownerDocument: document, querySelector: () => surface };
  root.classList.add('has-frame');
  const zoom = { help: node(), toolbar: node(), crop: null,
    position(media, viewport) {
      const bounds = CameraZoom.picture(viewport.width, viewport.height, media.width, media.height);
      media.style.transform = this.crop && bounds ? CameraZoom.transform(this.crop, bounds, viewport.width, viewport.height) : '';
    } };
  const pending = delayed ? new Promise(done => { resolve = done; }) : Promise.resolve({});
  const view = new CameraEnhancementView(root, zoom, () => { loads++; return unsupported ? Promise.reject(new Error('No WebGL')) : pending; },
    () => {
      const canvas = { ...node(), width: 640, height: 360 };
      const processor = { canvas, disposed: 0,
        render(media, settings) { if (processor.fail) throw new Error('Processing failed'); calls.push({ media, settings: plain(settings) }); },
        dispose() { this.disposed++; this.canvas.remove(); } };
      processors.push(processor); return processor;
    }, preferences);
  const tick = time => {
    const entry = frames.entries().next().value;
    if (entry) { frames.delete(entry[0]); entry[1](time); }
  };
  const enable = async () => {
    view.setFocused(true); if (!view.optionsOpen) view.toggle.emit('click');
    view.enabledToggle.checked = true; view.enabledToggle.emit('change'); await settle(); tick(0);
  };
  return { view, root, surface, img, video, document, window, zoom, frames, processors, calls, tick, enable,
    loads: () => loads, resolve: () => resolve({}) };
}

test('rewind loading and gaps never enhance retained live pictures and resume only with decoded history', async () => {
  const v = viewer(); await v.enable();
  v.root.classList.add('grid-media-ready'); v.root.classList.add('rewind-active');
  v.view.refresh();
  assert.equal(v.view.media(), null); assert.equal(v.view.enabled, true); assert.equal(v.processors[0].disposed, 1);
  v.root.classList.add('live-media-ready'); v.view.refresh(); await settle(); v.tick(100);
  assert.equal(v.calls.at(-1).media, v.video);
  v.root.classList.remove('live-media-ready'); v.view.refresh();
  assert.equal(v.view.media(), null); assert.equal(v.view.enabled, true); v.view.dispose();
});

test('grid and disabled enhancement controls never load the library or start pixel processing', async () => {
  const v = viewer(); assert.equal(v.view.panel.hidden, true);
  v.view.setEnabled(true); v.view.setOptionsOpen(true); await settle();
  assert.equal(v.loads(), 0); assert.equal(v.view.toggle['aria-expanded'], 'false'); assert.equal(v.view.enabledToggle.checked, false);
  v.view.setFocused(true); v.view.refresh(); await settle();
  assert.equal(v.loads(), 0); assert.equal(v.frames.size, 0); assert.equal(v.processors.length, 0);
});

test('opt-in exposes advanced settings and disabling immediately restores the original picture', async () => {
  const v = viewer(); await v.enable();
  assert.equal(v.view.panel.hidden, false); assert.equal(v.view.toggle['aria-expanded'], 'true');
  assert.equal(v.view.enabledToggle.checked, true);
  assert.equal(v.view.inputs.size, 8); assert.equal(v.calls.length, 1);
  assert.ok(v.root.classList.contains('enhancement-ready'));
  v.view.setEnabled(false);
  assert.equal(v.view.panel.hidden, false); assert.equal(v.view.enabledToggle.checked, false);
  assert.equal(v.frames.size, 0);
  assert.equal(v.root.classList.contains('enhancement-ready'), false); assert.equal(v.processors[0].disposed, 1);
});

test('opening the enhancement options and editing settings does not start processing', async () => {
  const v = viewer(); v.view.setFocused(true);
  v.view.toggle.emit('click');
  assert.equal(v.view.panel.hidden, false); assert.equal(v.view.enabledToggle.checked, false);
  const slider = v.view.inputs.get('brightness').input; slider.value = '18'; slider.emit('input');
  await settle(); assert.equal(v.loads(), 0); assert.equal(v.processors.length, 0); assert.equal(v.frames.size, 0);
  v.view.toggle.emit('click');
  assert.equal(v.view.panel.hidden, true); assert.equal(v.view.enabled, false);
  v.view.toggle.emit('click');
  assert.equal(v.view.inputs.get('brightness').input.value, 18);
});

test('the toolbar switch enables and disables processing while settings stay collapsed', async () => {
  const v = viewer(); v.view.setFocused(true);
  v.view.enabledToggle.checked = true; v.view.enabledToggle.emit('change'); await settle(); v.tick(0);
  assert.equal(v.view.panel.hidden, true); assert.equal(v.view.toggle['aria-expanded'], 'false');
  assert.equal(v.calls.length, 1); assert.ok(v.root.classList.contains('enhancement-ready'));
  v.view.toggle.emit('click'); assert.equal(v.view.panel.hidden, false);
  assert.equal(v.view.toggle['aria-expanded'], 'true'); assert.equal(v.view.toggle['aria-label'], 'Hide enhancement options');
  v.view.toggle.emit('click'); assert.equal(v.view.panel.hidden, true);
  assert.equal(v.view.enabledToggle.checked, true);
  v.view.enabledToggle.checked = false; v.view.enabledToggle.emit('change');
  assert.equal(v.view.panel.hidden, true); assert.equal(v.processors[0].disposed, 1);
  assert.equal(v.root.classList.contains('enhancement-ready'), false);
  v.view.dispose();
});

test('collapsing the enhancement options keeps the same renderer, settings and live frames running', async () => {
  const v = viewer(); await v.enable();
  v.view.preset.value = 'detail'; v.view.preset.emit('change'); v.tick(100);
  const processor = v.view.processor;
  v.view.toggle.emit('click');
  assert.equal(v.view.panel.hidden, true); assert.equal(v.view.enabled, true);
  assert.equal(v.view.enabledToggle.checked, true);
  assert.equal(v.view.processor, processor); assert.equal(processor.disposed, 0);
  v.img.currentSrc = 'blob:2'; v.view.refresh(); v.tick(200);
  assert.equal(v.calls.length, 3); assert.deepEqual(v.calls.at(-1).settings, plain(CameraEnhancements.presets.detail));
  v.root.classList.add('live-media-ready'); v.view.refresh(); v.tick(300);
  v.video.currentTime = 0.1; v.tick(400); assert.equal(v.calls.at(-1).media, v.video);
  v.view.toggle.emit('click');
  assert.equal(v.view.panel.hidden, false); assert.equal(v.view.preset.value, 'detail');
  assert.equal(v.view.processor, processor); assert.equal(v.loads(), 1);
});

test('the enable switch restores the original without closing options and retains settings for re-enabling', async () => {
  const v = viewer(); await v.enable();
  const slider = v.view.inputs.get('brightness').input; slider.value = '18'; slider.emit('input'); v.tick(100);
  v.view.enabledToggle.checked = false; v.view.enabledToggle.emit('change');
  assert.equal(v.view.panel.hidden, false); assert.equal(v.view.toggle['aria-expanded'], 'true');
  assert.equal(v.view.enabled, false);
  assert.equal(v.root.classList.contains('enhancement-ready'), false); assert.equal(v.processors[0].disposed, 1);
  v.view.enabledToggle.checked = true; v.view.enabledToggle.emit('change'); await settle(); v.tick(200);
  assert.equal(v.calls.at(-1).settings.brightness, 18); assert.equal(v.view.preset.value, 'custom');
  v.view.setOptionsOpen(false); v.view.setFocused(false);
  assert.equal(v.view.enabledToggle.checked, true); assert.equal(v.view.toggle['aria-expanded'], 'false');
  assert.equal(v.view.panel.hidden, true);
  v.view.dispose();
  assert.ok(v.processors.every(processor => processor.disposed === 1));
});

test('presets, sliders and reset redraw the retained picture and preserve source identity', async () => {
  const v = viewer(); await v.enable();
  v.view.preset.value = 'lowLight'; v.view.preset.emit('change'); v.tick(100);
  assert.deepEqual(v.calls.at(-1).settings, plain(CameraEnhancements.presets.lowLight));
  const slider = v.view.inputs.get('sharpness').input;
  slider.value = '88'; slider.emit('input'); v.tick(200);
  assert.equal(v.calls.at(-1).settings.sharpness, 88); assert.equal(v.view.preset.value, 'custom');
  v.view.resetButton.emit('click'); v.tick(300);
  assert.deepEqual(v.calls.at(-1).settings, plain(CameraEnhancements.presets.balanced));
  assert.ok(v.calls.every(call => call.media === v.img));
});

test('JPEG processing skips unchanged pictures and caps rapid frame and slider changes at 10 fps', async () => {
  const v = viewer(); await v.enable();
  v.view.refresh(); v.tick(20); assert.equal(v.calls.length, 1); assert.equal(v.frames.size, 0);
  v.img.currentSrc = 'blob:2'; v.view.refresh(); v.tick(30); assert.equal(v.calls.length, 1);
  v.view.invalidate(); v.tick(70); assert.equal(v.calls.length, 1); assert.equal(v.frames.size, 1);
  v.tick(100); assert.equal(v.calls.length, 2); assert.equal(v.frames.size, 0);
});

test('live video advances without JPEG loads, switches back to JPEG and follows zoom without reprocessing', async () => {
  const v = viewer(); await v.enable();
  v.root.classList.add('live-media-ready'); v.view.refresh(); v.tick(100);
  assert.equal(v.calls.at(-1).media, v.video);
  v.video.currentTime = 0.1; v.tick(200); assert.equal(v.calls.length, 3);
  v.zoom.crop = { left: 0.25, top: 0.25, width: 0.5, height: 0.5 }; v.view.refresh();
  assert.match(v.processors[0].canvas.style.transform, /scale/); assert.equal(v.calls.length, 3);
  v.zoom.crop = null; v.view.refresh(); assert.equal(v.processors[0].canvas.style.transform, '');
  v.root.classList.remove('live-media-ready'); v.tick(300);
  assert.equal(v.calls.at(-1).media, v.img); assert.equal(v.frames.size, 0);
});

test('hidden tabs release the renderer and resume with enhancements retained in the grid', async () => {
  const v = viewer(); await v.enable();
  v.document.hidden = true; v.document.emit('visibilitychange');
  assert.equal(v.processors[0].disposed, 1); assert.equal(v.view.enabled, true); assert.equal(v.frames.size, 0);
  v.document.hidden = false; v.document.emit('visibilitychange'); await settle(); v.tick(100);
  assert.equal(v.processors.length, 2); assert.equal(v.calls.length, 2);
  v.view.setFocused(false); assert.equal(v.view.enabled, true); assert.equal(v.view.panel.hidden, true);
  v.view.setFocused(true); await settle(); assert.equal(v.processors.length, 2);
});

test('late library loads cannot allocate a renderer after disabling or disposing the camera', async () => {
  for (const stop of [v => v.view.setEnabled(false), v => v.view.dispose()]) {
    const v = viewer({ delayed: true }); v.view.setFocused(true); v.view.setEnabled(true);
    stop(v); v.resolve(); await settle();
    assert.equal(v.processors.length, 0); assert.equal(v.frames.size, 0); assert.equal(v.view.enabled, false);
  }
});

test('unsupported processing, failed draws and context loss restore the original and allow a retry', async () => {
  const unsupported = viewer({ unsupported: true }); await unsupported.enable();
  assert.match(unsupported.view.status.textContent, /unavailable/); assert.equal(unsupported.view.enabledToggle.checked, false);
  assert.equal(unsupported.view.toggle['aria-expanded'], 'true'); assert.equal(unsupported.view.panel.hidden, false);
  for (const fail of [v => { v.processors[0].fail = true; v.view.invalidate(); v.tick(100); },
    v => v.processors[0].canvas.emit('webglcontextlost')]) {
    const v = viewer(); await v.enable(); fail(v);
    assert.equal(v.view.enabled, false); assert.equal(v.root.classList.contains('enhancement-ready'), false);
    assert.equal(v.processors[0].disposed, 1); assert.match(v.view.status.textContent, /Original picture/);
    await v.enable(); assert.equal(v.processors.length, 2); assert.ok(v.root.classList.contains('enhancement-ready'));
  }
});

test('picture expiry and repeated toggle cycles release every renderer and pending frame', async () => {
  const v = viewer(); await v.enable(); v.root.classList.remove('has-frame'); v.view.refresh();
  assert.equal(v.processors[0].disposed, 1); assert.equal(v.root.classList.contains('enhancement-ready'), false);
  v.root.classList.add('has-frame');
  for (let i = 0; i < 10; i++) { await v.enable(); v.view.setEnabled(false); }
  v.view.dispose(); assert.ok(v.processors.every(processor => processor.disposed === 1));
  assert.equal(v.frames.size, 0); assert.equal(v.view.events.signal.aborted, true);
});

test('filter order removes noise before sharpening and neutral values apply no adjustments', () => {
  const calls = [], canvas = {};
  for (const method of ['draw', 'denoise', 'curves', 'brightnessContrast', 'hueSaturation', 'vibrance', 'unsharpMask', 'update']) {
    canvas[method] = (...args) => { calls.push({ method, args }); return canvas; };
  }
  const neutral = { brightness: 0, contrast: 0, gamma: 1, denoise: 0, sharpness: 0, radius: 1, saturation: 0, vibrance: 0 };
  CameraEnhancements.apply(canvas, {}, neutral); assert.deepEqual(calls.map(call => call.method), ['draw', 'update']);
  calls.length = 0;
  CameraEnhancements.apply(canvas, {}, { ...neutral, denoise: 100, gamma: 1.5, sharpness: 100, contrast: 12, saturation: -100, vibrance: 10 });
  assert.deepEqual(calls.map(call => call.method), ['draw', 'denoise', 'curves', 'brightnessContrast', 'hueSaturation', 'vibrance', 'unsharpMask', 'update']);
  const curve = calls.find(call => call.method === 'curves').args[0];
  assert.deepEqual(plain(curve[0]), [0, 0]); assert.deepEqual(plain(curve.at(-1)), [1, 1]);
  assert.ok(curve[4][1] > 0.5);
  const normalized = CameraEnhancements.normalize({ gamma: Infinity, denoise: -10, sharpness: 999 });
  assert.equal(normalized.gamma, 1); assert.equal(normalized.denoise, 0); assert.equal(normalized.sharpness, 100);
});

test('processor bounds large source frames, reuses its texture, and releases GPU and staging resources', () => {
  let uploads = 0, destroyed = 0, lost = 0;
  const input = { width: 0, height: 0, getContext: () => ({ drawImage() {} }) };
  const canvas = { ...node(), texture(staging) {
    assert.equal(staging, input); uploads++; return { loadContentsOf() { uploads++; }, destroy() { destroyed++; } };
  }, draw() {}, brightnessContrast() {}, unsharpMask() {}, update() {},
    getContext: () => ({ getExtension: () => ({ loseContext() { lost++; } }) }) };
  const processor = new CameraEnhancementProcessor({ canvas: () => canvas }, { createElement: () => input });
  const media = { videoWidth: 3840, videoHeight: 2160 };
  processor.render(media, CameraEnhancements.presets.balanced);
  assert.equal(input.width, 1280); assert.equal(input.height, 720);
  processor.render(media, CameraEnhancements.presets.balanced); assert.equal(uploads, 2);
  processor.dispose(); assert.equal(destroyed, 1); assert.equal(lost, 1); assert.equal(canvas.removed, true);
  assert.equal(input.width * input.height, 1);
});


test('returning to the grid keeps enhancement and custom settings only on the opted-in camera', async () => {
  const selected = viewer(), other = viewer(); await selected.enable();
  const slider = selected.view.inputs.get('brightness').input; slider.value = '18'; slider.emit('input');
  const processor = selected.view.processor;
  selected.view.setFocused(false); other.view.setFocused(false);
  selected.img.currentSrc = 'blob:grid'; selected.view.refresh(); selected.tick(100); await settle();
  assert.equal(selected.view.enabledToggle.checked, true); assert.equal(selected.view.panel.hidden, true);
  assert.equal(selected.view.processor, processor); assert.equal(processor.disposed, 0);
  assert.equal(selected.calls.at(-1).settings.brightness, 18); assert.equal(selected.calls.at(-1).media, selected.img);
  assert.equal(other.view.enabled, false); assert.equal(other.loads(), 0); assert.equal(other.processors.length, 0);
  selected.view.setFocused(true); selected.view.setOptionsOpen(true);
  assert.equal(selected.view.enabledToggle.checked, true); assert.equal(selected.view.preset.value, 'custom');
  selected.view.setEnabled(false); selected.view.setFocused(false); await settle();
  assert.equal(selected.view.enabled, false); assert.equal(selected.view.processor, null);
});

test('opening another camera pauses the hidden enhanced camera and grid return resumes its settings', async () => {
  const selected = viewer(), other = viewer(); await selected.enable();
  selected.view.preset.value = 'lowLight'; selected.view.preset.emit('change');
  selected.root.classList.add('live-media-ready'); selected.view.refresh(); selected.tick(100);
  selected.view.setFocused(false, false); other.view.setFocused(true);
  assert.equal(selected.view.enabled, true); assert.equal(selected.view.enabledToggle.checked, true);
  assert.equal(selected.processors[0].disposed, 1); assert.equal(selected.frames.size, 0);
  selected.document.emit('visibilitychange'); selected.window.emit('pageshow'); selected.view.invalidate(); await settle();
  assert.equal(selected.processors.length, 1); assert.equal(other.loads(), 0);
  selected.view.setFocused(false, true); other.view.setFocused(false, true); await settle(); selected.tick(200);
  assert.equal(selected.processors.length, 2); assert.equal(selected.calls.at(-1).media, selected.video);
  assert.deepEqual(selected.calls.at(-1).settings, plain(CameraEnhancements.presets.lowLight));
});

test('a pending library load completes in the grid for the opted-in camera', async () => {
  const v = viewer({ delayed: true }); v.view.setFocused(true); v.view.setEnabled(true);
  v.view.setFocused(false); v.resolve(); await settle(); v.tick(0);
  assert.equal(v.view.enabled, true); assert.equal(v.processors.length, 1);
  assert.ok(v.root.classList.contains('enhancement-ready'));
});

test('a late library load cannot allocate for a hidden camera and can resume on grid return', async () => {
  const v = viewer({ delayed: true }); v.view.setFocused(true); v.view.setEnabled(true);
  v.view.setFocused(false, false); v.resolve(); await settle();
  assert.equal(v.view.enabled, true); assert.equal(v.processors.length, 0); assert.equal(v.frames.size, 0);
  v.view.setFocused(false, true); await settle(); v.tick(0);
  assert.equal(v.processors.length, 1); assert.ok(v.root.classList.contains('enhancement-ready'));
});

test('grid canvas frames stay enhanced and missing pictures release resources without losing opt-in', async () => {
  const v = viewer(); await v.enable(); v.view.setFocused(false);
  const canvas = { tagName: 'CANVAS', width: 640, height: 360, frameVersion: 1 };
  const querySelector = v.surface.querySelector;
  v.surface.querySelector = selector => selector === '.grid-camera-canvas' ? canvas : querySelector(selector);
  v.root.classList.add('grid-media-ready'); v.view.refresh(); v.tick(100);
  assert.equal(v.calls.at(-1).media, canvas); canvas.frameVersion++; v.tick(200);
  assert.equal(v.calls.length, 3);
  v.root.classList.remove('grid-media-ready'); v.root.classList.remove('has-frame'); v.tick(300);
  assert.equal(v.view.enabled, true); assert.equal(v.processors[0].disposed, 1); assert.equal(v.frames.size, 0);
  v.root.classList.add('has-frame'); v.view.refresh(); await settle(); v.tick(400);
  assert.equal(v.processors.length, 2); assert.equal(v.calls.at(-1).media, v.img);
});

test('grid processing failures disable only the affected camera and preserve its settings for retry', async () => {
  for (const fail of [v => { v.processors[0].fail = true; v.view.invalidate(); v.tick(100); },
    v => v.processors[0].canvas.emit('webglcontextlost')]) {
    const v = viewer(), other = viewer(); await v.enable(); await other.enable();
    v.view.preset.value = 'detail'; v.view.preset.emit('change');
    v.view.setFocused(false); other.view.setFocused(false); fail(v);
    assert.equal(v.view.enabledToggle.checked, false); assert.equal(v.view.processor, null);
    assert.equal(v.root.classList.contains('enhancement-ready'), false); assert.match(v.view.status.textContent, /Original picture/);
    assert.equal(other.view.enabled, true); assert.equal(other.processors[0].disposed, 0);
    v.view.setFocused(true); v.view.setEnabled(true); await settle(); v.tick(200);
    assert.deepEqual(v.calls.at(-1).settings, plain(CameraEnhancements.presets.detail));
  }
});

test('viewer arrangement marks all grid cameras visible and only the focused camera visible in single view', () => {
  const appSource = readFileSync(join(webRoot, 'app.js'), 'utf8');
  const arrangeSource = appSource.slice(appSource.indexOf('function arrange() {'), appSource.indexOf("window.addEventListener('resize'"));
  const selected = viewer(), other = viewer();
  const tiles = new Map([[1, { zoom: { setFocused() {} }, enhancements: selected.view }],
    [2, { zoom: { setFocused() {} }, enhancements: other.view }]]);
  const controls = { cameras: { clientWidth: 1000, clientHeight: 600 }, 'camera-focus': {}, 'grid-button': {} };
  const cameraPictureInPicture = { video: null }, layouts = [];
  const arrange = runInNewContext(`${arrangeSource}\nvalue => { focus = value; arrange(); }`, {
    tiles, cameraPictureInPicture, focus: null, pushToTalk: { channel: null }, renderLiveAudio() {}, updatePictureInPicture() {}, $: id => controls[id],
    cameraLayout: { configure(settings, tiles, columns, focus) { layouts.push({ columns, focus }); } }, state: { settings: {} }, window: { innerWidth: 1000 }, liveRewind: null
  });
  arrange(null); assert.equal(selected.view.visible, true); assert.equal(other.view.visible, true);
  arrange(1); assert.equal(selected.view.focused, true); assert.equal(other.view.visible, false);
  arrange(2); assert.equal(selected.view.visible, false); assert.equal(other.view.focused, true);
  arrange(null); assert.equal(selected.view.focused, false); assert.equal(other.view.focused, false);
  assert.equal(selected.view.visible, true); assert.equal(other.view.visible, true);
  cameraPictureInPicture.video = {}; controls.cameras.clientWidth = 0;
  const previousLayouts = layouts.length; arrange(null);
  assert.equal(layouts.length, previousLayouts, 'Android hiding the viewer must not reconfigure the camera layout.');
  assert.equal(selected.view.visible, true); assert.equal(other.view.visible, true);
  controls.cameras.clientWidth = 1000; arrange(1);
  assert.equal(layouts.length, previousLayouts + 1);
  assert.equal(selected.view.focused, true); assert.equal(other.view.visible, false);
});


function savedPreferences(value = {}) {
  const saves = [], timers = new Map();
  let timerId = 0;
  const preferences = new CameraEnhancementPreferences(value, async patch => saves.push(plain(patch)), {
    setTimer(callback) { timers.set(++timerId, callback); return timerId; },
    clearTimer(id) { timers.delete(id); }
  });
  return { preferences, saves, timers };
}

function slider(v, key, value, commit = true) {
  const input = v.view.inputs.get(key).input;
  input.value = String(value); input.emit('input');
  if (commit) input.emit('change');
}

test('saved values and switch restore in a fresh browser view, including grid processing', async () => {
  const stored = { enabled: true, ...plain(CameraEnhancements.presets.lowLight), radius: 2.4 };
  const v = viewer({ preferences: new CameraEnhancementPreferences(stored) });
  assert.equal(v.view.enabledToggle.checked, true); assert.equal(v.view.panel.hidden, true);
  for (const { key } of CameraEnhancements.controls) assert.equal(v.view.inputs.get(key).input.value, stored[key]);
  assert.equal(v.view.preset.value, 'custom');
  v.view.setFocused(false); await settle(); v.tick(0);
  assert.equal(v.calls.length, 1); assert.deepEqual(v.calls[0].settings, plain(CameraEnhancements.normalize(stored)));
  v.view.dispose();
});

test('each slider saves while enhancements are disabled and switch patches retain all values', async () => {
  const h = savedPreferences(), v = viewer(h);
  v.view.setFocused(true); v.view.setOptionsOpen(true);
  for (const { key, max } of CameraEnhancements.controls) { slider(v, key, max); await settle(); }
  assert.equal(v.loads(), 0); assert.equal(h.saves.length, 8);
  v.view.enabledToggle.checked = true; v.view.enabledToggle.emit('change'); await settle();
  v.view.enabledToggle.checked = false; v.view.enabledToggle.emit('change'); await settle();
  assert.deepEqual(h.saves.at(-1), { enabled: false });
  for (const { key, max } of CameraEnhancements.controls) assert.equal(h.preferences.value[key], max);
  assert.deepEqual(plain(v.view.settings), plain(CameraEnhancements.normalize(h.preferences.value)));
  v.view.dispose();
});

test('undo and redo restore sliders, presets and reset in order without changing the switch', async () => {
  const h = savedPreferences(), v = viewer(h); await v.enable();
  const baseline = plain(v.view.settings);
  slider(v, 'brightness', 18); await settle();
  v.view.preset.value = 'lowLight'; v.view.preset.emit('change'); await settle();
  v.view.resetButton.emit('click'); await settle();
  assert.deepEqual(plain(v.view.settings), baseline); assert.equal(v.view.redoButton.disabled, true);
  for (const [values, preset] of [[CameraEnhancements.presets.lowLight, 'lowLight'], [{ ...baseline, brightness: 18 }, 'custom'], [baseline, 'balanced']]) {
    v.view.undoButton.emit('click'); await settle();
    assert.deepEqual(plain(v.view.settings), plain(values)); assert.equal(v.view.preset.value, preset);
    assert.equal(v.view.enabled, true);
  }
  assert.equal(v.view.undoButton.disabled, true); assert.equal(v.view.redoButton.disabled, false);
  for (const [values, preset] of [[{ ...baseline, brightness: 18 }, 'custom'], [CameraEnhancements.presets.lowLight, 'lowLight'], [baseline, 'balanced']]) {
    v.view.redoButton.emit('click'); await settle();
    assert.deepEqual(plain(v.view.settings), plain(values)); assert.equal(v.view.preset.value, preset);
    assert.deepEqual(plain(CameraEnhancements.normalize(h.preferences.value)), plain(values));
  }
  assert.equal(v.view.redoButton.disabled, true); v.view.dispose();
});

test('one slider gesture with many input events is one undo action and supports undo before release', () => {
  const v = viewer(); v.view.setFocused(true); v.view.setOptionsOpen(true);
  for (const value of [1, 7, 18]) slider(v, 'brightness', value, false);
  assert.equal(v.view.undoButton.disabled, false);
  v.view.undoButton.emit('click'); assert.equal(v.view.settings.brightness, 0);
  assert.equal(v.view.undoButton.disabled, true);
  v.view.redoButton.emit('click'); assert.equal(v.view.settings.brightness, 18);
  slider(v, 'gamma', 1.5); v.view.undoButton.emit('click');
  assert.equal(v.view.settings.gamma, 1); assert.equal(v.view.settings.brightness, 18);
});

test('new edits discard redo, no-op reset adds no history, and reopening starts a fresh session', () => {
  const v = viewer(); v.view.setFocused(true); v.view.setOptionsOpen(true);
  assert.equal(v.view.undoButton.disabled, true); assert.equal(v.view.redoButton.disabled, true);
  v.view.resetButton.emit('click'); assert.equal(v.view.undoButton.disabled, true);
  slider(v, 'brightness', 18); slider(v, 'contrast', 25);
  v.view.undoButton.emit('click'); slider(v, 'gamma', 1.5);
  assert.equal(v.view.redoButton.disabled, true);
  v.view.setOptionsOpen(false); v.view.setOptionsOpen(true);
  assert.equal(v.view.undoButton.disabled, true); assert.equal(v.view.redoButton.disabled, true);
  assert.equal(v.view.settings.brightness, 18); assert.equal(v.view.settings.gamma, 1.5);
  v.view.setFocused(false); v.view.setFocused(true); v.view.setOptionsOpen(true);
  assert.equal(v.view.undoButton.disabled, true);
});

test('undo remains available while switched off and histories are independent between cameras', async () => {
  const first = viewer(), second = viewer(); await first.enable(); await second.enable();
  slider(first, 'brightness', 18); slider(second, 'gamma', 1.5);
  first.view.enabledToggle.checked = false; first.view.enabledToggle.emit('change');
  first.view.undoButton.emit('click'); assert.equal(first.view.settings.brightness, 0);
  assert.equal(first.view.enabled, false); assert.equal(second.view.settings.gamma, 1.5);
  first.view.redoButton.emit('click'); assert.equal(first.view.settings.brightness, 18);
  first.view.dispose(); second.view.dispose();
});

test('shared settings update closed controls but do not interrupt an open options session', () => {
  const v = viewer(); v.view.setFocused(true);
  const remote = { enabled: false, ...plain(CameraEnhancements.presets.detail) };
  v.view.synchronizePreferences(remote, v.view.preferences.revision);
  assert.equal(v.view.preset.value, 'detail'); assert.deepEqual(plain(v.view.settings), plain(CameraEnhancements.presets.detail));
  v.view.setOptionsOpen(true); slider(v, 'brightness', 18);
  v.view.synchronizePreferences({ ...remote, brightness: 4 }, v.view.preferences.revision);
  assert.equal(v.view.settings.brightness, 18);
  v.view.setOptionsOpen(false);
  v.view.synchronizePreferences({ ...remote, brightness: 4 }, v.view.preferences.revision);
  assert.equal(v.view.settings.brightness, 4); assert.equal(v.view.preset.value, 'custom');
});

test('rapid slider edits debounce into one patch and completion flushes it immediately', async () => {
  const h = savedPreferences(), v = viewer(h); v.view.setFocused(true); v.view.setOptionsOpen(true);
  for (const value of [1, 7, 18]) slider(v, 'brightness', value, false);
  assert.equal(h.saves.length, 0); assert.equal(h.timers.size, 1);
  v.view.inputs.get('brightness').input.emit('change'); await settle();
  assert.deepEqual(h.saves, [{ brightness: 18 }]); assert.equal(h.timers.size, 0);
});

test('saves are serialized, coalesced, and never rolled back by stale polling', async () => {
  const h = savedPreferences(); let release;
  h.preferences.save = patch => { h.saves.push(plain(patch)); return new Promise(resolve => { release = resolve; }); };
  const revision = h.preferences.revision;
  h.preferences.update({ brightness: 18 }); const saving = h.preferences.flush();
  h.preferences.update({ brightness: 22 }); h.preferences.update({ brightness: 30, gamma: 1.5 });
  assert.equal(h.preferences.synchronize({}, revision), false);
  assert.equal(h.preferences.synchronize({}, h.preferences.revision), false);
  assert.deepEqual(h.saves, [{ brightness: 18 }]);
  release(); await settle();
  assert.deepEqual(h.saves, [{ brightness: 18 }, { brightness: 30, gamma: 1.5 }]);
  release(); await saving;
  assert.equal(h.preferences.synchronize({}, revision), false);
  assert.equal(h.preferences.value.brightness, 30);
  assert.equal(h.preferences.synchronize({ brightness: 42 }, h.preferences.revision), true);
  assert.equal(h.preferences.value.brightness, 42);
});

test('failed saves retain pending changes and retry with the newest values', async () => {
  const h = savedPreferences(), v = viewer(h); v.view.setFocused(true); v.view.setOptionsOpen(true);
  h.preferences.save = async () => { throw new Error('Offline'); };
  slider(v, 'brightness', 18); await settle();
  assert.equal(v.view.retryButton.hidden, false); assert.match(v.view.saveStatus.textContent, /could not be saved/);
  assert.equal(h.preferences.synchronize({}, h.preferences.revision), false);
  h.preferences.update({ brightness: 22, gamma: 1.5 });
  h.preferences.save = async patch => h.saves.push(plain(patch));
  v.view.retryButton.emit('click'); await settle();
  assert.deepEqual(h.saves, [{ brightness: 22, gamma: 1.5 }]);
  assert.equal(v.view.retryButton.hidden, true); assert.equal(v.view.saveStatus.hidden, true);
});

test('leaving the page and disposing flush pending edits without persisting a disabled switch', async () => {
  for (const action of [v => v.window.emit('pagehide'), v => v.view.dispose()]) {
    const h = savedPreferences({ enabled: true }), v = viewer(h); v.view.setFocused(true); v.view.setOptionsOpen(true);
    slider(v, 'brightness', 18, false); action(v); await settle();
    assert.deepEqual(h.saves, [{ brightness: 18 }]); assert.equal(h.preferences.value.enabled, true);
  }
});

test('a local rendering failure does not disable enhancements in other browsers or erase saved sliders', async () => {
  const h = savedPreferences({ enabled: true, brightness: 18 }), v = viewer({ ...h, unsupported: true });
  v.view.setFocused(false); await settle();
  assert.equal(v.view.enabled, false); assert.equal(h.preferences.value.enabled, true);
  assert.equal(h.preferences.value.brightness, 18); assert.equal(h.saves.length, 0);
  v.view.synchronizePreferences(plain(h.preferences.value), h.preferences.revision);
  assert.equal(v.view.enabled, false); v.view.dispose();
});


test('Android persistent-video PiP releases the hidden renderer and restores enabled enhancement controls on return', async () => {
  const v = viewer(); await v.enable(); const settings = plain(v.view.settings);
  v.surface.getBoundingClientRect = () => ({ width: 0, height: 0 }); v.view.refresh();
  assert.equal(v.processors[0].disposed, 1); assert.equal(v.view.enabled, true);
  assert.equal(v.view.optionsOpen, true); assert.equal(v.frames.size, 0);
  v.surface.getBoundingClientRect = () => ({ width: 1000, height: 600 });
  v.view.refresh(); await settle(); v.tick(100);
  assert.equal(v.processors.length, 2); assert.deepEqual(plain(v.view.settings), settings);
  assert.equal(v.view.enabledToggle.checked, true); v.view.dispose();
});
