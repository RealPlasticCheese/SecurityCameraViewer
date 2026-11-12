// Real Chromium, RTSP, MSE and Worker/WASM regression test. Uses only generated video and its own processes.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, createWriteStream, unlinkSync } from 'node:fs';
import { resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import net from 'node:net';
const work = resolve('artifacts/browser-decoder/browser-' + randomUUID()); mkdirSync(work, { recursive: true });
const children = [], logs = [], errors = [];
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function port() { const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve)); const value = socket.address().port; await new Promise(resolve => socket.close(resolve)); return value; }
function start(command, args, env) { const child = spawn(command, args, { env: { ...process.env, ...env }, windowsHide: true }); children.push(child); const log = createWriteStream(`${work}/${children.length}.log`); logs.push(log); child.stdout.pipe(log); child.stderr.pipe(log); return child; }
async function until(action, label, seconds = 30) { const end = Date.now() + seconds * 1000; let error; while (Date.now() < end) { try { const result = await action(); if (result) return result; } catch (ex) { error = ex; } await sleep(200); } throw new Error('Timed out: ' + label + (error ? '; ' + error.message : '')); }
const liveLatency = process.argv.includes('--live-latency');
const queueRecovery = process.argv.includes('--queue-recovery');
const methodRecovery = process.argv.includes('--method-recovery');
let debug, evaluate, lastReports; const pipFullscreen = process.argv.includes('--pip-fullscreen'); const benchmark = process.argv.includes("--benchmark"); const hevc = process.argv.includes("--hevc"); const recovery = process.argv.includes('--sync-recovery'); const synchronize = recovery || process.argv.includes('--sync');
try {
  const rtspPort = await port(), webPort = await port();
  const settings = { ConfigurationId: 'default', ConfigurationName: 'Camera playback tests', Host: '127.0.0.1', RtspPort: rtspPort,
    Username: 'admin', ChannelCount: 3, AutoConnect: true, VideoTimeoutSeconds: queueRecovery ? 30 : 3,
    ...(queueRecovery ? { RetryDelaysSeconds: '1, 2, 4' } : {}),
    Sources: ['automatic', 'browser', 'server'].map((PlaybackMethod, index) => ({ Id: randomUUID().replaceAll('-', ''), Channel: index + 1,
      Name: PlaybackMethod, MainStream: `rtsp://127.0.0.1:${rtspPort}/ip0${index + 1}/0`, Username: 'admin', UseDefaultPassword: true, PlaybackMethod })) };
  const settingsPath = `${work}/settings.json`;
  writeFileSync(settingsPath, JSON.stringify({ Version: 1, ActiveConfigurationId: 'default', Configurations: [{ Connection: settings, Password: 'local-only-test', Credentials: {} }], Integrations: { Enabled: false } }));
  const stillFlag = `${work}/still`;
  const pauseFlag = `${work}/pause`;
  if (synchronize) writeFileSync(stillFlag, '');
  start('python', ['tests/rtsp_fixture.py'], { RTSP_FIXTURE_PORT: String(rtspPort), RTSP_FIXTURE_USER: 'admin', RTSP_FIXTURE_STILL_FILE: stillFlag, ...(hevc ? { RTSP_FIXTURE_CODEC: 'hevc' } : {}), ...(synchronize ? { RTSP_FIXTURE_MOVING_SCENE: '1' } : {}), ...(recovery ? { RTSP_FIXTURE_PACING_RATE: '1.2', RTSP_FIXTURE_PAUSE_FILE: pauseFlag } : {}) });
  const server = start('dotnet', [resolve(process.argv.slice(2).find(arg => !arg.startsWith('--')) || 'artifacts/browser-playback-web/SecurityCameraViewer.Web.dll'), '--urls', `http://127.0.0.1:${webPort}`, '--settings', settingsPath, '--ffmpeg', 'ffmpeg',
    ...(queueRecovery ? ['--LiveMedia:MaximumQueuedPackets', '3'] : [])]);
  const base = `http://127.0.0.1:${webPort}`;
  await until(async () => (await fetch(base + '/api/state')).ok, 'web server startup');
  const sourceResponse = await fetch(base + '/vendor/camera-decoder/ffmpeg-8.0.1.tar.xz'); assert.equal(sourceResponse.status, 200); await sourceResponse.body.cancel();
  for (const file of ['decoder.c', 'build.sh', 'LICENSE-LGPL.txt', 'LICENSE-EMSCRIPTEN.txt']) assert.equal((await fetch(base + '/vendor/camera-decoder/' + file)).status, 200);
  const chrome = start('C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${work}/chrome`, '--no-first-run', '--no-default-browser-check', 'about:blank']);
  const debugPort = await until(() => readFileSync(`${work}/chrome/DevToolsActivePort`, 'utf8').split('\n')[0], 'Chrome DevTools');
  const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
  debug = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { debug.onopen = resolve; debug.onerror = reject; });
  const requests = new Map(); let sequence = 0;
  debug.onmessage = event => { const message = JSON.parse(event.data); if (message.id) { const request = requests.get(message.id); requests.delete(message.id); message.error ? request.reject(message.error) : request.resolve(message.result); } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text); };
  const call = (method, params = {}) => new Promise((resolve, reject) => { const id = ++sequence; requests.set(id, { resolve, reject }); debug.send(JSON.stringify({ id, method, params })); });
  await call('Runtime.enable'); await call('Page.enable');
  evaluate = async (expression, userGesture = false) => { const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture }); if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text); return result.result.value; };
  await call('Page.navigate', { url: base });
  const reports = await until(async () => {
    const value = await evaluate('typeof CameraPlayback !== "undefined" ? [...CameraPlayback.reports.values()] : []');
    lastReports = value;
    return value.length === 3 && value.every(report => report.frames >= 5) && value;
  }, 'all three camera playback methods display live frames', 60);
  assert.ok(['native', 'browser'].includes(reports[0].method)); assert.deepEqual(reports.slice(1).map(report => report.method), ['browser', 'server']);
  if (!hevc) assert.equal(reports[0].method, 'native');
  if (hevc) assert.match(reports[1].mimeType, /hev1|hvc1/);
  assert.ok(reports.every(report => report.confirmed));
  assert.match(reports[0].message, /Automatic.*(Native|Browser)/); assert.match(reports[1].message, /client CPU/); assert.match(reports[2].message, /server CPU/);
  assert.equal(await evaluate('previewSelection'), '3');
  console.log('PASS: native, browser Worker/WASM and JPEG pictures play concurrently; only JPEG camera requests previews');

  if (methodRecovery) {
    await evaluate(`(() => {
      const tile = tiles.get(1);
      tile.playback.stop();
      tile.playback = new CameraPlayback(1, tile, 'native', { ...state.playback, retryDelaysSeconds: '2' }, updatePreviewSelection);
    })()`);
    await until(() => evaluate('tiles.get(1).playback.stats.frames >= 5'), 'forced native warmup');
    const unavailable = await evaluate(`(() => {
      const tile = tiles.get(1);
      window.failedNativeDecoder = tile.playback.player;
      failedNativeDecoder.failure('Injected native decoder failure');
      renderStatus();
      return tile.detail.textContent;
    })()`);
    assert.match(unavailable, /Retrying native video in/);
    await until(() => evaluate(`(() => {
      const playback = tiles.get(1).playback;
      return playback.method === 'native' && playback.player !== failedNativeDecoder && playback.stats.frames >= 5;
    })()`), 'forced native creates a fresh decoder without reload');
    await evaluate(`(() => {
      const tile = tiles.get(1);
      tile.playback.stop();
      tile.playback = new CameraPlayback(1, tile, 'automatic', { ...state.playback, retryDelaysSeconds: '8' }, updatePreviewSelection);
      tile.playback.upgradeRetry.delays = [3];
      tile.playback.player.failure('Injected native decoder failure');
      tile.playback.player.worker.onerror();
    })()`);
    await until(() => evaluate(`(() => {
      const tile = tiles.get(1);
      const video = tile.playback.probe?.player.video;
      return video && tile.playback.method === 'server' && previewSelection === '1,3' &&
        tile.url && getComputedStyle(video).visibility === 'hidden';
    })()`), 'visible JPEG fallback during a hidden native probe');
    await until(() => evaluate(`(() => {
      const tile = tiles.get(1);
      return tile.playback.method === 'native' && tile.playback.stats.frames >= 5 &&
        previewSelection === '3' && getComputedStyle(tile.playback.player.video).visibility === 'visible';
    })()`), 'native probe is promoted and JPEG subscription released');
    console.log('PASS: forced native recovers without reload and Automatic upgrades a visible JPEG fallback');
    await evaluate(`(() => {
      const tile = tiles.get(1);
      tile.playback.stop();
      tile.playback = new CameraPlayback(1, tile, 'automatic', state.playback, updatePreviewSelection);
    })()`);
    await until(() => evaluate('tiles.get(1).playback.stats.frames >= 5'), 'restore default Automatic playback');
  }

  if (queueRecovery) {
    await evaluate(`window.queueTestDelay = 0; window.originalQueueSend = WebSocket.prototype.send;
      WebSocket.prototype.send = function(data) {
        if (data === 'ok' && this.url.endsWith('/api/cameras/1/video') && queueTestDelay) {
          setTimeout(() => { if (this.readyState === WebSocket.OPEN) originalQueueSend.call(this, data); }, queueTestDelay);
        } else originalQueueSend.call(this, data);
      };`);
    for (const preference of ['browser', 'native', 'automatic']) {
      const method = preference === 'browser' ? 'browser' : 'native';
      await evaluate(`tiles.get(1).playback.stop(); tiles.get(1).playback = new CameraPlayback(1, tiles.get(1), '${preference}', state.playback, updatePreviewSelection); true`);
      await until(() => evaluate('tiles.get(1).playback.confirmed && tiles.get(1).playback.stats.frames >= 5'), preference + ' queue test warmup');
      const stream = await evaluate('tiles.get(1).playback.player.clock.streamId');
      await evaluate('queueTestDelay = 2000');
      await until(() => evaluate('tiles.get(1).playback.transport?.snapshot().reason === "queue-overflow" && tiles.get(1).playback.reconnecting'), preference + ' actual server queue overflow');
      const overflow = await evaluate(`queueTestDelay = 0; renderStatus(); (() => {
        const tile = tiles.get(1); return { method: tile.playback.method, failed: [...tile.playback.failed],
          retained: !!tile.playback.player.frozen || !!tile.playback.player.canvas && tile.root.classList.contains('grid-media-ready'),
          stale: !tile.stale.hidden, message: tile.detail.textContent,
          diagnostics: tile.playback.transport.snapshot(), frames: tile.playback.stats.frames };
      })()`);
      assert.equal(overflow.method, method); assert.deepEqual(overflow.failed, []);
      assert.ok(overflow.retained && overflow.stale); assert.match(overflow.message, /Viewer fell behind.*Reconnecting in/);
      assert.equal(overflow.diagnostics.queue.queuedPackets, 3);
      assert.equal(overflow.diagnostics.serverProtocol, 'HTTP/1.1');
      await until(() => evaluate(`tiles.get(1).playback.confirmed && !tiles.get(1).playback.reconnecting && tiles.get(1).playback.stats.frames > ${overflow.frames}`), preference + ' automatically recovers');
      assert.equal(await evaluate('tiles.get(1).playback.method'), method);
      assert.notEqual(await evaluate('tiles.get(1).playback.player.clock.streamId'), stream);
      assert.equal(await evaluate('previewSelection'), '3', 'Recovery must not request server JPEG for the encoded camera.');
      console.log('PASS:', preference, 'survives delayed acknowledgements, preserves stale pixels, and recovers the selected decoder without reload or JPEG');
    }
    await evaluate('WebSocket.prototype.send = originalQueueSend');
    const stream = await evaluate('tiles.get(1).playback.player.clock.streamId');
    await evaluate('window.dispatchEvent(new Event("online"))');
    await until(() => evaluate(`tiles.get(1).playback.player.clock.streamId && tiles.get(1).playback.player.clock.streamId !== ${JSON.stringify(stream)} && tiles.get(1).playback.confirmed`), 'network recovery starts fresh native footage');
    console.log('PASS: network restoration refreshes native footage without changing playback preference');
  }

  if (pipFullscreen) {
    const agent = await evaluate('navigator.userAgent');
    await call('Emulation.setUserAgentOverride', { userAgent: 'Mozilla/5.0 (Linux; Android 16) Chrome Mobile' });
    const bounds = await evaluate('[...tiles.values()].map(tile => [tile.root.style.left, tile.root.style.top, tile.root.style.width, tile.root.style.height])');
    await evaluate('fullscreen()', true);
    try {
      assert.equal(await evaluate('document.fullscreenElement === document.documentElement'), true);
      assert.deepEqual(await evaluate('[...tiles.values()].map(tile => [tile.root.style.left, tile.root.style.top, tile.root.style.width, tile.root.style.height])'), bounds);
      assert.equal(await evaluate('cameraPictureInPicture.video.controls'), false);
      const button = await evaluate('(() => { const box = document.getElementById("menu-toggle").getBoundingClientRect(); return { x: box.x + box.width / 2, y: box.y + box.height / 2 }; })()');
      await call('Input.dispatchMouseEvent', { type: 'mouseMoved', ...button }); await sleep(200);
      await call('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...button });
      await call('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...button });
      assert.equal(await evaluate('menu.hidden'), false);
      await evaluate('setMenu(false)');
      const sampled = await evaluate(`(() => {
        const canvas = document.createElement('canvas'); canvas.width = 64; canvas.height = 36;
        const pixels = canvas.getContext('2d');
        window.fullscreenBlanks = []; window.fullscreenTimes = [];
        window.fullscreenSampler = setInterval(() => {
          for (const [channel, tile] of tiles) {
            const media = tile.playback.player?.video || tile.playback.player?.canvas || tile.img;
            if (media?.readyState < 2 || media?.complete === false) continue;
            pixels.drawImage(media, 0, 0, 64, 36);
            const data = pixels.getImageData(0, 0, 64, 36).data;
            if (!Array.from(data).some((value, index) => index % 4 !== 3 && value > 64)) fullscreenBlanks.push(channel);
          }
          fullscreenTimes.push(tiles.get(1).playback.player.video.currentTime);
        }, 50);
        return true;
      })()`);
      assert.equal(sampled, true); await sleep(5000);
      const trace = await evaluate('clearInterval(fullscreenSampler); ({ blanks: fullscreenBlanks, times: fullscreenTimes })');
      assert.deepEqual(trace.blanks, [], 'Native, software and JPEG cameras must not flash black while the PiP underlay is running.');
      assert.ok(trace.times.at(-1) - trace.times[0] > 2);
      assert.equal(await evaluate('cameraPictureInPicture.frameUsesVideo'), false);
      console.log('PASS: actual RTSP native/software/JPEG camera playback stays non-black, original layout and menu remain available in Android viewer fullscreen');
      await evaluate('focus = 1; arrange();');
      assert.equal(await evaluate('tiles.get(1).playback.player.video.disablePictureInPicture'), false);
      await evaluate('tiles.get(1).playback.player.video.requestPictureInPicture()', true);
      assert.equal(await evaluate('document.pictureInPictureElement === tiles.get(1).playback.player.video'), true);
      const singleTime = await evaluate('tiles.get(1).playback.player.video.currentTime');
      await until(() => evaluate(`tiles.get(1).playback.player.video.currentTime > ${singleTime + 1}`), 'focused RTSP camera continues in native PiP');
      assert.equal(await evaluate('document.pictureInPictureElement === tiles.get(1).playback.player.video'), true);
      await evaluate('document.exitPictureInPicture();');
      await evaluate('focus = null; arrange();');
      console.log('PASS: focused RTSP native camera stays eligible and moving in PiP with the fullscreen grid helper present');
    } finally {
      await evaluate('document.pictureInPictureElement ? document.exitPictureInPicture() : Promise.resolve()');
      await evaluate('document.fullscreenElement ? document.exitFullscreen() : Promise.resolve()');
      await call('Emulation.setUserAgentOverride', { userAgent: agent });
    }
  }
  if (reports[0].method === 'native') {
    await sleep(4000); // Allow startup clock calibration before measuring steady live playback.
    await evaluate(`(() => {
      const video = tiles.get(1).playback.player.video;
      window.liveContinuity = { start: video.currentTime, previous: video.currentTime, backwards: [] };
      window.liveContinuityTimer = setInterval(() => {
        const current = video.currentTime, trace = window.liveContinuity;
        if (current < trace.previous - .05) trace.backwards.push({ from: trace.previous, to: current });
        trace.previous = current;
      }, 20);
    })()`);
    await sleep(6000);
    const continuity = await evaluate('clearInterval(window.liveContinuityTimer); window.liveContinuity');
    assert.deepEqual(continuity.backwards, [], 'Steady live native playback must never replay an earlier picture.');
    assert.ok(continuity.previous - continuity.start > 2, 'A monotonic player must continue showing fresh footage.');
    console.log('PASS: real Chromium native playback advances continuously without backwards correction seeks');
  }
  if (liveLatency) {
    const sample = () => evaluate(`(() => {
      const clock = LiveCameraClock.shared, at = clock.current();
      return { at, now: performance.now(), latency: Date.now() - at, lag: clock.edge - at, rate: clock.rate, members: clock.members.size,
        cameras: [1, 2].map(channel => ({ channel, streamId: tiles.get(channel).playback.player.clock.streamId,
          displayed: tiles.get(channel).detections.playbackTime })) };
    })()`);
    const baseline = await sample(), recoveries = [];
    for (let attempt = 0; attempt < 2; attempt++) {
      // Delay just this viewer's native fragment consumer. The camera and other
      // viewers keep producing footage, as with a slow append or browser pause.
      await evaluate(`(() => {
        const player = tiles.get(1).playback.player, original = player.fragment;
        const gate = new Promise(resolve => window.releaseLiveFragment = resolve);
        window.liveFragmentHeld = false;
        player.fragment = async function(...args) { window.liveFragmentHeld = true; await gate; return original.apply(this, args); };
        window.restoreLiveFragment = () => { player.fragment = original; releaseLiveFragment(); };
      })()`);
      await until(() => evaluate('window.liveFragmentHeld'), 'a native fragment enters the delayed consumer');
      await sleep(4000);
      await evaluate('restoreLiveFragment(); delete window.restoreLiveFragment; delete window.releaseLiveFragment; delete window.liveFragmentHeld;');
      const trace = [];
      await until(async () => { const value = await sample(); trace.push(value); return value.rate > 1; }, 'live buffering automatically enables catch-up');

      const delayed = await sample();
      assert.ok(delayed.latency > baseline.latency + 1000, 'The test must create real viewer latency.');
      let recovered;
      try { await until(async () => {
        recovered = await sample(); trace.push(recovered);
        return recovered.rate === 1 && recovered.latency <= baseline.latency + 500 &&
          recovered.cameras.every(camera => Number.isFinite(camera.displayed) && Math.abs(camera.displayed - recovered.at) < 300);
      }, 'native and software playback return near live without 2x or a page reload', 60); }
      finally { writeFileSync(`${work}/live-latency-trace-${attempt}.json`, JSON.stringify({ baseline, trace }, null, 2)); console.log('Live latency attempt:', attempt + 1, JSON.stringify(trace.at(-1))); }
      assert.equal(recovered.members, baseline.members);
      assert.deepEqual(recovered.cameras.map(camera => camera.streamId), baseline.cameras.map(camera => camera.streamId));
      recoveries.push({ delayed, recovered });
    }
    writeFileSync(`${work}/live-latency-recovery.json`, JSON.stringify({ baseline, recoveries }, null, 2));
    console.log('PASS: repeated viewer delivery stalls catch up native and software video automatically in the same stream epochs');
  }
  if (!hevc && !synchronize) {
    await evaluate('document.getElementById("recording-settings").click()');
    await until(() => evaluate('document.getElementById("zone-preview-status").textContent.startsWith("Snapshot for drawing zones")'), 'initial zone snapshot');
    await until(async () => (await (await fetch(base + '/api/state')).json()).cameras[0].status === 'idle', 'zone snapshot releases preview capture');
    const originalPicture = await evaluate('document.getElementById("zone-canvas").toDataURL()');
    const cached = await fetch(base + '/api/cameras/1/snapshot');
    assert.equal(cached.headers.get('cache-control'), 'no-store');
    const originalJpeg = Buffer.from(await cached.arrayBuffer());
    writeFileSync(stillFlag, '');
    const retained = await fetch(base + '/api/cameras/1/snapshot?refresh=false');
    assert.deepEqual(Buffer.from(await retained.arrayBuffer()), originalJpeg);
    await evaluate('document.getElementById("zone-refresh").click()');
    await until(() => evaluate('document.getElementById("zone-preview-status").textContent.startsWith("Snapshot for drawing zones")'), 'Refresh picture loads a fresh snapshot');
    assert.notEqual(await evaluate('document.getElementById("zone-canvas").toDataURL()'), originalPicture);
    await until(async () => (await (await fetch(base + '/api/state')).json()).cameras[0].status === 'idle', 'Refresh picture releases preview capture');
    assert.equal((await fetch(base + '/api/cameras/999/snapshot?refresh=true')).status, 404);
    assert.equal((await fetch(base + '/api/cameras/1/snapshot?refresh=invalid')).status, 400);
    unlinkSync(stillFlag);
    await evaluate('document.querySelector("[data-close=recording-dialog]").click()');
    console.log('PASS: Refresh picture changes actual canvas pixels while native playback has no JPEG subscription; retained snapshots, no-store headers and invalid/missing camera requests behave correctly');
  }
  if (synchronize) {
    const recording = (await (await fetch(base + '/api/recording')).json()).settings;
    Object.assign(recording, { enabled: true, directory: `${work}/recordings`,
      cameras: settings.Sources.map(source => ({ channel: source.Channel, cameraId: 'camera-' + source.Id,
        enabled: true, detectionMethod: 'adaptive', detectionSubstream: false, minimumAreaPercent: .1, defaultActionIds: [] })) });
    const saved = await fetch(base + '/api/recording', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(recording) });
    assert.ok(saved.ok, await saved.text());
    await evaluate('document.getElementById("detection-overlays").checked = true; document.getElementById("detection-overlays").onchange();');
    await until(() => evaluate('tiles.get(1).detections.frames.length >= 20'), 'stable detector warmup history', 60);
    unlinkSync(stillFlag);
    await until(() => evaluate('tiles.get(1).detections.presentedFrame?.detections.length > 0'), 'timestamped moving-object detections', 30);
    const sync = await evaluate(`(() => {
      const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360;
      const pixels = canvas.getContext('2d', { willReadFrequently: true });
      window.sampleDetectionSync = channel => {
        const tile = tiles.get(channel), view = tile.detections, player = tile.playback.player;
        pixels.drawImage(player.video || player.canvas, 0, 0, 640, 360);
        const row = pixels.getImageData(0, 170, 640, 1).data;
        const xs = []; for (let x = 0; x < 640; x++) if (row[x * 4] > 180) xs.push(x);
        const observation = view.presentedFrame?.detections.find(item => item.bounds.y > .3 && item.bounds.y < .6);
        if (!xs.length || !observation || view.layer.hidden) return null;
        const actual = (xs[0] + xs.at(-1)) / 2;
        const predicted = (observation.bounds.x + observation.bounds.width / 2) * 640;
        // At 120 px/s, 5 Hz analysis and 10 fps media permit at most ~36 px of sampling lag.
        return { channel, actual, predicted, error: Math.abs(actual - predicted),
          playbackTime: view.playbackTime, detectionTime: view.presentedFrame.captured,
          bufferedSeconds: player.video ? player.video.buffered.end(player.video.buffered.length - 1) - player.video.currentTime : null };
      };
      return [1, 2].map(channel => ({ channel, historySamples: tiles.get(channel).detections.frames.length,
        clockReady: tiles.get(channel).detections.playbackTime !== null }));
    })()`);
    assert.ok(sync.every(camera => camera.clockReady));
    const samples = [];
    for (let i = 0; i < 35; i++) { samples.push(...(await evaluate('[1, 2].map(sampleDetectionSync).filter(Boolean)'))); await sleep(100); }
    const paired = samples.filter((sample, index) => sample.channel === 1 && samples[index + 1]?.channel === 2)
      .map(sample => ({ native: sample, software: samples[samples.indexOf(sample) + 1] }));
    assert.ok(paired.length >= 10, 'Cross-camera synchronization must compare actual simultaneous decoded pictures.');
    const separation = paired.map(pair => Math.abs(pair.native.actual - pair.software.actual)).sort((a, b) => a - b);
    assert.ok(separation[Math.floor(separation.length * .9)] <= 48, JSON.stringify(paired));
    console.log('PASS: native and software cameras show the same physical moving-object position within frame sampling tolerance');
    assert.ok(samples.every(sample => sample.detectionTime <= sample.playbackTime), 'No detection from a future frame may be displayed.');
    for (const channel of [1, 2]) {
      const cameraSamples = samples.filter(sample => sample.channel === channel);
      assert.ok(cameraSamples.length >= 10, 'Moving-object pixel validation must observe enough live frames for camera ' + channel);
      const ordered = cameraSamples.map(sample => sample.error).sort((a, b) => a - b);
      assert.ok(ordered[Math.floor(ordered.length * .9)] <= 48, JSON.stringify(cameraSamples));
    }
    writeFileSync(`${work}/detection-sync.json`, JSON.stringify({ cameras: sync, samples }, null, 2));
    console.log('PASS: actual native and software pixels agree with timestamp-selected moving-object boxes within detector sampling tolerance; future boxes stay hidden');
    // Hold both playback and correction seeks through fragment arrivals. A final
    // compositor callback after pause may still report the last displayed frame.
    await evaluate('window.resumeSyncClock = tiles.get(1).playback.player.synchronize; tiles.get(1).playback.player.synchronize = () => false; tiles.get(1).playback.player.video.pause();');
    let lastPausedTime;
    await until(async () => {
      const current = await evaluate('tiles.get(1).detections.playbackTime');
      const stable = current === lastPausedTime; lastPausedTime = current;
      return stable;
    }, 'last paused compositor frame settles');
    const pausedTime = lastPausedTime;
    const pausedPixels = await evaluate('(() => { const canvas = document.createElement("canvas"); canvas.width = 640; canvas.height = 360; canvas.getContext("2d").drawImage(tiles.get(1).playback.player.video, 0, 0, 640, 360); return canvas.toDataURL(); })()');
    await sleep(700); assert.equal(await evaluate('tiles.get(1).detections.playbackTime'), pausedTime);
    assert.equal(await evaluate('(() => { const canvas = document.createElement("canvas"); canvas.width = 640; canvas.height = 360; canvas.getContext("2d").drawImage(tiles.get(1).playback.player.video, 0, 0, 640, 360); return canvas.toDataURL(); })()'), pausedPixels);
    await evaluate('tiles.get(1).playback.player.synchronize = window.resumeSyncClock; delete window.resumeSyncClock;');
    await until(() => evaluate(`tiles.get(1).detections.playbackTime > ${pausedTime}`), 'overlay timeline resumes with displayed footage');
    if (recovery) {
      const before = await evaluate('[1, 2].map(channel => ({ channel, streamId: tiles.get(channel).playback.player.clock.streamId, captured: tiles.get(channel).detections.presentedFrame.captured }))');
      writeFileSync(pauseFlag, ''); await sleep(4000); unlinkSync(pauseFlag);
      await until(() => evaluate(`[1, 2].every(channel => tiles.get(channel).detections.presentedFrame?.captured > ${Math.max(...before.map(camera => camera.captured)) + 4000})`), 'fresh detection samples recover after the RTSP source pause', 20);
      const recovered = [];
      for (let i = 0; i < 35; i++) { recovered.push(...(await evaluate('[1, 2].map(sampleDetectionSync).filter(Boolean)'))); await sleep(100); }
      for (const channel of [1, 2]) {
        const cameraSamples = recovered.filter(sample => sample.channel === channel);
        assert.ok(cameraSamples.length >= 10, 'Detection remains visible after source recovery for camera ' + channel);
        assert.ok(cameraSamples.every(sample => sample.detectionTime <= sample.playbackTime));
        const ordered = cameraSamples.map(sample => sample.error).sort((a, b) => a - b);
        assert.ok(ordered[Math.floor(ordered.length * .9)] <= 48, JSON.stringify(cameraSamples));
        assert.equal(await evaluate(`tiles.get(${channel}).playback.player.clock.streamId`), before.find(camera => camera.channel === channel).streamId);
      }
      const liveDetections = await (await fetch(base + '/api/detections')).json();
      assert.ok(liveDetections.cameras.every(camera => camera.history.frames.length > 0 && !['retrying', 'stopped', 'analysis-error'].includes(camera.status)));
      writeFileSync(`${work}/detection-recovery.json`, JSON.stringify({ before, samples: recovered }, null, 2));
      console.log('PASS: slower-than-advertised RTSP pacing stays aligned and detection recovers after a source pause in the same stream epoch');
    }
  }
  await evaluate('document.getElementById("connection").click()');
  const configuration = await until(() => evaluate(`document.querySelectorAll('[data-playback-channel]').length === 3 && document.querySelector('[data-playback-channel="1"]').options.length === 4`), 'playback controls');
  assert.ok(configuration);
  assert.match(await evaluate(`document.getElementById(document.querySelector('[data-playback-channel="1"]').getAttribute('aria-describedby')).textContent`), /Currently using.*Automatic.*(Native|Browser)/);
  await evaluate('document.getElementById("setup-close").click(); focus = 2; arrange();');
  assert.ok(await evaluate('tiles.get(2).zoom.picture(tiles.get(2).root.querySelector(".surface").getBoundingClientRect()).width > 0'));
  await until(() => evaluate('tiles.get(2).zoom.focused && tiles.get(2).root.classList.contains("grid-media-ready")'), 'focused software picture');
  assert.match(await evaluate('tiles.get(2).zoom.crop = { left: .2, top: .2, width: .5, height: .5 }; tiles.get(2).zoom.refresh(); tiles.get(2).root.querySelector(".grid-camera-canvas").style.transform'), /translate.*scale/);
  console.log('PASS: camera setup shows four choices and the automatic method in use; software pictures support focus and zoom');
  await evaluate('tiles.get(2).playback.suspend(true)'); assert.equal(await evaluate('!!tiles.get(2).playback.player'), false);
  await evaluate('tiles.get(2).playback.suspend(false)');
  await until(() => evaluate('tiles.get(2).playback.stats.frames > 3'), 'software decoder reconnect after suspension');
  // Force an actual decoder availability failure without changing the persisted preference.
  if (reports[0].method === 'native') await evaluate('tiles.get(1).playback.fail("Native decoder unavailable in the fixture")');
  await until(() => evaluate('tiles.get(1).playback.method === "browser" && tiles.get(1).playback.stats.frames > 3'), 'automatic browser fallback');
  await evaluate('tiles.get(1).playback.fail("Browser decoder unavailable in the fixture")');
  await until(() => evaluate('tiles.get(1).playback.method === "server" && tiles.get(1).playback.stats.frames > 3 && previewSelection === "1,3"'), 'automatic server fallback');
  console.log('PASS: suspension releases decoding; automatic fallbacks report the reason and request JPEG only when needed');
  assert.equal(errors.length, 0, errors.join('\n'));
  writeFileSync(`${work}/reports.json`, JSON.stringify({ initial: reports, final: await evaluate('[...CameraPlayback.reports.values()]') }, null, 2));
  if (benchmark) {
    assert.equal(process.platform, 'win32', 'This process CPU sampler uses Windows APIs; use profile-web.sh on Linux.');
    const measurements = [];
    for (const method of ['server', 'native', 'browser']) {
      await evaluate(`for (const [channel, tile] of tiles) { tile.playback.stop(); tile.playback = new CameraPlayback(channel, tile, '${method}', state.playback, updatePreviewSelection); } true`);
      await until(() => evaluate(`tiles.size === 3 && [...tiles.values()].every(tile => tile.playback.method === '${method}' && tile.playback.stats.frames >= 20)`), method + ' benchmark warmup', 60);
      await sleep(12000);
      await evaluate('for (const tile of tiles.values()) tile.playback.stats.reset()');
      const output = `${work}/${method}-cpu.json`;
      const sampler = start('pwsh', ['-NoProfile', '-File', 'tests/sample_camera_playback.ps1', '-ServerProcessId', String(server.pid), '-BrowserProcessId', String(chrome.pid), '-OutputPath', output]);
      await new Promise((resolve, reject) => { sampler.on('error', reject); sampler.on('exit', code => code ? reject(new Error('CPU sampler failed: ' + code)) : resolve()); });
      const cpu = JSON.parse(readFileSync(output, 'utf8').replace(/^\uFEFF/, ''));
      const report = await evaluate('[...CameraPlayback.reports.values()]');
      assert.ok(cpu.elapsedSeconds > 0 && cpu.serverCores + cpu.ffmpegCores > 0 && cpu.browserCores > 0, 'The process CPU sampler must return nonzero live-process readings.');
      assert.ok(report.every(camera => camera.method === method && camera.frames > 0), 'All measured cameras must keep displaying the requested method.');
      measurements.push({ method, ...cpu, reports: report });
      console.log('BENCHMARK:', method, JSON.stringify(cpu));
    }
    const results = { date: new Date().toISOString(), environment: await evaluate('({userAgent: navigator.userAgent, logicalProcessors: navigator.hardwareConcurrency})'),
      source: 'Three generated 640x360 H.264 cameras at 10 fps, one local Chrome viewer, no recording/detection/rewind', warmupSeconds: 12, measurements };
    writeFileSync(`${work}/benchmark.json`, JSON.stringify(results, null, 2));
    console.log('BENCHMARK RESULTS:', `${work}/benchmark.json`);
  }
  console.log('All camera playback browser checks passed. Artifacts:', work);
} catch (error) {
  console.error('Browser artifacts:', work, 'Reports:', lastReports, 'Errors:', errors);
  if (evaluate) console.error('State:', await evaluate('({ controls: [...document.querySelectorAll("[data-playback-channel]")].map(s => ({ channel: s.dataset.playbackChannel, options: s.options.length })), text: document.body.innerText.slice(0, 2500), state: typeof state === "undefined" ? null : state })'));
  throw error;
} finally {
  debug?.close();
  for (const child of children.reverse()) { if (child.exitCode === null) { if (process.platform === 'win32') await new Promise(resolve => { const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true }); killer.on('close', resolve); }); else child.kill('SIGTERM'); } }
  logs.forEach(log => log.end());
}
