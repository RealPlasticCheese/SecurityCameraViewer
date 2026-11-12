'use strict';
const $ = id => document.getElementById(id);
const form = $('connection-form');
const dialog = $('connection-dialog');
const menu = $('viewer-menu');
const menuToggle = $('menu-toggle');
const tiles = new Map();
let state, focus = null, busy = false, serverOnline = false, socket, socketOnline = false;
let generation = -1;
let detectionOverlays;
let liveRewind;
let cameraPictureInPicture, fullscreenPending = false;
let previewSelection, previewScheduled = false;
function updatePreviewSelection() {
  if (previewScheduled) return;
  previewScheduled = true;
  queueMicrotask(() => {
    previewScheduled = false;
    const selection = [...tiles].filter(([, tile]) => tile.playback?.method === 'server' && !tile.playback.suspended)
      .map(([channel]) => channel).sort((a, b) => a - b).join(',');
    if (selection === previewSelection) return;
    previewSelection = selection;
    if (socket) { const old = socket; socket = null; old.close(); connectVideo(); }
  });
}
const liveAudio = new LiveCameraAudioController(() => renderLiveAudio());
const pushToTalk = new PushToTalk(() => renderLiveAudio());
// Only the displayed camera video should be offered to the browser for automatic PiP.
function updatePictureInPicture() {
  const singleCamera = [...tiles.values()].filter(tile => !tile.root.hidden).length === 1;
  for (const [channel, tile] of tiles) {
    const grid = tile.playback?.player?.video;
    const audio = liveAudio.players.get(channel)?.video;
    const rewind = liveRewind?.players.get(channel)?.video;
    // Chrome can choose either fullscreen-sized video. Both contain the same
    // picture in single view, so the displayed source must remain PiP eligible.
    const active = cameraPictureInPicture?.video && !singleCamera || tile.root.hidden ? null : tile.root.classList.contains('live-media-ready')
      ? (liveRewind?.historical ? rewind : audio) : tile.root.classList.contains('grid-media-ready') ? grid : null;
    for (const video of [grid, audio, rewind]) {
      if (video) video.disablePictureInPicture = video !== active || video.readyState < 2;
    }
  }
}
function cameraAudio(channel) {
  const camera = state?.cameras.find(camera => camera.channel === channel);
  const player = liveAudio.players.get(channel);
  const hasAudio = state?.demo ? false : liveAudio.audioAvailability.get(channel) ?? camera?.hasAudio ?? null;
  const codec = player?.hasAudio && player.mimeType ? CameraMediaCodecs.describe(player.mimeType).audio : null;
  return { hasAudio, codec: codec || camera?.audioCodec || null };
}
function renderLiveAudio() {
  updatePictureInPicture();
  liveAudio.setTalkbackActive(pushToTalk.channel !== null);
  const focusedPlayer = liveAudio.players.get(focus);
  const disabled = busy || !serverOnline || !state?.connected || !!liveRewind?.historical;
  const enabled = player => player?.channel != null && !player.muted && player.status !== 'blocked';
  const label = player => enabled(player) ? 'Audio ON' : 'Audio OFF';
  const hint = 'Click to toggle audio. Ctrl+click to keep other cameras audible.';
  $('live-listen').textContent = label(focusedPlayer);
  $('live-listen').setAttribute('aria-pressed', String(enabled(focusedPlayer)));
  $('live-listen').disabled = disabled || focus === null || cameraAudio(focus).hasAudio !== true;
  $('live-listen').title = hint;
  $('live-volume').disabled = !liveAudio.active;
  const status = focusedPlayer?.message || 'Use Audio OFF on a camera to turn sound on. Ctrl+click to keep other cameras audible.';
  if ($('live-sound-status').textContent !== status) $('live-sound-status').textContent = status;
  for (const [channel, tile] of tiles) {
    const player = liveAudio.players.get(channel), audio = cameraAudio(channel);
    const audioLabel = audio.hasAudio === false ? 'No Audio' : audio.hasAudio === true ?
      audio.codec ? `${audio.codec} Audio` : 'Audio' : 'Checking audio…';
    if (tile.audioLabel.textContent !== audioLabel) tile.audioLabel.textContent = audioLabel;
    tile.audioLabel.title = audio.hasAudio === null ? 'Waiting for the camera stream description. Audio availability is not yet known.' : '';
    tile.listen.hidden = audio.hasAudio !== true;
    tile.listen.textContent = label(player);
    tile.listen.setAttribute('aria-pressed', String(enabled(player)));
    tile.listen.setAttribute('aria-label', `${tile.name}: ${label(player)}. ${enabled(player) ? 'Turn audio off' : 'Turn audio on'}.`);
    tile.listen.title = player?.status === 'blocked' ? `${player.message} Click to retry. ${hint}` :
      player?.status === 'error' || player?.status === 'unsupported' ? `${player.message} ${hint}` : hint;
    tile.listen.disabled = disabled;
    const message = ['blocked', 'error', 'unsupported'].includes(player?.status) ? player.message : '';
    if (tile.soundStatus.textContent !== message) tile.soundStatus.textContent = message;
    const capability = tile.talkback;
    const talking = pushToTalk.channel === channel;
    tile.talk.hidden = channel !== focus || !capability?.supported;
    tile.talk.disabled = disabled || capability?.status === 'busy' || !capability?.canTalk || !PushToTalk.available() || !state?.cameras.find(camera => camera.channel === channel)?.live;
    tile.talk.textContent = talking ? 'Release to stop' : 'Hold to talk';
    tile.talk.setAttribute('aria-pressed', String(talking));
    tile.talk.title = !PushToTalk.available() ? 'Microphone capture requires HTTPS (or localhost) and AudioWorklet support.' :
      !capability?.canTalk ? 'Talking requires control permission.' : capability?.status === 'busy' ? capability.message :
        'Hold the primary pointer, Space or Enter; release to stop.';
    const talkMessage = channel === focus && capability?.supported ? pushToTalk.message : '';
    tile.talkStatus.hidden = !talkMessage;
    if (tile.talkStatus.textContent !== talkMessage) tile.talkStatus.textContent = talkMessage;
    tile.sound.hidden = tile.talk.hidden && tile.talkStatus.hidden;
    tile.zoom?.refresh();
  }
}

function refreshTalkback() {
  const tile = tiles.get(focus), channel = focus, epoch = generation;
  if (!tile || !serverOnline || !state?.connected || !state.cameras.find(camera => camera.channel === channel)?.live) return;
  if (pushToTalk.channel !== null || tile.talkbackProbe || performance.now() - tile.talkbackChecked < 15000) return;
  tile.talkbackChecked = performance.now();
  const probe = tile.talkbackProbe = new AbortController();
  const timeout = setTimeout(() => probe.abort(), 9000);
  fetch(`/api/cameras/${channel}/talkback`, { signal: probe.signal }).then(async response => {
    if (!response.ok) throw new Error('Talkback check failed');
    const capability = await response.json();
    if (tiles.get(channel) === tile && generation === epoch && !probe.signal.aborted) {
      tile.talkback = capability; renderLiveAudio();
    }
  }).catch(() => {}).finally(() => { clearTimeout(timeout); if (tile.talkbackProbe === probe) tile.talkbackProbe = null; });
}
function toggleListening(channel, event) {
  if (busy || !serverOnline || !state?.connected || liveRewind?.historical || cameraAudio(channel).hasAudio !== true) return;
  liveAudio.listen(channel, tiles.get(channel), !!event?.ctrlKey);
}
$('live-listen').onclick = event => toggleListening(focus, event);
$('live-volume').oninput = event => liveAudio.setVolume(Number(event.target.value) / 100);
$('live-audio-compatibility').checked = liveAudio.compatibility;
$('live-audio-compatibility').onchange = event => liveAudio.setCompatibility(event.target.checked);
window.addEventListener('pagehide', () => { cameraPictureInPicture?.stop(); liveAudio.stop(); });
const cameraLayout = new CameraLayoutView($('cameras'), {
  getItem: key => localStorage.getItem(key),
  setItem: (key, value) => localStorage.setItem(key, value),
  removeItem: key => localStorage.removeItem(key)
}, (message, error) => {
  $('layout-status').textContent = message;
  $('layout-status').classList.toggle('error', error);
  if (error) setMenu(true);
});
detectionOverlays = new DetectionOverlayController($('detection-overlays'), $('detection-overlay-metadata'), $('detection-overlay-status'), () => tiles,
  () => ({ configurationId: state?.settings.configurationId, generation, online: !!(serverOnline && socketOnline && state?.connected) }));

function setMenu(open, restoreFocus = false) {
  menu.hidden = !open;
  menuToggle.setAttribute('aria-expanded', String(open));
  menuToggle.setAttribute('aria-label', open ? 'Close viewer menu' : 'Open viewer menu');
  if (restoreFocus) menuToggle.focus();
}

menuToggle.onclick = () => setMenu(menu.hidden);
document.addEventListener('pointerdown', event => {
  if (!event.target.closest('.viewer-controls')) setMenu(false);
});
document.addEventListener('focusin', event => {
  if (!event.target.closest('.viewer-controls')) setMenu(false);
});

async function api(path, body, method = 'POST') {
  const response = await fetch(`/api/${path}`, {
    method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000)
  });
  const data = response.headers.get('content-type')?.includes('application/json') ? await response.json() : null;
  if (response.status === 401) document.dispatchEvent(new Event('scv-sign-in-required'));
  if (!response.ok) throw new Error(data?.message || `The server could not complete the request (${response.status}).`);
  return data;
}

function arrange() {
  if (cameraPictureInPicture?.video && $('cameras').clientWidth === 0) return;
  if (pushToTalk.channel !== null && pushToTalk.channel !== focus) pushToTalk.stop();
  renderLiveAudio();
  const count = Math.max(1, tiles.size);
  const grid = $('cameras');
  let columns = 1, largestPicture = 0;
  // Choose the arrangement that gives 16:9 video the most space in this viewport.
  for (let candidate = 1; candidate <= count; candidate++) {
    const rows = Math.ceil(count / candidate);
    const width = (grid.clientWidth - candidate + 1) / candidate - 2;
    const height = (grid.clientHeight - rows + 1) / rows - 2;
    const picture = Math.min(width, height * 16 / 9);
    if (picture > largestPicture) { largestPicture = picture; columns = candidate; }
  }
  cameraLayout.configure(state.settings, tiles, columns, focus, window.innerWidth);
  for (const [channel, tile] of tiles) { tile.zoom.setFocused(channel === focus); tile.enhancements.setFocused(channel === focus, focus === null || channel === focus); }
  $('camera-focus').value = focus === null ? '' : String(focus);
  $('grid-button').textContent = focus ? 'Back to grid' : 'Camera grid';
  liveRewind?.viewChanged();
  updatePictureInPicture();
}
window.addEventListener('resize', () => { if (state) arrange(); });

function createTiles() {
  cameraPictureInPicture?.stop();
  liveRewind?.reset();
  pushToTalk.stop();
  for (const tile of tiles.values()) { tile.playback?.stop(); tile.talkbackProbe?.abort(); tile.detections.dispose(); tile.enhancements.dispose(); tile.zoom.dispose(); }
  liveAudio.stop();
  for (const tile of tiles.values()) if (tile.url) URL.revokeObjectURL(tile.url);
  tiles.clear(); $('cameras').replaceChildren(); focus = null;
  $('camera-focus').replaceChildren(new Option('All cameras', ''));
  for (const { source, number } of CameraCollection.entries(state.settings, false)) {
    const channel = source.channel, name = source.name?.trim() || `Camera ${number}`;
    const root = document.createElement('article'); root.className = 'tile'; root.dataset.channel = channel;
    // Only fixed markup goes through innerHTML. Recorder data is inserted as text.
    root.innerHTML = '<div class="tile-header"><div class="tile-title"><span class="channel"></span><span class="name"></span></div><span class="badge">OFFLINE</span></div><div class="surface"><img alt=""><div class="placeholder"><small></small><strong>Ready to connect</strong><p>Your live camera picture will appear here.</p></div><div class="stale-label" hidden></div></div>';
    root.querySelector('.channel').textContent = String(number).padStart(2, '0');
    root.querySelector('.name').textContent = name;
    const recordingBadge = document.createElement('span'); recordingBadge.className = 'recording-badge'; root.querySelector('.tile-header').append(recordingBadge);
    root.querySelector('.tile-title').title = name;
    $('camera-focus').add(new Option(`${number}. ${name}`, String(channel)));
    root.querySelector('small').textContent = `CAMERA ${String(number).padStart(2, '0')}`;
    const tile = { root, name, img: root.querySelector('img'), badge: root.querySelector('.badge'),
      message: root.querySelector('strong'), detail: root.querySelector('.placeholder p'),
      stale: root.querySelector('.stale-label'), lastFrame: 0, url: null };
    const sound = document.createElement('div'); sound.className = 'live-sound-overlay'; sound.hidden = true;
    sound.setAttribute('role', 'group'); sound.setAttribute('aria-label', `${name} live sound controls`);
    sound.innerHTML = '<button type="button" class="tile-talk" aria-pressed="false" hidden>Hold to talk</button><span class="tile-talk-status" role="status" hidden></span>';
    tile.sound = sound;
    tile.talk = sound.querySelector('.tile-talk'); tile.talkStatus = sound.querySelector('.tile-talk-status');
    tile.talkback = null; tile.talkbackChecked = -Infinity;
    pushToTalk.bind(tile.talk, channel, name);
    // Buttons must not trigger the picture's double-click focus/grid action.
    sound.addEventListener('dblclick', event => event.stopPropagation());
    root.querySelector('.surface').append(sound);
    tile.img.alt = `${name} live picture`;
    tile.zoom = new CameraZoomView(root, () => { tile.enhancements?.refresh(); tile.detections?.refresh(); }, {
      main: source.mainStreamAspectRatio,
      preview: state.settings.substream && source.subStream ? source.subStreamAspectRatio : source.mainStreamAspectRatio,
      jpeg: source.motionActivated ? source.mainStreamAspectRatio || 'auto' : undefined
    });
    const cameraId = source.id || channel.toString(16).padStart(32, '0');
    const enhancementPath = `/api/configurations/${encodeURIComponent(state.settings.configurationId || 'default')}/cameras/${encodeURIComponent(cameraId)}/enhancements`;
    tile.enhancementId = cameraId;
    const enhancementPreferences = new CameraEnhancementPreferences(state.enhancements?.[cameraId], async patch => {
      const response = await fetch(enhancementPath, { method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(patch), keepalive: true });
      if (!response.ok) throw new Error('Could not save enhancement settings.');
    });
    tile.enhancements = new CameraEnhancementView(root, tile.zoom, undefined, undefined, enhancementPreferences);
    tile.detections = new CameraDetectionOverlay(root, tile.zoom);
    const toggle = () => {
      if (focus === channel && tile.zoom.reset()) return;
      focus = focus === channel ? null : channel; arrange();
    };
    root.querySelector('.surface').addEventListener('dblclick', toggle);
    const check = document.createElement('button'); check.type = 'button'; check.textContent = 'Check now';
    check.className = 'camera-check'; check.title = 'Check stream availability; this does not wake the camera';
    check.hidden = !state.settings.sources?.find(c => c.channel === channel)?.motionActivated;
    check.onclick = async () => {
      check.disabled = true;
      try { await api(`cameras/${channel}/check`, {}); }
      catch (error) { tile.detail.textContent = error.message; tile.stale.textContent = error.message; }
      finally { check.disabled = false; }
    };
    tile.check = check; root.querySelector('.tile-header').append(check);
    tiles.set(channel, tile); $('cameras').append(root);
    tile.playbackStatus = document.createElement('span'); tile.playbackStatus.className = 'camera-playback-status';
    tile.playbackStatus.setAttribute('role', 'status'); root.querySelector('.tile-header').append(tile.playbackStatus);
    tile.audioLabel = document.createElement('span'); tile.audioLabel.className = 'camera-audio-label';
    tile.audioLabel.setAttribute('role', 'status');
    tile.listen = document.createElement('button'); tile.listen.type = 'button'; tile.listen.className = 'tile-listen';
    tile.listen.textContent = 'Audio OFF'; tile.listen.hidden = true;
    tile.listen.onclick = event => toggleListening(channel, event);
    tile.soundStatus = document.createElement('span'); tile.soundStatus.className = 'camera-audio-status';
    tile.soundStatus.setAttribute('role', 'status');
    root.querySelector('.tile-header').append(tile.audioLabel, tile.listen, tile.soundStatus);
    tile.playback = new CameraPlayback(channel, tile, source.playbackMethod || 'automatic',
      state.playback || { maximumQueuedBytes: 4 * 1024 * 1024, maximumQueuedPackets: 8 }, updatePreviewSelection, state.demo);
  }
  arrange();
  updatePreviewSelection();
}

function renderStatus() {
  if (!state) return;
  let live = 0;
  for (const [channel, tile] of tiles) {
    const camera = state.cameras.find(c => c.channel === channel);
    tile.playback?.suspend(!serverOnline || !state.connected || !camera || !!liveRewind?.historical);
    tile.playback?.updateMethodRetry();
    const timeout = state.settings.videoTimeoutSeconds ?? 0;
    if (timeout > 0 && performance.now() - tile.lastFrame >= timeout * 1000) {
      tile.playback?.expire();
      tile.root.classList.remove('has-frame');
      tile.img.removeAttribute('src');
      if (tile.url) URL.revokeObjectURL(tile.url); tile.url = null;
    }
    const gridVideo = tile.root.classList.contains('grid-media-ready');
    const hasFrame = tile.url || gridVideo;
    const fresh = serverOnline && socketOnline && !tile.playback?.reconnecting && performance.now() - tile.lastFrame < 4000 &&
      (tile.playback?.method === 'server' ? camera?.live && tile.url : gridVideo);
    const liveVideo = tile.root.classList.contains('live-media-ready') ? tile.root.querySelector('.live-camera-video') : null;
    tile.detections.setAvailable(!liveRewind?.historical && (!!fresh || !!(serverOnline && state.connected && camera?.live && liveVideo && !liveVideo.paused && liveVideo.readyState >= 2)));
    if (fresh) live++;
    const waiting = serverOnline && socketOnline && state.connected && camera?.status === 'waiting';
    tile.check.disabled = busy || !serverOnline || !state.connected;
    tile.badge.textContent = fresh ? '● LIVE' : waiting ? 'WAITING' : camera?.status === 'error' ? 'ERROR' : hasFrame ? '● STALE' : state.connected || state.connecting ? 'CONNECTING' : 'OFFLINE';
    tile.badge.className = `badge ${fresh ? 'live' : hasFrame ? 'stale' : ''}`;
    tile.message.textContent = !serverOnline ? 'Server unavailable' : state.connecting ? 'Checking connection…' : waiting ? 'Waiting for activity' : state.connected ? 'Waiting for camera' : 'Ready to connect';
    const message = !serverOnline ? 'Reconnecting to the web server…' : !socketOnline && state.connected ? 'Reconnecting to video…' :
      tile.playback?.connectionMessage || camera?.message || state.notice;
    tile.detail.textContent = tile.playback?.method === null ? tile.playback.connectionMessage || tile.playback.message : message;
    tile.stale.hidden = !hasFrame || !!fresh;
    const age = Math.max(camera?.frameAgeSeconds || 0, (performance.now() - tile.lastFrame) / 1000);
    const lastSeen = camera?.lastSeenAt ? new Date(camera.lastSeenAt).toLocaleString() : `${Math.floor(age)}s ago`;
    tile.stale.textContent = `Last picture · ${lastSeen} · ${message}`;
    const playback = liveRewind?.players.get(channel);
    if (playback) {
      tile.badge.textContent = playback.message ? 'NO FOOTAGE' : liveRewind.clock.paused ? 'PAUSED' : 'PLAYBACK';
      tile.badge.className = 'badge playback'; tile.stale.hidden = true;
      tile.message.textContent = playback.message || 'Loading buffered video…';
      tile.detail.textContent = playback.message ? new Date(liveRewind.clock.current()).toLocaleString() : '';
    }
  }
  $('overall').querySelector('span').textContent = !serverOnline ? 'Server unavailable — reconnecting' : state.connecting ? 'Connecting' : state.connected ? `${live} of ${tiles.size} cameras live` : 'Disconnected';
  $('overall').querySelector('i').classList.toggle('live', live > 0);
  $('connect').disabled = busy || !serverOnline || state.connecting;
  $('disconnect').disabled = busy || !serverOnline || !state.connected;
  $('quality').disabled = busy || !serverOnline || state.connecting;
  $('connection').disabled = busy || !serverOnline;
  if (liveAudio.players.size && (!serverOnline || !state.connected)) liveAudio.stop();
  if (pushToTalk.channel !== null && (!serverOnline || !state.connected || !state.cameras.find(camera => camera.channel === pushToTalk.channel)?.live)) {
    pushToTalk.stop('Camera is unavailable.');
  }
  renderLiveAudio();
  detectionOverlays?.refresh();
  refreshTalkback();
}

async function refresh() {
  try {
    const enhancementRevisions = new Map([...tiles.values()].map(tile => [tile, tile.enhancements.preferences.revision]));
    const next = await api('state', undefined, 'GET');
    const changed = !state || generation !== next.generation || JSON.stringify(state.settings.cameraNames) !== JSON.stringify(next.settings.cameraNames) || state.settings.channelCount !== next.settings.channelCount;
    state = next; serverOnline = true; generation = state.generation;
    $('recorder').textContent = `${state.demo ? 'TEST VIDEO  /  ' : ''}${state.settings.configurationName || state.settings.host}  /  ${state.settings.channelCount} cameras`;
    $('quality').textContent = `Quality: ${state.settings.substream ? 'Substream' : 'Main stream'}`;
    if (!busy) { $('notice').textContent = state.notice; $('notice').classList.remove('error'); }
    if (changed) createTiles();
    else for (const tile of tiles.values()) {
      tile.enhancements.synchronizePreferences(state.enhancements?.[tile.enhancementId], enhancementRevisions.get(tile));
    }
  } catch { serverOnline = false; }
  renderStatus();
}

function connectVideo() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/video?channels=${previewSelection ?? ''}`);
  socket = ws; ws.binaryType = 'arraybuffer';
  let pending = [];
  ws.onopen = () => { socketOnline = true; renderStatus(); };
  ws.onmessage = event => {
    if (typeof event.data === 'string') {
      const batch = pending; pending = [];
      Promise.allSettled(batch).then(() => { if (ws.readyState === WebSocket.OPEN) ws.send('ok'); });
      return;
    }
    const data = new Uint8Array(event.data);
    if (data.length < 18) return;
    const header = new DataView(data.buffer);
    const channel = header.getInt32(0, true), frameGeneration = header.getInt32(4, true);
    const frameAge = header.getFloat64(8, true), receivedAt = performance.now();
    if (!Number.isFinite(frameAge) || frameAge < 0) return;
    const tile = tiles.get(channel);
    if (!tile || frameGeneration !== generation || tile.playback?.method !== 'server' || tile.playback.suspended) return;
    const url = URL.createObjectURL(new Blob([data.subarray(16)], { type: 'image/jpeg' }));
    const picture = new Image(); picture.src = url;
    pending.push(picture.decode().then(() => {
      if (tiles.get(channel) !== tile || frameGeneration !== generation || socket !== ws || tile.playback?.method !== 'server' || tile.playback.suspended) { URL.revokeObjectURL(url); return; }
      const lastFrame = receivedAt - frameAge;
      const timeout = state.settings.videoTimeoutSeconds ?? 0;
      if (timeout > 0 && performance.now() - lastFrame >= timeout * 1000) { URL.revokeObjectURL(url); return; }
      const previous = tile.url;
      tile.img.src = url; tile.url = url; tile.lastFrame = lastFrame;
      tile.playback.frame(performance.now() - receivedAt);
      tile.root.classList.add('has-frame');
      if (previous) URL.revokeObjectURL(previous);
    }).catch(() => URL.revokeObjectURL(url)));
  };
  ws.onerror = () => ws.close();
  ws.onclose = () => { if (socket !== ws) return; socketOnline = false; renderStatus(); setTimeout(() => { if (socket === ws) connectVideo(); }, 2000); };
}

async function action(work) {
  if (busy) return;
  busy = true; renderStatus();
  for (const element of form.elements) element.disabled = true;
  try { await work(); }
  catch (error) {
    const target = dialog.open ? $('form-result') : $('notice');
    target.textContent = error.message || 'Could not reach the server.'; target.classList.add('error');
    if (!dialog.open) setMenu(true);
  }
  finally {
    await refresh(); busy = false; renderStatus();
    for (const element of form.elements) element.disabled = false;
  }
}

function connectionRequest() {
  const value = name => form.elements.namedItem(name);
  return {
    settings: { ...state.settings, host: value('host').value.trim(), rtspPort: Number(value('rtspPort').value),
      username: value('username').value, pathTemplate: value('pathTemplate').value.trim(),
      channelCount: Number(value('channelCount').value), cameraNames: value('cameraNames').value.split(/\r?\n/),
      substream: value('substream').checked, autoConnect: value('autoConnect').checked },
    password: value('clearPassword').checked ? '' : value('password').value || null
  };
}

$('connect').onclick = () => action(() => api('connect'));
$('disconnect').onclick = () => action(() => api('disconnect'));
$('quality').onclick = () => action(() => api('quality'));
$('grid-button').onclick = () => { focus = null; if (state) arrange(); setMenu(false, true); };
$('reset-layout').onclick = () => { cameraLayout.reset(); focus = null; if (state) arrange(); };
$('camera-focus').onchange = event => {
  focus = event.target.value ? Number(event.target.value) : null;
  if (state) arrange();
  setMenu(false, true);
};
$('connection').onclick = () => {
  if (!state) return;
  setMenu(false, true);
  for (const name of ['host', 'rtspPort', 'username', 'pathTemplate', 'channelCount']) form.elements.namedItem(name).value = state.settings[name];
  for (const name of ['substream', 'autoConnect']) form.elements.namedItem(name).checked = state.settings[name];
  form.elements.namedItem('cameraNames').value = state.settings.cameraNames.join('\n');
  form.elements.namedItem('password').value = '';
  form.elements.namedItem('password').placeholder = state.hasPassword ? 'Leave blank to keep current password' : 'Enter recorder password';
  form.elements.namedItem('clearPassword').checked = false;
  $('form-result').textContent = ''; dialog.showModal();
};
for (const id of ['close-dialog', 'cancel']) $(id).onclick = () => dialog.close();
dialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
dialog.addEventListener('close', () => { form.elements.namedItem('password').value = ''; menuToggle.focus(); });
document.querySelectorAll('[data-path]').forEach(button => button.onclick = () => { form.elements.namedItem('pathTemplate').value = button.dataset.path; });
$('test').onclick = () => {
  if (!form.reportValidity()) return;
  const request = connectionRequest();
  action(async () => { $('form-result').textContent = 'Testing camera 1…'; const result = await api('test', request); $('form-result').textContent = result.message + (result.codec ? ` ${result.codec}` : ''); });
};
form.onsubmit = event => {
  event.preventDefault();
  const request = connectionRequest();
  action(async () => { $('form-result').textContent = 'Saving and connecting…'; await api('connection', request); dialog.close(); });
};
async function fullscreen() {
  if (fullscreenPending) return;
  fullscreenPending = true;
  try {
    if (document.fullscreenElement) await document.exitFullscreen();
    else if (/\bAndroid\b/i.test(navigator.userAgent) && tiles.size && !liveRewind?.historical) {
      cameraPictureInPicture ||= new CameraPictureInPictureView(() => [...tiles.values()].filter(tile => !tile.root.hidden),
        () => ({ width: $('cameras').clientWidth, scale: window.devicePixelRatio || 1 }), error => {
          $('notice').textContent = error.message; setMenu(true); updatePictureInPicture();
        });
      const entering = cameraPictureInPicture.enterFullscreen();
      updatePictureInPicture(); await entering;
    } else await document.documentElement.requestFullscreen();
  } catch (error) {
    cameraPictureInPicture?.stop(); updatePictureInPicture();
    $('notice').textContent = 'Use your browser’s fullscreen control on this device.' + (cameraPictureInPicture && error.message ? ` ${error.message}` : ''); setMenu(true);
  } finally { fullscreenPending = false; }
}
$('fullscreen').onclick = () => { setMenu(false, true); fullscreen(); };
function sidewaysView() {
  cameraLayout.sideways = !cameraLayout.sideways;
  const button = $('sideways-view');
  const label = cameraLayout.sideways ? 'Use automatic camera layout' : 'Use sideways camera layout';
  button.setAttribute('aria-pressed', String(cameraLayout.sideways));
  button.setAttribute('aria-label', label); button.title = label;
  if (state) arrange();
  setMenu(false, true);
}
$('sideways-view').onclick = sidewaysView;
$('refresh-app').onclick = () => window.location.reload();
document.addEventListener('keydown', event => {
  if (document.querySelector('dialog[open]')) return;
  if (event.key === 'Escape' && !menu.hidden) { event.preventDefault(); setMenu(false, true); return; }
  if (event.key === 'F11') { event.preventDefault(); fullscreen(); }
  if (event.key === 'Escape' && tiles.get(focus)?.zoom.reset()) { event.preventDefault(); return; }
  if (event.key === 'Escape' && focus !== null) { focus = null; arrange(); }
});
document.addEventListener('fullscreenchange', () => {
  if (cameraPictureInPicture?.video && document.fullscreenElement !== document.documentElement && document.pictureInPictureElement !== cameraPictureInPicture.video) cameraPictureInPicture.stop();
  updatePictureInPicture();
  const active = Boolean(document.fullscreenElement);
  const button = $('fullscreen');
  const label = active ? 'Exit full screen' : 'Full screen';
  button.querySelector('[data-fullscreen-icon="expand"]').toggleAttribute('hidden', active);
  button.querySelector('[data-fullscreen-icon="shrink"]').toggleAttribute('hidden', !active);
  button.setAttribute('aria-label', label);
  button.setAttribute('aria-pressed', String(active));
  button.title = `${label} (F11)`;
});
document.addEventListener('leavepictureinpicture', () => {
  if (cameraPictureInPicture?.video && document.fullscreenElement !== document.documentElement) cameraPictureInPicture.stop();
  updatePictureInPicture();
}, true);
async function poll() { await refresh(); setTimeout(poll, 1000); }
setInterval(renderStatus, 1000);
poll(); connectVideo();
