// Real Chromium regression checks using the application's markup, styles and rewind controller.
// Run: node --test tests/test_playback_controls_browser.mjs
// Set SCV_TEST_BROWSER to a Chromium executable outside the usual install locations.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/playback-controls-browser', randomUUID());
const profile = resolve(work, 'chrome');
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
let browser, debug, exited, call, evaluate, mediaFixture;
const requests = new Map(), browserErrors = [];
let sequence = 0;
const readAsset = name => readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot', name), 'utf8');

async function until(action, label) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    const value = await action();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('Timed out: ' + label);
}

before(async () => {
  assert.ok(browserPath, 'Install Chromium or set SCV_TEST_BROWSER.');
  mkdirSync(work, { recursive: true });
  const mediaPath = resolve(work, 'reverse-scan.mp4');
  const generated = spawnSync(process.env.SCV_TEST_FFMPEG || 'ffmpeg', ['-hide_banner', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=8', '-t', '18', '-an', '-c:v', 'libx264',
    '-pix_fmt', 'yuv420p', '-g', '8', '-keyint_min', '8', '-sc_threshold', '0', '-bf', '0',
    '-movflags', 'frag_keyframe+empty_moov+default_base_moof', '-frag_duration', '1000000', mediaPath],
    { windowsHide: true, encoding: 'utf8' });
  assert.equal(generated.status, 0, generated.error?.message || generated.stderr);
  const media = readFileSync(mediaPath), boxes = [];
  for (let at = 0; at < media.length;) {
    const size = media.readUInt32BE(at), type = media.toString('ascii', at + 4, at + 8);
    assert.ok(size >= 8 && at + size <= media.length, 'Valid generated MP4 box.');
    boxes.push({ at, size, type }); at += size;
  }
  const first = boxes.find(box => box.type === 'moof');
  const fragments = boxes.filter(box => box.type === 'moof').map(box => {
    const next = boxes[boxes.indexOf(box) + 1]; assert.equal(next.type, 'mdat');
    return media.subarray(box.at, next.at + next.size).toString('base64');
  });
  assert.equal(fragments.length, 18);
  mediaFixture = { init: media.subarray(0, first.at).toString('base64'), fragments };
  browser = spawn(browserPath, ['--headless=new', '--remote-debugging-port=0',
    '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check', 'about:blank'],
    { windowsHide: true, stdio: 'ignore' });
  exited = new Promise(resolve => browser.once('exit', resolve));
  browser.once('error', error => browserErrors.push(error.message));
  const port = await until(() => {
    assert.deepEqual(browserErrors, []);
    const path = resolve(profile, 'DevToolsActivePort');
    try { return existsSync(path) && readFileSync(path, 'utf8').split('\n')[0]; }
    catch (error) { if (['EBUSY', 'ENOENT'].includes(error.code)) return false; throw error; }
  }, 'Chromium startup');
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
      browserErrors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
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
  await call('Runtime.enable'); await call('Page.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 600, deviceScaleFactor: 1, mobile: false });
  const app = readAsset('app.js');
  const start = app.indexOf('function createTiles() {'), end = app.indexOf('function renderStatus() {', start);
  assert.ok(start >= 0 && end > start, 'Exercise the actual camera footer markup and click handlers.');
  const fullscreen = app.slice(app.indexOf('async function fullscreen()'), app.indexOf("$('fullscreen').onclick"));
  const fullscreenListener = app.slice(app.indexOf("document.addEventListener('fullscreenchange'"), app.indexOf('async function poll()'));
  const menuFunction = app.slice(app.indexOf('function setMenu('), app.indexOf('menuToggle.onclick'));
  const fixture = `
    window.fetch = () => new Promise(() => {});
    const tiles = new Map(), liveAudio = { volume: 1, players: new Map(), stop() {} }, pushToTalk = { stop() {}, bind() {} };
    const $ = id => document.getElementById(id);
    const menu = $('viewer-menu'), menuToggle = $('menu-toggle');
    ${menuFunction}
    menuToggle.onclick = () => setMenu(menu.hidden);
    ${readAsset('camera-pip.js')}
    ${fullscreen}
    ${fullscreenListener}
    function updatePictureInPicture() {}
    let liveRewind, cameraPictureInPicture, fullscreenPending = false, focus = null, generation = 1;
    const state = { settings: { sources: ['Front door', 'Driveway', 'Garden', 'Garage'].map((name, index) =>
      ({ channel: index + 1, name, motionActivated: true })) } };
    class View { dispose() {} stop() {} refresh() {} }
    class CameraEnhancementPreferences {}
    const CameraEnhancementView = View, CameraDetectionOverlay = View, CameraPlayback = View;
    ${readAsset('camera-zoom.js')}
    function updatePreviewSelection() {}
    function arrange() {
      for (const [channel, tile] of tiles) {
        const index = channel - 1;
        tile.root.hidden = focus !== null && focus !== channel;
        tile.zoom?.setFocused(focus === channel);
        tile.root.style.cssText = focus === null
          ? 'left:' + (index % 2 * 50) + '%;top:' + (Math.floor(index / 2) * 50) + '%;width:50%;height:50%'
          : 'left:0;top:0;width:100%;height:100%';
      }
    }
    window.checkCalls = []; window.audioCalls = []; window.checkError = null;
    async function api(path) { checkCalls.push(path); if (checkError) throw new Error(checkError); }
    function toggleListening(channel) { audioCalls.push(channel); }
    function renderStatus() {}
    ${readAsset('camera-collection.js')}
    ${app.slice(start, end)}
    createTiles();
    for (const [channel, tile] of tiles) {
      tile.root.classList.add('has-frame');
      tile.img.src = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#507568"/></svg>');
      tile.root.querySelector('.surface').style.background = 'linear-gradient(135deg,#1c3240,#507568)';
      tile.root.querySelector('.tile-header').id = 'footer-' + channel;
      tile.check.id = 'check-' + channel;
      tile.listen.id = 'audio-' + channel; tile.listen.hidden = false;
      tile.badge.textContent = 'LIVE'; tile.badge.classList.add('live');
      tile.playbackStatus.textContent = 'Auto: Native video · H.264'; tile.audioLabel.textContent = 'AAC Audio';
    }
    ${readAsset('playback-controls.js')}
    ${readAsset('live-rewind.js')}
    ${readAsset('camera-overlay-appearance.js')}
    window.controller = liveRewind;
    window.encodeRewindWindow = (playlist, media) => {
      const decode = value => Uint8Array.from(atob(value), character => character.charCodeAt(0));
      const initialization = decode(media.init), fragments = playlist.fragments.map(fragment => decode(media.fragments[fragment.sequence - 1]));
      const metadata = new TextEncoder().encode(JSON.stringify({ playlist, initializationLength: initialization.length, fragmentLengths: fragments.map(bytes => bytes.length) }));
      const bytes = new Uint8Array(4 + metadata.length + initialization.length + fragments.reduce((sum, fragment) => sum + fragment.length, 0));
      new DataView(bytes.buffer).setUint32(0, metadata.length, true); bytes.set(metadata, 4);
      let at = 4 + metadata.length; bytes.set(initialization, at); at += initialization.length;
      for (const fragment of fragments) { bytes.set(fragment, at); at += fragment.length; }
      return bytes.buffer;
    };
    // Media transport is covered separately; retain the real tiles for footer interaction checks.
    controller.context = () => ({ tiles: new Map(), focus, generation, volume: 1 });
    controller.snapshot = { generation: 1, settings: { enabled: true }, cameras: [
      { channel: 1, ranges: [{ epoch: 'first', start: 100000, end: 160000 }] }
    ] };
    controller.online = true;
    controller.tick();
    window.cameraEscapes = 0;
    document.addEventListener('keydown', event => { if (event.key === 'Escape') cameraEscapes++; });
  `;
  const html = readAsset('index.html').replace(/<script\b[^>]*src=[^>]*><\/script>/g, '')
    .replace(/<link\b[^>]*>/g, '').replace('</head>', '<style>' + readAsset('app.css') + '</style></head>')
    .replace('</body>', '<script>' + fixture + '</script></body>');
  await call('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(html).toString('base64') });
  await until(() => evaluate('window.controller?.controls.available'), 'rewind fixture');
  await until(() => evaluate('[...tiles.values()].every(tile => tile.img.naturalWidth > 0)'), 'camera zoom fixture pictures');
});

after(async () => {
  if (debug?.readyState === WebSocket.OPEN) {
    await call('Browser.close').catch(() => {});
    debug.close();
  }
  if (browser && browser.exitCode === null) browser.kill();
  if (exited) await exited;
  assert.equal(dirname(profile), work, 'Only remove this isolated browser profile.');
  if (existsSync(profile)) rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  assert.deepEqual(browserErrors, [], 'No browser script failures.');
});

const settle = () => evaluate('new Promise(resolve => setTimeout(resolve, 200))');
async function assertMenuSurface(revealed) {
  await until(() => evaluate(`Math.abs(document.querySelector('#menu-toggle span').getBoundingClientRect().right - ${revealed ? 35 : 5}) < .1`),
    revealed ? 'hamburger revealed with main playback controls' : 'hamburger retracted to edge tab');
  assert.equal(await evaluate('document.getElementById("viewer-menu").hidden'), true, 'Revealing the button does not open the menu.');
}
const move = (x, y) => call('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
async function revealTab() {
  const area = await box('playback-tab-area');
  await move(area.x + area.width - 2, area.y + 2);
  await settle();
}
async function reveal() {
  await revealTab();
  await click('playback-tab');
}
async function touch(point, type = 'touchStart') {
  await call('Input.dispatchTouchEvent', { type, touchPoints: type === 'touchEnd' ? [] : [point] });
}
async function tap(id) {
  const rect = await box(id);
  await touch({ x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 });
  await touch(null, 'touchEnd');
}
async function assertTabSurface(revealed) {
  await until(() => evaluate(`Math.abs($('playback-tab').querySelector('span').getBoundingClientRect().right - ${revealed ? 35 : 5}) < .1`),
    revealed ? 'playback chevron revealed' : 'playback edge sliver');
}

async function box(id) {
  return evaluate(`(() => { const r = document.getElementById(${JSON.stringify(id)}).getBoundingClientRect();
    return { x: r.x, y: r.y, width: r.width, height: r.height }; })()`);
}
async function click(id) {
  await settle();
  const rect = await box(id), x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
  await move(x, y);
  await call('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
  await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
  await settle();
}
async function reset(width = 1000, height = 600) {
  await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await call('Emulation.setTouchEmulationEnabled', { enabled: false });
  await move(500, 100);
  await evaluate(`controller.controls.suspend(); controller.controls.autoHideMilliseconds = 3000;
    focus = null; arrange(); checkCalls.length = audioCalls.length = 0; checkError = null;
    for (const tile of tiles.values()) tile.playbackStatus.textContent = 'Auto: Native video · H.264';
    controller.snapshot.settings.enabled = true; controller.online = true; controller.status = ''; controller.goLive();
    document.getElementById('overlay-transparency').value = '0';
    document.getElementById('overlay-transparency').dispatchEvent(new Event('input'));`);
  await settle();
}
async function screenshot(name) {
  const result = await call('Page.captureScreenshot', { format: 'png' });
  const path = resolve(work, name + '.png');
  writeFileSync(path, Buffer.from(result.data, 'base64'));
  console.log('Playback screenshot: ' + path);
}

async function assertFooterClearance(id = 'playback-controls') {
  const top = await evaluate(`Math.min(...[...document.querySelectorAll('.tile')].filter(tile => {
    const r = tile.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && Math.abs(r.bottom - $('cameras').getBoundingClientRect().bottom) <= 1;
  }).map(tile => Math.max(tile.getBoundingClientRect().top, tile.querySelector('.tile-header').getBoundingClientRect().top)))`);
  const overlay = await box(id);
  assert.ok(Math.abs(top - (overlay.y + overlay.height) - 4) < .01, 'Playback ends exactly 4 CSS pixels above the visible bottom footer. ' + JSON.stringify({ id, top, overlay }));
}

test('main live controls reveal the hamburger and automatic hiding retracts it', async () => {
  await reset();
  await assertMenuSurface(false);
  await evaluate('controller.controls.autoHideMilliseconds = 500');
  await reveal();
  await assertMenuSurface(true);
  await move(500, 100);
  await until(() => evaluate('!controller.controls.open'), 'main controls automatically hide');
  await assertMenuSurface(false);
});

test('compact historical dock does not reveal the hamburger but the main historical controls do', async () => {
  await reset();
  await evaluate('controller.clock.pause(true); controller.seek(125000); controller.controls.close();');
  assert.equal(await evaluate('controller.controls.dock.hidden'), false);
  await assertMenuSurface(false);
  await reveal();
  await assertMenuSurface(true);
  await move(500, 100);
  await evaluate('controller.controls.close()');
  assert.equal(await evaluate('controller.controls.dock.hidden'), false);
  await assertMenuSurface(false);
});

test('tablet vicinity tap reveals only the playback tab and its activation reveals the main controls and hamburger', async () => {
  await reset(320, 568);
  await call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 2 });
  await tap('playback-tab-area'); await assertTabSurface(true); await assertMenuSurface(false);
  assert.equal(await evaluate('controller.controls.open'), false);
  await tap('playback-tab'); await assertMenuSurface(true);
  assert.equal(await evaluate('controller.controls.open'), true);
  await evaluate('controller.controls.suspend()'); await assertMenuSurface(false); await assertTabSurface(false);
});

test('hidden playback overlay does not reveal the hamburger even with a stale open class', async () => {
  await reset();
  await evaluate('controller.controls.root.classList.add("is-open"); controller.controls.root.hidden = true;');
  await assertMenuSurface(false);
});

test('native Check now and Audio clicks work with playback hidden without revealing it', async () => {
  await reset();
  for (const channel of [3, 4]) {
    await click('check-' + channel); await click('audio-' + channel);
    assert.equal(await evaluate('controller.controls.open'), false);
  }
  assert.deepEqual(await evaluate('checkCalls'), ['cameras/3/check', 'cameras/4/check']);
  assert.deepEqual(await evaluate('audioCalls'), [3, 4]);
});

test('open and pinned playback leave real footer controls clickable at full transparency', async () => {
  await reset(); await reveal(); await assertFooterClearance();
  await click('check-3'); await click('audio-4');
  assert.equal(await evaluate('controller.controls.open'), true);
  await click('playback-pin');
  await evaluate(`$('overlay-transparency').value = '100'; $('overlay-transparency').dispatchEvent(new Event('input'));`);
  await click('check-4'); await click('audio-3');
  assert.equal(await evaluate('controller.controls.pinned'), true);
  await assertFooterClearance();
  assert.deepEqual(await evaluate('checkCalls'), ['cameras/3/check', 'cameras/4/check']);
  assert.deepEqual(await evaluate('audioCalls'), [4, 3]);
  await screenshot('footer-controls-accessible');
});

test('wrapped status and viewport changes move pinned playback above the measured footer', async () => {
  await reset(); await reveal(); await click('playback-pin');
  const originalOffset = await evaluate('controller.controls.layout.offset');
  await evaluate(`tiles.get(3).playbackStatus.textContent = 'Auto: Software video · H.265 · Decoder unavailable, checking the camera connection again';`);
  await call('Emulation.setDeviceMetricsOverride', { width: 620, height: 600, deviceScaleFactor: 1, mobile: false });
  await settle();
  assert.ok(await evaluate('controller.controls.layout.offset') > originalOffset, 'Wrapped text increases the reserved footer height.');
  await assertFooterClearance();
  await click('check-3'); await click('audio-4');
  assert.deepEqual(await evaluate('checkCalls'), ['cameras/3/check']);
  assert.deepEqual(await evaluate('audioCalls'), [4]);
  await call('Emulation.setDeviceMetricsOverride', { width: 1200, height: 720, deviceScaleFactor: 1, mobile: false });
  await settle(); await assertFooterClearance();
  assert.equal((await box('cameras')).height, 718, 'Playback does not reserve grid space.');
});

test('focused views ignore hidden camera footers and Check now failure still reaches its handler', async () => {
  await reset(620, 600); await reveal(); await click('playback-pin');
  await evaluate(`tiles.get(3).playbackStatus.textContent = 'Software decoder unavailable, checking the camera connection again';`);
  await settle();
  const gridOffset = await evaluate('controller.controls.layout.offset');
  await evaluate('focus = 4; arrange(); checkError = "Check failed";'); await settle();
  assert.ok(await evaluate('controller.controls.layout.offset') < gridOffset, 'Hidden tall footers no longer affect the focused camera.');
  await assertFooterClearance();
  await click('check-4'); await click('audio-4');
  assert.equal(await evaluate('tiles.get(4).detail.textContent'), 'Check failed');
  assert.equal(await evaluate('tiles.get(4).check.disabled'), false, 'Failure restores the check button.');
  assert.deepEqual(await evaluate('audioCalls'), [4]);
});

test('compact paused playback also clears the footer and leaves its buttons usable', async () => {
  await reset();
  await evaluate('controller.clock.pause(true); controller.seek(125000); controller.controls.close();');
  await settle(); await assertFooterClearance('playback-dock');
  assert.equal(await evaluate('controller.clock.paused'), true);
  await click('check-4'); await click('audio-4');
  assert.equal(await evaluate('controller.controls.open'), false);
  assert.deepEqual(await evaluate('checkCalls'), ['cameras/4/check']);
  assert.deepEqual(await evaluate('audioCalls'), [4]);
});

test('single-camera views keep both full and compact playback above usable footer controls', async () => {
  for (const width of [1000, 320]) {
    await reset(width, 600);
    await evaluate('focus = 3; arrange();'); await settle();
    const grid = await box('cameras');
    await click('check-3'); await click('audio-3');
    assert.equal(await evaluate('controller.controls.open'), false, 'The single-camera footer does not reveal playback.');
    await reveal(); await click('playback-pin'); await assertFooterClearance();
    await click('check-3'); await click('audio-3');
    assert.equal(await evaluate('controller.controls.pinned'), true);
    await evaluate('controller.clock.pause(true); controller.seek(125000);'); await settle();
    assert.equal(await evaluate('$("playback-sound").hidden'), false, 'Focused playback includes its sound control.');
    await assertFooterClearance();
    await evaluate('controller.controls.close();'); await settle();
    await assertFooterClearance('playback-dock');
    await click('check-3'); await click('audio-3');
    assert.equal(await evaluate('controller.controls.open'), false);
    assert.deepEqual(await evaluate('checkCalls'), Array(3).fill('cameras/3/check'));
    assert.deepEqual(await evaluate('audioCalls'), [3, 3, 3]);
    assert.deepEqual(await box('cameras'), grid, 'Neither playback panel shrinks the focused camera.');
    if (width === 1000) await screenshot('single-camera-compact');
    await click('playback-compact-live');
    assert.equal(await evaluate('controller.historical'), false);
  }
});

test('live viewing has only a small playback edge tab and opening controls preserves full-size cameras', async () => {
  await reset();
  const grid = await box('cameras');
  assert.equal(grid.height, 598);
  await assertTabSurface(false);
  assert.equal(await evaluate('$("playback-tab").getAttribute("aria-controls")'), 'playback-controls');
  assert.equal(await evaluate('document.getElementById("playback-dock").hidden'), true);
  assert.equal((await box('playback-overlay')).height, 0, 'Closed live playback has no visible footprint.');
  assert.equal(await evaluate('controller.controls.bar.inert'), true);
  await screenshot('hidden');
  await reveal();
  assert.equal(await evaluate('controller.controls.open'), true);
  assert.deepEqual(await box('cameras'), grid);
  const bar = await box('playback-controls');
  assert.ok(bar.y < grid.y + grid.height && bar.y + bar.height <= 600, 'Drawer lies over the bottom camera row.');
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '1');
  await screenshot('open');
  await evaluate('controller.controls.close()'); await settle();
  assert.equal(await evaluate('controller.controls.open'), false);
  assert.deepEqual(await box('cameras'), grid);
});

test('mouse vicinity reveals the chevron only, clicking opens playback and inactivity restores the edge sliver', async () => {
  await reset();
  await move(500, 550); await move(100, 599); await settle();
  assert.equal(await evaluate('controller.controls.open || controller.controls.tabRevealed'), false);
  await revealTab(); await assertTabSurface(true); await assertMenuSurface(false);
  assert.equal(await evaluate('controller.controls.open'), false);
  assert.equal(await evaluate('controller.controls.bar.inert'), true);
  await screenshot('edge-tab-revealed');
  await move(500, 100); await assertTabSurface(false);
  await reveal(); assert.equal(await evaluate('controller.controls.open'), true);
  await move(100, 100);
  await until(() => evaluate('!controller.controls.open'), 'three-second auto-hide');
  await settle(); await assertTabSurface(false);
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).visibility'), 'hidden');
  assert.equal(await evaluate('controller.controls.bar.inert'), true);
});

test('the existing transparency slider controls the drawer, which becomes readable during use', async () => {
  await reset(); await reveal(); await move(100, 100);
  await evaluate(`document.getElementById('overlay-transparency').value = '75';
    document.getElementById('overlay-transparency').dispatchEvent(new Event('input'));`);
  await settle();
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '0.25');
  const bar = await box('playback-controls'); await move(10, bar.y + 10); await settle();
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '1');
  await evaluate(`document.getElementById('overlay-transparency').value = '100';
    document.getElementById('overlay-transparency').dispatchEvent(new Event('input'));`);
  await settle(); assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '1');
  await move(100, 100); await settle();
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '0');
});

test('keyboard focus reveals transparent controls and Escape does not reach camera shortcuts', async () => {
  await reset();
  await evaluate(`document.getElementById('overlay-transparency').value = '100';
    document.getElementById('overlay-transparency').dispatchEvent(new Event('input'));
    document.getElementById('cameras').focus();`);
  for (let tab = 0; tab < 12; tab++) {
    await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
    await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
    if (await evaluate('document.activeElement.id === "playback-tab"')) break;
  }
  await settle();
  assert.equal(await evaluate('document.activeElement.id'), 'playback-tab');
  await assertTabSurface(true);
  assert.equal(await evaluate('controller.controls.open'), false);
  await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
  await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
  await settle();
  assert.equal(await evaluate('document.activeElement.id'), 'playback-back');
  assert.equal(await evaluate('document.activeElement.matches(":focus-visible")'), true);
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '1');
  assert.equal(await evaluate('controller.controls.hideTimer'), null);
  const before = await evaluate('cameraEscapes');
  await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  assert.equal(await evaluate('controller.controls.open'), false);
  assert.equal(await evaluate('document.activeElement.id'), 'cameras');
  assert.equal(await evaluate('cameraEscapes'), before);
  assert.equal(await evaluate('controller.controls.bar.inert'), true, 'Closed controls remain inert.');
});

test('pin remains available while the buffer is filling and while the server is unavailable', async () => {
  await reset();
  await evaluate('controller.snapshot.cameras = []; controller.online = false; controller.render();');
  await reveal(); await click('playback-pin'); await move(100, 100);
  assert.equal(await evaluate('document.getElementById("playback-pin").disabled'), false);
  assert.equal(await evaluate('document.getElementById("playback-pause").disabled'), true);
  assert.equal(await evaluate('controller.controls.pinned'), true);
  assert.equal(await evaluate('controller.controls.hideTimer'), null);
  await click('playback-pin'); await move(100, 100);
  assert.equal(await evaluate('controller.controls.pinned'), false);
  assert.equal(await evaluate('controller.controls.hideTimer !== null'), true);
  await evaluate('controller.snapshot.cameras = [{ channel: 1, ranges: [{ epoch: "first", start: 100000, end: 160000 }] }];');
});

test('paused playback survives hiding and compact Go live returns the controller to live', async () => {
  await reset(); await reveal(); await click('playback-pause');
  await evaluate('controller.seek(125000);');
  await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await settle();
  assert.equal(await evaluate('controller.historical && controller.clock.paused'), true);
  assert.equal(await evaluate('controller.clock.current()'), 125000);
  assert.match(await evaluate('document.getElementById("playback-compact-status").textContent'), /^PAUSED · 0× · 34s behind live$/);
  await screenshot('paused');
  await click('playback-compact-live');
  assert.equal(await evaluate('controller.historical'), false);
  assert.equal(await evaluate('document.getElementById("playback-compact-live").hidden'), true);
  assert.equal(await evaluate('controller.controls.open'), false);
});

test('touch users reveal the tab, open and operate a narrow drawer, and return to the sliver without resizing videos', async () => {
  await reset(320, 568);
  await evaluate(`$('overlay-transparency').value = '100'; $('overlay-transparency').dispatchEvent(new Event('input'));`);
  await call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 2 });
  const grid = await box('cameras');
  await tap('playback-tab-area'); await settle(); await assertTabSurface(true);
  assert.equal(await evaluate('controller.controls.open'), false);
  assert.equal(await evaluate('controller.controls.bar.inert'), true);
  await screenshot('touch-tab-revealed');
  await tap('playback-tab'); await settle();
  assert.equal(await evaluate('controller.controls.open'), true);
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).opacity'), '1');
  assert.deepEqual(await box('cameras'), grid);
  assert.equal(await evaluate('document.documentElement.scrollWidth'), 320);
  assert.equal(await evaluate('controller.controls.bar.scrollWidth > controller.controls.bar.clientWidth'), false);
  await tap('playback-pause'); assert.equal(await evaluate('controller.clock.paused'), true);
  await until(() => evaluate('!controller.controls.open'), 'touch auto-hide after using a button');
  await settle(); await assertTabSurface(false); await assertFooterClearance('playback-tab-area');
  assert.deepEqual(await box('cameras'), grid);
  await screenshot('touch-paused');
});

test('two-stage touch tab access preserves playback and keeps the compact dock and footers clear at desktop, phone and landscape sizes', async () => {
  const playbackState = () => evaluate(`({ position: controller.clock.current(), paused: controller.clock.paused,
    rate: controller.signedRate, historical: controller.historical, sound: controller.sound, slider: $('playback-position').value })`);
  for (const [width, height] of [[1458, 746], [320, 568], [568, 320]]) {
    await reset(width, height);
    await call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 2 });
    await evaluate('controller.clock.pause(true); controller.seek(125000);'); await settle();
    const before = await playbackState(), grid = await box('cameras'), area = await box('playback-tab-area');
    const dock = await box('playback-dock');
    assert.ok(dock.x >= area.x + area.width, 'Historical status does not cover the playback tab.');
    await assertFooterClearance('playback-tab-area');
    await touch({ x: area.x + area.width - 1, y: area.y + 1 }); await touch(null, 'touchEnd');
    await settle(); await assertTabSurface(true);
    assert.equal(await evaluate('controller.controls.open'), false);
    assert.deepEqual(await playbackState(), before);
    await tap('playback-tab'); await settle();
    assert.equal(await evaluate('controller.controls.open'), true);
    assert.deepEqual(await playbackState(), before);
    assert.deepEqual(await box('cameras'), grid);
    const slider = await box('playback-position');
    await touch({ x: slider.x + slider.width / 4, y: slider.y + slider.height / 2 }); await touch(null, 'touchEnd');
    assert.notEqual((await playbackState()).slider, before.slider);
    await evaluate('controller.controls.close();'); await settle(); await assertTabSurface(false);
    await tap('playback-compact-live'); assert.equal(await evaluate('controller.historical'), false);
  }
});

test('bottom camera double-tap focus, pinch zoom and panning remain available outside the small tab area', async () => {
  for (const [width, height] of [[1000, 600], [320, 568], [568, 320]]) {
    await reset(width, height);
    await call('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 2 });
    // Keep optional footer metadata compact so even the small grid has visible camera pictures.
    await evaluate("for (const tile of tiles.values()) { tile.playbackStatus.textContent = ''; tile.audioLabel.textContent = ''; }");
    await settle();
    const point = { x: width * .75, y: await evaluate('window.innerHeight - controller.controls.layout.offset - 40') };
    for (let i = 0; i < 2; i++) { await touch(point); await touch(null, 'touchEnd'); }
    await until(() => evaluate('focus === 4'), 'bottom camera double-tap focus');
    assert.equal(await evaluate('controller.controls.open || controller.controls.tabRevealed'), false);
    const first = { x: width * .4, y: height / 2, id: 10 }, second = { x: width * .6, y: height / 2, id: 11 };
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [first] });
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [first, second] });
    await call('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ ...first, x: width * .3 }, { ...second, x: width * .7 }] });
    await touch(null, 'touchEnd');
    const crop = await evaluate('({ ...tiles.get(4).zoom.crop })');
    assert.ok(crop.width < 1, 'Pinch zoom reaches the actual camera zoom controller.');
    await touch({ x: width / 2, y: height / 2 });
    await touch({ x: width / 2 + 20, y: height / 2 + 10 }, 'touchMove');
    await touch(null, 'touchEnd');
    assert.notDeepEqual(await evaluate('({ ...tiles.get(4).zoom.crop })'), crop, 'Dragging pans the zoomed camera.');
    assert.equal(await evaluate('controller.controls.open || controller.controls.tabRevealed'), false);
  }
});

test('desktop double-click focus works on the bottom camera while playback stays closed', async () => {
  for (const width of [1000, 320]) {
    await reset(width, 600);
    await evaluate("for (const tile of tiles.values()) { tile.playbackStatus.textContent = ''; tile.audioLabel.textContent = ''; }");
    await settle(); await revealTab();
    const x = width * .75, y = await evaluate('window.innerHeight - controller.controls.layout.offset - 40');
    await move(x, y);
    for (const clickCount of [1, 2]) {
      await call('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount });
      await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount });
    }
    assert.equal(await evaluate('focus'), 4);
    assert.equal(await evaluate('controller.controls.open || controller.controls.tabRevealed'), false);
  }
});

test('visible viewer menu controls receive touches when overlapping the hidden drawer', async () => {
  await reset(320, 568);
  await call('Emulation.setTouchEmulationEnabled', { enabled: true });
  await evaluate('controller.controls.show();');
  const bar = await box('playback-controls');
  const point = { x: 100, y: bar.y + bar.height / 2 };
  await evaluate(`controller.controls.close(); $('viewer-menu').hidden = false; window.menuTouchCalls = 0;
    window.menuTouchTarget = document.createElement('button'); menuTouchTarget.id = 'menu-touch-target';
    menuTouchTarget.textContent = 'Menu touch fixture';
    menuTouchTarget.style.cssText = 'position:fixed;left:80px;top:${point.y - 16}px;width:100px;height:32px;z-index:11';
    menuTouchTarget.onpointerdown = () => menuTouchCalls++; $('viewer-menu').append(menuTouchTarget);`);
  try {
    assert.equal(await evaluate(`document.elementFromPoint(${point.x}, ${point.y}).id`), 'menu-touch-target');
    await call('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
    await call('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    assert.equal(await evaluate('menuTouchCalls'), 1);
    assert.equal(await evaluate('controller.controls.open'), false);
  } finally {
    await evaluate("menuTouchTarget.remove(); $('viewer-menu').hidden = true;");
  }
});

test('reduced motion suppresses the fade and unavailable buffers hide all playback UI', async () => {
  await reset();
  await call('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  await reveal();
  assert.equal(await evaluate('getComputedStyle(controller.controls.bar).transitionDuration'), '0s');
  assert.equal(await evaluate('getComputedStyle($("playback-tab").querySelector("span")).transitionDuration'), '0s');
  await evaluate('controller.snapshot.settings.enabled = false; controller.goLive();');
  assert.equal(await evaluate('controller.controls.root.hidden'), true);
  assert.equal(await evaluate('controller.controls.bar.inert'), true);
  await call('Emulation.setEmulatedMedia', { features: [] });
});

test('Lucide transport order, accessible names, signed input and pin icons fit a desktop row', async () => {
  for (const width of [1458, 1024]) {
    await reset(width, 746); await reveal();
    const layout = await evaluate(`(() => {
      const buttons = [...document.querySelector('.playback-buttons').children].filter(el => el.tagName === 'BUTTON' && !el.hidden);
      return { ids: buttons.map(el => el.id), tops: buttons.map(el => el.getBoundingClientRect().top),
        names: buttons.map(el => el.getAttribute('aria-label')), pinRight: $('playback-pin').getBoundingClientRect().right,
        icons: buttons.filter(el => !el.id.startsWith('playback-speed-') && el.id !== 'playback-save').map(el => el.querySelector('svg use')?.getAttribute('href')),
        speedMin: $('playback-speed').getAttribute('min'), reverseTransform: getComputedStyle($('playback-reverse').querySelector('svg')).transform,
        pauseIcon: $('playback-pause').querySelector('use').getAttribute('href'), overflow: document.documentElement.scrollWidth > window.innerWidth };
    })()`);
    assert.deepEqual(layout.ids, ['playback-back', 'playback-reverse', 'playback-pause', 'playback-play', 'playback-forward',
      'playback-slower', 'playback-speed-0.5', 'playback-speed-1', 'playback-speed-2', 'playback-speed-4', 'playback-faster', 'playback-live', 'playback-save', 'playback-pin']);
    assert.equal(new Set(layout.tops).size, 1, 'All buttons occupy one row.');
    assert.ok(layout.names.every(Boolean)); assert.ok(layout.icons.every(icon => icon?.startsWith('#lucide-')));
    assert.equal(layout.speedMin, null); assert.equal(layout.reverseTransform, 'matrix(-1, 0, 0, 1, 0, 0)');
    assert.equal(layout.pauseIcon, '#lucide-pause'); assert.equal(layout.overflow, false);
    assert.deepEqual(await evaluate(`[$('playback-compact-live').getAttribute('aria-label'), $('playback-compact-live').querySelector('use').getAttribute('href')]`), ['Go live', '#lucide-radio']);
    assert.ok(layout.pinRight >= width - 10, 'Pin sits at the right edge.');
    await click('playback-pin');
    assert.deepEqual(await evaluate(`[getComputedStyle($('playback-pin').querySelector('.pin')).display,
      getComputedStyle($('playback-pin').querySelector('.unpin')).display, $('playback-pin').getAttribute('aria-label')]`),
      ['none', 'block', 'Unpin playback controls']);
  }
  await evaluate('controller.clock.pause(true); controller.seek(125000);');
  await screenshot('lucide-controls-desktop');
});

test('real transport clicks, manual speed, animals and browser speed rejection keep consistent highlights', async () => {
  await reset(); await reveal(); await click('playback-pin');
  await evaluate('controller.clock.pause(true); controller.seek(125000);');
  await click('playback-reverse'); assert.equal(await evaluate('controller.signedRate'), -1);
  await click('playback-reverse'); assert.equal(await evaluate('controller.signedRate'), -2);
  await click('playback-speed-0.5'); assert.equal(await evaluate('controller.signedRate'), -0.5);
  await click('playback-slower'); assert.equal(await evaluate('controller.signedRate'), -0.25);
  assert.equal(await evaluate(`$('playback-slower').getAttribute('aria-pressed')`), 'true');
  await click('playback-pause'); assert.equal(await evaluate(`$('playback-speed').value`), '0');
  await click('playback-reverse'); assert.equal(await evaluate('controller.signedRate'), -1);
  await click('playback-play'); assert.equal(await evaluate('controller.signedRate'), 1);
  await evaluate(`$('playback-speed').value = '-8'; $('playback-speed').dispatchEvent(new Event('change'));`);
  assert.deepEqual(await evaluate(`[controller.signedRate, $('playback-reverse').getAttribute('aria-pressed'), $('playback-faster').getAttribute('aria-pressed')]`), [-8, 'true', 'true']);
  await click('playback-reverse'); assert.equal(await evaluate('controller.signedRate'), -16);
  await evaluate(`$('playback-speed').value = '16'; $('playback-speed').dispatchEvent(new Event('change'));`);
  assert.equal(await evaluate('controller.signedRate'), 16, 'Installed Chromium accepts 16×.');
  await click('playback-faster');
  assert.deepEqual(await evaluate(`[controller.signedRate, $('playback-speed').value, $('playback-play').getAttribute('aria-pressed')]`), [16, '16', 'true']);
  assert.match(await evaluate(`$('playback-status').textContent`), /does not support 32×.*remains 16×/);
  await click('playback-reverse'); assert.equal(await evaluate('controller.signedRate'), -1);
  await evaluate(`$('playback-speed').value = '0'; $('playback-speed').dispatchEvent(new Event('change'));`);
  assert.deepEqual(await evaluate(`[controller.signedRate, $('playback-pause').getAttribute('aria-pressed'), $('playback-pause').querySelector('use').getAttribute('href')]`), [0, 'true', '#lucide-pause']);
});

test('rewind loading and gaps hide retained live pictures for every grid playback method', async () => {
  await reset();
  await evaluate(`(() => {
    const tile = tiles.get(2), surface = tile.root.querySelector('.surface');
    window.retainedLayers = ['grid-camera-canvas', 'grid-camera-video', 'camera-enhanced-picture'].map(className => {
      const layer = document.createElement(className.includes('video') ? 'video' : 'canvas');
      layer.className = className; surface.append(layer); return layer;
    });
    tile.root.classList.add('rewind-active', 'grid-media-ready', 'has-frame');
  })()`);
  try {
    for (const [gap, enhanced] of [[false, false], [true, false], [false, true], [true, true]]) {
      await evaluate(`tiles.get(2).root.classList.toggle('rewind-gap', ${gap});`);
      await evaluate(`tiles.get(2).root.classList.toggle('enhancement-ready', ${enhanced});`);
      const visibility = await evaluate('retainedLayers.map(layer => ({ type: layer.className, visibility: getComputedStyle(layer).visibility }))');
      assert.ok(visibility.every(layer => layer.visibility === 'hidden'), JSON.stringify(visibility));
      assert.equal(await evaluate('getComputedStyle(tiles.get(2).root.querySelector(".placeholder")).display'), 'flex');
    }
  } finally {
    await evaluate("retainedLayers.forEach(layer => layer.remove()); tiles.get(2).root.classList.remove('rewind-active', 'rewind-gap', 'grid-media-ready', 'enhancement-ready');");
  }
});

test('generated buffered H.264 decodes changing frames in reverse with paused native video', async () => {
  await reset();
  await evaluate(`(async () => { window.scanMedia = ${JSON.stringify(mediaFixture)};
    window.scanRequests = [];
    window.scanRange = { epoch: 'scan', start: 100000, end: 118000 };
    window.scanRoot = document.createElement('div'); scanRoot.className = 'tile';
    scanRoot.style.cssText = 'position:fixed;top:50px;left:50px;width:320px;height:180px;z-index:20';
    scanRoot.innerHTML = '<div class="surface"></div>'; document.body.append(scanRoot);
    window.scanPlayer = new RewindCameraPlayer(1, { root: scanRoot, name: 'Generated reverse test' }, async (url, signal, binary) => {
      scanRequests.push(url);
      if (signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      const at = Number(url.split('at=')[1]), index = Math.floor((at - 100000) / 1000);
      const playlist = { epoch: 'scan', mimeType: 'video/mp4; codecs="avc1.64000c"', hasAudio: false,
        fragments: scanMedia.fragments.slice(index, index + 12).map((_, i) => ({ sequence: index + i + 1,
          start: 100000 + (index + i) * 1000, mediaStartSeconds: index + i, durationSeconds: 1 })) };
      return encodeRewindWindow(playlist, scanMedia);
    }, () => {});
    window.scanFrame = () => { const canvas = document.createElement('canvas'); canvas.width = 32; canvas.height = 18;
      const ctx = canvas.getContext('2d'); ctx.drawImage(scanPlayer.video, 0, 0, 32, 18);
      return Array.from(ctx.getImageData(0, 0, 32, 18).data).join(','); };
    await scanPlayer.synchronize(scanRange, 112500, false, -1); })()`);
  try {
    await until(() => evaluate('scanPlayer.video.readyState >= 2 && !scanPlayer.video.seeking'), 'initial reverse frame decoded');
    assert.equal(await evaluate('scanPlayer.message'), '');
    const later = await evaluate('scanFrame()');
    await evaluate('(async () => { scanPlayer.sound(true, 1); await scanPlayer.synchronize(scanRange, 110500, false, -2); })()');
    await until(() => evaluate('!scanPlayer.video.seeking && scanPlayer.video.readyState >= 2'), 'earlier reverse frame decoded');
    assert.notEqual(await evaluate('scanFrame()'), later, 'Reverse scanning renders a different decoded frame.');
    assert.deepEqual(await evaluate('[scanPlayer.video.paused, scanPlayer.video.muted, scanPlayer.video.currentTime, scanPlayer.video.playbackRate]'), [true, true, 8.5, 1]);
    assert.equal(await evaluate('scanRequests.length'), 1, 'Nearby reverse seeks reuse media.');
    await evaluate('scanPlayer.synchronize(scanRange, 100500, false, -8)');
    await until(() => evaluate('!scanPlayer.video.seeking && scanPlayer.video.readyState >= 2'), 'reverse reload at earlier cache window');
    assert.deepEqual(await evaluate('[scanPlayer.message, scanPlayer.video.currentTime, scanPlayer.video.paused]'), ['', 0.5, true]);
    assert.equal(await evaluate('scanRequests.length'), 2);
    await evaluate('scanPlayer.synchronize(scanRange, 103000, false, 2)');
    await until(() => evaluate('!scanPlayer.video.paused'), 'forward media resumes after reverse');
    assert.equal(await evaluate('scanPlayer.video.playbackRate'), 2);
  } finally { await evaluate('scanPlayer.dispose(); scanRoot.remove();'); }
});

test('generated rewind video stays decoded across cadence-sized timing seams and resumes as coverage returns', async () => {
  await reset();
  await evaluate(`(async () => {
    window.gapMedia = ${JSON.stringify(mediaFixture)};
    window.gapRequests = [];
    const boundaries = [100000, 101000, 102000, 103000, 104158, 105158, 106285, 107285, 108405, 109563, 110563, 111690, 112690];
    window.gapPlaylist = { epoch: 'gaps', mimeType: 'video/mp4; codecs="avc1.64000c"', hasAudio: false,
      fragments: boundaries.slice(0, -1).map((start, i) => ({ sequence: i + 1, start, end: boundaries[i + 1],
        mediaStartSeconds: i, durationSeconds: 1, clockRate: (boundaries[i + 1] - start) / 1000 })) };
    window.gapRange = { epoch: 'gaps', start: boundaries[0], end: boundaries.at(-1) };
    window.gapRoot = document.createElement('div'); gapRoot.className = 'tile';
    gapRoot.style.cssText = 'position:fixed;top:50px;left:50px;width:320px;height:180px;z-index:20';
    gapRoot.innerHTML = '<div class="surface"></div>'; document.body.append(gapRoot);
    window.gapPlayer = new RewindCameraPlayer(1, { root: gapRoot, name: 'Generated timing gap test' }, async (url, signal, binary) => {
      gapRequests.push(url);
      if (signal.aborted) throw new DOMException('Cancelled', 'AbortError');
      return encodeRewindWindow(gapPlaylist, gapMedia);
    }, () => {});
    await gapPlayer.synchronize(gapRange, 103990, true, 1);
  })()`);
  try {
    for (const at of [104001, 104100, 104157, 104158, 106284, 106285, 108404, 108405]) {
      await evaluate(`gapPlayer.synchronize(gapRange, ${at}, true, 1)`);
      await until(() => evaluate('gapPlayer.video.readyState >= 2 && !gapPlayer.video.seeking'), 'paused timing-boundary picture decoded');
      assert.deepEqual(await evaluate('[gapPlayer.message, gapPlayer.epoch, gapRoot.classList.contains("rewind-gap")]'), ['', 'gaps', false]);
      const position = await evaluate(`(() => { const fragment = gapPlaylist.fragments.find(item => item.start <= ${at} && ${at} < item.end);
        return { actual: gapPlayer.video.currentTime, expected: fragment.mediaStartSeconds + (${at} - fragment.start) / (fragment.clockRate * 1000) }; })()`);
      assert.ok(Math.abs(position.actual - position.expected) < .05, JSON.stringify(position));
    }
    await evaluate('gapPlayer.synchronize(gapRange, 103900, true, 1)');
    await until(() => evaluate('!gapPlayer.video.seeking && gapPlayer.video.readyState >= 2'), 'forward start picture decoded');
    // Advance the shared target by actual elapsed browser time. Fixed steps with
    // host round trips would simulate a slow clock and deliberately rewind video.
    const pictures = await evaluate(`(async () => {
      const started = performance.now(), pictures = [];
      while (performance.now() - started <= 700) {
        await gapPlayer.synchronize(gapRange, 103900 + performance.now() - started, false, 1);
        pictures.push({ time: gapPlayer.video.currentTime, message: gapPlayer.message, gap: gapRoot.classList.contains('rewind-gap') });
        await new Promise(resolve => setTimeout(resolve, 50));
      }
      return pictures;
    })()`);
    assert.ok(pictures.length >= 5, 'Forward decoding progresses through the timing boundary.');
    let previous;
    for (const picture of pictures) {
      assert.equal(picture.message, ''); assert.equal(picture.gap, false);
      if (previous !== undefined) assert.ok(picture.time >= previous, JSON.stringify(pictures));
      previous = picture.time;
    }
    for (const at of [104300, 104100, 104001, 103900]) {
      await evaluate(`gapPlayer.synchronize(gapRange, ${at}, false, -1)`);
      await until(() => evaluate('!gapPlayer.video.seeking && gapPlayer.video.readyState >= 2'), 'reverse timing-boundary picture decoded');
      assert.deepEqual(await evaluate('[gapPlayer.message, gapPlayer.video.paused, gapRoot.classList.contains("rewind-gap")]'), ['', true, false]);
    }
    assert.equal(await evaluate('gapRequests.length'), 1, 'All timing-boundary seeks retain one media window.');
    await evaluate('gapPlayer.synchronize(undefined, gapRange.end + 1000, false, 1)');
    assert.match(await evaluate('gapPlayer.message'), /No buffered footage/);
    await evaluate('gapPlayer.synchronize(gapRange, 104100, true, 1)');
    assert.equal(await evaluate('gapRequests.length'), 2, 'Available coverage immediately replaces the missing-footage state.');
    await until(() => evaluate('gapPlayer.video.readyState >= 2 && !gapPlayer.video.seeking'), 'returned historical coverage decoded');
    assert.deepEqual(await evaluate('[gapPlayer.message, gapPlayer.epoch, gapRoot.classList.contains("rewind-gap")]'), ['', 'gaps', false]);
  } finally { await evaluate('gapPlayer.dispose(); gapRoot.remove();'); }
});

test('a short filling rewind window survives a newer target and hides retained live pictures until history is decoded', async () => {
  await reset();
  await evaluate(`(() => {
    window.fillMedia = ${JSON.stringify(mediaFixture)};
    window.fillRequests = []; window.fillCount = 3;
    const playlist = () => ({ epoch: 'filling', mimeType: 'video/mp4; codecs="avc1.64000c"', hasAudio: false,
      fragments: Array.from({ length: fillCount }, (_, i) => ({ sequence: i + 1, start: 100000 + i * 1000,
        mediaStartSeconds: i, durationSeconds: 1 })) });
    window.fillRoot = document.createElement('div'); fillRoot.className = 'tile grid-media-ready has-frame';
    fillRoot.style.cssText = 'position:fixed;top:50px;left:50px;width:320px;height:180px;z-index:20';
    fillRoot.innerHTML = '<div class="surface"><div class="placeholder"><strong>Loading buffered video…</strong></div><canvas class="grid-camera-canvas" width="320" height="180"></canvas></div>';
    document.body.append(fillRoot);
    const retained = fillRoot.querySelector('canvas').getContext('2d'); retained.fillStyle = 'red'; retained.fillRect(0, 0, 320, 180);
    window.fillPlayer = new RewindCameraPlayer(1, { root: fillRoot, name: 'Generated filling buffer' }, async url => {
      fillRequests.push(url); const admitted = encodeRewindWindow(playlist(), fillMedia);
      if (fillRequests.length === 1) await new Promise(resolve => { window.releaseFillWindow = resolve; });
      return admitted;
    }, () => {});
    window.fillLoad = fillPlayer.synchronize({ epoch: 'filling', start: 100000, end: 103000 }, 101500, false, 1);
  })()`);
  try {
    await until(() => evaluate('typeof releaseFillWindow === "function"'), 'captured startup window awaiting delivery');
    assert.equal(await evaluate('getComputedStyle(fillRoot.querySelector(".grid-camera-canvas")).visibility'), 'hidden');
    await evaluate("fillPlayer.synchronize({ epoch: 'filling', start: 100000, end: 106000 }, 104500, false, 1); releaseFillWindow(); fillLoad");
    assert.deepEqual(await evaluate('[fillPlayer.epoch, fillPlayer.waiting, fillPlayer.video.paused, fillRoot.classList.contains("live-media-ready")]'), ['filling', true, true, false]);
    const admittedUrl = await evaluate('fillPlayer.url');
    await evaluate("fillCount = 6; fillPlayer.synchronize({ epoch: 'filling', start: 100000, end: 106000 }, 104500, false, 1)");
    await until(() => evaluate('fillRoot.classList.contains("live-media-ready") && !fillPlayer.video.seeking && fillPlayer.video.readyState >= 2'), 'newer startup target decoded');
    assert.equal(await evaluate('fillPlayer.url'), admittedUrl, 'Filling history extends the same MediaSource instead of starting over.');
    assert.ok(Math.abs(await evaluate('fillPlayer.video.currentTime') - 4.5) < .2);
    assert.equal(await evaluate('getComputedStyle(fillRoot.querySelector(".grid-camera-canvas")).visibility'), 'hidden');
    assert.deepEqual(await evaluate('[fillPlayer.message, fillPlayer.waiting, fillRequests.length, fillRequests.every(url => url.includes("/window?"))]'), ['', false, 2, true]);
  } finally { await evaluate('fillPlayer.dispose(); fillRoot.remove();'); }
});

test('real save controls select a grid interval and a backwards focused interval on desktop and mobile', async () => {
  for (const width of [1458, 320]) {
    await reset(width, 746);
    await evaluate(`window.savedRequests = [];
      controller.context = () => ({ tiles, focus, generation, volume: 1 });
      window.fetch = async (url, options) => {
        if (options?.method === 'POST') { savedRequests.push(JSON.parse(options.body)); return { ok: true, json: async () => [{}] }; }
        return new Promise(() => {});
      };
      controller.clock.pause(true); controller.seek(125000);`);
    await reveal();
    // Programmatic keyboard entry opens the drawer without moving the camera footers.
    await evaluate("controller.controls.root.focus(); $('playback-save').scrollIntoView({ block: 'nearest' });");
    await click('playback-save');
    await screenshot('save-click-' + width);
    assert.equal(await evaluate('controller.clock.paused'), true);
    assert.equal(await evaluate('controller.saveButton.textContent'), 'Save end');
    assert.match(await evaluate('controller.saveLabel.textContent'), /4 cameras.*choose the end/);
    await screenshot('save-selection-' + width);
    await evaluate(`$('playback-position').value = '135000'; $('playback-position').dispatchEvent(new Event('input'));`);
    await click('playback-save');
    await until(() => evaluate('!controller.saving'), 'grid clip saved');
    assert.deepEqual(await evaluate('savedRequests[0]'), { generation: 1, start: 125000, channels: [1, 2, 3, 4], end: 135000 });
    assert.match(await evaluate('controller.saveLabel.textContent'), /View recordings/);
    await evaluate('focus = 2; arrange(); controller.viewChanged(); controller.clock.pause(true); controller.seek(125000);');
    await click('playback-save');
    await evaluate(`$('playback-position').value = '115000'; $('playback-position').dispatchEvent(new Event('input'));`);
    await click('playback-save');
    await until(() => evaluate('!controller.saving'), 'focused clip saved');
    assert.deepEqual(await evaluate('savedRequests[1]'), { generation: 1, start: 125000, channels: [2], end: 115000 });
    assert.equal(await evaluate('document.documentElement.scrollWidth > window.innerWidth'), false);
    await evaluate('controller.goLive(); controller.context = () => ({ tiles: new Map(), focus, generation, volume: 1 });');
    assert.equal(await evaluate('controller.saveButton.disabled'), true);
  }
});


test('saved confirmation disappears after ten seconds and restores drawer height on desktop and mobile', async () => {
  for (const width of [1458, 320]) {
    await reset(width, 746);
    await evaluate("focus = 1; arrange(); controller.context = () => ({ tiles: new Map([[1, tiles.get(1)]]), focus, generation, volume: 1 }); " +
      "window.fetch = async (url, options) => options?.method === 'POST' ? { ok: true, json: async () => [{}] } : new Promise(() => {}); " +
      "controller.clock.pause(true); controller.seek(125000); controller.controls.show();");
    await click('playback-pin');
    const originalHeight = (await box('playback-controls')).height;
    await evaluate("(async () => { await controller.saveRecording(); controller.seek(135000); await controller.saveRecording(); })()");
    assert.equal(await evaluate('controller.saveLabel.hidden'), false);
    assert.ok((await box('playback-controls')).height > originalHeight, 'Confirmation occupies space while shown.');
    await screenshot('save-confirmation-' + width);
    await evaluate('new Promise(resolve => setTimeout(resolve, 8000))');
    assert.equal(await evaluate('controller.saveLabel.hidden'), false, 'Confirmation remains visible before ten seconds.');
    await until(() => evaluate('controller.saveLabel.hidden'), 'ten-second save confirmation expiry');
    assert.equal(await evaluate('controller.saveLabel.textContent'), '');
    assert.equal((await box('playback-controls')).height, originalHeight, 'Confirmation leaves no extra lines after expiry.');
    assert.deepEqual(await evaluate('[controller.clock.current(), controller.clock.paused, controller.historical]'), [135000, true, true]);
    await assertFooterClearance();
    await screenshot('save-confirmation-expired-' + width);
    await evaluate('controller.goLive(); controller.context = () => ({ tiles: new Map(), focus, generation, volume: 1 });');
  }
});


test('Android fullscreen preserves the normal camera layout and lets the menu open over the PiP underlay', async () => {
  await reset(393, 852);
  const agent = await evaluate('navigator.userAgent');
  await call('Emulation.setUserAgentOverride', { userAgent: 'Mozilla/5.0 (Linux; Android 16) Chrome Mobile' });
  const before = await evaluate('[...tiles.values()].map(tile => { const r = tile.root.getBoundingClientRect(); return [r.x, r.y, r.width, r.height]; })');
  const entering = await call('Runtime.evaluate', { expression: 'fullscreen()', returnByValue: true, awaitPromise: true, userGesture: true });
  assert.equal(entering.exceptionDetails, undefined);
  try {
    assert.equal(await evaluate('document.fullscreenElement === document.documentElement'), true);
    assert.deepEqual(await evaluate('[...tiles.values()].map(tile => { const r = tile.root.getBoundingClientRect(); return [r.x, r.y, r.width, r.height]; })'), before);
    assert.equal(await evaluate('cameraPictureInPicture.video.controls'), false);
    assert.equal(await evaluate('getComputedStyle(cameraPictureInPicture.video).pointerEvents'), 'none');
    await click('menu-toggle');
    assert.equal(await evaluate('menu.hidden'), false, 'The real menu is reachable while fullscreen remains active.');
    assert.equal(await evaluate('document.fullscreenElement === document.documentElement'), true);
    await click('menu-toggle'); assert.equal(await evaluate('menu.hidden'), true);
    assert.equal(await evaluate('document.elementFromPoint(300, 400).closest(".tile") !== null'), true, 'Camera interactions stay above the captured underlay.');
    await screenshot('android-fullscreen-viewer-menu');
  } finally {
    await evaluate('document.exitFullscreen()');
    await call('Emulation.setUserAgentOverride', { userAgent: agent });
    assert.equal(await evaluate('cameraPictureInPicture.video === null'), true);
  }
});
