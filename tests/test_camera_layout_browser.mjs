// Real Chromium layout checks using the viewer markup, tile builder and layout controller.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/camera-layout-browser', randomUUID()), profile = resolve(work, 'chrome');
const executable = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe', '/usr/bin/chromium', '/usr/bin/google-chrome'
].find(existsSync);
const asset = name => readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot', name), 'utf8');
let browser, debug, exited, call, evaluate, sequence = 0;
const requests = new Map(), errors = [];

async function until(action, label) {
  const end = Date.now() + 15000;
  while (Date.now() < end) {
    const result = await action(); if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('Timed out: ' + label);
}

before(async () => {
  assert.ok(executable, 'Install Chromium or set SCV_TEST_BROWSER.'); mkdirSync(work, { recursive: true });
  browser = spawn(executable, ['--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + profile,
    '--no-first-run', '--no-default-browser-check', 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  exited = new Promise(resolve => browser.once('exit', resolve)); browser.once('error', error => errors.push(error.message));
  const port = await until(() => {
    assert.deepEqual(errors, []);
    const file = resolve(profile, 'DevToolsActivePort'); return existsSync(file) && readFileSync(file, 'utf8').split('\n')[0];
  }, 'browser startup');
  const targets = await (await fetch('http://127.0.0.1:' + port + '/json')).json();
  debug = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { debug.onopen = resolve; debug.onerror = reject; });
  debug.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const request = requests.get(message.id); requests.delete(message.id);
      message.error ? request.reject(new Error(message.error.message)) : request.resolve(message.result);
    } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
  };
  call = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence; requests.set(id, { resolve, reject }); debug.send(JSON.stringify({ id, method, params }));
  });
  evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  await call('Runtime.enable'); await call('Page.enable');
  const app = asset('app.js');
  const fixture = `
    const $ = id => document.getElementById(id), menu = $('viewer-menu'), menuToggle = $('menu-toggle');
    const tiles = new Map(), liveAudio = { stop() {} }, pushToTalk = { channel: null, stop() {}, bind() {} };
    let focus = null, cameraPictureInPicture, liveRewind, state = { settings: { configurationId: 'home', sources:
      ['Front door', 'Driveway', 'Garden', 'Garage'].map((name, i) => ({ id: 'camera-' + i, channel: i + 1, name })) } };
    class View { dispose() {} stop() {} refresh() {} setFocused() {} reset() { return false; } }
    class CameraEnhancementPreferences {}
    const CameraZoomView = View, CameraEnhancementView = View, CameraDetectionOverlay = View, CameraPlayback = View;
    function renderLiveAudio() {} function updatePictureInPicture() {} function updatePreviewSelection() {}
    ${asset('camera-layout.js')}
    ${asset('camera-collection.js')}
    const cameraLayout = new CameraLayoutView($('cameras'), { getItem: () => null, setItem() {}, removeItem() {} }, () => {});
    ${app.slice(app.indexOf('function setMenu('), app.indexOf('menuToggle.onclick'))}
    menuToggle.onclick = () => setMenu(menu.hidden);
    ${app.slice(app.indexOf('function arrange() {'), app.indexOf('function createTiles() {'))}
    ${app.slice(app.indexOf('function createTiles() {'), app.indexOf('function renderStatus() {'))}
    ${app.slice(app.indexOf('function sidewaysView() {'), app.indexOf("$('sideways-view').onclick"))}
    $('sideways-view').onclick = sidewaysView;
    createTiles();
    for (const [channel, tile] of tiles) {
      tile.root.classList.add('has-frame'); tile.badge.textContent = 'LIVE';
      tile.img.src = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="' + ['#507568','#275168','#785949','#65588a'][channel - 1] + '"/></svg>');
    }
  `;
  const html = asset('index.html').replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, '').replace(/<link\b[^>]*>/g, '')
    .replace('</head>', '<style>' + asset('app.css') + '</style></head>').replace('</body>', '<script>' + fixture + '</script></body>');
  await call('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(html).toString('base64') });
  await until(() => evaluate('typeof cameraLayout !== "undefined" && tiles.size === 4'), 'viewer fixture');
});

after(async () => {
  if (debug?.readyState === WebSocket.OPEN) { await call('Browser.close').catch(() => {}); debug.close(); }
  if (browser && browser.exitCode === null) await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]);
  if (browser && browser.exitCode === null) { browser.kill(); await exited; }
  rmSync(profile, { recursive: true, force: true }); assert.deepEqual(errors, []);
});

async function resize(width, height, touch = true) {
  await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: touch });
  await call('Emulation.setTouchEmulationEnabled', { enabled: touch });
  await evaluate('arrange()');
}
async function click(id) {
  const point = await evaluate(`(() => { const r = $('${id}').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  await call('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
  await call('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
}
async function assertGrid() {
  const boxes = await evaluate('[...tiles.values()].map(tile => { const r = tile.root.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; })');
  assert.ok(boxes[0].x < boxes[1].x); assert.equal(boxes[0].y, boxes[1].y);
  assert.equal(boxes[0].x, boxes[2].x); assert.ok(boxes[0].y < boxes[2].y);
  assert.equal(boxes[2].y, boxes[3].y); assert.equal(boxes[1].x, boxes[3].x);
  assert.ok(boxes.every(box => box.width > 0 && box.height > 0));
}

test('mobile split-screen aspect automatically changes stacked cameras to 2x2 and back', async () => {
  await resize(393, 852);
  assert.equal(await evaluate('cameraLayout.profile.stacked'), true);
  await resize(393, 393); assert.equal(await evaluate('cameraLayout.profile.stacked'), true);
  await resize(393, 392); await assertGrid();
  await resize(393, 350); await assertGrid();
  assert.equal(await evaluate('cameraLayout.sideways'), false);
  await resize(844, 350); await assertGrid();
  await resize(393, 852);
  assert.equal(await evaluate('cameraLayout.profile.stacked'), true);
});

test('the button beside fullscreen forces 2x2 in tall mobile splits and retains it through focus and resizing', async () => {
  await resize(393, 550);
  assert.equal(await evaluate('$(' + JSON.stringify('fullscreen') + ').nextElementSibling.id'), 'sideways-view');
  assert.equal(await evaluate('getComputedStyle($("sideways-view")).display'), 'flex');
  await evaluate('setMenu(true)'); await click('sideways-view'); await assertGrid();
  assert.equal(await evaluate('$("sideways-view").getAttribute("aria-pressed")'), 'true');
  await evaluate('tiles.get(2).root.querySelector(".surface").dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));');
  assert.deepEqual(await evaluate('[...tiles.values()].map(tile => tile.root.hidden)'), [true, false, true, true]);
  await evaluate('tiles.get(2).root.querySelector(".surface").dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));');
  await resize(393, 700); await assertGrid();
  await evaluate('setMenu(true)');
  const screenshot = await call('Page.captureScreenshot', { format: 'png' });
  const path = resolve(work, 'mobile-sideways-menu.png'); writeFileSync(path, Buffer.from(screenshot.data, 'base64'));
  console.log('Sideways layout screenshot:', path);
  await click('sideways-view');
  assert.equal(await evaluate('cameraLayout.profile.stacked'), true);
  assert.equal(await evaluate('$("sideways-view").getAttribute("aria-pressed")'), 'false');
  await resize(1000, 600, false);
  assert.equal(await evaluate('getComputedStyle($("sideways-view")).display'), 'none');
});
