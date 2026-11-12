const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const { webcrypto } = require('node:crypto');
const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/detection-templates.js'), 'utf8');
const api = runInNewContext(`${source}\nDetectionTemplateFile`, { structuredClone, crypto: webcrypto });
const catalog = { methods: [{ id: 'adaptive' }, { id: 'adaptive-ai' }, { id: 'ai' }], models: [{ id: 'yolox-nano' }] };
const template = () => ({ id: '0123456789abcdef0123456789abcdef', name: 'Outdoor', settings: {
  detectionMethod: 'adaptive', analysisWidth: 640, sensitivity: 80, minimumAreaPercent: 0.2,
  adaptive: { learnNoise: true, smoothFrames: false, regionPersistenceFrames: 2, illuminationCompensation: 'robust', includeDiagnostics: true },
  aiModel: 'yolox-nano', aiClasses: ['person', 'car'], aiIgnoreClasses: ['dog'], aiOptions: { iouThreshold: 0.5 }
} });
const plain = value => JSON.parse(JSON.stringify(value));

test('portable template files round-trip policy and allocate fresh IDs for independent installation', () => {
  const original = template(), data = api.write([original]);
  assert.equal(data.version, 1); assert.equal(data.templates[0].id, undefined);
  const first = api.read(plain(data), catalog)[0], second = api.read(plain(data), catalog)[0];
  assert.deepEqual(plain(first.settings), original.settings); assert.equal(first.name, original.name);
  assert.match(first.id, /^[a-f0-9]{32}$/); assert.notEqual(first.id, original.id); assert.notEqual(first.id, second.id);
});

test('templates can be installed on an HTTP LAN origin without crypto.randomUUID', () => {
  const lanApi = runInNewContext(`${source}\nDetectionTemplateFile`, { structuredClone, crypto: { getRandomValues: value => webcrypto.getRandomValues(value) } });
  const installed = lanApi.read(plain(lanApi.write([template()])), catalog)[0];
  assert.match(installed.id, /^[a-f0-9]{32}$/);
});

test('exports contain only detection policy even when callers provide camera-specific data', () => {
  const original = template(); original.settings.password = 'private'; original.settings.zones = [{ name: 'Private zone' }];
  original.settings.detectionTemplateId = original.id;
  const exported = JSON.stringify(api.write([original]));
  assert.doesNotMatch(exported, /password|private|zones|detectionTemplateId/);
});

test('malformed or unsupported imported policy is rejected before installation', () => {
  const invalid = [
    { version: 2, templates: [template()] }, { version: 1, templates: [] },
    { version: 1, templates: [{ ...template(), name: '' }] },
    { version: 1, templates: [{ ...template(), settings: null }] },
    { version: 1, templates: [{ ...template(), settings: { detectionMethod: 'unknown' } }] },
    { version: 1, templates: [{ ...template(), settings: { aiModel: 'not-installed-here' } }] },
    { version: 1, templates: [{ ...template(), settings: { sensitivity: 'high' } }] },
    { version: 1, templates: [{ ...template(), settings: { aiClasses: [], aiIgnoreClasses: [] } }] },
    { version: 1, templates: [{ ...template(), settings: { aiClasses: ['person'], aiIgnoreClasses: ['person'] } }] },
    { version: 1, templates: [{ ...template(), settings: { analysisWidth: 999 } }] },
    { version: 1, templates: [{ ...template(), settings: { adaptive: { suppressShadows: 'yes' } } }] },
    { version: 1, templates: [{ ...template(), settings: { adaptive: { regionPersistenceFrames: 1.5 } } }] },
    { version: 1, templates: [{ ...template(), settings: { adaptive: { shadowColorTolerance: -1 } } }] },
    { version: 1, templates: [{ ...template(), settings: { adaptive: { candidateMinimumPixels: Infinity } } }] },
    { version: 1, templates: [{ ...template(), settings: { aiOptions: { maxDetections: 1.2 } } }] },
    { version: 1, templates: [{ ...template(), settings: { password: 'unexpected' } }] }
  ];
  for (const file of invalid) assert.throws(() => api.read(file, catalog));
});

test('imports respect the selected model definition and its supported option ranges', () => {
  const described = { ...catalog, models: [{ id: 'custom', classes: ['person'], options: [{ id: 'confidence', minimum: 0.5, maximum: 0.8, label: 'Confidence' }] }] };
  const file = settings => ({ version: 1, templates: [{ name: 'Model policy', settings: { aiModel: 'custom', ...settings } }] });
  assert.throws(() => api.read(file({ aiClasses: ['car'], aiConfidence: 0.6 }), described));
  assert.throws(() => api.read(file({ aiClasses: ['person'], aiConfidence: 0.4 }), described));
  assert.throws(() => api.read(file({ aiClasses: ['person'], aiConfidence: 0.6, aiOptions: { iouThreshold: 0.5 } }), described));
  assert.equal(api.read(file({ aiClasses: ['person'], aiConfidence: 0.6 }), described).length, 1);
});

test('import validation is atomic and cannot partially return a malformed collection', () => {
  const data = { version: 1, templates: [template(), { name: 'Broken', settings: { adaptive: null } }] };
  assert.throws(() => api.read(data, catalog));
  assert.equal(data.templates.length, 2); assert.equal(data.templates[0].id, template().id);
});
