const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function sidewaysView() {'), end = source.indexOf("$('sideways-view').onclick", start);

for (const ready of [true, false]) {
  test(`sideways control toggles accessible state and ${ready ? 'rearranges the loaded viewer' : 'can be set before cameras load'}`, () => {
    const cameraLayout = { sideways: false }, button = { setAttribute(name, value) { this[name] = value; } };
    let arrangements = 0, menus = 0;
    const toggle = runInNewContext(source.slice(start, end) + '\nsidewaysView;', {
      cameraLayout, state: ready ? {} : undefined, $: () => button, arrange() { arrangements++; },
      setMenu(open, restoreFocus) { assert.equal(open, false); assert.equal(restoreFocus, true); menus++; }
    });
    toggle(); assert.equal(cameraLayout.sideways, true); assert.equal(button['aria-pressed'], 'true');
    assert.equal(button['aria-label'], 'Use automatic camera layout'); assert.equal(button.title, button['aria-label']);
    toggle(); assert.equal(cameraLayout.sideways, false); assert.equal(button['aria-pressed'], 'false');
    assert.equal(button['aria-label'], 'Use sideways camera layout');
    assert.equal(arrangements, ready ? 2 : 0); assert.equal(menus, 2);
  });
}
