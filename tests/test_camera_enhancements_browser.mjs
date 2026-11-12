// End-to-end Chromium checks using the real web app and its backend settings file.
// Build: dotnet build tests/SecurityCameraViewer.Web.Tests -c Release
// Run: node --test tests/test_camera_enhancements_browser.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { createServer } from 'node:net';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/camera-enhancements-browser', randomUUID());
const profile = resolve(work, 'chrome');
const cameraId = '00000000000000000000000000000001';
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
const webDll = process.env.SCV_TEST_WEB_DLL || resolve(root, 'src/SecurityCameraViewer.Web/bin/Release/net10.0/SecurityCameraViewer.Web.dll');
const balanced = { brightness: 0, contrast: 8, gamma: 1, denoise: 0, sharpness: 30, radius: 1.2, saturation: 0, vibrance: 0 };
const errors = [], connections = [];
let browser, server, browserExit, serverExit, url, first, second, serverLog = '';

async function until(action, label) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    try { if (await action()) return; } catch (error) { if (error.name !== 'TypeError') throw error; }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('Timed out: ' + label);
}

async function connection(webSocketUrl) {
  const socket = new WebSocket(webSocketUrl), pending = new Map(); let sequence = 0;
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const request = pending.get(message.id); pending.delete(message.id);
      if (message.error) request.reject(new Error(message.error.message)); else request.resolve(message.result);
    } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
  };
  const call = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence; pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  await call('Runtime.enable'); await call('Page.enable');
  const page = { call, evaluate, socket }; connections.push(page); return page;
}

before(async () => {
  assert.ok(browserPath, 'Install Chromium or set SCV_TEST_BROWSER.'); assert.ok(existsSync(webDll), 'Build the web app first.');
  mkdirSync(work, { recursive: true });
  const listener = createServer(); await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
  url = 'http://127.0.0.1:' + port;
  const settingsPath = resolve(work, 'settings.json');
  writeFileSync(settingsPath, JSON.stringify({ Version: 1, ActiveConfigurationId: 'default', Integrations: {}, Configurations: [{
    Connection: { AutoConnect: false, Sources: [1, 2].map(channel => ({
      Id: channel.toString(16).padStart(32, '0'), Channel: channel, Name: 'Camera ' + channel, MainStream: 'rtsp://127.0.0.1:18554/test'
    })) }
  }] }));
  server = spawn('dotnet', [webDll, '--urls', url, '--settings', settingsPath], {
    cwd: resolve(root, 'src/SecurityCameraViewer.Web'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  server.stdout.on('data', data => { serverLog += data; }); server.stderr.on('data', data => { serverLog += data; });
  serverExit = new Promise(resolve => server.once('exit', resolve));
  await until(async () => (await fetch(url + '/api/state')).ok, 'web server startup');
  browser = spawn(browserPath, ['--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + profile,
    '--no-first-run', '--no-default-browser-check', '--enable-unsafe-swiftshader', 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  browserExit = new Promise(resolve => browser.once('exit', resolve));
  await until(() => existsSync(resolve(profile, 'DevToolsActivePort')), 'Chromium startup');
  const debugPort = readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0];
  const targets = await (await fetch('http://127.0.0.1:' + debugPort + '/json')).json();
  first = await connection(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await first.call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 800, deviceScaleFactor: 1, mobile: false });
  const target = await first.call('Target.createTarget', { url: 'about:blank' });
  const newTargets = await (await fetch('http://127.0.0.1:' + debugPort + '/json')).json();
  second = await connection(newTargets.find(item => item.id === target.targetId).webSocketDebuggerUrl);
  for (const page of [first, second]) {
    await page.call('Page.navigate', { url });
    await until(() => page.evaluate('typeof tiles !== "undefined" && tiles.size === 2'), 'camera controls');
  }
});

after(async () => {
  if (first?.socket.readyState === WebSocket.OPEN) await first.call('Browser.close').catch(() => {});
  for (const page of connections) page.socket.close();
  if (browser && browser.exitCode === null) browser.kill();
  if (browserExit) await browserExit;
  if (server && server.exitCode === null) server.kill();
  if (serverExit) await serverExit;
  writeFileSync(resolve(work, 'server.log'), serverLog);
  assert.equal(dirname(profile), work, 'Only remove this isolated browser profile.');
  if (existsSync(profile)) rmSync(profile, { recursive: true, force: true });
  assert.deepEqual(errors, [], 'No browser script failures.');
});

async function open(page, channel = 1) {
  await page.evaluate(`focus = ${channel}; arrange();
    (() => { const tile = tiles.get(${channel}); tile.url = URL.createObjectURL(new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#436658"/></svg>'], { type: 'image/svg+xml' })); tile.img.src = tile.url;
      tile.root.classList.add('has-frame'); })();`);
  await until(() => page.evaluate(`tiles.get(${channel}).img.naturalWidth === 640`), 'retained test picture');
  await page.evaluate(`tiles.get(${channel}).zoom.refresh(); tiles.get(${channel}).enhancements.setOptionsOpen(true);`);
}

async function click(page, label) {
  const box = await page.evaluate(`(() => { const button = tiles.get(focus).root.querySelector('[aria-label="${label}"]');
    const rect = button.getBoundingClientRect(); return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2, width: rect.width, height: rect.height }; })()`);
  assert.ok(box.width > 0 && box.height > 0, label + ' is visible');
  await page.call('Input.dispatchMouseEvent', { type: 'mousePressed', x: box.x, y: box.y, button: 'left', clickCount: 1 });
  await page.call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: box.x, y: box.y, button: 'left', clickCount: 1 });
}

async function editSlider(page, key, values) {
  await page.evaluate(`(() => { const input = tiles.get(focus).enhancements.inputs.get('${key}').input;
    for (const value of ${JSON.stringify(values)}) { input.value = value; input.dispatchEvent(new Event('input')); }
    input.dispatchEvent(new Event('change')); })()`);
  await saved(page);
}
async function saved(page) {
  await until(() => page.evaluate('!tiles.get(focus).enhancements.preferences.saving && !Object.keys(tiles.get(focus).enhancements.preferences.pending).length'), 'settings save');
}
const values = page => page.evaluate('tiles.get(focus).enhancements.settings');
async function backendValues() { return (await (await fetch(url + '/api/state')).json()).enhancements[cameraId]; }

test('real controls save every slider and restore settings after refresh and in another browser', async () => {
  await open(first);
  const custom = { brightness: 18, contrast: 22, gamma: 1.35, denoise: 60, sharpness: 88, radius: 2.4, saturation: -10, vibrance: 15 };
  for (const [key, value] of Object.entries(custom)) await editSlider(first, key, [value]);
  await click(first, 'Enable picture enhancements'); await saved(first);
  assert.deepEqual(await backendValues(), { enabled: true, ...custom });
  await until(() => second.evaluate('tiles.get(1).enhancements.settings.brightness === 18 && tiles.get(1).enhancements.enabled'), 'other browser sharing');
  assert.equal(await second.evaluate('tiles.get(2).enhancements.enabled'), false);
  await first.call('Page.reload');
  await until(() => first.evaluate('typeof tiles !== "undefined" && tiles.size === 2 && tiles.get(1).enhancements.settings.brightness === 18'), 'refresh restoration');
  assert.equal(await first.evaluate('tiles.get(1).enhancements.preferences.value.enabled'), true);
  await open(first); assert.deepEqual(await values(first), custom);
  await click(first, 'Enable picture enhancements'); await saved(first);
  assert.deepEqual(await backendValues(), { enabled: false, ...custom });
});

test('native undo/redo buttons restore one drag, presets and reset and save their results', async () => {
  await click(first, 'Reset enhancement settings'); await saved(first);
  await first.evaluate('tiles.get(1).enhancements.setOptionsOpen(false); tiles.get(1).enhancements.setOptionsOpen(true);');
  await editSlider(first, 'brightness', [1, 7, 18]);
  await first.evaluate('tiles.get(1).enhancements.preset.value = "lowLight"; tiles.get(1).enhancements.preset.dispatchEvent(new Event("change"));');
  await saved(first); const lowLight = await values(first);
  await click(first, 'Reset enhancement settings'); await saved(first);
  for (const expected of [lowLight, { ...balanced, brightness: 18 }, balanced]) {
    await click(first, 'Undo enhancement change'); await saved(first); assert.deepEqual(await values(first), expected);
    assert.deepEqual(await backendValues(), { enabled: false, ...expected });
  }
  assert.equal(await first.evaluate('tiles.get(1).enhancements.undoButton.disabled'), true);
  for (const expected of [{ ...balanced, brightness: 18 }, lowLight, balanced]) {
    await click(first, 'Redo enhancement change'); await saved(first); assert.deepEqual(await values(first), expected);
  }
  assert.equal(await first.evaluate('tiles.get(1).enhancements.redoButton.disabled'), true);
  await first.evaluate('tiles.get(1).enhancements.setOptionsOpen(false); tiles.get(1).enhancements.setOptionsOpen(true);');
  assert.equal(await first.evaluate('tiles.get(1).enhancements.undoButton.disabled'), true);
});

test('save failures retain the edit and offer a working retry', async () => {
  await first.call('Network.enable');
  await first.call('Network.emulateNetworkConditions', { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await first.evaluate('const input = tiles.get(1).enhancements.inputs.get("brightness").input; input.value = 21; input.dispatchEvent(new Event("input")); input.dispatchEvent(new Event("change"));');
  await until(() => first.evaluate('tiles.get(1).enhancements.preferences.error'), 'failed save feedback');
  assert.equal((await values(first)).brightness, 21); assert.equal((await backendValues()).brightness, 0);
  await first.call('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await click(first, 'Retry saving'); await saved(first);
  assert.equal((await backendValues()).brightness, 21);
});

test('Reset, Undo and Redo remain visible and inside the panel on narrow touch screens', async () => {
  await first.call('Emulation.setDeviceMetricsOverride', { width: 375, height: 850, deviceScaleFactor: 1, mobile: true });
  await first.evaluate('arrange(); tiles.get(1).zoom.refresh();');
  const buttons = await first.evaluate('Array.from(tiles.get(1).root.querySelectorAll(".camera-enhancement-actions button"), button => { const r = button.getBoundingClientRect(); return { text: button.textContent, left: r.left, right: r.right, top: r.top, bottom: r.bottom }; })');
  assert.deepEqual(buttons.map(button => button.text), ['Reset settings', 'Undo', 'Redo']);
  assert.ok(buttons.every(button => button.left >= 0 && button.right <= 375 && button.top >= 0 && button.bottom <= 850));
  const screenshot = await first.call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(resolve(work, 'enhancement-controls-narrow.png'), Buffer.from(screenshot.data, 'base64'));
  console.log('Enhancement screenshot: ' + resolve(work, 'enhancement-controls-narrow.png'));
});
