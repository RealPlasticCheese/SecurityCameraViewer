const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-collection.js'), 'utf8');
const collection = runInNewContext(`${source}\nCameraCollection`);
const cameras = [
  { id: 'front', channel: 3, name: 'Front yard' },
  { id: 'drive', channel: 2, name: 'Driveway', enabled: false },
  { id: 'gate', channel: 1, name: 'Side gate' }
];

test('numbers follow collection positions while requests retain internal channels', () => {
  const settings = { sources: cameras };
  assert.equal(collection.label(settings, 3), '1. Front yard');
  assert.equal(collection.label(settings, 2), '2. Driveway');
  const active = collection.entries(settings, false);
  assert.deepEqual(Array.from(active, entry => [entry.number, entry.source.channel]), [[1, 3], [3, 1]]);
  assert.equal(collection.label({ sources: [...cameras].reverse() }, 3), '3. Front yard');
  assert.equal(collection.label({ sources: cameras.slice(1) }, 2), '1. Driveway');
  assert.equal(collection.label(settings, 9, 'Retired camera'), 'Retired camera');
  assert.equal(cameras[0].channel, 3);
  assert.equal(Object.hasOwn(cameras[0], 'number'), false);
});

test('legacy sources and collections larger than sixteen have names and positions', () => {
  const legacy = { channelCount: 32, cameraNames: ['Entrance'] };
  const entries = collection.entries(legacy);
  assert.equal(entries.length, 32);
  assert.equal(collection.label(legacy, 1), '1. Entrance');
  assert.equal(collection.label(legacy, 32), '32. Camera 32');
  const large = { sources: Array.from({ length: 64 }, (_, i) => ({ channel: 1000 + i, name: `Feed ${i}` })) };
  assert.equal(collection.label(large, 1063), '64. Feed 63');
});

test('internal allocation has no sixteen-camera ceiling and retains disabled channels', () => {
  assert.equal(collection.nextChannel(cameras), 4);
  assert.equal(collection.nextChannel(cameras.filter(camera => camera.channel !== 2)), 2);
  const sources = Array.from({ length: 64 }, (_, index) => ({ channel: index + 1, enabled: false }));
  assert.equal(collection.nextChannel(sources), 65);
});
