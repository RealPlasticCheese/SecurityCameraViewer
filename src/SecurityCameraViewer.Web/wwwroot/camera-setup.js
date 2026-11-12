'use strict';
(() => {
  const byId = id => document.getElementById(id);
  const setup = document.createElement('dialog');
  setup.className = 'camera-setup'; setup.setAttribute('aria-labelledby', 'setup-title');
  setup.innerHTML = `
    <div class="dialog-heading"><div><h2 id="setup-title">Cameras &amp; configurations</h2><p class="muted">Mix recorder channels and individual cameras in one collection.</p></div><button type="button" id="setup-close" aria-label="Close camera setup">×</button></div>
    <div class="form-scroll">
      <div class="setup-toolbar"><label>Saved configuration<select id="setup-configurations"></select></label><button id="setup-activate">Activate selected</button><button id="setup-new">New collection</button><button id="setup-copy">Duplicate</button><button id="setup-delete">Delete selected</button></div>
      <p id="setup-active" class="help"></p>
      <label>Configuration name<input id="setup-name" maxlength="80" required></label>
      <div class="setup-toolbar"><label class="check"><input id="setup-auto" type="checkbox">Connect at startup</label><label class="check"><input id="setup-sub" type="checkbox">Use lower-bandwidth streams when available</label></div>
      <details><summary>Shared recorder password</summary><p class="help">Used only by cameras with “Use shared password” selected. Blank keeps the current password. Passwords are saved on the server and restored after restart.</p><input id="setup-password" type="password" autocomplete="new-password" aria-label="Shared recorder password"><label class="check"><input id="setup-clear-password" type="checkbox">Clear shared password</label></details>
      <div id="setup-cameras" class="setup-camera-list"></div>
      <div class="setup-toolbar"><button id="setup-add">Add RTSP camera</button><button id="setup-recorder-open">Add recorder channels</button><button id="setup-onvif-open">Discover / add ONVIF</button></div>
      <details id="setup-recorder"><summary>Recorder preset or custom template</summary>
        <div class="form-row"><label>Recorder address<input id="preset-host" placeholder="192.0.2.10"></label><label>RTSP port<input id="preset-port" type="number" value="554" min="1" max="65535"></label></div>
        <div class="form-row"><label>Username<input id="preset-user" value="admin" autocomplete="off"></label><label>Password<input id="preset-password" type="password" autocomplete="new-password"></label></div>
        <label>Preset<select id="preset-kind"><option value="/ip{channel:00}/{stream}">NVR /ip01/0</option><option value="/ch{channel:00}/{stream}">DVR /ch01/0</option><option value="custom">Custom template</option></select></label>
        <label>Path template<input id="preset-path" value="/ip{channel:00}/{stream}" maxlength="2048"></label>
        <p class="help">{channel}, {channel:00}, {channel0}; {stream} is 0 for main and 1 for substream.</p>
        <div class="form-row"><label>First recorder channel<input id="preset-first" type="number" min="1" max="256" value="1"></label><label>Number of channels<input id="preset-count" type="number" min="1" value="4"></label></div><button id="preset-add">Add channels to draft</button>
      </details>
      <details id="setup-onvif"><summary>ONVIF discovery and profiles</summary>
        <p class="help">Discovery runs on the viewer server. Multicast usually stays on its local subnet. Enter a device service address for another reachable network. Device settings are never changed.</p>
        <div class="setup-toolbar"><label>Server network interface<select id="onvif-interface"><option value="">All IPv4 interfaces</option></select></label><button id="onvif-discover">Discover devices</button></div>
        <div id="onvif-devices"></div>
        <label>ONVIF device service address<input id="onvif-address" type="url" placeholder="http://192.168.1.20/onvif/device_service" maxlength="2048"></label>
        <div class="form-row"><label>ONVIF username<input id="onvif-user" autocomplete="off" maxlength="256"></label><label>ONVIF password<input id="onvif-password" type="password" autocomplete="new-password" maxlength="4096"></label></div>
        <button id="onvif-inspect">Read device and streams</button><button id="onvif-export" disabled>Export this inspection</button>
        <p id="onvif-status" role="status"></p><div id="onvif-profiles"></div>
        <details><summary>Full ONVIF metadata</summary><pre id="onvif-report"></pre></details>
      </details>
      <label>Video timeout (seconds)<input id="setup-video-timeout" type="number" min="0" max="2147483647" step="1" required aria-describedby="setup-video-timeout-help"></label>
      <p id="setup-video-timeout-help" class="help">Return each camera to its default screen after this many seconds without fresh video. For motion-activated cameras, this shows “Waiting for activity”. Set 0 to keep the last picture indefinitely. Applies to all cameras in this configuration.</p>
      <label>Feed retry delays (seconds)<input id="setup-retry-delays" type="text" required aria-describedby="setup-retry-delays-help"></label>
      <button type="button" id="setup-retry-default">Restore default retry delays</button>
      <p id="setup-retry-delays-help" class="help">Comma-separated positive whole seconds between connection attempts for continuous cameras. Applies to native video, browser software decoding and server JPEG previews in this configuration. The last delay repeats indefinitely. Resets after 30 seconds of healthy video. Motion-activated cameras use their availability polling interval while waiting for activity.</p>
      <details><summary>Portable configuration (without passwords)</summary><p class="help">This file contains exact stream URLs, including any vendor tokens in the path or query. Use the redacted metadata export when sharing troubleshooting information. Import creates a new collection with new camera identities.</p><button id="setup-export-config">Export configuration</button><label>Import configuration<input id="setup-import-config" type="file" accept=".json,application/json"></label></details>
    </div>
    <p id="setup-result" role="status"></p><div class="dialog-actions"><button id="setup-save" class="primary">Save and activate</button><button id="setup-metadata">View active camera metadata</button></div>`;
  document.body.append(setup);
  const metadata = document.createElement('dialog'); metadata.className = 'camera-setup'; metadata.setAttribute('aria-labelledby', 'metadata-title');
  metadata.innerHTML = `<div class="dialog-heading"><div><h2 id="metadata-title">Camera metadata &amp; diagnostics</h2><p class="muted">Saved configuration and timestamped connection tests.</p></div><button id="metadata-close" aria-label="Close camera metadata">×</button></div><div class="form-scroll"><p class="help">Exports omit credentials and redact URL query values by default. Camera names, addresses and device identifiers remain. Review before sharing with a human or AI. An RTSP response alone does not prove video is arriving.</p><label class="check"><input id="metadata-passwords" type="checkbox">Include passwords in the JSON</label><p class="help">Only enable this when you need to inspect credentials on a trusted screen. Passwords will be present in the report and downloaded export until this is unchecked.</p><div id="metadata-cameras"></div><details open><summary>Full structured report</summary><pre id="metadata-report"></pre></details></div><p id="metadata-result" role="status"></p><div class="dialog-actions"><button id="metadata-refresh">Refresh metadata</button><a id="metadata-export" href="/api/setup/metadata/export" download="camera-diagnostics.json">Export JSON report</a></div>`;
  document.body.append(metadata);
  let catalog, draft, credentials = {}, inspection, inspectionSavedId = null, pending = false, dirty = false, lastTests = [], savedPasswords;
  let activeCameraId = null;
  const uuid = () => crypto.randomUUID ? crypto.randomUUID().replaceAll('-', '') : [...crypto.getRandomValues(new Uint8Array(16))].map(v => v.toString(16).padStart(2, '0')).join('');
  const button = (label, handler) => { const element = document.createElement('button'); element.type = 'button'; element.textContent = label; element.onclick = handler; return element; };
  const hideMenu = () => { byId('viewer-menu').hidden = true; byId('menu-toggle').setAttribute('aria-expanded', 'false'); };
  async function request(path, body, method = 'POST') {
    const response = await fetch(`/api/setup/${path}`, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(60000) });
    const data = response.headers.get('content-type')?.includes('application/json') ? await response.json() : null;
    if (response.status === 401) document.dispatchEvent(new Event('scv-sign-in-required'));
    if (!response.ok) throw new Error(data?.message || `Request failed (${response.status}).`);
    return data;
  }
  async function work(action, status = 'setup-result') {
    if (pending) return;
    pending = true;
    const controls = [...setup.querySelectorAll('button, input, select'), ...metadata.querySelectorAll('button, input')];
    const disabled = controls.map(control => control.disabled); controls.forEach(control => control.disabled = true);
    byId(status).textContent = 'Working…';
    try { await action(); }
    catch (error) { byId(status).textContent = error.message || 'Could not reach the viewer server.'; }
    finally { pending = false; controls.forEach((control, index) => control.disabled = disabled[index]); byId('onvif-export').disabled = !inspection; }
  }
  function allowDiscard() { return !dirty || window.confirm('Discard the unsaved changes to this configuration?'); }
  function readFields() {
    draft.configurationName = byId('setup-name').value.trim(); draft.autoConnect = byId('setup-auto').checked; draft.substream = byId('setup-sub').checked;
    draft.videoTimeoutSeconds = Number(byId('setup-video-timeout').value);
    draft.retryDelaysSeconds = byId('setup-retry-delays').value.trim();
    draft.channelCount = draft.sources.length;
    return draft;
  }
  function sharedPassword() { return byId('setup-clear-password').checked ? '' : byId('setup-password').value || null; }
  async function configurationPasswords() {
    const id = draft.configurationId;
    if (!catalog.configurations.some(c => c.configurationId === id)) return { sharedPassword: '', cameraPasswords: {} };
    if (!savedPasswords) savedPasswords = request(`configurations/${id}/passwords`, undefined, 'GET');
    try { return await savedPasswords; }
    catch (error) { savedPasswords = null; throw error; }
  }
  const passwordError = error => { byId('setup-result').textContent = error.message || 'Could not load the saved password.'; };
  setup.querySelectorAll('input[type=password]').forEach(input => PasswordVisibility.attach(input, undefined, passwordError));
  byId('setup-password').passwordVisibility.setLoader(async () => byId('setup-clear-password').checked ? '' : (await configurationPasswords()).sharedPassword);
  byId('setup-clear-password').onchange = () => { if (byId('setup-clear-password').checked) { byId('setup-password').value = ''; byId('setup-password').passwordVisibility.reset(); } };
  function loadDraft(value) {
    draft = structuredClone(value); credentials = {}; dirty = false; lastTests = []; savedPasswords = null; activeCameraId = null;
    byId('setup-name').value = draft.configurationName; byId('setup-auto').checked = draft.autoConnect; byId('setup-sub').checked = draft.substream;
    byId('setup-video-timeout').value = draft.videoTimeoutSeconds ?? 0;
    byId('setup-retry-delays').value = draft.retryDelaysSeconds ?? catalog.defaultRetryDelaysSeconds;
    byId('setup-retry-delays').setCustomValidity('');
    byId('setup-password').value = ''; byId('setup-password').passwordVisibility.reset(); byId('setup-clear-password').checked = false;
    byId('setup-active').textContent = `Active: ${catalog.configurations.find(c => c.configurationId === catalog.activeId)?.configurationName || 'None'}. Editing this draft does not change live cameras until saved.`;
    renderCameras();
  }
  async function reload() {
    catalog = await request('configurations', undefined, 'GET');
    byId('setup-configurations').replaceChildren(...catalog.configurations.map(c => new Option(c.configurationName, c.configurationId)));
    byId('setup-configurations').value = catalog.activeId;
    loadDraft(catalog.configurations.find(c => c.configurationId === catalog.activeId));
  }
  function field(parent, text, value, change, type = 'text') {
    const label = document.createElement('label'); label.textContent = text;
    const input = document.createElement('input'); input.type = type; input.value = value ?? ''; input.autocomplete = type === 'password' ? 'new-password' : 'off';
    input.oninput = () => { change(input.value); dirty = true; }; label.append(input); parent.append(label); return input;
  }
  function aspectRatioField(parent, camera, property, text) {
    const label = document.createElement('label'); label.textContent = text;
    const select = document.createElement('select');
    const presets = ['auto', '4:3', '16:9', '1:1', '9:16'], current = camera[property] ?? 'auto';
    select.append(...presets.map(value => new Option(value === 'auto' ? 'Auto' : value, value)), new Option('Custom', 'custom'));
    select.value = presets.includes(current) ? current : 'custom';
    label.append(select); parent.append(label);
    const customLabel = document.createElement('label'); customLabel.textContent = `${text} — custom width:height`;
    const custom = document.createElement('input'); custom.type = 'text'; custom.placeholder = 'e.g. 32:9 or 1.85:1';
    custom.value = current === 'auto' ? '' : current; custom.dataset.aspectRatio = property;
    customLabel.append(custom); parent.append(customLabel);
    const update = (changed = true) => {
      const isCustom = select.value === 'custom'; customLabel.hidden = !isCustom; custom.disabled = !isCustom;
      const value = isCustom ? custom.value.trim() : select.value;
      custom.setCustomValidity(isCustom && CameraZoom.ratio(value) === null ? 'Enter positive width:height numbers, such as 32:9 or 1.85:1.' : '');
      if (changed) { camera[property] = value; dirty = true; }
    };
    select.onchange = () => update(); custom.oninput = () => update(); update(false);
  }
  function selectCamera(cameraId, focus = false) {
    activeCameraId = cameraId;
    const list = byId('setup-cameras');
    list.querySelectorAll('[role=tab]').forEach(tab => {
      const selected = tab.dataset.cameraId === cameraId;
      tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
      if (selected && focus) tab.focus();
    });
    list.querySelectorAll('[role=tabpanel]').forEach(panel => { panel.hidden = panel.dataset.cameraId !== cameraId; });
  }
  function renderCameras(focus = false) {
    const list = byId('setup-cameras');
    list.replaceChildren();
    if (!draft.sources.length) {
      activeCameraId = null;
      const note = document.createElement('p'); note.textContent = 'Add at least one camera before saving this collection.'; list.append(note); return;
    }
    if (!draft.sources.some(camera => camera.id === activeCameraId)) activeCameraId = draft.sources[0].id;
    const tabs = document.createElement('div'); tabs.className = 'setup-camera-tabs';
    tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', 'Cameras'); list.append(tabs);
    for (const [index, camera] of draft.sources.entries()) {
      const tab = button('', () => selectCamera(camera.id)); tab.id = `setup-camera-tab-${camera.id}`; tab.dataset.cameraId = camera.id;
      tab.setAttribute('role', 'tab'); tab.setAttribute('aria-controls', `setup-camera-panel-${camera.id}`);
      tab.onkeydown = event => {
        let nextIndex;
        if (event.key === 'ArrowRight') nextIndex = (index + 1) % draft.sources.length;
        else if (event.key === 'ArrowLeft') nextIndex = (index + draft.sources.length - 1) % draft.sources.length;
        else if (event.key === 'Home') nextIndex = 0;
        else if (event.key === 'End') nextIndex = draft.sources.length - 1;
        else return;
        event.preventDefault(); selectCamera(draft.sources[nextIndex].id, true);
      };
      tabs.append(tab);
      const section = document.createElement('section'); section.className = 'setup-camera-panel'; section.id = `setup-camera-panel-${camera.id}`;
      section.dataset.cameraId = camera.id; section.setAttribute('role', 'tabpanel'); section.setAttribute('aria-labelledby', tab.id);
      const card = document.createElement('div'); const heading = document.createElement('h3');
      const updateLabels = () => {
        const label = `${index + 1}. ${camera.name || 'New camera'}`;
        tab.textContent = label; tab.className = `setup-camera-tab${camera.enabled === false ? ' camera-disabled' : ''}`;
        tab.setAttribute('aria-description', camera.enabled === false ? 'Camera disabled' : 'Camera enabled');
        heading.textContent = `${label} - ${camera.origin || 'RTSP'}`;
      };
      updateLabels(); section.append(heading, card);
      const enabled = document.createElement('label'); enabled.className = 'check'; const enabledInput = document.createElement('input'); enabledInput.type = 'checkbox'; enabledInput.checked = camera.enabled !== false; enabledInput.onchange = () => { camera.enabled = enabledInput.checked; updateLabels(); dirty = true; }; enabled.append(enabledInput, 'Enabled'); card.append(enabled);
      const row = document.createElement('div'); row.className = 'form-row'; card.append(row);
      field(row, 'Camera name', camera.name, value => { camera.name = value; updateLabels(); }, 'text').maxLength = 80;
      field(row, 'RTSP username', camera.username, value => { camera.username = value; }).maxLength = 256;
      field(card, 'Main RTSP URL', camera.mainStream, value => { camera.mainStream = value.trim(); }, 'url').placeholder = 'rtsp://192.168.1.20:554/live/main';
      aspectRatioField(card, camera, 'mainStreamAspectRatio', 'Main stream aspect ratio');
      field(card, 'Substream RTSP URL (optional)', camera.subStream, value => { camera.subStream = value.trim() || null; }, 'url');
      aspectRatioField(card, camera, 'subStreamAspectRatio', 'Substream aspect ratio');
      const aspectHelp = document.createElement('p'); aspectHelp.className = 'help';
      aspectHelp.textContent = 'Auto uses the video’s reported aspect ratio. Choose a preset or Custom to correct a squashed or stretched picture. This changes browser display only.'; card.append(aspectHelp);
      const playbackLabel = document.createElement('label'); playbackLabel.textContent = 'Live picture playback';
      const playback = document.createElement('select'); playback.dataset.playbackChannel = String(camera.channel);
      playback.setAttribute('aria-describedby', `playback-help-${camera.id}`);
      playback.add(new Option('Automatic (recommended)', 'automatic'));
      playback.add(new Option('Native video — lowest server CPU', 'native'));
      playback.add(new Option('Browser software decoding — Warning: extra client CPU', 'browser'));
      playback.add(new Option('Server JPEG previews — Warning: extra server CPU', 'server'));
      playback.value = camera.playbackMethod || 'automatic';
      playback.onchange = () => { camera.playbackMethod = playback.value; dirty = true; updatePlaybackHelp(playback, playbackHelp); };
      playbackLabel.append(playback); card.append(playbackLabel);
      const playbackHelp = document.createElement('p'); playbackHelp.className = 'help camera-playback-help';
      playbackHelp.id = `playback-help-${camera.id}`; card.append(playbackHelp);
      updatePlaybackHelp(playback, playbackHelp);
      card.append(button('Export playback benchmark', () => {
        const report = CameraPlayback.reports.get(camera.channel);
        if (!report || catalog.activeId !== draft.configurationId) { byId('setup-result').textContent = 'Activate and view this camera before exporting playback measurements.'; return; }
        download('camera-playback-benchmark.json', JSON.stringify({ capturedAt: new Date().toISOString(), browser: navigator.userAgent,
          secureContext: window.isSecureContext, report }, null, 2));
      }));
      const modeLabel = document.createElement('label'); modeLabel.textContent = 'Camera mode';
      const mode = document.createElement('select');
      mode.add(new Option('Continuous', 'continuous')); mode.add(new Option('Motion-activated / battery camera', 'battery'));
      mode.value = camera.motionActivated ? 'battery' : 'continuous'; modeLabel.append(mode); card.append(modeLabel);
      const interval = field(card, 'Poll interval (seconds, 0.1–3600)', camera.pollIntervalSeconds ?? 1,
        value => { camera.pollIntervalSeconds = Number(value); }, 'number');
      interval.min = '0.1'; interval.max = '3600'; interval.step = '0.1'; interval.disabled = !camera.motionActivated;
      mode.onchange = () => { camera.motionActivated = mode.value === 'battery'; interval.disabled = !camera.motionActivated; dirty = true; };
      const batteryHelp = document.createElement('p'); batteryHelp.className = 'help';
      batteryHelp.textContent = 'Enter seconds with up to one decimal place: 0.1 = 100 ms, 0.5 = 500 ms, 1.6 = 1600 ms. Battery mode checks the main stream locally and waits quietly between events. Short intervals catch more of each event. Recording available video is optional in Recording settings. Check now tests availability; this viewer has no local wake command for Eufy HomeBase 2.';
      card.append(batteryHelp);
      const initialCredentials = credentials[camera.id];
      const password = field(card, 'Camera password (blank keeps current; use Clear for an empty password)', initialCredentials?.password ?? '', value => {
        if (value) credentials[camera.id] = { ...credentials[camera.id], password: value };
        else if (initialCredentials) credentials[camera.id] = initialCredentials;
        else delete credentials[camera.id];
      }, 'password'); password.maxLength = 4096;
      PasswordVisibility.attach(password, async () => {
        if (credentials[camera.id]?.password !== undefined) return credentials[camera.id].password;
        const saved = await configurationPasswords();
        return camera.useDefaultPassword ? sharedPassword() ?? saved.sharedPassword : saved.cameraPasswords[camera.id] ?? '';
      }, passwordError);
      const shared = document.createElement('label'); shared.className = 'check'; const check = document.createElement('input'); check.type = 'checkbox'; check.checked = !!camera.useDefaultPassword;
      check.onchange = () => { camera.useDefaultPassword = check.checked; if (!credentials[camera.id]) { password.value = ''; password.passwordVisibility.reset(); } dirty = true; }; shared.append(check, 'Use shared recorder password'); card.append(shared);
      const controls = document.createElement('div'); controls.className = 'setup-toolbar'; card.append(controls);
      controls.append(button('Clear camera password', () => { credentials[camera.id] = { ...credentials[camera.id], password: '' }; password.value = ''; password.passwordVisibility.reset(); camera.useDefaultPassword = false; check.checked = false; dirty = true; }));
      controls.append(button('Test main video', () => testDraft(camera, false)), button('Test substream', () => testDraft(camera, true)));
      const move = delta => { if (index + delta < 0 || index + delta >= draft.sources.length) return; [draft.sources[index], draft.sources[index + delta]] = [draft.sources[index + delta], draft.sources[index]]; dirty = true; renderCameras(true); };
      const moveLeft = button('Move left', () => move(-1)), moveRight = button('Move right', () => move(1));
      moveLeft.disabled = index === 0; moveRight.disabled = index === draft.sources.length - 1;
      controls.append(moveLeft, moveRight, button('Remove', () => {
        if (!window.confirm(`Remove ${camera.name} from this draft? Its recording rules will be removed when saved.`)) return;
        draft.sources.splice(index, 1); delete credentials[camera.id];
        if (activeCameraId === camera.id) activeCameraId = draft.sources[Math.min(index, draft.sources.length - 1)]?.id ?? null;
        dirty = true; renderCameras(true);
        if (!draft.sources.length) byId('setup-add').focus();
      }));
      if (camera.onvifAddress) {
        const details = document.createElement('details'); const summary = document.createElement('summary'); summary.textContent = 'ONVIF source and profile selection'; details.append(summary);
        field(details, 'Device service', camera.onvifAddress, value => { camera.onvifAddress = value; }, 'url');
        const info = document.createElement('p'); info.textContent = `Main profile: ${camera.onvifProfileToken || 'unknown'} · Sub profile: ${camera.onvifSubProfileToken || 'none'} · Video source: ${camera.videoSourceToken || 'not supplied'}`; details.append(info);
        details.append(button('Inspect / refresh ONVIF profiles', () => { byId('setup-onvif').open = true; byId('onvif-address').value = camera.onvifAddress; byId('onvif-user').value = camera.username; byId('onvif-status').textContent = 'Enter the ONVIF password, then read streams. Choose “Update existing camera” to refresh its stream addresses while retaining its identity.'; byId('setup-onvif').scrollIntoView(); }));
        details.append(button('Refresh using saved ONVIF credentials', () => work(async () => {
          inspection = await request(`onvif/${camera.id}`); inspectionSavedId = camera.id;
          byId('setup-onvif').open = true; byId('onvif-address').value = camera.onvifAddress;
          byId('onvif-report').textContent = JSON.stringify(inspection, null, 2); renderProfiles();
          byId('onvif-status').textContent = inspection.warnings.join(' ') || 'Profiles refreshed using credentials held by the server. Apply profiles to keep this camera identity.';
          byId('setup-result').textContent = 'Select refreshed profiles in the ONVIF panel below.'; byId('setup-onvif').scrollIntoView();
        })));
        card.append(details);
      }
      list.append(section);
    }
    selectCamera(activeCameraId, focus);
  }
  function updatePlaybackHelp(select, help) {
    const active = catalog?.activeId === draft?.configurationId ? CameraPlayback.reports.get(Number(select.dataset.playbackChannel)) : null;
    const available = CameraPlayback.capabilities();
    const failed = new Set(active?.failedMethods || []);
    select.options[1].textContent = `Native video — lowest server CPU${failed.has('native') || active?.nativeSupported === false || !available.native ? ' (unavailable for this browser/stream)' : active?.nativeSupported === true ? ' (available for this stream)' : ' (codec checked on connection)'}`;
    select.options[2].textContent = `Browser software decoding — Warning: extra client CPU${failed.has('browser') ? ' (decoder failed for this stream)' : available.browser ? ' (H.264/H.265 available)' : ' (unavailable in this browser)'}`;
    const selected = select.value;
    const choice = PlaybackPolicy.choose(selected, { ...available, native: available.native && active?.nativeSupported !== false,
      reason: active?.message }, failed);
    help.textContent = (active ? `${active.confirmed ? 'Currently using' : 'Checking'}: ${active.message} ` : 'No active playback on this device. ') +
      (selected === 'automatic' ? `On this device: ${choice.message} Automatic tries native video, then browser software decoding, then server JPEG previews. The tile shows the method in use. ` : choice.message + ' ') +
      'Availability is specific to this browser and device. The setting applies to live grid pictures; recording, detection, audio and rewind keep their existing pipelines. ' +
      (active ? `Displayed ${active.framesPerSecond.toFixed(1)} fps; ${active.dropped} frames skipped. ` : '') +
      'Save and activate to apply changes.';
  }
  document.addEventListener('scv-playback-method', event => {
    if (!setup.open) return;
    const select = setup.querySelector(`[data-playback-channel="${event.detail.channel}"]`);
    if (select) updatePlaybackHelp(select, document.getElementById(select.getAttribute('aria-describedby')));
  });
  function addSource(values, secret) {
    const source = { id: uuid(), channel: CameraCollection.nextChannel(draft.sources), name: 'Camera', mainStream: '', subStream: null, username: '', origin: 'RTSP', ...values };
    draft.sources.push(source); if (secret) credentials[source.id] = secret; activeCameraId = source.id; dirty = true; renderCameras(true); return source;
  }
  async function testDraft(camera, substream) {
    await work(async () => {
      const result = await request('preview', { settings: { ...readFields(), sources: [camera], substream }, password: sharedPassword(), cameraCredentials: credentials });
      lastTests.push(result); byId('setup-result').textContent = `${camera.name}: ${result.rtsp.message} ${result.playbackMessage}`;
    });
  }
  byId('connection').onclick = () => { hideMenu(); setup.showModal(); work(async () => { await reload(); byId('setup-result').textContent = ''; const addresses = await request('interfaces', undefined, 'GET'); byId('onvif-interface').replaceChildren(new Option('All IPv4 interfaces', ''), ...addresses.map(a => new Option(a, a))); }); };
  byId('setup-close').onclick = () => { if (!pending && allowDiscard()) setup.close(); };
  setup.addEventListener('cancel', event => { if (pending || !allowDiscard()) event.preventDefault(); });
  setup.addEventListener('close', () => { credentials = {}; inspection = null; savedPasswords = null; setup.querySelectorAll('input[data-secret="true"]').forEach(input => { input.value = ''; input.passwordVisibility.reset(); }); byId('onvif-report').textContent = ''; byId('onvif-profiles').replaceChildren(); byId('menu-toggle').focus(); });
  for (const id of ['setup-name', 'setup-auto', 'setup-sub', 'setup-video-timeout', 'setup-retry-delays', 'setup-password', 'setup-clear-password']) byId(id).addEventListener('input', () => { dirty = true; });
  byId('setup-retry-delays').addEventListener('input', () => byId('setup-retry-delays').setCustomValidity(''));
  byId('setup-retry-default').onclick = () => {
    byId('setup-retry-delays').value = catalog.defaultRetryDelaysSeconds;
    byId('setup-retry-delays').setCustomValidity('');
    dirty = true;
  };
  byId('setup-configurations').onchange = event => { if (!allowDiscard()) { event.target.value = draft.configurationId; return; } loadDraft(catalog.configurations.find(c => c.configurationId === event.target.value)); };
  byId('setup-new').onclick = () => { if (!allowDiscard()) return; loadDraft({ ...draft, configurationId: uuid(), configurationName: 'New collection', sources: [], channelCount: 0 }); dirty = true; };
  byId('setup-copy').onclick = () => { readFields(); const copy = { ...draft, configurationId: uuid(), configurationName: `${draft.configurationName} copy`.slice(0, 80), sources: draft.sources.map(c => ({ ...c, id: uuid(), useDefaultPassword: false })) }; loadDraft(copy); dirty = true; byId('setup-result').textContent = 'Duplicate created as a draft. Supply passwords for its cameras. Recording rules are configured separately.'; };
  byId('setup-delete').onclick = () => { if (draft.configurationId === catalog.activeId) { byId('setup-result').textContent = 'Activate another collection before deleting this one.'; return; } if (!window.confirm('Delete this saved configuration? Its recording files are preserved.')) return; work(async () => { await request(`configurations/${draft.configurationId}`, undefined, 'DELETE'); await reload(); byId('setup-result').textContent = 'Configuration deleted.'; }); };
  byId('setup-activate').onclick = () => { if (!allowDiscard()) return; work(async () => { await request(`configurations/${byId('setup-configurations').value}/activate`); await reload(); byId('setup-result').textContent = 'Configuration activated.'; }); };
  byId('setup-save').onclick = () => {
    for (const input of setup.querySelectorAll('input[data-aspect-ratio]')) {
      if (!input.disabled && !input.checkValidity()) { selectCamera(input.closest('[role=tabpanel]').dataset.cameraId); input.reportValidity(); return; }
    }
    if (!byId('setup-video-timeout').reportValidity()) return;
    const retryInput = byId('setup-retry-delays');
    const valid = retryInput.value.split(',').every(entry => /^[0-9]+$/.test(entry.trim()) && Number(entry) > 0 && Number(entry) <= 2147483647);
    retryInput.setCustomValidity(valid ? '' : 'Enter comma-separated positive whole seconds (each at most 2147483647).');
    if (!retryInput.reportValidity()) return;
    return work(async () => { if (!draft.sources.length) throw new Error('Add at least one camera.'); await request('configurations', { settings: readFields(), password: sharedPassword(), cameraCredentials: credentials }); await reload(); byId('setup-result').textContent = 'Saved and activated. Cameras reconnect independently; recording rules belong to this collection.'; });
  };
  byId('setup-add').onclick = () => { try { addSource({ name: `Camera ${draft.sources.length + 1}` }); } catch (error) { byId('setup-result').textContent = error.message; } };
  byId('setup-recorder-open').onclick = () => { byId('setup-recorder').open = true; byId('setup-recorder').scrollIntoView(); };
  byId('setup-onvif-open').onclick = () => { byId('setup-onvif').open = true; byId('setup-onvif').scrollIntoView(); };
  byId('preset-kind').onchange = event => { if (event.target.value !== 'custom') byId('preset-path').value = event.target.value; };
  byId('preset-add').onclick = () => {
    try {
      const first = Number(byId('preset-first').value), count = Number(byId('preset-count').value), port = Number(byId('preset-port').value), host = byId('preset-host').value.trim(), template = byId('preset-path').value.trim();
      if (!Number.isInteger(first) || first < 1 || first > 256 || !Number.isSafeInteger(count) || count < 1 || !Number.isInteger(port) || port < 1 || port > 65535 || !host || /[\s/@?#]/.test(host) || !template.startsWith('/')) throw new Error('Check recorder address, port, channel range and path.');
      const uri = (channel, stream) => { const path = template.replaceAll('{channel:00}', String(channel).padStart(2, '0')).replaceAll('{channel}', channel).replaceAll('{channel0}', channel - 1).replaceAll('{stream}', stream); if (/[{}\s]/.test(path)) throw new Error('Unsupported template placeholder or whitespace.'); return new URL(`rtsp://${host}:${port}${path}`).href; };
      const entries = Array.from({ length: count }, (_, i) => ({ name: `Camera ${first + i}`, mainStream: uri(first + i, 0), subStream: template.includes('{stream}') ? uri(first + i, 1) : null, username: byId('preset-user').value, origin: 'Recorder template' }));
      for (const entry of entries) addSource(entry, { password: byId('preset-password').value });
      byId('preset-password').value = ''; byId('setup-result').textContent = `${count} recorder channels added to the draft.`;
    } catch (error) { byId('setup-result').textContent = error.message; }
  };
  byId('onvif-discover').onclick = () => work(async () => {
    const devices = await request('discover', { interfaceAddress: byId('onvif-interface').value || null });
    byId('onvif-devices').replaceChildren();
    for (const device of devices) for (const address of device.addresses) byId('onvif-devices').append(button(`${address} · ${device.id}`, () => { byId('onvif-address').value = address; }));
    byId('onvif-status').textContent = `${devices.length} device(s) found. ${devices.length ? 'Select an address and enter ONVIF credentials.' : 'Check the interface, firewall and ONVIF enablement, or enter an address manually.'}`;
  }, 'onvif-status');
  byId('onvif-inspect').onclick = () => work(async () => {
    inspectionSavedId = null;
    inspection = await request('onvif', { address: byId('onvif-address').value.trim(), username: byId('onvif-user').value, password: byId('onvif-password').value });
    byId('onvif-report').textContent = JSON.stringify(inspection, null, 2);
    byId('onvif-status').textContent = `${inspection.manufacturer || 'Unknown manufacturer'} ${inspection.model || ''} · ${inspection.profiles.length} profiles. ${inspection.warnings.join(' ')}`;
    renderProfiles();
  }, 'onvif-status');
  function renderProfiles() {
    const target = byId('onvif-profiles'); target.replaceChildren();
    const profiles = inspection.profiles.filter(p => p.streamUri);
    if (!profiles.length) return;
    const description = p => `${p.name} · ${p.codec || '?'} ${p.width || '?'}×${p.height || '?'} · source ${p.videoSourceToken || 'unknown'} · ${p.service.includes('ver20') ? 'Media2' : 'Media'}`;
    const label = (text, control) => { const element = document.createElement('label'); element.append(text, control); target.append(element); };
    const main = document.createElement('select'); main.append(...profiles.map((p, i) => new Option(description(p), i))); label('Main profile', main);
    const sub = document.createElement('select'); label('Optional substream from the same video source', sub);
    const update = () => { const selected = profiles[Number(main.value)]; sub.replaceChildren(new Option('Use main stream only', ''), ...profiles.map((p, i) => ({ p, i })).filter(({ p, i }) => i !== Number(main.value) && p.videoSourceToken && p.videoSourceToken === selected.videoSourceToken).map(({ p, i }) => new Option(description(p), i))); };
    main.onchange = update; update();
    const existing = document.createElement('select'); existing.append(...(inspectionSavedId ? [] : [new Option('Add a new camera', '')]), ...draft.sources.filter(c => c.onvifAddress === inspection.address).map(c => new Option(`Update ${c.name}`, c.id))); label('Destination', existing); if (inspectionSavedId) existing.value = inspectionSavedId;
    const name = document.createElement('input'); name.value = inspection.model || 'ONVIF camera'; name.maxLength = 80; label('Camera name', name);
    target.append(button('Apply selected profiles to draft', () => {
      try {
        const primary = profiles[Number(main.value)], secondary = sub.value === '' ? null : profiles[Number(sub.value)];
        const values = { name: name.value || primary.name, mainStream: primary.streamUri, subStream: secondary?.streamUri || null, username: byId('onvif-user').value, useDefaultPassword: false, origin: 'ONVIF', onvifAddress: inspection.address, onvifProfileToken: primary.token, onvifSubProfileToken: secondary?.token || null, videoSourceToken: primary.videoSourceToken, device: inspection };
        const secret = { password: byId('onvif-password').value, onvifUsername: byId('onvif-user').value, onvifPassword: byId('onvif-password').value };
        if (existing.value) { const camera = draft.sources.find(c => c.id === existing.value); if (inspectionSavedId) { values.username = camera.username; values.useDefaultPassword = camera.useDefaultPassword; values.name = camera.name; } Object.assign(camera, values); if (!inspectionSavedId) credentials[camera.id] = secret; dirty = true; renderCameras(); }
        else {
          const duplicate = draft.sources.some(c => c.mainStream === primary.streamUri || c.onvifAddress === inspection.address && c.videoSourceToken && c.videoSourceToken === primary.videoSourceToken);
          if (duplicate) throw new Error('This camera is already in the draft. Select its existing entry to update it. Alternate profiles are usually quality variants, not extra cameras.');
          addSource(values, secret);
        }
        byId('setup-result').textContent = 'Profiles applied to the draft. Test video, then save. If RTSP uses different credentials, edit them in the camera card.';
      } catch (error) { byId('setup-result').textContent = error.message; }
    }));
  }
  function download(name, text) { const link = document.createElement('a'); const url = URL.createObjectURL(new Blob([text], { type: 'application/json' })); link.href = url; link.download = name; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
  byId('onvif-export').onclick = () => work(async () => { const redacted = await request('export', { schemaVersion: 1, exportedAt: new Date().toISOString(), onvif: inspection, tests: lastTests }); download('onvif-inspection.json', JSON.stringify(redacted, null, 2)); byId('onvif-status').textContent = 'Redacted inspection exported.'; }, 'onvif-status');
  byId('setup-export-config').onclick = () => download('camera-configuration.json', JSON.stringify(readFields(), null, 2));
  byId('setup-import-config').onchange = async event => {
    try { const file = event.target.files[0]; if (!file || !allowDiscard()) return; if (file.size > 1024 * 1024) throw new Error('Choose a configuration smaller than 1 MB.'); const imported = JSON.parse(await file.text()); if (!Array.isArray(imported.sources) || imported.sources.length < 1) throw new Error('Expected a configuration containing at least one camera source.'); imported.configurationId = uuid(); imported.sources = imported.sources.map(c => ({ ...c, id: uuid(), useDefaultPassword: false })); delete imported.credentials; loadDraft(imported); dirty = true; byId('setup-result').textContent = 'Imported as a new draft. Supply passwords, test cameras and save.'; } catch (error) { byId('setup-result').textContent = error.message; } finally { event.target.value = ''; }
  };
  async function refreshMetadata() {
    const includePasswords = byId('metadata-passwords').checked; const report = await request(`metadata?includePasswords=${includePasswords}`, undefined, 'GET'); byId('metadata-report').textContent = JSON.stringify(report, null, 2); byId('metadata-export').href = `/api/setup/metadata/export?includePasswords=${includePasswords}`; byId('metadata-cameras').replaceChildren();
    for (const { source: camera, number } of CameraCollection.entries(report.settings)) {
      const card = document.createElement('fieldset'); const legend = document.createElement('legend'); legend.textContent = `${number}. ${camera.name}`; card.append(legend);
      const detail = document.createElement('p'); const live = report.live.find(c => c.channel === camera.channel); detail.textContent = `${live?.live ? 'Live' : live?.message || 'Disconnected'} · ${camera.origin} · ${camera.mainStream}`; card.append(detail);
      const onvif = document.createElement('p'); onvif.textContent = camera.device ? `${camera.device.manufacturer || ''} ${camera.device.model || ''} · firmware ${camera.device.firmware || '?'} · inspected ${camera.device.inspectedAt}` : 'No ONVIF metadata saved.'; card.append(onvif);
      if (camera.enabled !== false) for (const [name, substream] of [['Test main', false], ['Test substream', true]]) card.append(button(name, () => work(async () => { const result = await request('test', { channel: camera.channel, substream }); await refreshMetadata(); byId('metadata-result').textContent = `${camera.name}: ${result.rtsp.message} ${result.playbackMessage}`; }, 'metadata-result'))); else { const disabled = document.createElement('p'); disabled.className = 'help'; disabled.textContent = 'Disabled in the camera viewer.'; card.append(disabled); }
      byId('metadata-cameras').append(card);
    }
  }
  const showMetadata = () => { hideMenu(); metadata.showModal(); work(async () => { await refreshMetadata(); byId('metadata-result').textContent = ''; }, 'metadata-result'); };
  byId('camera-metadata').onclick = showMetadata; byId('setup-metadata').onclick = showMetadata;
  byId('metadata-close').onclick = () => { if (!pending) metadata.close(); };
  metadata.addEventListener('cancel', event => { if (pending) event.preventDefault(); });
  byId('metadata-refresh').onclick = () => work(async () => { await refreshMetadata(); byId('metadata-result').textContent = 'Metadata refreshed. Run a test to update stream diagnostics.'; }, 'metadata-result');
  byId('metadata-passwords').onchange = () => work(async () => { await refreshMetadata(); byId('metadata-result').textContent = byId('metadata-passwords').checked ? 'Passwords are included in this report.' : 'Passwords were removed from this report.'; }, 'metadata-result');
})();
