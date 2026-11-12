const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const root = join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot');
const filterSource = readFileSync(join(root, 'camera-collection.js'), 'utf8') + '\n' +
  readFileSync(join(root, 'recording-filters.js'), 'utf8');
const librarySource = readFileSync(join(root, 'recording-archive.js'), 'utf8') + '\n' + readFileSync(join(root, 'recording.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const tick = () => new Promise(resolve => setImmediate(resolve));

class Element {
  constructor(tag = 'div') {
    this.tag = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.value = '';
    this.textContent = ''; this.checked = false; this.open = false; this.disabled = false;
  }
  append(...children) {
    for (const child of children) {
      if (typeof child === 'string') this.textContent += child; else this.children.push(child);
    }
  }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  add(child) { this.append(child); }
  get options() { return this.children; }
  addEventListener(type, callback) { (this.listeners[type] ??= []).push(callback); }
  pause() {}
  load() {}
  removeAttribute(name) { delete this[name]; }
  showModal() { this.open = true; }
  close() { this.open = false; for (const callback of this.listeners.close || []) callback(); }
  focus() { this.focused = true; }
  reportValidity() { return true; }
  setAttribute() {}
  getContext() { return {}; }
  querySelectorAll(selector) {
    const key = selector === '[data-zone]' ? 'zone' : selector === '[data-camera]' ? 'camera' : null;
    return this.children.flatMap(child => [ ...(key && child.dataset[key] !== undefined ? [child] : []), ...child.querySelectorAll(selector) ]);
  }
}
function Option(text, value) { const option = new Element('option'); option.textContent = text; option.value = value; return option; }

function environment() {
  const elements = new Map();
  const el = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  el('recordings-camera').append(new Option('All cameras', ''), new Option('Entrance', '1'), new Option('Yard', '2'));
  el('recordings-filter-form').elements = ['recordings-save-target', 'recordings-filter-name'].map(el);
  const document = { getElementById: el, createElement: tag => new Element(tag), querySelectorAll: () => [], dispatchEvent() {} };
  const context = { document, Option, AbortSignal, URLSearchParams, setTimeout() {}, structuredClone, Event, confirm: () => true };
  const Filters = runInNewContext(`${filterSource}\nRecordingFilters`, context);
  return { el, document, context, Filters };
}

const viewer = { settings: { channelCount: 2, cameraNames: ['Entrance', 'Yard'] } };
const recording = { settings: { configurationId: 'default', cameras: [
  { channel: 1, zones: [{ id: 'priority', name: 'Priority' }, { id: 'normal', name: 'Normal' }, { id: 'excluded', name: 'Ignored', exclude: true }] },
  { channel: 2, zones: [{ id: 'priority', name: 'Yard priority' }] }
] }, cameras: [], recordedCameras: [], eventCount: 0, usedBytes: 0 };
const recorded = [{ channel: 1, cameraName: 'Entrance', zones: [{ id: 'retired', name: 'Old zone' }, { id: 'priority', name: 'Before rename' }] }];

test('zone checkboxes form camera groups, retain historical zones and show partial selection', async () => {
  const env = environment(); let changes = 0;
  const filters = new env.Filters(async path => path.endsWith('/zones') ? recorded : [], () => changes++, assert.fail);
  await filters.initialize(viewer, recording);
  const tree = env.el('recordings-zone-tree');
  assert.equal(tree.children.length, 2);
  const choices = tree.querySelectorAll('[data-zone]');
  assert.equal(choices.length, 4);
  assert.ok(!choices.some(input => input.dataset.zone.includes('excluded')));
  const priority = choices.find(input => input.dataset.zone === '1:priority');
  priority.checked = true; priority.onchange();
  assert.deepEqual(plain(filters.snapshot().zones), [{ channel: 1, id: 'priority', name: 'Priority' }]);
  const group = tree.querySelectorAll('[data-camera]')[0];
  assert.equal(group.indeterminate, true);
  group.checked = true; group.onchange();
  assert.equal(filters.snapshot().zones.length, 3);
  assert.equal(group.indeterminate, false);
  assert.equal(choices.find(input => input.dataset.zone === '2:priority').checked, false);
  assert.equal(changes, 2);
});

test('loading restores all filters and clear resets camera, date, zones and preset selection', async () => {
  const env = environment(); let changes = 0;
  const saved = [{ id: 'saved', name: 'Priority videos', filter: { channel: 1, date: '2026-10-04', zones: [{ channel: 1, id: 'priority', name: 'Priority' }] } }];
  const filters = new env.Filters(async path => path.endsWith('/zones') ? recorded : saved, () => changes++, assert.fail);
  await filters.initialize(viewer, recording);
  env.el('recordings-saved-filter').value = 'saved';
  env.el('recordings-saved-filter').onchange({ target: env.el('recordings-saved-filter') });
  assert.deepEqual(plain(filters.snapshot()), saved[0].filter);
  env.el('recordings-zone-picker').open = true;
  env.el('recordings-clear').onclick();
  assert.deepEqual(plain(filters.snapshot()), { channel: null, date: null, zones: [] });
  assert.equal(env.el('recordings-saved-filter').value, '');
  assert.equal(env.el('recordings-zone-picker').open, false);
  assert.equal(changes, 2);
});

test('saved removed cameras and zones remain selectable and configurations reset active filters', async () => {
  const env = environment();
  const filters = new env.Filters(async path => path.endsWith('/zones') ? recorded : [], () => {}, assert.fail);
  await filters.initialize(viewer, recording);
  const removed = { channel: 7, date: null, zones: [{ channel: 7, id: 'removed', name: 'Removed zone' }] };
  filters.apply(removed);
  assert.deepEqual(plain(filters.snapshot()), removed);
  assert.ok(env.el('recordings-zone-tree').querySelectorAll('[data-zone]').some(input => input.dataset.zone === '7:removed' && input.checked));
  await filters.initialize(viewer, { ...recording, settings: { ...recording.settings, configurationId: 'other' } });
  assert.deepEqual(plain(filters.snapshot()), { channel: null, date: null, zones: [] });
});

test('save dialog creates named presets or replaces an explicit target and preserves values', async () => {
  const env = environment(); const posts = [];
  const saved = [{ id: 'existing', name: 'Priority', filter: {} }];
  const request = async (path, body) => {
    if (!body) return path.endsWith('/zones') ? [] : saved;
    posts.push(plain(body)); return { id: body.id || 'new', name: body.id ? 'Priority' : body.name, filter: body.filter };
  };
  const filters = new env.Filters(request, () => {}, assert.fail);
  await filters.initialize(viewer, recording);
  filters.apply({ channel: 2, date: '2026-10-04', zones: [{ channel: 2, id: 'priority', name: 'Yard priority' }] });
  await filters.openSave();
  assert.equal(env.el('recordings-filter-save-dialog').open, true);
  env.el('recordings-filter-name').value = ' Yard activity ';
  await filters.save();
  assert.equal(posts[0].name, 'Yard activity');
  assert.deepEqual(posts[0].filter, plain(filters.snapshot()));
  assert.equal(env.el('recordings-saved-filter').value, 'new');
  await filters.openSave();
  env.el('recordings-save-target').value = 'existing'; filters.updateSaveTarget();
  assert.equal(env.el('recordings-filter-name').disabled, true);
  await filters.save();
  assert.equal(posts[1].id, 'existing');
  assert.equal(posts[1].name, null);
  assert.equal(env.el('recordings-saved-filter').value, 'existing');
});

test('failed preset saves leave the prompt open and retain the current filter', async () => {
  const env = environment();
  const filters = new env.Filters(async (_path, body) => { if (body) throw new Error('Name already exists'); return []; }, () => {}, assert.fail);
  filters.apply({ channel: 1, date: '2026-10-04' });
  await filters.openSave(); env.el('recordings-filter-name').value = 'Duplicate';
  await filters.save();
  assert.equal(env.el('recordings-filter-save-dialog').open, true);
  assert.equal(env.el('recordings-filter-save-result').textContent, 'Name already exists');
  assert.equal(env.el('recordings-filter-name').disabled, false);
  assert.equal(filters.snapshot().channel, 1);
});

test('rapid filter changes ignore stale requests and clear sends an unfiltered search', async () => {
  const env = environment(); const pending = []; let opened = false;
  env.context.AiModelSettingsView = class {};
  env.context.DetectionTemplateSettingsDialog = class {};
  const response = value => ({ ok: true, headers: { get: () => 'application/json' }, json: async () => value });
  env.context.fetch = async (url, options) => {
    if (url === '/api/state') return response(viewer);
    if (url === '/api/recording') return response(recording);
    if (url === '/api/recordings/zones') return response(recorded);
    if (url === '/api/recordings/filters') return response([]);
    if (url === '/api/recordings/search') {
      if (!opened) { opened = true; return response([]); }
      return new Promise(resolve => pending.push({ query: JSON.parse(options.body), resolve: items => resolve(response(items)) }));
    }
    throw new Error(`Unexpected URL: ${url}`);
  };
  runInNewContext(librarySource, env.context);
  await env.el('recordings').onclick();
  env.el('recordings-camera').value = '1'; env.el('recordings-camera').onchange();
  env.el('recordings-camera').value = '2'; env.el('recordings-camera').onchange();
  const item = name => ({ id: name, cameraName: name, triggeredAt: '2026-10-04T00:00:00Z', durationSeconds: 1, sizeBytes: 1, status: 'recording' });
  pending[1].resolve([item('Yard')]); await tick();
  pending[0].resolve([item('Entrance')]); await tick();
  assert.equal(env.el('recording-list').children.length, 1);
  assert.ok(env.el('recording-list').children[0].children[0].children[0].textContent.startsWith('Yard'));
  env.el('recordings-date').value = '2026-10-04'; env.el('recordings-date').onchange();
  assert.equal(pending[2].query.since, new Date('2026-10-04T00:00:00').toISOString());
  assert.equal(pending[2].query.before, new Date('2026-10-05T00:00:00').toISOString());
  env.el('recordings-clear').onclick();
  assert.deepEqual(pending[3].query, { channel: null, limit: 50, zones: [] });
  pending[3].resolve([]); pending[2].resolve([item('Old date result')]); await tick();
  assert.equal(env.el('recording-list').children.length, 0);
});

test('replay-only filter is included in saved presets, restored, changed and cleared', async () => {
  const env = environment(); let changes = 0;
  const filters = new env.Filters(async () => [], () => changes++, assert.fail);
  filters.apply({ channel: 1, date: '2026-10-11', savedReplayOnly: true });
  assert.equal(env.el('recordings-replay-only').checked, true);
  assert.equal(filters.snapshot().savedReplayOnly, true);
  env.el('recordings-replay-only').checked = false; env.el('recordings-replay-only').onchange();
  assert.equal(changes, 1); assert.equal(filters.snapshot().savedReplayOnly, undefined);
  filters.apply({ savedReplayOnly: true }); filters.clear();
  assert.equal(env.el('recordings-replay-only').checked, false); assert.equal(filters.snapshot().savedReplayOnly, undefined);
});
