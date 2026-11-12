const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-collection.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/recording-archive.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/detection-templates.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/detection-template-settings.js'), 'utf8') + '\n' +
  readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/recording.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
const plain = value => JSON.parse(JSON.stringify(value));
const rules = channel => ({ channel, enabled: true, detectionSubstream: true, sensitivity: 60 + channel,
  minimumAreaPercent: 1, detectionMethod: 'adaptive', analysisWidth: 320, analysisFramesPerSecond: 5,
  aiModel: 'yolox-nano', aiDevice: 'cpu', aiClasses: ['person'], aiIgnoreClasses: [], aiOptions: {},
  motionGateSensitivity: 80, motionGateMinimumAreaPercent: .1, streamSchedule: [], defaultActionIds: [], zones: [] });

function environment(sources) {
  const nodes = [], images = [], posts = [], activations = [];
  let drawnImage = null;
  class Element {
    constructor(tagName) { this.tagName = tagName; this.children = []; this.dataset = {}; this.attributes = {}; this.listeners = {}; this.value = ''; nodes.push(this); }
    append(...children) { this.children.push(...children.filter(child => typeof child !== 'string')); }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    add(child) { this.append(child); }
    get options() { return this.children; }
    get lastElementChild() { return this.children.at(-1); }
    querySelector(tagName) {
      for (const child of this.children) {
        const match = child.tagName === tagName ? child : child.querySelector(tagName);
        if (match) return match;
      }
      return null;
    }
    setAttribute(name, value) { this.attributes[name] = value; }
    addEventListener(type, callback) { (this.listeners[type] ??= []).push(callback); }
    pause() {}
    load() {}
    removeAttribute(name) { delete this[name]; }
    showModal() { this.open = true; }
    close() { this.open = false; for (const callback of this.listeners.close || []) callback(); }
    focus() { this.focused = true; }
    reportValidity() { return true; }
    getContext() { return { clearRect() { drawnImage = null; }, drawImage(image) { drawnImage = image; },
      beginPath() {}, moveTo() {}, lineTo() {}, closePath() {}, fill() {}, stroke() {}, fillRect() {}, fillText() {} }; }
  }
  const el = id => nodes.find(node => node.id === id) || Object.assign(new Element(), { id });
  function Option(text, value) { return Object.assign(new Element(), { textContent: text, value }); }
  const document = { getElementById: el, createElement: tag => new Element(tag), querySelectorAll: () => [], dispatchEvent() {} };
  const fields = ['enabled', 'directory', 'timeZoneId', 'preCaptureSeconds', 'postCaptureSeconds', 'maximumEventSeconds',
    'bufferMegabytesPerCamera', 'retentionDays', 'maximumStorageGigabytes'];
  const form = el('recording-form'); form.elements = fields.map(el); form.elements.namedItem = el;
  el('detection-template-form').elements = ['detection-template-select', 'detection-template-name', 'detection-template-close', 'detection-template-cancel', 'motion-sensitivity'].map(el);
  el('ai-device').append(new Option('CPU', 'cpu'), new Option('GPU', 'gpu'), new Option('Automatic', 'auto'));
  const viewer = { settings: { sources, channelCount: 2, cameraNames: ['Old name 1', 'Old name 2', 'Old name 3'] } };
  const recording = { settings: { configurationId: 'default', directory: '/recordings', timeZoneId: 'UTC', enabled: false,
    preCaptureSeconds: 10, postCaptureSeconds: 20, maximumEventSeconds: 300, bufferMegabytesPerCamera: 32,
    retentionDays: 7, maximumStorageGigabytes: 100, actions: [], cameras: (sources || [{ channel: 1 }, { channel: 2 }]).map(camera => rules(camera.channel)) },
    detector: 'Test', cameras: [], recordedCameras: [{ channel: 9, name: 'Retired camera' }], eventCount: 0, usedBytes: 0 };
  recording.settings.detectionTemplates = recording.settings.cameras.map(camera => ({ id: String(camera.channel).padStart(32, '0'), name: `Policy ${camera.channel}`, settings: { sensitivity: camera.sensitivity, minimumAreaPercent: camera.minimumAreaPercent, aiClasses: ['person'] } }));
  for (const camera of recording.settings.cameras) camera.detectionTemplateId = String(camera.channel).padStart(32, '0');
  const catalog = { methods: [{ id: 'adaptive', name: 'Motion' }], models: [{ id: 'yolox-nano', license: 'Test' }], gpuDevices: [], notices: [] };
  const response = value => ({ ok: true, headers: { get: () => 'application/json' }, json: async () => structuredClone(value) });
  const context = { document, Option, crypto: require('node:crypto').webcrypto, AbortSignal, structuredClone, Event, setTimeout() {},
    RecordingFilters: class { async initialize() {} snapshot() { return { channel: null, zones: [] }; } },
    AiModelSettingsView: class { load() {} save() {} setEnabled() {} },
    Image: class {
      constructor() { images.push(this); this.naturalWidth = 640; this.naturalHeight = 360; }
      decode() { return new Promise((resolve, reject) => { this.resolve = resolve; this.reject = reject; }); }
    },
    async fetch(url, options) {
      if (url === '/api/state') return response(viewer);
      if (url === '/api/recording') {
        if (options.method === 'POST') { posts.push(JSON.parse(options.body)); recording.settings = structuredClone(posts.at(-1)); return response(null); }
        return response(recording);
      }
      if (url === '/api/recording/detection' || url === '/api/recording/detection/reload') return response(catalog);
      if (url === '/api/recording/storage/activate') { activations.push(JSON.parse(options.body)); return response(null); }
      if (url === '/api/recordings/search') return response([]);
      throw new Error(`Unexpected URL: ${url}`);
    }
  };
  runInNewContext(source, context);
  return { el, viewer, recording, catalog, images, posts, activations, context, get drawnImage() { return drawnImage; },
    get tabs() { return el('recording-camera-tabs').children; }, open: () => el('recording-settings').onclick(),
    save: () => form.onsubmit({ preventDefault() {} }), openTemplates: () => el('detection-templates-open').onclick(),
    saveTemplates: () => el('detection-template-form').onsubmit({ preventDefault() {} }),
    editCameraTemplate: () => el('camera-detection-template-edit').onclick({ preventDefault() {} }),
    editZoneTemplate: index => el(`zone-template-edit-${index}`).onclick({ preventDefault() {} }) };
}

test('recording storage saves independent mirror quotas and can activate a caught-up drive', async () => {
  const env = environment([]);
  env.recording.settings.mirrors = [{ directory: '/usb/first', maximumStorageGigabytes: 120 }];
  env.recording.storageLocations = [
    { directory: '/recordings', status: 'offline', active: false },
    { directory: '/usb/first', status: 'ready', active: false, canActivate: true }
  ];
  await env.open();
  assert.equal(env.el('recording-primary-status').textContent, 'Offline');
  const list = env.el('recording-mirror-list');
  const actions = list.children[0].lastElementChild.children[1];
  await actions.children.find(control => control.textContent === 'Make active').onclick();
  assert.deepEqual(env.activations, [{ directory: '/usb/first', configurationId: 'default' }]);
  env.el('recording-mirror-add').onclick();
  const row = list.lastElementChild;
  const [folder, quota] = row.children[0].children.map(label => label.children[0]);
  folder.value = '/usb/second'; folder.oninput();
  quota.value = '510'; quota.oninput();
  await env.save();
  assert.deepEqual(env.posts[0].mirrors.map(value => [value.directory, value.maximumStorageGigabytes]), [
    ['/usb/first', 120], ['/usb/second', 510]
  ]);
});

const cameras = [
  { channel: 7, name: 'Garden', enabled: true },
  { channel: 1, name: 'Disabled entrance', enabled: false },
  { channel: 3, name: 'Garage', enabled: true }
];

test('camera tabs use collection positions, omit disabled cameras and keep stable channel edits separate', async () => {
  const env = environment(cameras); await env.open();
  assert.equal(env.el('recording-dialog').open, true);
  assert.deepEqual(env.tabs.map(tab => tab.textContent), ['1. Garden', '3. Garage']);
  assert.equal(env.tabs[0].attributes['aria-selected'], 'true');
  assert.equal(env.el('recording-camera-panel').attributes['aria-labelledby'], env.tabs[0].id);
  assert.match(env.images[0].src, /\/cameras\/7\/snapshot\?/);
  env.el('camera-detection-template').value = String(3).padStart(32, '0'); env.tabs[1].onclick();
  assert.equal(env.tabs[1].attributes['aria-selected'], 'true');
  assert.match(env.images[1].src, /\/cameras\/3\/snapshot\?/);
  assert.equal(env.el('camera-detection-template').value, String(3).padStart(32, '0'));
  env.el('camera-detection-template').value = String(7).padStart(32, '0'); env.tabs[0].onclick();
  assert.equal(env.el('camera-detection-template').value, String(3).padStart(32, '0'));
  await env.save();
  assert.equal(env.posts[0].cameras.find(camera => camera.channel === 3).detectionTemplateId, String(7).padStart(32, '0'));
  assert.equal(env.posts[0].cameras.find(camera => camera.channel === 7).detectionTemplateId, String(3).padStart(32, '0'));
  assert.deepEqual(env.posts[0].cameras.find(camera => camera.channel === 1), plain(env.recording.settings.cameras[1]));
});

test('camera tabs support arrow keys, Home and End with one focusable selected tab', async () => {
  const env = environment(cameras); await env.open(); let prevented = false;
  const key = (tab, value) => tab.onkeydown({ key: value, preventDefault() { prevented = true; } });
  key(env.tabs[0], 'ArrowRight'); assert.equal(prevented, true);
  assert.equal(env.tabs[1].focused, true); assert.equal(env.tabs[1].tabIndex, 0); assert.equal(env.tabs[0].tabIndex, -1);
  key(env.tabs[1], 'ArrowRight'); assert.equal(env.tabs[0].attributes['aria-selected'], 'true');
  key(env.tabs[0], 'End'); assert.equal(env.tabs[1].attributes['aria-selected'], 'true');
  key(env.tabs[1], 'Home'); assert.equal(env.tabs[0].attributes['aria-selected'], 'true');
  key(env.tabs[0], 'ArrowLeft'); assert.equal(env.tabs[1].attributes['aria-selected'], 'true');
});

test('switching tabs clears the previous picture and ignores late snapshots from another camera', async () => {
  const env = environment(cameras); await env.open();
  env.images[0].resolve(); await tick(); assert.equal(env.drawnImage, env.images[0]);
  env.tabs[1].onclick(); assert.equal(env.drawnImage, null);
  env.tabs[0].onclick(); env.images[2].resolve(); await tick();
  env.images[1].resolve(); await tick(); assert.equal(env.drawnImage, env.images[2]);
  env.tabs[1].onclick(); env.images[3].reject(new Error('No camera picture')); await tick();
  assert.equal(env.drawnImage, null); assert.match(env.el('zone-preview-status').textContent, /No live picture/);
});

test('opening zones and clicking Refresh picture explicitly request newly captured snapshots', async () => {
  const env = environment(cameras); await env.open();
  assert.equal(new URL(env.images[0].src, 'http://viewer').searchParams.get('refresh'), 'true');
  env.images[0].resolve(); await tick();
  const refresh = env.el('zone-refresh').onclick();
  assert.equal(env.drawnImage, null);
  assert.match(env.el('zone-preview-status').textContent, /Loading camera picture/);
  const url = new URL(env.images[1].src, 'http://viewer');
  assert.equal(url.pathname, '/api/cameras/7/snapshot');
  assert.equal(url.searchParams.get('refresh'), 'true');
  env.images[1].resolve(); await refresh;
  assert.equal(env.drawnImage, env.images[1]);
  assert.match(env.el('zone-preview-status').textContent, /Snapshot for drawing zones/);
});

test('a late refresh cannot overwrite a newer refreshed picture', async () => {
  const env = environment(cameras); await env.open();
  const earlier = env.el('zone-refresh').onclick(), latest = env.el('zone-refresh').onclick();
  env.images[2].resolve(); await latest;
  env.images[1].resolve(); await earlier;
  env.images[0].resolve(); await tick();
  assert.equal(env.drawnImage, env.images[2]);
});

test('a failed refresh reports unavailable video and preserves existing zone edits', async () => {
  const env = environment(cameras);
  const zones = [{ id: 'zone', name: 'Driveway', x: .1, y: .2, width: .3, height: .4, exclude: true, times: [] }];
  env.recording.settings.cameras[0].zones = structuredClone(zones);
  await env.open(); env.images[0].resolve(); await tick();
  const refresh = env.el('zone-refresh').onclick();
  env.images[1].reject(new Error('Camera offline')); await refresh;
  assert.equal(env.drawnImage, null);
  assert.match(env.el('zone-preview-status').textContent, /Check the camera connection/);
  await env.save(); assert.deepEqual(env.posts[0].cameras[0].zones, zones);
});

test('all-disabled configurations show an empty state and save global settings without altering camera rules', async () => {
  const env = environment(cameras.map(camera => ({ ...camera, enabled: false }))); await env.open();
  assert.equal(env.tabs.length, 0); assert.equal(env.images.length, 0);
  assert.equal(env.el('recording-camera-panel').hidden, true); assert.equal(env.el('recording-camera-settings').disabled, true);
  assert.equal(env.el('recording-no-cameras').hidden, false);
  env.el('postCaptureSeconds').value = '30'; await env.save();
  assert.equal(env.posts[0].postCaptureSeconds, 30); assert.deepEqual(env.posts[0].cameras, plain(env.recording.settings.cameras));
  assert.equal(env.el('recording-camera-settings').disabled, true);
});

test('legacy recorder channels remain selectable and the recording library retains disabled and historical cameras', async () => {
  const legacy = environment(); await legacy.open();
  assert.deepEqual(legacy.tabs.map(tab => tab.textContent), ['1. Old name 1', '2. Old name 2']);
  const env = environment(cameras); await env.el('recordings').onclick();
  assert.deepEqual(env.el('recordings-camera').options.map(option => option.textContent),
    ['All cameras', '1. Garden', '2. Disabled entrance', '3. Garage', 'Retired camera']);
});

test('recording modes retain independent schedules and optional continuous detection across camera tabs', async () => {
  const env = environment(cameras); await env.open();
  env.el('camera-recording-mode').value = 'continuous'; env.el('camera-recording-mode').onchange();
  assert.equal(env.el('stream-schedule-panel').hidden, false);
  assert.equal(env.el('software-motion-settings').disabled, true);
  env.el('continuous-detection').checked = true; env.el('continuous-detection').onchange();
  assert.equal(env.el('software-motion-settings').disabled, false);
  env.tabs[1].onclick(); env.tabs[0].onclick();
  assert.equal(env.el('camera-recording-mode').value, 'continuous');
  assert.equal(env.el('continuous-detection').checked, true);
  await env.save();
  const camera = env.posts[0].cameras.find(item => item.channel === 7);
  assert.equal(camera.mode, 'continuous'); assert.equal(camera.recordWhenStreaming, false);
  assert.equal(camera.detectDuringContinuousRecording, true);
});


test('the menu opens the template form independently and saves policies without changing camera settings', async () => {
  const env = environment(cameras); const before = plain(env.recording.settings); await env.openTemplates();
  assert.equal(env.el('detection-template-dialog').open, true); assert.ok(!env.el('recording-dialog').open); assert.equal(env.images.length, 0);
  env.el('motion-sensitivity').value = '85'; env.el('adaptive-learn-noise').checked = true;
  env.el('adaptive-persistence').value = '2';

  await env.saveTemplates();
  assert.equal(env.posts[0].detectionTemplates[0].settings.sensitivity, 85);
  assert.equal(env.posts[0].detectionTemplates[0].settings.adaptive.learnNoise, true);
  assert.equal(env.posts[0].detectionTemplates[0].settings.adaptive.regionPersistenceFrames, 2);
  assert.deepEqual(env.posts[0].cameras, before.cameras); assert.equal(env.el('detection-template-dialog').open, false); assert.equal(env.el('menu-toggle').focused, true);
});

test('template selection saves independent edits and unused templates can be duplicated and removed', async () => {
  const env = environment(cameras); await env.openTemplates();
  env.el('motion-sensitivity').value = '84';
  env.el('detection-template-select').value = String(3).padStart(32, '0'); env.el('detection-template-select').onchange();
  assert.equal(env.el('motion-sensitivity').value, 63);
  env.el('detection-template-clone').onclick();
  assert.equal(env.el('detection-template-delete').disabled, false);
  env.el('detection-template-delete').onclick();
  assert.equal(env.el('detection-template-delete').disabled, true);
  await env.saveTemplates();
  assert.equal(env.posts[0].detectionTemplates.length, 3);
  assert.equal(env.posts[0].detectionTemplates[0].settings.sensitivity, 84);
});

test('excess legacy inline policies retain their unassigned selection when saving shared templates', async () => {
  const env = environment(cameras);
  env.recording.settings.cameras[0].detectionTemplateId = null;
  await env.open(); assert.equal(env.el('camera-detection-template').value, '');
  assert.equal(env.el('camera-detection-template-edit').hidden, true); await env.openTemplates();
  env.el('motion-sensitivity').value = '80'; await env.saveTemplates();
  assert.equal(env.posts[0].cameras[0].detectionTemplateId, null);
  assert.equal(env.posts[0].cameras[0].sensitivity, env.recording.settings.cameras[0].sensitivity);
});

test('installing a starter adds an independent editable template without changing camera assignments', async () => {
  const env = environment(cameras);
  env.catalog.templates = [{ id: 'starter', name: 'Outdoor foliage', settings: { adaptive: { learnNoise: true, regionPersistenceFrames: 2 } } }];
  await env.openTemplates();
  env.el('detection-template-starter').value = 'starter'; env.el('detection-template-install').onclick();
  assert.equal(env.el('adaptive-learn-noise').checked, true);
  assert.equal(env.el('adaptive-persistence').value, 2);
  await env.saveTemplates();
  const installed = env.posts[0].detectionTemplates.at(-1);
  assert.equal(installed.name, 'Outdoor foliage'); assert.match(installed.id, /^[a-f0-9]{32}$/);
  assert.equal(env.posts[0].cameras[0].detectionTemplateId, String(7).padStart(32, '0'));
});

test('zone overrides preserve camera inheritance and exclusions have no template selector', async () => {
  const env = environment(cameras);
  env.recording.settings.cameras[0].zones = [{ id: 'detect', name: 'Trees', exclude: false }, { id: 'exclude', name: 'Timestamp', exclude: true }];
  await env.open();
  const selector = () => env.el('zone-list').children[0].children.find(label => label.textContent === 'Detection template').children[0];
  assert.equal(selector().options[0].textContent, 'Use camera template: Policy 7');
  assert.equal(env.el('zone-list').children[1].children.some(label => label.textContent === 'Detection template'), false);
  selector().value = String(3).padStart(32, '0'); selector().onchange();
  env.el('camera-detection-template').value = String(1).padStart(32, '0'); env.el('camera-detection-template').onchange();
  assert.equal(selector().value, String(3).padStart(32, '0'));
  assert.equal(selector().options[0].textContent, 'Use camera template: Policy 1');
  await env.save();
  assert.equal(env.posts[0].cameras[0].zones[0].detectionTemplateId, String(3).padStart(32, '0'));
});

test('saving waits for template import and cancelled imports cannot modify a reopened template draft', async () => {
  const env = environment(cameras); await env.openTemplates();
  let resolve;
  env.el('detection-template-import').files = [{ size: 30, text: () => new Promise(done => { resolve = done; }) }];
  const importing = env.el('detection-template-import').onchange();
  await env.saveTemplates(); assert.equal(env.posts.length, 0);
  assert.match(env.el('detection-template-result').textContent, /import to finish/);
  env.el('detection-template-cancel').onclick(); await env.openTemplates();
  resolve(JSON.stringify({ version: 1, templates: [{ name: 'Old draft', settings: {} }] }));
  await importing; await env.saveTemplates();
  assert.equal(env.posts[0].detectionTemplates.length, 3);
});

test('camera shortcut selects its assigned template and cancel preserves pending recording edits', async () => {
  const env = environment(cameras); await env.open();
  env.el('postCaptureSeconds').value = '31'; env.el('camera-recording-enabled').checked = false;
  env.el('camera-detection-template').value = String(3).padStart(32, '0'); env.el('camera-detection-template').onchange();
  await env.editCameraTemplate();
  assert.equal(env.el('detection-template-select').value, String(3).padStart(32, '0'));
  assert.equal(env.el('recording-dialog').open, true);
  env.el('motion-sensitivity').value = '92'; env.el('detection-template-name').value = 'Discard this';
  env.el('detection-template-name').oninput(); env.el('detection-template-cancel').onclick();
  assert.equal(env.el('postCaptureSeconds').value, '31'); assert.equal(env.el('camera-recording-enabled').checked, false);
  assert.equal(env.el('camera-detection-template-edit').focused, true); assert.equal(env.posts.length, 0);
  await env.save();
  assert.equal(env.posts[0].postCaptureSeconds, 31); assert.equal(env.posts[0].cameras[0].enabled, false);
  assert.equal(env.posts[0].detectionTemplates.at(-1).name, 'Policy 3');
  assert.equal(env.posts[0].detectionTemplates.at(-1).settings.sensitivity, 63);
});

test('saving a template preserves pending recording edits and the later recording save retains that template', async () => {
  const env = environment(cameras); await env.open();
  env.el('postCaptureSeconds').value = '31'; env.el('camera-recording-enabled').checked = false;
  env.el('camera-detection-template').value = String(3).padStart(32, '0'); env.el('camera-detection-template').onchange();
  await env.editCameraTemplate();
  env.el('motion-sensitivity').value = '92'; env.el('detection-template-name').value = 'Shared foliage'; env.el('detection-template-name').oninput();
  await env.saveTemplates();
  assert.equal(env.posts[0].postCaptureSeconds, 20); assert.equal(env.posts[0].cameras[0].enabled, true);
  assert.equal(env.posts[0].cameras[0].detectionTemplateId, String(7).padStart(32, '0'));
  assert.equal(env.el('postCaptureSeconds').value, '31'); assert.equal(env.el('camera-recording-enabled').checked, false);
  assert.equal(env.el('recording-dialog').open, true);
  assert.equal(env.el('camera-detection-template').options.at(-1).textContent, 'Shared foliage');
  await env.save();
  assert.equal(env.posts[1].postCaptureSeconds, 31); assert.equal(env.posts[1].cameras[0].enabled, false);
  assert.equal(env.posts[1].cameras[0].detectionTemplateId, String(3).padStart(32, '0'));
  assert.equal(env.posts[1].detectionTemplates.at(-1).settings.sensitivity, 92);
});

test('zone shortcuts open explicit overrides or the inherited camera template and restore focus', async () => {
  const env = environment(cameras);
  env.recording.settings.cameras[0].zones = [{ id: 'detect', name: 'Trees', exclude: false },
    { id: 'override', name: 'Road', detectionTemplateId: String(3).padStart(32, '0') }, { id: 'exclude', name: 'Timestamp', exclude: true }];
  await env.open(); await env.editZoneTemplate(0);
  assert.equal(env.el('detection-template-select').value, String(7).padStart(32, '0'));
  env.el('detection-template-cancel').onclick(); await env.editZoneTemplate(1);
  assert.equal(env.el('detection-template-select').value, String(3).padStart(32, '0'));
  env.el('detection-template-name').value = 'Road policy'; env.el('detection-template-name').oninput(); await env.saveTemplates();
  assert.equal(env.el('zone-template-edit-1').focused, true);
  assert.equal(env.el('zone-list').children[2].children.some(child => child.textContent === 'Edit this template'), false);
});

test('template deletion respects saved assignments and pending zone assignments', async () => {
  const env = environment(cameras);
  const unusedId = 'f'.repeat(32);
  env.recording.settings.detectionTemplates.push({ id: unusedId, name: 'Unused', settings: {} });
  env.recording.settings.cameras[0].zones = [{ id: 'detect', name: 'Trees', exclude: false }];
  await env.open();
  const selector = env.el('zone-list').children[0].children.find(label => label.textContent === 'Detection template').children[0];
  selector.value = unusedId; selector.onchange(); await env.editZoneTemplate(0);
  assert.equal(env.el('detection-template-delete').disabled, true);
  env.el('detection-template-delete').onclick();
  env.el('detection-template-select').value = String(7).padStart(32, '0'); env.el('detection-template-select').onchange();
  assert.equal(env.el('detection-template-delete').disabled, true);
  await env.saveTemplates(); assert.equal(env.posts[0].detectionTemplates.length, 4);
  assert.equal(env.posts[0].cameras[0].zones[0].detectionTemplateId, undefined);
});

test('template saves use current saved recording fields and reject concurrently changed policies', async () => {
  const env = environment(cameras); await env.openTemplates();
  env.recording.settings.postCaptureSeconds = 27; env.el('motion-sensitivity').value = '88';
  await env.saveTemplates(); assert.equal(env.posts[0].postCaptureSeconds, 27);
  await env.openTemplates();
  env.recording.settings.detectionTemplates[0].name = 'Changed elsewhere';
  await env.saveTemplates(); assert.equal(env.posts.length, 1);
  assert.match(env.el('detection-template-result').textContent, /Templates changed elsewhere/);
  assert.equal(env.el('detection-template-dialog').open, true);
  assert.equal(env.el('motion-sensitivity').disabled, false);
});

test('saving either form refuses a changed camera configuration', async () => {
  const env = environment(cameras); await env.open(); await env.editCameraTemplate();
  env.recording.settings.configurationId = 'other'; await env.saveTemplates();
  assert.match(env.el('detection-template-result').textContent, /configuration changed/);
  env.el('detection-template-cancel').onclick(); await env.save();
  assert.match(env.el('recording-result').textContent, /configuration changed/);
  assert.equal(env.posts.length, 0);
});

test('recording saves retain templates edited elsewhere while the recording form is open', async () => {
  const env = environment(cameras); await env.open();
  env.recording.settings.detectionTemplates[0].name = 'Updated policy';
  env.recording.settings.detectionTemplates[0].settings.sensitivity = 91;
  env.el('postCaptureSeconds').value = '32'; await env.save();
  assert.equal(env.posts[0].postCaptureSeconds, 32);
  assert.equal(env.posts[0].detectionTemplates[0].name, 'Updated policy');
  assert.equal(env.posts[0].detectionTemplates[0].settings.sensitivity, 91);
});

test('a late model refresh from a cancelled form cannot overwrite a reopened template draft', async () => {
  const env = environment(cameras); await env.openTemplates();
  let resolve;
  const fetch = env.context.fetch;
  env.context.fetch = (url, options) => url === '/api/recording/detection/reload' ? new Promise(done => { resolve = done; }) : fetch(url, options);
  const refreshing = env.el('ai-model-refresh').onclick();
  await env.saveTemplates(); assert.equal(env.posts.length, 0);
  env.el('detection-template-cancel').onclick(); await env.openTemplates();
  env.el('motion-sensitivity').value = '89';
  resolve({ ok: true, headers: { get: () => 'application/json' }, json: async () => ({ ...env.catalog, methods: [] }) });
  await refreshing; await env.saveTemplates();
  assert.equal(env.posts[0].detectionTemplates[0].settings.sensitivity, 89);
  assert.equal(env.posts[0].detectionTemplates[0].settings.detectionMethod, 'adaptive');
});
