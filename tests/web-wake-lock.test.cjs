const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/wake-lock.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));

function lock() {
  const sentinel = new EventTarget();
  sentinel.released = false;
  sentinel.release = async () => {
    sentinel.released = true;
    sentinel.dispatchEvent(new Event('release'));
  };
  return sentinel;
}

function viewer({ secure = true, supported = true, request } = {}) {
  const button = new EventTarget();
  button.attributes = {};
  button.setAttribute = (name, value) => { button.attributes[name] = value; };
  const status = {};
  const document = new EventTarget();
  document.visibilityState = 'visible';
  document.getElementById = id => id === 'keep-awake' ? button : status;
  const window = new EventTarget();
  window.isSecureContext = secure;
  const locks = [];
  let requests = 0;
  const navigator = supported ? { wakeLock: { request: async type => {
    assert.equal(type, 'screen');
    requests++;
    const acquired = request ? await request() : lock();
    locks.push(acquired);
    return acquired;
  } } } : {};
  runInNewContext(source, { document, window, navigator });
  return {
    button, status, locks, get requests() { return requests; },
    click() { button.dispatchEvent(new Event('click')); },
    visibility(value) { document.visibilityState = value; document.dispatchEvent(new Event('visibilitychange')); },
    page(type) { window.dispatchEvent(new Event(type)); }
  };
}

test('opt-in toggle acquires and releases the screen lock', async () => {
  const v = viewer();
  assert.equal(v.requests, 0);
  assert.equal(v.button.attributes['aria-pressed'], 'false');
  v.click(); await settle();
  assert.equal(v.button.textContent, 'Keep screen awake: On');
  assert.equal(v.button.attributes['aria-pressed'], 'true');
  v.click(); await settle();
  assert.equal(v.locks[0].released, true);
  assert.equal(v.button.textContent, 'Keep screen awake: Off');
  v.visibility('hidden'); v.visibility('visible'); await settle();
  assert.equal(v.requests, 1);
});

test('tab switching releases and reacquires, including a rapid return', async () => {
  const v = viewer();
  v.click(); await settle();
  v.visibility('hidden'); await settle();
  assert.equal(v.locks[0].released, true);
  assert.equal(v.button.textContent, 'Keep screen awake: Paused');
  v.visibility('visible'); await settle();
  assert.equal(v.requests, 2);
  v.visibility('hidden'); v.visibility('visible'); await settle();
  assert.equal(v.requests, 3);
  assert.equal(v.button.textContent, 'Keep screen awake: On');
});

test('turning off during acquisition releases a late result', async () => {
  let resolve;
  const v = viewer({ request: () => new Promise(done => { resolve = done; }) });
  v.click();
  v.click();
  const acquired = lock(); resolve(acquired); await settle();
  assert.equal(acquired.released, true);
  assert.equal(v.button.textContent, 'Keep screen awake: Off');
});

test('a stale request cannot replace a newer lock after off/on', async () => {
  const pending = [];
  const v = viewer({ request: () => new Promise(resolve => pending.push(resolve)) });
  v.click(); v.click(); v.click();
  const old = lock(), current = lock();
  pending[1](current); await settle();
  pending[0](old); await settle();
  assert.equal(old.released, true);
  assert.equal(current.released, false);
  assert.equal(v.button.textContent, 'Keep screen awake: On');
  v.click(); await settle();
  assert.equal(current.released, true);
});

test('browser denial reports failure and allows a manual retry', async () => {
  let deny = true;
  const v = viewer({ request: () => { if (deny) throw new Error('NotAllowedError'); return lock(); } });
  v.click(); await settle();
  assert.equal(v.button.attributes['aria-pressed'], 'false');
  assert.match(v.status.textContent, /permissions or power-saving/);
  deny = false; v.click(); await settle();
  assert.equal(v.button.textContent, 'Keep screen awake: On');
});

test('system release shows paused without a retry loop', async () => {
  const v = viewer();
  v.click(); await settle();
  await v.locks[0].release(); await settle();
  assert.equal(v.button.textContent, 'Keep screen awake: Paused');
  assert.equal(v.requests, 1);
  v.visibility('hidden'); v.visibility('visible'); await settle();
  assert.equal(v.requests, 2);
});

test('navigation releases the lock and back/forward restoration reacquires it', async () => {
  const v = viewer();
  v.click(); await settle();
  v.page('pagehide'); await settle();
  assert.equal(v.locks[0].released, true);
  assert.equal(v.requests, 1);
  v.page('pageshow'); await settle();
  assert.equal(v.requests, 2);
});

test('insecure and unsupported browsers explain why the toggle is unavailable', () => {
  for (const options of [{ secure: false }, { supported: false }]) {
    const v = viewer(options);
    assert.equal(v.button.disabled, true);
    assert.match(v.status.textContent, options.secure === false ? /HTTPS or localhost/ : /does not support/);
    v.click();
    assert.equal(v.requests, 0);
  }
});
