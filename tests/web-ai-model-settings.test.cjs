const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/ai-model-settings.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
function setup() {
  const document = { createElement: node };
  function node() { return { ownerDocument: document, children: [], textContent: '', value: '', hidden: false,
    append(...values) { this.children.push(...values); }, replaceChildren(...values) { this.children = values; },
    setAttribute(name, value) { this[name] = value; } }; }
  const api = runInNewContext(`${source}\n({ AiModelSettings, AiModelSettingsView })`);
  const preset = node(), options = node(), classes = node(), search = node(), status = node();
  return { ...api, preset, options, classes, search, status, view: new api.AiModelSettingsView(preset, options, classes, search, status) };
}
const model = { id: 'custom-model', classes: ['person', 'cat', 'parcel'], options: [
  { id: 'confidence', label: 'Confidence', minimum: .2, maximum: .9, step: .01, default: .5, help: 'Model confidence' },
  { id: 'framesPerSecond', label: 'Checks per second', minimum: .2, maximum: 3, step: .1, default: 1, help: 'Rate' },
  { id: 'maxDetections', label: 'Maximum objects', minimum: 1, maximum: 20, step: 1, default: 20, help: 'Limit' }
], presets: [{ id: 'default', name: 'Original defaults', triggerClasses: ['person'], ignoreClasses: [], options: { confidence: .5, framesPerSecond: 1, maxDetections: 20 }, device: 'cpu' }] };
const camera = () => ({ aiModel: model.id, aiDevice: 'cpu', aiClasses: ['person'], aiIgnoreClasses: [], aiConfidence: .5, aiFramesPerSecond: 1 });

test('built-in defaults enable all vehicles and the original people-and-cars preset remains selectable', () => {
  const descriptions = JSON.parse(readFileSync(join(__dirname, '../src/SecurityCameraViewer.Recording/BuiltInModels.json'), 'utf8'));
  const builtIn = { ...descriptions.models[0], classes: descriptions.labels, options: descriptions.options };
  const s = setup(), settings = { aiDevice: 'cpu' }; s.view.load(settings, builtIn);
  for (const object of ['person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck', 'boat']) {
    assert.equal(s.view.rows.find(row => row.object === object).select.value, 'trigger');
  }
  assert.equal(s.preset.value, 'default');
  s.preset.value = 'original'; s.preset.onchange();
  assert.deepEqual(plain(settings.aiClasses), ['person', 'car']); assert.equal(s.view.rows.find(row => row.object === 'bus').select.value, 'none');
  s.preset.value = 'default'; s.preset.onchange();
  assert.equal(settings.aiClasses.length, 9); assert.deepEqual(plain(settings.aiIgnoreClasses), []);
});

test('model descriptions drive class controls, ranges and defaults, preserving saved custom choices', () => {
  const s = setup(), settings = camera(); settings.aiClasses = ['parcel']; settings.aiIgnoreClasses = ['cat']; settings.aiConfidence = .6;
  s.view.load(settings, model);
  assert.equal(s.view.rows.length, 3); assert.equal(s.view.rows[1].select.value, 'ignore'); assert.equal(s.view.rows[2].select.value, 'trigger');
  assert.equal(s.preset.value, 'custom'); assert.equal(s.view.inputs.get('confidence').min, '.2'.replace(/^\./, '0.'));
  assert.equal(s.view.inputs.get('framesPerSecond').max, '3'); assert.equal(s.view.inputs.get('confidence').value, '0.6');
  assert.match(s.status.textContent, /1 trigger, 1 ignore/);
});

test('trigger and ignore roles are mutually exclusive per object without clearing another trigger', () => {
  const s = setup(), settings = camera(); s.view.load(settings, model);
  const cat = s.view.rows[1].select; cat.value = 'ignore'; cat.onchange();
  assert.deepEqual(settings.aiClasses, ['person']); assert.deepEqual(plain(settings.aiIgnoreClasses), ['cat']);
  cat.value = 'trigger'; cat.onchange(); assert.deepEqual(plain(settings.aiClasses), ['person', 'cat']); assert.deepEqual(plain(settings.aiIgnoreClasses), []);
  cat.value = 'none'; cat.onchange(); assert.deepEqual(plain(settings.aiClasses), ['person']); assert.equal(s.preset.value, 'default');
});

test('selectable original defaults restore classes, options and device after custom edits', () => {
  const s = setup(), settings = camera(); s.view.load(settings, model);
  s.view.inputs.get('confidence').value = '.7'; s.view.inputs.get('confidence').oninput(); assert.equal(s.preset.value, 'custom');
  settings.aiIgnoreClasses = ['cat']; settings.aiDevice = 'gpu'; settings.aiOptions = { maxDetections: 2 };
  s.preset.value = 'default'; s.preset.onchange();
  assert.equal(settings.aiConfidence, .5); assert.equal(settings.aiDevice, 'cpu'); assert.deepEqual(plain(settings.aiIgnoreClasses), []);
  assert.equal(settings.aiOptions.maxDetections, 20); assert.equal(s.preset.value, 'default');
});

test('filtering and disabling controls preserve rules and switching cameras keeps their values separate', () => {
  const s = setup(), first = camera(); s.view.load(first, model);
  s.search.value = 'cat'; s.search.oninput(); assert.equal(s.view.rows[0].row.hidden, true); assert.equal(s.view.rows[1].row.hidden, false);
  s.view.setEnabled(false); assert.ok([...s.view.inputs.values()].every(input => input.disabled && !input.required));
  const second = camera(); second.aiClasses = ['parcel']; s.view.load(second, model); s.view.setEnabled(true);
  assert.equal(s.view.rows[2].select.value, 'trigger'); assert.deepEqual(first.aiClasses, ['person']);
  assert.ok([...s.view.inputs.values()].every(input => !input.disabled && input.required));
});

test('unavailable or changed definitions preserve saved choices and make the mismatch actionable', () => {
  const s = setup(), settings = camera(); settings.aiIgnoreClasses = ['missing-class'];
  s.view.load(settings, model); assert.match(s.status.textContent, /missing-class/);
  s.view.load(settings, null); assert.match(s.status.textContent, /unavailable/); s.view.save();
  assert.deepEqual(settings.aiIgnoreClasses, ['missing-class']); assert.equal(s.preset.disabled, true);
  const hostile = { ...model, classes: ['<script>custom label</script>'], presets: [{ ...model.presets[0], triggerClasses: ['<script>custom label</script>'] }] };
  s.view.load(settings, hostile); assert.equal(s.view.rows[0].row.children[0].textContent, '<script>custom label</script>');
});
