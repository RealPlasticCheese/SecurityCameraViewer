// Run: node --test tests/test_viewer_menu_browser.mjs
// Set SCV_TEST_BROWSER to a Chromium executable if needed.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { createServer } from 'node:http';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/viewer-menu-browser', randomUUID());
const profile = resolve(work, 'chrome');
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
let browser, exited, debug, call, evaluate;
let fixtureHtml = '', fixtureUrl;
const server = createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  response.end(fixtureHtml);
});
let sequence = 0;
const requests = new Map(), errors = [];

async function until(action, label) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    if (await action()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error('Timed out: ' + label);
}

before(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  fixtureUrl = 'http://127.0.0.1:' + server.address().port;
  assert.ok(browserPath, 'Install Chromium or set SCV_TEST_BROWSER.');
  mkdirSync(work, { recursive: true });
  browser = spawn(browserPath, ['--headless=new', '--remote-debugging-port=0',
    '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check', 'about:blank'],
    { windowsHide: true, stdio: 'ignore' });
  exited = new Promise(resolve => browser.once('exit', resolve));
  browser.once('error', error => errors.push(error.message));
  const portFile = resolve(profile, 'DevToolsActivePort');
  await until(() => { assert.deepEqual(errors, []); return existsSync(portFile); }, 'browser startup');
  const port = readFileSync(portFile, 'utf8').split('\n')[0];
  const targets = await (await fetch('http://127.0.0.1:' + port + '/json')).json();
  debug = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { debug.onopen = resolve; debug.onerror = reject; });
  debug.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const request = requests.get(message.id);
      requests.delete(message.id);
      if (message.error) request.reject(new Error(message.error.message));
      else request.resolve(message.result);
    } else if (message.method === 'Runtime.exceptionThrown') {
      errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
    }
  };
  call = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence;
    requests.set(id, { resolve, reject });
    debug.send(JSON.stringify({ id, method, params }));
  });
  evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  await call('Runtime.enable');
  await call('Page.enable');
});

after(async () => {
  if (debug?.readyState === WebSocket.OPEN) {
    await call('Browser.close').catch(() => {});
    debug.close();
  }
  if (browser && browser.exitCode === null) browser.kill();
  if (exited) await exited;
  await new Promise(resolve => server.close(resolve));
  assert.equal(dirname(profile), work, 'Remove only the isolated test browser profile.');
  if (existsSync(profile)) rmSync(profile, { recursive: true, force: true });
  assert.deepEqual(errors, [], 'No browser script failures.');
});

async function reset(touch = false) {
  await call('Emulation.setDeviceMetricsOverride', { width: 800, height: 600, deviceScaleFactor: 1, mobile: touch });
  await call('Emulation.setTouchEmulationEnabled', { enabled: touch });
  await call('Emulation.setEmulatedMedia', { features: [] });
  const css = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/app.css'), 'utf8');
  const html = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/index.html'), 'utf8');
  const controls = html.slice(html.indexOf('  <div class="viewer-controls">'), html.indexOf('  <section id="cameras"'));
  const app = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
  const start = app.indexOf('function setMenu('), end = app.indexOf('async function api(', start);
  assert.ok(start >= 0 && end > start, 'Exercise the actual menu event handlers.');
  const controlsStart = app.indexOf('async function fullscreen()'), controlsEnd = app.indexOf('async function poll()', controlsStart);
  assert.ok(controlsStart >= 0 && controlsEnd > controlsStart, 'Exercise the actual refresh and fullscreen handlers.');
  fixtureHtml = '<meta name="viewport" content="width=device-width,initial-scale=1"><style>' + css +
    '</style>' + controls + '<button id="outside" style="position:absolute;left:200px;top:200px">Video</button><script>' +
    'const menu = document.getElementById("viewer-menu"), menuToggle = document.getElementById("menu-toggle");' +
    app.slice(start, end) + 'const $ = id => document.getElementById(id); const tiles = new Map(); let focus = null; function arrange() {}' + app.slice(controlsStart, controlsEnd) +
    'window.reloadMarker = Math.random();</script>';
  await call('Page.navigate', { url: fixtureUrl });
  await until(() => evaluate('!!document.getElementById("menu-toggle")?.onclick'), 'menu fixture');
  await move(600, 400);
  await move(300, 300);
  await surface(false);
}

const move = (x, y) => call('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
async function click(x, y) {
  await move(x, y);
  await call('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
  await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
}
async function key(key, code) {
  const windowsVirtualKeyCode = key === 'Enter' ? 13 : 9;
  await call('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode, text: key === 'Enter' ? '\r' : '' });
  await call('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode });
}
async function surface(visible) {
  await until(async () => {
    const right = await evaluate('document.querySelector("#menu-toggle span").getBoundingClientRect().right');
    return Math.abs(right - (visible ? 35 : 5)) < .1;
  }, visible ? 'revealed control' : 'minimal edge tab');
}

test('idle control shows five pixels with a doubled invisible tap target', async () => {
  await reset();
  assert.equal(await evaluate('document.getElementById("menu-toggle").getBoundingClientRect().width'), 34);
  assert.deepEqual(await evaluate(`(() => { const style = getComputedStyle(menuToggle, '::before');
    return [style.width, style.height]; })()`), ['68px', '68px']);
  for (const [x, y] of [[25, 20], [65, 20], [20, 65], [68, 68]]) {
    assert.equal(await evaluate(`document.elementFromPoint(${x}, ${y}).id`), 'menu-toggle');
  }
  for (const [x, y] of [[69, 20], [20, 69], [69, 69]]) {
    assert.notEqual(await evaluate(`document.elementFromPoint(${x}, ${y}).id`), 'menu-toggle');
  }
  assert.equal(await evaluate('document.getElementById("menu-toggle").getAttribute("aria-expanded")'), 'false');
  assert.equal(await evaluate('getComputedStyle(document.querySelector("#menu-toggle span")).transitionDuration'), '0.18s');
});

test('nearby pointer reveals the control, leaving retracts it, and repeated approaches work', async () => {
  await reset();
  for (let attempt = 0; attempt < 3; attempt++) {
    await move(93, 93);
    await surface(true);
    await move(300, 300);
    await surface(false);
  }
});

for (const fullscreen of [false, true]) {
  test((fullscreen ? 'fullscreen' : 'windowed') + ' hamburger reveals at the exact left edge, top edge and corner', async () => {
    await reset();
    if (fullscreen) {
      await click(20, 20);
      const point = await evaluate(`(() => { const r = document.getElementById('fullscreen').getBoundingClientRect();
        return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
      await click(point.x, point.y);
      await until(() => evaluate('!!document.fullscreenElement'), 'enter fullscreen for edge hover');
    }
    await move(300, 300);
    await surface(false);
    for (const [x, y] of [[0, 20], [20, 0], [0, 0], [80, 20], [20, 80], [93, 93]]) {
      await move(x, y);
      await surface(true);
      assert.equal(await evaluate('menu.hidden'), true, 'Hover reveals only the hamburger button.');
      await move(300, 300);
      await surface(false);
    }
    for (const [x, y] of [[0, 100], [100, 0], [94, 93], [93, 94], [94, 94]]) {
      await move(x, y);
      await surface(false);
    }
    if (fullscreen) await evaluate('document.exitFullscreen()');
  });
}

test('open menu stays visible away from the pointer and retracts after outside dismissal', async () => {
  await reset();
  await click(20, 20);
  await move(500, 500);
  await surface(true);
  assert.equal(await evaluate('menu.hidden'), false);
  assert.equal(await evaluate('menuToggle.getAttribute("aria-expanded")'), 'true');
  await click(500, 500);
  await surface(false);
  assert.equal(await evaluate('menu.hidden'), true);
});

test('closing with the toggle does not pin it open through mouse focus', async () => {
  await reset();
  await click(20, 20);
  await click(20, 20);
  await surface(true);
  await move(500, 500);
  await surface(false);
});

test('keyboard focus reveals the toggle and keyboard activation opens and closes the menu', async () => {
  await reset();
  await key('Tab', 'Tab');
  await surface(true);
  assert.equal(await evaluate('document.activeElement.id'), 'menu-toggle');
  await key('Enter', 'Enter');
  assert.equal(await evaluate('menu.hidden'), false);
  await key('Enter', 'Enter');
  await surface(true);
  await key('Tab', 'Tab');
  await surface(false);
});

test('tablet uses the same edge tab and tapping the invisible button area opens and closes it', async () => {
  await reset(true);
  assert.equal(await evaluate('matchMedia("(hover:hover) and (pointer:fine)").matches'), false);
  const tap = async () => {
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: 25, y: 20 }] });
    await call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  };
  await tap();
  await surface(true);
  assert.equal(await evaluate('menu.hidden'), false);
  await tap();
  await surface(false);
  assert.equal(await evaluate('menu.hidden'), true);
});

test('touch opens the menu throughout the doubled area but ignores points past either boundary', async () => {
  for (const [x, y] of [[65, 20], [20, 65], [68, 68]]) {
    await reset(true);
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
    await call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await surface(true);
    assert.equal(await evaluate('menu.hidden'), false, `${x}, ${y}`);
    assert.equal(await evaluate('menuToggle.getAttribute("aria-expanded")'), 'true');
    assert.equal(await evaluate('getComputedStyle(menuToggle, "::before").content'), 'none', 'The expanded target cannot cover open menu items.');
  }
  // Touch browsers may adjust hits near an edge towards a nearby button.
  // Exact CSS boundaries are verified above with elementFromPoint.
  for (const [x, y] of [[100, 20], [20, 100], [100, 100]]) {
    await reset(true);
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
    await call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await surface(false);
    assert.equal(await evaluate('menu.hidden'), true, `${x}, ${y}`);
  }
});

test('external menu closure also retracts the control and reduced motion removes animation', async () => {
  await reset();
  await call('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  assert.equal(await evaluate('getComputedStyle(document.querySelector("#menu-toggle span")).transitionDuration'), '0s');
  await evaluate('setMenu(true)');
  await surface(true);
  await evaluate('menu.hidden = true');
  await surface(false);
});

test('accessible refresh icon sits beside Full screen and reloads the front end', async () => {
  await reset();
  await click(20, 20);
  const button = await evaluate(`(() => {
    const refresh = document.getElementById('refresh-app'), fullscreen = document.getElementById('fullscreen');
    const r = refresh.getBoundingClientRect(), f = fullscreen.getBoundingClientRect();
    return { label: refresh.getAttribute('aria-label'), title: refresh.title, type: refresh.type,
      hasIcon: refresh.querySelector('svg[aria-hidden="true"]') !== null,
      adjacent: refresh.previousElementSibling === fullscreen, right: r.right, fullRight: f.right,
      x: r.x + r.width / 2, y: r.y + r.height / 2, width: r.width, height: r.height };
  })()`);
  assert.equal(button.label, 'Refresh app');
  assert.equal(button.title, 'Refresh app');
  assert.equal(button.type, 'button');
  assert.equal(button.hasIcon, true);
  assert.equal(button.adjacent, true);
  assert.ok(button.right > button.fullRight && button.right <= 321);
  assert.ok(button.width >= 32 && button.height >= 32);
  const marker = await evaluate('window.reloadMarker');
  await click(button.x, button.y);
  await until(() => evaluate(`window.reloadMarker !== undefined && window.reloadMarker !== ${marker}`), 'front end document reload');
  assert.equal(await evaluate('menu.hidden'), true);
});

test('fullscreen icons switch between expand and shrink with accessible labels', async () => {
  await reset();
  const state = () => evaluate(`(() => {
    const button = document.getElementById('fullscreen');
    return { label: button.getAttribute('aria-label'), pressed: button.getAttribute('aria-pressed'),
      title: button.title, expand: getComputedStyle(button.querySelector('[data-fullscreen-icon="expand"]')).display,
      shrink: getComputedStyle(button.querySelector('[data-fullscreen-icon="shrink"]')).display };
  })()`);
  await click(20, 20);
  assert.deepEqual(await state(), { label: 'Full screen', pressed: 'false', title: 'Full screen (F11)', expand: 'block', shrink: 'none' });
  const activate = async () => {
    const point = await evaluate(`(() => { const r = document.getElementById('fullscreen').getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
    await click(point.x, point.y);
  };
  await activate();
  await until(() => evaluate('!!document.fullscreenElement'), 'enter fullscreen');
  await until(() => evaluate('document.getElementById("fullscreen").getAttribute("aria-pressed") === "true"'), 'fullscreen change event');
  await evaluate('setMenu(true)');
  assert.deepEqual(await state(), { label: 'Exit full screen', pressed: 'true', title: 'Exit full screen (F11)', expand: 'none', shrink: 'block' });
  await activate();
  await until(() => evaluate('!document.fullscreenElement'), 'exit fullscreen');
  await until(() => evaluate('document.getElementById("fullscreen").getAttribute("aria-pressed") === "false"'), 'fullscreen exit event');
  await evaluate('setMenu(true)');
  assert.deepEqual(await state(), { label: 'Full screen', pressed: 'false', title: 'Full screen (F11)', expand: 'block', shrink: 'none' });
});
