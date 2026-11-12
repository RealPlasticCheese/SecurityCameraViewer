// Real Chromium/MSE/PiP regression checks using generated H.264 and application styles.
// Run: node --test tests/test_picture_in_picture_browser.mjs
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { before, after, test } from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const work = resolve(root, 'artifacts/picture-in-picture-browser', randomUUID());
const profile = resolve(work, 'chrome');
const browserPath = process.env.SCV_TEST_BROWSER || [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
].find(existsSync);
const asset = name => readFileSync(resolve(root, 'src/SecurityCameraViewer.Web/wwwroot', name), 'utf8');
let browser, debug, exited, server, call, evaluate, pageId, sequence = 0;
const requests = new Map(), errors = [];
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
  const clip = resolve(work, 'generated.mp4');
  const generated = spawnSync(process.env.SCV_TEST_FFMPEG || 'ffmpeg', ['-hide_banner', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=8', '-t', '18', '-an', '-c:v', 'libx264',
    '-profile:v', 'baseline', '-level', '3.0', '-pix_fmt', 'yuv420p', '-g', '8', '-bf', '0',
    '-movflags', 'frag_keyframe+empty_moov+default_base_moof', '-frag_duration', '1000000', clip],
    { windowsHide: true, encoding: 'utf8' });
  assert.equal(generated.status, 0, generated.error?.message || generated.stderr);
  const media = readFileSync(clip), boxes = [];
  for (let at = 0; at < media.length;) {
    const size = media.readUInt32BE(at), type = media.toString('ascii', at + 4, at + 8);
    assert.ok(size >= 8 && at + size <= media.length); boxes.push({ at, size, type }); at += size;
  }
  const first = boxes.find(box => box.type === 'moof');
  const fragments = boxes.filter(box => box.type === 'moof').map(box => {
    const next = boxes[boxes.indexOf(box) + 1]; assert.equal(next.type, 'mdat');
    return media.subarray(box.at, next.at + next.size).toString('base64');
  });
  const app = asset('app.js');
  const fullscreen = app.slice(app.indexOf('async function fullscreen()'), app.indexOf("$('fullscreen').onclick"));
  const fullscreenListener = app.slice(app.indexOf("document.addEventListener('fullscreenchange'"), app.indexOf('async function poll()'));
  const selection = app.slice(app.indexOf('function updatePictureInPicture() {'), app.indexOf('function cameraAudio('));
  const fixture = `
    const tiles = new Map(), liveAudio = { players: new Map() }; let liveRewind, cameraPictureInPicture, fullscreenPending = false;
    const $ = id => document.getElementById(id);
    function setMenu(open) { window.testMenuOpen = open; }
    ${asset('camera-frame-clock.js')}
    ${asset('camera-video.js')}
    ${asset('camera-pip.js')}
    ${selection}
    ${fullscreen}
    ${fullscreenListener}
    const root = document.getElementById('camera');
    const tile = { root, name: 'Generated PiP video' }; tiles.set(1, tile);
    window.frames = 0; window.activities = 0;
    const player = new NativeCameraVideo(tile, () => frames++, () => { activities++; updatePictureInPicture(); });
    tile.playback = { player };
    // Reproduce background compositor throttling while native media keeps playing.
    player.video.requestVideoFrameCallback = () => 1; player.video.cancelVideoFrameCallback = () => {};
    const decode = value => Uint8Array.from(atob(value), character => character.charCodeAt(0));
    const init = decode(${JSON.stringify(media.subarray(0, first.at).toString('base64'))});
    const fragments = ${JSON.stringify(fragments)}.map(decode);
    window.ready = (async () => {
      await player.initialize({ mimeType: 'video/mp4; codecs="avc1.42C01E"' });
      await player.append(init); for (const fragment of fragments) await player.append(fragment);
      player.paint();
      window.audio = document.createElement('video'); audio.className = 'live-camera-video';
      audio.muted = true; audio.playsInline = true;
      audio.src = URL.createObjectURL(new Blob([init, ...fragments], { type: 'video/mp4' }));
      root.querySelector('.surface').append(audio); await audio.play();
      liveAudio.players.set(1, { video: audio }); updatePictureInPicture();
      return true;
    })();
    window.pixels = video => {
      const canvas = document.createElement('canvas'); canvas.width = 32; canvas.height = 18;
      const context = canvas.getContext('2d'); context.drawImage(video, 0, 0, 32, 18);
      return [...context.getImageData(0, 0, 32, 18).data].filter((_, i) => i % 4 !== 3).reduce((sum, value) => sum + value, 0);
    };
  `;
  const html = '<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><style>' + asset('app.css') + '</style><main><div id="cameras"><article id="camera" class="tile" style="inset:0"><div class="surface"></div></article></div></main><button id="fullscreen"><span data-fullscreen-icon="expand"></span><span data-fullscreen-icon="shrink" hidden></span></button><div id="notice"></div><script>' + fixture + '</script>';
  server = createServer((_, response) => { response.setHeader('Content-Type', 'text/html'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  browser = spawn(browserPath, ['--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + profile,
    '--no-first-run', '--no-default-browser-check', 'about:blank'], { windowsHide: true, stdio: 'ignore' });
  exited = new Promise(resolve => browser.once('exit', resolve));
  browser.once('error', error => errors.push(error.message));
  const port = await until(() => {
    assert.deepEqual(errors, []);
    try { return readFileSync(resolve(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]; }
    catch (error) { if (['EBUSY', 'ENOENT'].includes(error.code)) return false; throw error; }
  }, 'Chromium startup');
  const targets = await (await fetch('http://127.0.0.1:' + port + '/json')).json();
  const page = targets.find(target => target.type === 'page'); pageId = page.id;
  debug = new WebSocket(page.webSocketDebuggerUrl);
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
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  await call('Runtime.enable'); await call('Page.enable');
  await call('Page.navigate', { url: 'http://127.0.0.1:' + server.address().port });
  await until(() => evaluate('typeof ready !== "undefined" && ready'), 'decoded generated video');
});

after(async () => {
  if (evaluate) await evaluate('player.stop(); audio.pause(); URL.revokeObjectURL(audio.src); audio.remove();').catch(() => {});
  if (debug?.readyState === WebSocket.OPEN) { await call('Browser.close').catch(() => {}); debug.close(); }
  if (browser && browser.exitCode === null) browser.kill();
  if (exited) await exited;
  if (server) await new Promise(resolve => server.close(resolve));
  assert.equal(dirname(profile), work, 'Only remove this isolated browser profile.');
  if (existsSync(profile)) rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  assert.deepEqual(errors, [], 'No browser script failures.');
});

test('native media progress survives unavailable frame callbacks and retains non-black camera pixels', async () => {
  await until(() => evaluate('activities >= 2'), 'background native progress');
  assert.ok(await evaluate('player.video.currentTime') > .2);
  assert.ok(await evaluate('pixels(player.video)') > 100000);
  assert.equal(await evaluate('root.classList.contains("grid-media-ready")'), true);
  assert.ok(await evaluate('frames') <= 1, 'Time updates do not inflate compositor frame statistics.');
});

test('the covered grid player is excluded and the actual PiP video stays visible behind enhancement layers', async () => {
  await evaluate(`root.classList.add('live-media-ready', 'enhancement-ready'); updatePictureInPicture();
    window.enhanced = document.createElement('canvas'); enhanced.className = 'camera-enhanced-picture';
    enhanced.width = 32; enhanced.height = 18; enhanced.getContext('2d').fillRect(0, 0, 32, 18);
    root.querySelector('.surface').insertBefore(enhanced, audio);`);
  assert.deepEqual(await evaluate('[player.video.disablePictureInPicture, audio.disablePictureInPicture]'), [true, false]);
  assert.equal(await evaluate('getComputedStyle(player.video).visibility'), 'hidden');
  assert.equal(await evaluate('getComputedStyle(player.video).display'), 'none');
  assert.equal(await evaluate('getComputedStyle(audio).opacity'), '1', 'Android legacy PiP must see the original under the enhancement canvas.');
  assert.equal(await evaluate(`(() => {
    enhanced.style.pointerEvents = audio.style.pointerEvents = 'auto';
    const box = audio.getBoundingClientRect();
    const front = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    enhanced.style.pointerEvents = audio.style.pointerEvents = '';
    return front === enhanced;
  })()`), true, 'An audio video inserted after the enhanced canvas must stay behind it in the viewer.');
  assert.equal(await evaluate('player.video.requestPictureInPicture().then(() => "unexpected", error => error.name)'), 'InvalidStateError');
  try {
    await evaluate('audio.requestPictureInPicture()');
    assert.equal(await evaluate('document.pictureInPictureElement === audio'), true);
    assert.deepEqual(await evaluate('[getComputedStyle(audio).visibility, getComputedStyle(audio).opacity]'), ['visible', '1']);
    assert.ok(await evaluate('pixels(audio)') > 100000);
  } finally {
    await evaluate('document.pictureInPictureElement ? document.exitPictureInPicture() : Promise.resolve()');
    await evaluate('root.classList.remove("live-media-ready", "enhancement-ready"); updatePictureInPicture();');
    await evaluate('enhanced.remove();');
  }
  assert.deepEqual(await evaluate('[player.video.disablePictureInPicture, audio.disablePictureInPicture]'), [false, true]);
  assert.deepEqual(await evaluate('[audio.clientWidth, audio.clientHeight]'), [0, 0]);
});


test('native PiP continues advancing when the Chrome page is hidden by another tab', async () => {
  let other;
  try {
    await evaluate('player.video.requestPictureInPicture()');
    const before = await evaluate('({ time: player.video.currentTime, activities })');
    other = await call('Target.createTarget', { url: 'about:blank' });
    await call('Target.activateTarget', { targetId: other.targetId });
    await until(() => evaluate('document.hidden'), 'background page visibility');
    await until(() => evaluate(`activities > ${before.activities} && player.video.currentTime > ${before.time + .3}`), 'background PiP continues playing');
    assert.equal(await evaluate('document.pictureInPictureElement === player.video'), true);
    assert.deepEqual(await evaluate('[player.video.paused, getComputedStyle(player.video).visibility]'), [false, 'visible']);
    assert.ok(await evaluate('pixels(player.video)') > 100000);
  } finally {
    if (other) await call('Target.closeTarget', { targetId: other.targetId });
    await call('Target.activateTarget', { targetId: pageId });
    await evaluate('document.pictureInPictureElement ? document.exitPictureInPicture() : Promise.resolve()');
  }
});


test('a composed four-camera video keeps every slot moving in native PiP while the page is hidden', async () => {
  let other;
  await evaluate(`(async () => { window.pipTiles = Array.from({ length: 4 }, (_, i) => {
    const element = document.createElement('article'); element.className = 'tile grid-media-ready';
    element.innerHTML = '<div class="surface"></div>'; document.body.append(element);
    const tile = { root: element, name: 'Camera ' + (i + 1) };
    tile.player = new NativeCameraVideo(tile, () => {}); tile.video = tile.player.video;
    return tile;
  });
  window.composed = new CameraPictureInPictureView(() => pipTiles, () => ({ width: 640 }), error => { throw error; });
  window.compositionPixels = () => {
    const video = composed.video, canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360;
    const context = canvas.getContext('2d'); context.drawImage(video, 0, 0, 640, 360);
    return [[0,0],[320,0],[0,180],[320,180]].map(([x,y]) => {
      const values = [...context.getImageData(x, y, 320, 150).data];
      return { sum: values.filter((_, i) => i % 4 !== 3).reduce((sum, value) => sum + value, 0),
        hash: values.reduce((hash, value) => (Math.imul(hash, 31) + value) | 0, 0) };
    });
  };
  await Promise.all(pipTiles.map(async tile => {
    await tile.player.initialize({ mimeType: 'video/mp4; codecs="avc1.42C01E"' });
    await tile.player.append(init); for (const fragment of fragments) await tile.player.append(fragment);
    tile.player.paint(); await tile.video.play();
  })); composed.start(); await composed.playing; })()`);
  try {
    await evaluate('composed.video.requestPictureInPicture()');
    const before = await evaluate('({ pixels: compositionPixels(), time: composed.video.currentTime, sources: pipTiles.map(tile => tile.video.currentTime) })');
    assert.ok(before.pixels.every(pixel => pixel.sum > 1000000), 'All four camera slots contain decoded video.');
    other = await call('Target.createTarget', { url: 'about:blank' });
    await call('Target.activateTarget', { targetId: other.targetId });
    await until(() => evaluate('document.hidden'), 'hidden composite camera page');
    await until(() => evaluate(`composed.video.currentTime > ${before.time + 1} && compositionPixels().every((pixel, i) => pixel.hash !== ${JSON.stringify(before.pixels.map(pixel => pixel.hash))}[i])`), 'each camera slot advances in background PiP');
    assert.ok((await evaluate('pipTiles.map(tile => tile.video.currentTime)')).every((time, i) => time > before.sources[i]), 'Source cameras keep playing.');
  } finally {
    if (other) await call('Target.closeTarget', { targetId: other.targetId });
    await call('Target.activateTarget', { targetId: pageId });
    await evaluate('document.pictureInPictureElement ? document.exitPictureInPicture() : Promise.resolve()');
    await evaluate('composed.stop(); for (const tile of pipTiles) { tile.player.stop(); tile.root.remove(); }');
  }
});


test('Android keeps the viewer fullscreen while its underlay supplies every MSE camera to automatic PiP', async () => {
  const originalAgent = await evaluate('navigator.userAgent');
  await call('Emulation.setUserAgentOverride', { userAgent: 'Mozilla/5.0 (Linux; Android 16; Galaxy S24) AppleWebKit/537.36 Chrome/142.0.0.0 Mobile Safari/537.36' });
  await call('Emulation.setDeviceMetricsOverride', { width: 393, height: 852, deviceScaleFactor: 1, mobile: true });
  await evaluate(`(async () => {
    tiles.clear();
    await Promise.all(pipTiles.map(async (tile, i) => {
      document.body.append(tile.root); tile.player = new NativeCameraVideo(tile, () => {}); tile.video = tile.player.video;
      tile.playback = { player: tile.player }; tiles.set(i + 1, tile);
      await tile.player.initialize({ mimeType: 'video/mp4; codecs="avc1.42C01E"' });
      await tile.player.append(init); for (const fragment of fragments) await tile.player.append(fragment);
      tile.player.paint(); await tile.video.play();
    }));
    await fullscreen();
    composed = cameraPictureInPicture;
  })()`);
  try {
    assert.equal(await evaluate('document.fullscreenElement === document.documentElement'), true);
    assert.equal(await evaluate('document.fullscreenElement === composed.video'), false);
    assert.equal(await evaluate('pipTiles.every(tile => tile.video.disablePictureInPicture)'), true);
    await until(() => evaluate('compositionPixels().every(pixel => pixel.sum > 1000000)'), 'all four full-screen camera images');
    const dimensions = await evaluate('({ width: composed.video.clientWidth, height: composed.video.clientHeight, viewportWidth: innerWidth, viewportHeight: innerHeight, canvasWidth: composed.canvas.width, gridWidth: document.getElementById("cameras").clientWidth, canvasHeight: composed.canvas.height })');
    assert.equal(dimensions.width, dimensions.viewportWidth); assert.equal(dimensions.height, dimensions.viewportHeight);
    assert.equal(dimensions.canvasWidth, dimensions.gridWidth);
    assert.equal(await evaluate('getComputedStyle(composed.video).zIndex'), '0');
    assert.equal(await evaluate('getComputedStyle(document.querySelector("main")).zIndex'), '1');
    assert.equal(await evaluate('composed.video.controls'), false);
    assert.equal(await evaluate('composed.frameUsesVideo'), false);
    const intersection = await evaluate('new Promise(resolve => { const observer = new IntersectionObserver(entries => { resolve(entries.at(-1).intersectionRatio); observer.disconnect(); }); observer.observe(composed.video); })');
    assert.equal(intersection, 1, 'The unclipped underlay fills the fullscreen viewport used by Chrome automatic-PiP detection.');
    const screenshot = await call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    const path = resolve(work, 'android-grid-fullscreen.png'); writeFileSync(path, Buffer.from(screenshot.data, 'base64'));
    console.log('Android viewer fullscreen screenshot:', path);
    // Model Chromium's persistent-video UA rules (core/css/fullscreen.css):
    // foreground siblings disappear and the persistent video fills the viewport.
    await evaluate(`window.persistentStyle = document.createElement('style'); persistentStyle.textContent = 'body > :not(.camera-pip-video){display:none!important} .camera-pip-video{position:fixed!important;left:0!important;top:0!important;width:100%!important;height:100%!important;transform:none!important}'; document.head.append(persistentStyle); window.dispatchEvent(new Event('resize'));`);
    assert.equal(await evaluate('composed.isPresented()'), true);
    assert.equal(await evaluate('composed.frameUsesVideo'), true);
    const before = await evaluate('compositionPixels().map(pixel => pixel.hash)');
    await until(() => evaluate(`compositionPixels().every((pixel, i) => pixel.hash !== ${JSON.stringify(before)}[i])`), 'each camera advances in the simulated Android persistent-video layout');
    assert.ok((await evaluate('compositionPixels()')).every(pixel => pixel.sum > 1000000));
    await evaluate('persistentStyle.remove(); window.dispatchEvent(new Event("resize"));');
    assert.equal(await evaluate('composed.frameUsesVideo'), false);
    await evaluate('pipTiles.forEach((tile, i) => { tile.root.hidden = i !== 0; }); pipTiles[0].root.classList.add("enhancement-ready"); updatePictureInPicture();');
    assert.deepEqual(await evaluate('pipTiles.map(tile => tile.video.disablePictureInPicture)'), [false, true, true, true]);
    assert.equal(await evaluate('getComputedStyle(pipTiles[0].video).visibility'), 'visible');
    // Legacy Android PiP can promote the focused source instead of the helper.
    // Model that choice, with the occluded helper and other cameras hidden.
    await evaluate(`pipTiles[0].root.id = 'focused-source'; persistentStyle.textContent = 'body > :not(#focused-source){display:none!important} #focused-source{position:fixed!important;inset:0!important} #focused-source .grid-camera-video{position:fixed!important;inset:0!important;width:100%!important;height:100%!important;transform:none!important}'; document.head.append(persistentStyle); window.dispatchEvent(new Event('resize'));`);
    const singleTime = await evaluate('pipTiles[0].video.currentTime');
    await until(() => evaluate(`pipTiles[0].video.currentTime > ${singleTime + 0.3} && pixels(pipTiles[0].video) > 10000`), 'the focused source keeps playing when Android hides the captured helper');
    assert.equal(await evaluate('composed.video !== null && !pipTiles[0].video.disablePictureInPicture'), true);
    await evaluate('persistentStyle.remove(); window.dispatchEvent(new Event("resize"));');
    await evaluate('pipTiles.forEach(tile => { tile.root.hidden = false; }); updatePictureInPicture();');
    assert.equal(await evaluate('pipTiles.every(tile => tile.video.disablePictureInPicture)'), true);
    await evaluate('pipTiles.forEach((tile, i) => { tile.root.hidden = i !== 0; }); updatePictureInPicture();');
    await evaluate('pipTiles[0].video.requestPictureInPicture()');
    assert.equal(await evaluate('document.pictureInPictureElement === pipTiles[0].video'), true);
    const nativeTime = await evaluate('pipTiles[0].video.currentTime');
    await until(() => evaluate(`pipTiles[0].video.currentTime > ${nativeTime + 0.3} && pixels(pipTiles[0].video) > 10000`), 'the focused native PiP window retains moving camera pictures');
    await evaluate('document.exitPictureInPicture()');
  } finally {
    await evaluate('document.pictureInPictureElement ? document.exitPictureInPicture() : Promise.resolve()');
    await evaluate('if (window.persistentStyle) persistentStyle.remove();');
    await evaluate('document.fullscreenElement ? document.exitFullscreen() : Promise.resolve()');
    await until(() => evaluate('cameraPictureInPicture.video === null'), 'fullscreen capture released');
    await evaluate('for (const tile of pipTiles) { tile.player.stop(); tile.root.remove(); } tiles.clear();');
    await call('Emulation.setUserAgentOverride', { userAgent: originalAgent });
    await call('Emulation.clearDeviceMetricsOverride');
  }
});
