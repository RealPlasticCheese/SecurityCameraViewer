const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
const handler = source.split('\n').find(line => line.startsWith("$('refresh-app').onclick ="));

function harness(reload) {
  const button = {};
  const location = { reload };
  assert.ok(handler, 'Use the actual application refresh handler.');
  runInNewContext(handler, { window: { location }, $: id => {
    assert.equal(id, 'refresh-app');
    return button;
  } });
  return { button, location };
}

test('refresh does not reload during setup and reloads the current document on activation', () => {
  let calls = 0, receiver;
  const { button, location } = harness(function () { calls++; receiver = this; });
  assert.equal(calls, 0);
  button.onclick();
  assert.equal(calls, 1);
  assert.equal(receiver, location, 'Call the native location API with its required receiver.');
});

test('a cancelled browser reload leaves the refresh action available for retry', () => {
  let calls = 0;
  const { button } = harness(() => { calls++; });
  button.onclick();
  button.onclick();
  assert.equal(calls, 2);
});
