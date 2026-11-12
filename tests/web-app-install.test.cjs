const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/app-install.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));

function viewer({ secure = true, standalone = false, iosStandalone = false } = {}) {
  const button = new EventTarget();
  const controls = {}, status = { textContent: '' };
  const elements = { 'app-install-controls': controls, 'install-app': button, 'app-install-status': status };
  const window = new EventTarget();
  window.isSecureContext = secure;
  const displayMode = new EventTarget();
  displayMode.matches = standalone;
  window.matchMedia = query => {
    assert.equal(query, '(display-mode: standalone)');
    return displayMode;
  };
  runInNewContext(source, {
    window, document: { getElementById: id => elements[id] }, navigator: { standalone: iosStandalone }
  });
  return {
    button, controls, status,
    offer(prompt = async () => ({ outcome: 'accepted' }), userChoice) {
      const event = new Event('beforeinstallprompt', { cancelable: true });
      let calls = 0;
      event.prompt = () => { calls++; return prompt(); };
      event.userChoice = userChoice;
      window.dispatchEvent(event);
      return { event, get calls() { return calls; } };
    },
    click() { button.dispatchEvent(new Event('click')); },
    installed() { window.dispatchEvent(new Event('appinstalled')); },
    display(matches) { displayMode.matches = matches; displayMode.dispatchEvent(new Event('change')); }
  };
}

test('install option appears only when the browser offers installation', () => {
  const v = viewer();
  assert.equal(v.controls.hidden, true);
  v.click();
  const offer = v.offer();
  assert.equal(offer.event.defaultPrevented, true);
  assert.equal(v.controls.hidden, false);
  assert.equal(v.button.hidden, false);
  assert.equal(v.status.hidden, true);
  assert.equal(offer.calls, 0);
});

test('HTTP and installed displays never offer the install action', () => {
  for (const options of [{ secure: false }, { standalone: true }, { iosStandalone: true }]) {
    const v = viewer(options);
    const offer = v.offer();
    v.click();
    assert.equal(v.controls.hidden, true);
    assert.equal(offer.calls, 0);
  }
});

test('prompt uses the click gesture once and reports acceptance', async () => {
  const v = viewer();
  let resolve;
  const offer = v.offer(() => new Promise(done => { resolve = done; }));
  v.click();
  assert.equal(offer.calls, 1);
  assert.equal(v.button.hidden, true);
  v.click();
  assert.equal(offer.calls, 1);
  resolve({ outcome: 'accepted' });
  await settle();
  assert.equal(v.button.hidden, true);
  assert.equal(v.controls.hidden, false);
  assert.match(v.status.textContent, /Install started/);
});

test('legacy userChoice dismissal allows a fresh browser prompt', async () => {
  const v = viewer();
  const dismissed = v.offer(async () => undefined, Promise.resolve({ outcome: 'dismissed' }));
  v.click();
  await settle();
  assert.equal(dismissed.calls, 1);
  assert.equal(v.button.hidden, true);
  assert.match(v.status.textContent, /Install dismissed/);
  const fresh = v.offer();
  assert.equal(v.button.hidden, false);
  assert.equal(v.status.hidden, true);
  v.click();
  await settle();
  assert.equal(fresh.calls, 1);
});

test('browser denial consumes the prompt and shows manual Android instructions', async () => {
  const v = viewer();
  const offer = v.offer(() => { throw new Error('NotAllowedError'); });
  v.click();
  await settle();
  assert.equal(v.button.hidden, true);
  assert.match(v.status.textContent, /Android.*Install app or Add to Home screen/);
  v.click();
  assert.equal(offer.calls, 1);
});

test('installation from the browser menu clears a captured prompt', () => {
  const v = viewer();
  const offer = v.offer();
  v.installed();
  v.click();
  assert.equal(offer.calls, 0);
  assert.equal(v.button.hidden, true);
  assert.match(v.status.textContent, /is installed/);
  v.offer();
  assert.equal(v.button.hidden, true);
});

test('installation finishing during a pending prompt keeps the installed status', async () => {
  const v = viewer();
  let resolve;
  v.offer(() => new Promise(done => { resolve = done; }));
  v.click();
  v.installed();
  resolve({ outcome: 'accepted' });
  await settle();
  assert.equal(v.button.hidden, true);
  assert.match(v.status.textContent, /is installed/);
});

test('switching to standalone discards the prompt even after returning to the tab', () => {
  const v = viewer();
  const offer = v.offer();
  v.display(true);
  assert.equal(v.controls.hidden, true);
  v.display(false);
  v.click();
  assert.equal(v.controls.hidden, true);
  assert.equal(offer.calls, 0);
});
