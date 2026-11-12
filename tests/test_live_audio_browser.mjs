// Real Chromium gestures and MSE playback against generated RTSP audio/video.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, createWriteStream } from 'node:fs';
import { resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import net from 'node:net';
const work = resolve('artifacts/live-audio/browser-' + randomUUID()); mkdirSync(work, { recursive: true });
const children = [], logs = [], errors = [];
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function port() {
  const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const value = socket.address().port; await new Promise(resolve => socket.close(resolve)); return value;
}
function start(command, args, env) {
  const child = spawn(command, args, { env: { ...process.env, ...env }, windowsHide: true }); children.push(child);
  const log = createWriteStream(`${work}/${children.length}.log`); logs.push(log); child.stdout.pipe(log); child.stderr.pipe(log); return child;
}
async function until(action, label, seconds = 45) {
  const end = Date.now() + seconds * 1000; let error;
  while (Date.now() < end) { try { const result = await action(); if (result) return result; } catch (ex) { error = ex; } await sleep(200); }
  throw new Error('Timed out: ' + label + (error ? '; ' + error.message : ''));
}
let debug, evaluate;
try {
  const rtspPort = await port(), webPort = await port();
  const settings = { ConfigurationId: 'default', ConfigurationName: 'Audio controls tests', Host: '127.0.0.1', RtspPort: rtspPort,
    Username: 'admin', ChannelCount: 4, AutoConnect: true,
    Sources: ['automatic', 'server', 'browser', 'server'].map((PlaybackMethod, index) => ({ Id: randomUUID().replaceAll('-', ''), Channel: index + 1,
      Name: 'Camera ' + (index + 1), MainStream: `rtsp://127.0.0.1:${rtspPort}/ip0${index + 1}/0`, Username: 'admin', UseDefaultPassword: true, PlaybackMethod })) };
  const settingsPath = `${work}/settings.json`;
  writeFileSync(settingsPath, JSON.stringify({ Version: 1, ActiveConfigurationId: 'default', Configurations: [{ Connection: settings, Password: 'local-only-test', Credentials: {} }], Integrations: { Enabled: false } }));
  start('python', ['tests/rtsp_fixture.py'], { RTSP_FIXTURE_PORT: String(rtspPort), RTSP_FIXTURE_USER: 'admin', RTSP_FIXTURE_AUDIO_CHANNELS: '1,2,3' });
  start('dotnet', [resolve(process.argv[2] || 'artifacts/live-web/SecurityCameraViewer.Web.dll'), '--urls', `http://127.0.0.1:${webPort}`, '--settings', settingsPath, '--ffmpeg', 'ffmpeg']);
  const base = `http://127.0.0.1:${webPort}`;
  await until(async () => (await fetch(base + '/api/state')).ok, 'web server startup');
  const chrome = process.env.CHROME_PATH || (process.platform === 'win32' ? 'C:/Program Files/Google/Chrome/Application/chrome.exe' : 'google-chrome');
  start(chrome, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${work}/chrome`, '--window-size=1280,900', '--no-first-run', '--no-default-browser-check', 'about:blank']);
  const debugPort = await until(() => readFileSync(`${work}/chrome/DevToolsActivePort`, 'utf8').split('\n')[0], 'Chrome DevTools');
  const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
  debug = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { debug.onopen = resolve; debug.onerror = reject; });
  const requests = new Map(); let sequence = 0;
  debug.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id) { const request = requests.get(message.id); requests.delete(message.id); message.error ? request.reject(message.error) : request.resolve(message.result); }
    else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
  };
  const call = (method, params = {}) => new Promise((resolve, reject) => { const id = ++sequence; requests.set(id, { resolve, reject }); debug.send(JSON.stringify({ id, method, params })); });
  await call('Runtime.enable'); await call('Page.enable');
  evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text); return result.result.value;
  };
  async function click(expression, ctrl = false) {
    const point = await evaluate(`(() => { const el = ${expression}; const visible = !el.hidden && !el.disabled; if (!visible) throw new Error('Button unavailable'); const r = el.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1, modifiers: ctrl ? 2 : 0 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1, modifiers: ctrl ? 2 : 0 });
  }
  const listen = (channel, ctrl) => click(`tiles.get(${channel}).listen`, ctrl);
  const playing = channel => evaluate(`(() => { const p = liveAudio.players.get(${channel}); return p?.hasAudio && p.status === 'playing' && !p.video.paused && p.video.readyState >= 2; })()`);
  const muted = channel => evaluate(`liveAudio.players.get(${channel}).video.muted`);
  await call('Page.navigate', { url: base });
  await until(() => evaluate('typeof tiles !== "undefined" && tiles.size === 4 && [...tiles.values()].every(t => t.playback.stats.frames >= 3)'), 'grid pictures');
  assert.equal(await evaluate('focus'), null);
  await until(() => evaluate('[...tiles.values()].every(t => t.audioLabel.textContent !== "Checking audio\u2026")'), 'automatic audio metadata');
  assert.equal(await evaluate('[1,2,3].every(ch => tiles.get(ch).audioLabel.textContent === "PCMU Audio" && tiles.get(ch).listen.textContent === "Audio OFF" && !tiles.get(ch).listen.hidden && !tiles.get(ch).listen.disabled)'), true);
  assert.equal(await evaluate('tiles.get(4).audioLabel.textContent'), 'No Audio');
  assert.equal(await evaluate('tiles.get(4).listen.hidden'), true);
  assert.equal(await evaluate('liveAudio.players.size'), 0);
  async function checkFooter() {
    const metrics = await evaluate(`(() => { const t = tiles.get(1), label = t.audioLabel, button = t.listen;
      const a = label.getBoundingClientRect(), b = button.getBoundingClientRect(), h = t.root.querySelector('.tile-header').getBoundingClientRect();
      return { footer: button.parentElement === t.root.querySelector('.tile-header'), follows: button.previousElementSibling === label,
        sameFont: getComputedStyle(button).fontSize === getComputedStyle(label).fontSize,
        labelHeight: a.height, buttonHeight: b.height, sameLine: Math.abs((a.y+a.height/2)-(b.y+b.height/2)) < 1,
        inside: b.right <= h.right && b.left >= h.left, toggle: document.elementFromPoint(b.x+b.width/2,b.y+b.height/2) === button }; })()`);
    assert.equal(metrics.footer, true); assert.equal(metrics.follows, true); assert.equal(metrics.sameFont, true);
    assert.ok(metrics.buttonHeight <= metrics.labelHeight + 2, JSON.stringify(metrics));
    assert.equal(metrics.sameLine, true); assert.equal(metrics.inside, true); assert.equal(metrics.toggle, true);
  }
  await checkFooter();
  await call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
  await checkFooter();
  await call('Emulation.clearDeviceMetricsOverride');
  await sleep(300); await checkFooter();
  const initialScreenshot = await call('Page.captureScreenshot'); writeFileSync(`${work}/grid-audio-off.png`, Buffer.from(initialScreenshot.data, 'base64'));
  await listen(1); assert.equal(await evaluate('liveAudio.players.has(1)'), true, 'Footer mouse gesture starts audio'); await until(() => playing(1), 'first camera audio'); assert.equal(await muted(1), false);
  assert.equal(await evaluate('tiles.get(1).listen.textContent'), 'Audio ON');
  assert.equal(await evaluate('tiles.get(1).audioLabel.textContent'), 'AAC Audio');
  await listen(2, true); await until(() => playing(2), 'Ctrl adds second audio stream');
  assert.equal(await muted(1), false); assert.equal(await muted(2), false);
  const firstVideoTime = await evaluate('liveAudio.players.get(1).video.currentTime');
  await listen(3); await until(() => playing(3), 'ordinary Listen switches audible camera');
  assert.equal(await muted(1), true); assert.equal(await muted(2), true); assert.equal(await muted(3), false);
  assert.equal(await evaluate(`liveAudio.players.get(1).video.currentTime > ${firstVideoTime}`), true);
  assert.equal(await evaluate('[1,2,3].every(ch => liveAudio.players.get(ch).channel === ch)'), true);
  await listen(1, true); assert.equal(await muted(1), false); assert.equal(await muted(3), false);
  await listen(3); assert.equal(await evaluate('liveAudio.players.get(3).channel'), null); assert.equal(await muted(1), false);
  await listen(2); assert.equal(await muted(1), true); assert.equal(await muted(2), false);
  await listen(1, true);
  console.log('PASS: footer ON/OFF uses actual gestures; Ctrl mixes audio; normal clicks mute peers; OFF releases a camera');
  await evaluate('setMenu(true)');
  await click('document.getElementById("live-audio-compatibility")');
  await until(() => playing(1), 'first compatibility listener');
  await until(() => playing(2), 'second compatibility listener');
  assert.equal(await evaluate('[1,2].every(ch => liveAudio.players.get(ch).compatibility && liveAudio.players.get(ch).socket.url.endsWith("?compatibility=true"))'), true);
  assert.equal(await evaluate('localStorage.getItem("scv.live-audio-compatibility")'), 'true');
  assert.equal(await muted(1), false); assert.equal(await muted(2), false);
  await click('document.getElementById("live-audio-compatibility")');
  await until(() => playing(1), 'original listener restored');
  assert.equal(await evaluate('liveAudio.players.get(1).socket.url.includes("compatibility")'), false);
  await evaluate('setMenu(false)');
  console.log('PASS: compatibility checkbox persists locally and restarts active audio through the selected endpoint');
  const screenshot = await call('Page.captureScreenshot'); writeFileSync(`${work}/grid-audio.png`, Buffer.from(screenshot.data, 'base64'));
  await evaluate('focus = 2; arrange();'); assert.equal(await evaluate('liveAudio.players.get(1).channel'), 1);
  assert.equal(await evaluate('document.getElementById("live-listen").textContent'), 'Audio ON');
  await evaluate('document.getElementById("live-volume").value = "30"; document.getElementById("live-volume").dispatchEvent(new Event("input"));');
  assert.equal(await evaluate('[...liveAudio.players.values()].every(p => p.volume === .3 && (!p.video || p.video.volume === .3))'), true);
  await evaluate('focus = null; arrange(); window.dispatchEvent(new Event("resize"));');
  assert.equal(await muted(1), false); assert.equal(await muted(2), false);
  assert.equal(await evaluate('liveAudio.players.has(4)'), false);
  assert.equal(await muted(1), false); assert.equal(await muted(2), false);
  console.log('PASS: availability appears before clicking; footer font size and alignment work at desktop and narrow widths; focus/grid retains audio and volume');
  await listen(1); assert.equal(await evaluate('liveAudio.players.get(1).channel'), null);
  assert.equal(await evaluate('tiles.get(1).listen.textContent'), 'Audio OFF'); assert.equal(await muted(2), false);
  await evaluate('liveAudio.setTalkbackActive(true)'); assert.equal(await muted(2), true);
  await evaluate('liveAudio.setTalkbackActive(false)'); assert.equal(await muted(2), false);
  // A pending browser play rejection must expose Audio OFF for retry on its own tile.
  await evaluate('window.savedPlay = liveAudio.players.get(2).video.play; liveAudio.players.get(2).video.play = () => Promise.reject(new DOMException("Blocked", "NotAllowedError")); liveAudio.players.get(2).play();');
  await until(() => evaluate('tiles.get(2).listen.textContent === "Audio OFF" && liveAudio.players.get(2).status === "blocked"'), 'blocked audio feedback');
  await evaluate('liveAudio.players.get(2).video.play = window.savedPlay');
  await listen(2); assert.equal(await evaluate('liveAudio.players.get(2).channel'), 2);
  await fetch(base + '/api/disconnect', { method: 'POST' });
  await until(() => evaluate('liveAudio.players.size === 0 && document.querySelectorAll(".live-camera-video").length === 0'), 'disconnect releases every listening session');
  assert.equal(errors.length, 0, errors.join('\n'));
  console.log('PASS: stop releases one camera, talkback restores mute choices, blocked playback retries, and disconnect clears all media; no browser errors');
  console.log('All live audio browser checks passed. Artifacts:', work);
} catch (error) {
  console.error('Browser artifacts:', work, 'Errors:', errors);
  if (evaluate) console.error('Audio state:', await evaluate('typeof liveAudio === "undefined" ? null : [...liveAudio.players].map(([channel,p]) => ({ channel, active: p.channel, hasAudio: p.hasAudio, muted: p.muted, status: p.status, message: p.message }))'));
  if (evaluate) console.error('Viewer state:', await evaluate('typeof state === \"undefined\" ? null : ({ busy, serverOnline, connected: state?.connected, cameras: state?.cameras, focus, width: window.innerWidth })'));
  throw error;
} finally {
  debug?.close();
  for (const child of children.reverse()) {
    if (child.exitCode === null) {
      if (process.platform === 'win32') await new Promise(resolve => { const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true }); killer.on('close', resolve); });
      else child.kill('SIGTERM');
    }
  }
  logs.forEach(log => log.end());
}
