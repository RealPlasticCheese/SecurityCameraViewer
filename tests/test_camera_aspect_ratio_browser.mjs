// Aspect-ratio settings and presentation checks in Chromium against the real web settings API.
// Build: dotnet build tests/SecurityCameraViewer.Web.Tests -c Release
// Run: node --test tests/test_camera_aspect_ratio_browser.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { createServer } from 'node:net';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/camera-aspect-ratio-browser', randomUUID());
const profile = resolve(work, 'chrome');
const cameraId = '00000000000000000000000000000001';
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
const webDll = process.env.SCV_TEST_WEB_DLL || resolve(root, 'src/SecurityCameraViewer.Web/bin/Release/net10.0/SecurityCameraViewer.Web.dll');
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
      Id: channel.toString(16).padStart(32, '0'), Channel: channel, Name: 'Camera ' + channel, MainStream: 'rtsp://127.0.0.1:1/main', SubStream: channel === 1 ? 'rtsp://127.0.0.1:1/sub' : null
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

async function openSetup() {
  await first.evaluate('document.getElementById("connection").click()');
  await until(() => first.evaluate('!!document.querySelector(".setup-camera-panel select") && !document.getElementById("setup-save").disabled'), 'camera setup');
}

const selectors = `Array.from(document.querySelectorAll('.setup-camera-panel:not([hidden]) label')).filter(label => /^(Main stream|Substream) aspect ratio$/.test(label.firstChild.textContent)).map(label => label.querySelector('select'))`;
const near = (actual, expected) => assert.ok(Math.abs(actual - expected) < 0.05, `${actual} != ${expected}`);

test('independent aspect controls follow their URLs, default to Auto and persist across browsers and refresh', async () => {
  await openSetup();
  const controls = await first.evaluate(`(${selectors}).map(select => ({ value: select.value, options: Array.from(select.options, option => [option.text, option.value]), previous: select.parentElement.previousElementSibling.firstChild.textContent }))`);
  const options = [['Auto', 'auto'], ['4:3', '4:3'], ['16:9', '16:9'], ['1:1', '1:1'], ['9:16', '9:16'], ['Custom', 'custom']];
  assert.deepEqual(controls, [{ value: 'auto', options, previous: 'Main RTSP URL' }, { value: 'auto', options, previous: 'Substream RTSP URL (optional)' }]);
  assert.equal(await first.evaluate('Array.from(document.querySelectorAll("input[data-aspect-ratio]")).every(input => input.disabled && getComputedStyle(input.parentElement).display === "none")'), true);
  await first.evaluate(`(${selectors}).forEach((select, index) => { select.value = index ? '16:9' : '4:3'; select.dispatchEvent(new Event('change')); }); document.getElementById('setup-save').click();`);
  await until(async () => {
    const catalog = await (await fetch(url + '/api/setup/configurations')).json();
    return catalog.configurations[0].sources[0].mainStreamAspectRatio === '4:3' && catalog.configurations[0].sources[0].subStreamAspectRatio === '16:9';
  }, 'both saved aspect ratios');
  await until(() => first.evaluate('!document.getElementById("setup-save").disabled && document.getElementById("setup-result").textContent.startsWith("Saved and activated")'), 'setup saved');
  await first.evaluate('document.getElementById("setup-close").click()');
  await until(() => second.evaluate('state.settings.sources[0].subStreamAspectRatio === "16:9"'), 'other browser configuration');
  await first.call('Page.reload');
  await until(() => first.evaluate('typeof tiles !== "undefined" && tiles.size === 2 && state.settings.sources[0].mainStreamAspectRatio === "4:3"'), 'refresh restoration');
  await openSetup(); assert.deepEqual(await first.evaluate(`(${selectors}).map(select => select.value)`), ['4:3', '16:9']);
  await first.evaluate(`(${selectors})[0].scrollIntoView({ block: 'center' })`);
  const screenshot = await first.call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(resolve(work, 'aspect-ratio-settings.png'), Buffer.from(screenshot.data, 'base64'));
  await first.evaluate('document.getElementById("setup-close").click()');
});

async function picture(quality, channel = 1) {
  await first.evaluate(`state.settings.substream = ${quality}; createTiles(); (() => {
    const tile = tiles.get(${channel}); tile.root.classList.add('has-frame');
    tile.url = URL.createObjectURL(new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="640" height="480"><rect width="640" height="480" fill="#436658"/><ellipse cx="320" cy="240" rx="135" ry="180" fill="#94e4cd"/></svg>'], { type: 'image/svg+xml' })); tile.img.src = tile.url;
  })()`);
  await until(() => first.evaluate(`tiles.get(${channel}).img.complete && tiles.get(${channel}).img.naturalWidth === 640`), 'test frame');
  await first.evaluate(`tiles.get(${channel}).zoom.refresh()`);
}

async function geometry(expression = 'tiles.get(1).img') {
  return first.evaluate(`(() => { const tile = tiles.get(1), media = ${expression}, surface = tile.root.querySelector('.surface').getBoundingClientRect(), r = media.getBoundingClientRect();
    return { left: r.left - surface.left, top: r.top - surface.top, width: r.width, height: r.height, surfaceWidth: surface.width, surfaceHeight: surface.height, fit: getComputedStyle(media).objectFit }; })()`);
}

test('main, substream, portrait, square and custom pictures resize correctly without cropping', async () => {
  for (const [main, sub] of [['4:3', '16:9'], ['1:1', '9:16'], ['1.85:1', '32:9']]) {
    await first.evaluate(`state.settings.sources[0].mainStreamAspectRatio = '${main}'; state.settings.sources[0].subStreamAspectRatio = '${sub}'`);
    for (const [quality, ratio] of [[false, main], [true, sub]]) {
      const [rw, rh] = ratio.split(':').map(Number), expected = rw / rh;
      await picture(quality);
      for (const width of [1000, 375]) {
        await first.call('Emulation.setDeviceMetricsOverride', { width, height: 800, deviceScaleFactor: 1, mobile: width === 375 });
        for (const focused of [false, true]) {
          await first.evaluate(`focus = ${focused ? 1 : 'null'}; arrange(); tiles.get(1).zoom.refresh();`);
          const box = await geometry(); near(box.width / box.height, expected); assert.equal(box.fit, 'fill');
          near(box.left, (box.surfaceWidth - box.width) / 2); near(box.top, (box.surfaceHeight - box.height) / 2);
          assert.ok(box.width <= box.surfaceWidth + 0.01 && box.height <= box.surfaceHeight + 0.01);
        }
      }
    }
  }
  const screenshot = await first.call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(resolve(work, 'corrected-substream.png'), Buffer.from(screenshot.data, 'base64'));
  console.log('Aspect ratio screenshots: ' + work);
});

test('grid video/canvas and main-stream sound/rewind use their own ratios and enhancement canvas stays aligned', async () => {
  await first.evaluate('state.settings.sources[0].mainStreamAspectRatio = "4:3"; state.settings.sources[0].subStreamAspectRatio = "16:9"');
  await picture(true);
  const ratios = await first.evaluate(`(() => {
    const tile = tiles.get(1), surface = tile.root.querySelector('.surface'), values = [];
    for (const className of ['grid-camera-video', 'grid-camera-canvas', 'live-camera-video', 'camera-enhanced-picture']) {
      const canvas = document.createElement(className.includes('video') ? 'video' : 'canvas'); canvas.className = className;
      if (canvas.tagName === 'VIDEO') { Object.defineProperty(canvas, 'videoWidth', { value: 640 }); Object.defineProperty(canvas, 'videoHeight', { value: 480 }); }
      else { canvas.width = 640; canvas.height = 480; }
      surface.append(canvas); tile.root.classList.add('grid-media-ready');
      if (className === 'camera-enhanced-picture') tile.root.classList.add('live-media-ready');
      tile.zoom.position(canvas, surface.getBoundingClientRect());
      const r = canvas.getBoundingClientRect(); values.push(r.width / r.height); canvas.remove();
    }
    tile.root.classList.remove('live-media-ready', 'grid-media-ready'); return values;
  })()`);
  [16 / 9, 16 / 9, 4 / 3, 4 / 3].forEach((value, index) => near(ratios[index], value));
  await first.call('Page.bringToFront');
  await first.evaluate(`focus = 1; arrange(); (() => { const view = tiles.get(1).enhancements; view.enabledToggle.checked = true; view.enabledToggle.dispatchEvent(new Event('change')); })()`);
  await until(() => first.evaluate('tiles.get(1).root.classList.contains("enhancement-ready")'), 'enhanced corrected picture');
  const enhanced = await geometry('tiles.get(1).enhancements.processor.canvas'); near(enhanced.width / enhanced.height, 16 / 9);
  await first.evaluate(`(() => { const view = tiles.get(1).enhancements; view.enabledToggle.checked = false; view.enabledToggle.dispatchEvent(new Event('change')); })()`);
});

test('Auto and cameras without a substream retain intrinsic playback; invalid ratios are rejected', async () => {
  await picture(true, 2);
  assert.equal(await first.evaluate('tiles.get(2).zoom.aspectRatios.preview'), 'auto');
  assert.equal(await first.evaluate('getComputedStyle(tiles.get(2).img).objectFit'), 'contain');
  await first.evaluate(`state.settings.sources[1].mainStreamAspectRatio = '4:3'; state.settings.sources[1].subStreamAspectRatio = '16:9'`);
  await picture(true, 2); assert.equal(await first.evaluate('tiles.get(2).zoom.aspectRatios.preview'), '4:3');
  const catalog = await (await fetch(url + '/api/setup/configurations')).json();
  const settings = catalog.configurations[0]; settings.sources[0].mainStreamAspectRatio = '0:1';
  const response = await fetch(url + '/api/setup/configurations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ settings }) });
  assert.equal(response.status, 400);
});

test('Custom validates before saving and restores decimal ratios independently from portrait presets', async () => {
  await openSetup();
  await first.evaluate(`(${selectors}).forEach((select, index) => { select.value = index ? '9:16' : 'custom'; select.dispatchEvent(new Event('change')); });`);
  for (const value of ['', '0:1', '1:0', '-2:1', 'garbage', 'Infinity:1', '1e3:1']) {
    await first.evaluate(`(() => { const input = document.querySelector('input[data-aspect-ratio="mainStreamAspectRatio"]'); input.value = ${JSON.stringify(value)}; input.dispatchEvent(new Event('input')); document.getElementById('setup-save').click(); })()`);
    const status = await first.evaluate('(() => { const input = document.querySelector("input[data-aspect-ratio=mainStreamAspectRatio]"); return { open: document.getElementById("setup-save").closest("dialog").open, valid: input.checkValidity(), disabled: input.disabled, value: input.value, message: input.validationMessage }; })()');
    assert.ok(status.open && !status.valid, JSON.stringify(status));
    const catalog = await (await fetch(url + '/api/setup/configurations')).json(); assert.equal(catalog.configurations[0].sources[0].mainStreamAspectRatio, '4:3');
  }
  await first.evaluate(`(() => { const input = document.querySelector('input[data-aspect-ratio="mainStreamAspectRatio"]'); input.value = '1.85:1'; input.dispatchEvent(new Event('input')); document.getElementById('setup-save').click(); })()`);
  await until(async () => {
    const catalog = await (await fetch(url + '/api/setup/configurations')).json();
    return catalog.configurations[0].sources[0].mainStreamAspectRatio === '1.85:1' && catalog.configurations[0].sources[0].subStreamAspectRatio === '9:16';
  }, 'custom and portrait ratios saved');
  await first.call('Page.reload');
  await until(() => first.evaluate('typeof tiles !== "undefined" && tiles.size === 2'), 'custom settings refresh');
  await openSetup(); assert.deepEqual(await first.evaluate(`(${selectors}).map(select => select.value)`), ['custom', '9:16']);
  assert.equal(await first.evaluate('document.querySelector("input[data-aspect-ratio=mainStreamAspectRatio]").value'), '1.85:1');
  await first.evaluate('document.querySelector("input[data-aspect-ratio=mainStreamAspectRatio]").scrollIntoView({ block: "center" })');
  const screenshot = await first.call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(resolve(work, 'custom-aspect-ratio-settings.png'), Buffer.from(screenshot.data, 'base64'));
  await first.evaluate(`(${selectors})[0].value = 'auto'; (${selectors})[0].dispatchEvent(new Event('change')); document.getElementById('setup-save').click();`);
  await until(async () => (await (await fetch(url + '/api/setup/configurations')).json()).configurations[0].sources[0].mainStreamAspectRatio === 'auto', 'Auto replaces custom');
});
