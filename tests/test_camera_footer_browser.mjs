// Real browser layout regressions using the application's tile builder and stylesheet.
// Run: node --test tests/test_camera_footer_browser.mjs
// Set SCV_TEST_BROWSER to a Chromium executable when it is not in a standard location.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/camera-footer-browser', randomUUID());
const profile = resolve(work, 'chrome');
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
let browser, debug, exited, call, evaluate;
const requests = new Map(), browserErrors = [];
let sequence = 0;

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
  assert.ok(browserPath, 'Install Chromium or set SCV_TEST_BROWSER to its executable.');
  mkdirSync(work, { recursive: true });
  browser = spawn(browserPath, ['--headless=new', '--remote-debugging-port=0',
    '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check', 'about:blank'],
    { windowsHide: true, stdio: 'ignore' });
  exited = new Promise(resolve => browser.once('exit', resolve));
  browser.once('error', error => browserErrors.push(error.message));
  const debugPort = await until(() => {
    assert.deepEqual(browserErrors, []);
    const path = resolve(profile, 'DevToolsActivePort');
    return existsSync(path) && readFileSync(path, 'utf8').split('\n')[0];
  }, 'Chromium startup');
  const targets = await (await fetch('http://127.0.0.1:' + debugPort + '/json')).json();
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
  await call('Runtime.enable');
  await call('Page.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1000, height: 300, deviceScaleFactor: 1, mobile: false });
  const source = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/app.js'), 'utf8');
  const start = source.indexOf('function createTiles() {'), end = source.indexOf('function renderStatus() {', start);
  assert.ok(start >= 0 && end > start, 'Use the actual application tile builder.');
  const collection = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/camera-collection.js'), 'utf8');
  const css = readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot/app.css'), 'utf8');
  const fixture = [
    'let cameraPictureInPicture;',
    'const tiles = new Map(), liveRewind = null, liveAudio = { stop() {} };',
    'const pushToTalk = { stop() {}, bind() {} }, $ = id => document.getElementById(id);',
    'let focus = null, state;',
    'class View { dispose() {} stop() {} }',
    'class CameraEnhancementPreferences {} const CameraZoomView = View, CameraEnhancementView = View, CameraDetectionOverlay = View, CameraPlayback = View;',
    'function arrange() {}',
    'function updatePreviewSelection() {}',
    source.slice(start, end),
    'window.measureFooter = ' + function (options) {
      state = { settings: { sources: [{ channel: 1, name: options.name || 'Front door', motionActivated: !!options.check }] } };
      createTiles();
      const tile = tiles.get(1), header = tile.root.querySelector('.tile-header');
      tile.root.style.cssText = 'position:relative;height:' + (options.picture ? 280 : 100) + 'px;width:' + options.width + 'px';
      tile.root.classList.add('has-frame');
      tile.badge.textContent = options.badge || '● LIVE';
      tile.badge.className = 'badge' + (tile.badge.textContent.includes('LIVE') ? ' live' : '');
      header.querySelector('.recording-badge').textContent = options.recording || '';
      tile.playbackStatus.textContent = options.status;
      tile.playbackStatus.classList.toggle('playback-warning', /software|JPEG|unavailable/.test(options.status));
      tile.audioLabel.textContent = options.audio || 'No Audio';
      tile.listen.hidden = !options.listen;
      if (options.picture) {
        const surface = tile.root.querySelector('.surface');
        const picture = document.createElement(options.picture === 'enhanced' ? 'canvas' : options.picture);
        picture.className = options.picture === 'enhanced' ? 'camera-enhanced-picture' :
          options.picture === 'video' ? 'grid-camera-video' : 'grid-camera-canvas';
        picture.style.cssText = 'visibility:visible;pointer-events:auto';
        if (options.zoom) picture.style.transform = 'scale(2)';
        surface.append(picture);
        if (options.detections) {
          const detections = document.createElement('div');
          detections.className = 'detection-overlay';
          detections.style.pointerEvents = 'auto';
          surface.append(detections);
        }
        // Hit testing follows paint order. Include noninteractive footer areas too.
        header.style.pointerEvents = 'auto';
      }
      const rect = element => {
        const box = element.getBoundingClientRect();
        return { left: box.left, right: box.right, width: box.width, height: box.height };
      };
      return {
        title: rect(header.querySelector('.tile-title')), badge: rect(tile.badge),
        titleTextRight: Math.min(rect(header.querySelector('.name')).right, rect(header.querySelector('.tile-title')).right),
        recording: rect(header.querySelector('.recording-badge')),
        playback: rect(tile.playbackStatus), audio: rect(tile.audioLabel), header: rect(header),
        check: rect(tile.check), listen: rect(tile.listen),
        playbackScrollWidth: tile.playbackStatus.scrollWidth, playbackClientWidth: tile.playbackStatus.clientWidth,
        titleScrollWidth: header.querySelector('.tile-title').scrollWidth,
        titleClientWidth: header.querySelector('.tile-title').clientWidth,
        lineHeight: parseFloat(getComputedStyle(tile.playbackStatus).lineHeight),
        headerScrollWidth: header.scrollWidth, headerClientWidth: header.clientWidth,
        overlay: options.picture ? (() => {
          const box = header.getBoundingClientRect(), surface = tile.root.querySelector('.surface').getBoundingClientRect();
          const painted = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
          return { onTop: header.contains(painted), bottom: box.bottom, surfaceBottom: surface.bottom };
        })() : null
      };
    }.toString()
  ].join('\n');
  const html = '<meta charset="utf-8"><style>' + css + '</style><select id="camera-focus" hidden></select><div id="cameras"></div><script>' +
    collection + '\n' + fixture + '</script>';
  await call('Page.navigate', { url: 'data:text/html;base64,' + Buffer.from(html).toString('base64') });
  await until(() => evaluate('typeof measureFooter === "function"'), 'footer fixture');
});

after(async () => {
  if (debug?.readyState === WebSocket.OPEN) {
    await measure({ width: 950, recording: 'MOTION', status: softwareStatus, audio: 'AAC Audio', listen: true });
    const screenshot = await call('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 76, width: 950, height: 24, scale: 2 } });
    writeFileSync(resolve(work, 'footer.png'), Buffer.from(screenshot.data, 'base64'));
    console.log('Footer screenshot: ' + resolve(work, 'footer.png'));
    await call('Browser.close').catch(() => {});
    debug.close();
  }
  if (browser && browser.exitCode === null) browser.kill();
  if (exited) await exited;
  assert.equal(dirname(profile), work, 'Only remove the isolated test browser profile.');
  if (existsSync(profile)) rmSync(profile, { recursive: true, force: true });
  assert.deepEqual(browserErrors, [], 'No browser script failures.');
});

const softwareStatus = 'Auto: Browser software decoding · H.264 · client CPU';
const measure = options => evaluate('measureFooter(' + JSON.stringify(options) + ')');

for (const width of [220, 390, 950]) {
  for (const picture of ['video', 'canvas', 'enhanced']) {
    for (const zoom of [false, true]) {
      for (const detections of [false, true]) {
        test(`${width}px footer overlays ${picture}, zoom ${zoom}, detections ${detections}`, async () => {
          const result = await measure({ width, picture, zoom, detections,
            status: softwareStatus, recording: 'MOTION', listen: true });
          assert.ok(result.overlay.onTop, 'Status line paints above pictures and detection overlays.');
          assert.ok(Math.abs(result.overlay.bottom - result.overlay.surfaceBottom) <= 1,
            'Status remains over the bottom of the full-height picture.');
          assertContained(result);
        });
      }
    }
  }
}

function assertAdjacent(result, recording) {
  assert.ok(Math.abs(result.badge.left - result.titleTextRight - 1) < 1, 'Playback badge follows the camera label.');
  if (recording) {
    assert.ok(Math.abs(result.recording.left - result.badge.right - 5) < 1, 'Recording badge follows the playback badge.');
    assert.ok(result.playback.left >= result.recording.right, 'Playback details follow the left status group.');
  }
}
function assertContained(result) {
  assert.ok(result.headerScrollWidth <= result.headerClientWidth, 'Footer stays within the tile.');
  assert.ok(result.playbackScrollWidth <= result.playbackClientWidth + 1, 'Playback text fits its allocated width.');
  for (const name of ['title', 'badge', 'recording', 'playback', 'audio', 'check', 'listen']) {
    if (result[name].width > 0) {
      assert.ok(result[name].left >= result.header.left && result[name].right <= result.header.right, name + ' stays inside the footer.');
    }
  }
}

for (const width of [630, 950]) {
  test('software decoding uses the available space on a ' + width + 'px tile', async () => {
    const result = await measure({ width, status: softwareStatus });
    if (width === 630) assert.ok(result.playback.width > result.header.width * .4, 'Status can occupy more than 40% of the footer.');
    assert.equal(result.playback.height, result.lineHeight, 'Software decoding remains on one line when it fits.');
    assertAdjacent(result);
    assertContained(result);
  });
}

for (const badge of ['● LIVE', 'PAUSED', 'PLAYBACK', 'NO FOOTAGE', 'CONNECTING', 'ERROR', 'OFFLINE']) {
  for (const recording of ['', '● REC', 'MOTION', 'REC ERROR']) {
    test(badge + ' and ' + (recording || 'no recording badge') + ' stay beside the camera name', async () => {
      const result = await measure({ width: 950, badge, recording, status: softwareStatus, audio: 'AAC Audio', listen: true });
      assertAdjacent(result, recording);
      assert.equal(result.playback.height, result.lineHeight);
      assertContained(result);
    });
  }
}

for (const width of [220, 320]) {
  test('a ' + width + 'px tile wraps details only when space is exhausted', async () => {
    const result = await measure({ width, recording: 'MOTION', status: softwareStatus, audio: 'AAC Audio', listen: true });
    assertAdjacent(result, true);
    assert.ok(result.playback.height > result.lineHeight, 'Details wrap on a genuinely narrow tile.');
    assertContained(result);
  });
}

test('long camera names are ellipsized without detaching the status badges', async () => {
  const result = await measure({ width: 630, name: 'Front door overlooking the driveway and front garden '.repeat(8),
    recording: '● REC', status: 'Auto: Native video · H.264' });
  assert.ok(result.titleScrollWidth > result.titleClientWidth, 'An oversized camera name is ellipsized.');
  assertAdjacent(result, true);
  assertContained(result);
});

test('unavailable playback and long unbroken failure text remain within a narrow footer', async () => {
  for (const status of ['Playback unavailable', 'DecoderFailure'.repeat(30)]) {
    const result = await measure({ width: 220, badge: 'ERROR', status });
    assertAdjacent(result);
    assertContained(result);
  }
});

test('the camera availability control remains visible beside left status badges', async () => {
  const result = await measure({ width: 320, check: true, badge: 'WAITING', recording: 'MOTION', status: 'Auto: Checking Native video' });
  assertAdjacent(result, true);
  assert.ok(result.check.width > 0);
  assertContained(result);
});
