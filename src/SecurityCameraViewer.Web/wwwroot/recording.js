'use strict';
(() => {
  const el = id => document.getElementById(id);
  const settingsDialog = el('recording-dialog'), libraryDialog = el('recordings-dialog'), form = el('recording-form');
  const canvas = el('zone-canvas'), ctx = canvas.getContext('2d');
  const fields = ['directory', 'timeZoneId', 'preCaptureSeconds', 'postCaptureSeconds', 'maximumEventSeconds', 'bufferMegabytesPerCamera', 'retentionDays', 'maximumStorageGigabytes'];
  let latest, draft, viewer, currentChannel = 1, picture = null, points = [], drag = null, cursor = null;
  let saving = false, libraryRequestVersion = 0, lastEventTime = null, previewVersion = 0;
  const libraryFilters = new RecordingFilters(request, () => loadEvents(false), error => { el('recordings-result').textContent = errorText(error); });
  const archivePlayer = new RecordingArchivePlayer(request);
  const cameraDraft = () => draft.cameras.find(c => c.channel === currentChannel);
  const templateDialog = new DetectionTemplateSettingsDialog(request, settings => {
    if (settingsDialog.open && draft.configurationId === settings.configurationId) {
      draft.detectionTemplates = structuredClone(settings.detectionTemplates);
      if (currentChannel !== null) { renderTemplateAssignment(); renderZones(); }
    }
    refreshStatus();
  });
  const button = (text, click) => { const b = document.createElement('button'); b.type = 'button'; b.textContent = text; b.onclick = click; return b; };
  async function request(path, body, method = 'GET') {
    const response = await fetch(`/api/${path}`, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(180000) });
    const data = response.headers.get('content-type')?.includes('application/json') ? await response.json() : null;
    if (response.status === 401) document.dispatchEvent(new Event('scv-sign-in-required'));
    if (!response.ok) throw new Error(data?.message || `Request failed (${response.status}).`);
    return data;
  }
  const errorText = error => error.message || 'Could not reach the server.';
  const channels = (includeDisabled = false) => CameraCollection.entries(viewer.settings, includeDisabled).map(entry => entry.source.channel);
  const cameraName = channel => CameraCollection.entries(viewer.settings).find(entry => entry.source.channel === channel)?.source.name || `Camera ${channel}`;
  const hideMenu = () => { el('viewer-menu').hidden = true; el('menu-toggle').setAttribute('aria-expanded', 'false'); };
  function populateCameras(select, all) {
    select.replaceChildren(); if (all) select.add(new Option('All cameras', ''));
    const names = new Map();
    for (const i of channels(all)) names.set(i, cameraName(i));
    if (all) for (const camera of latest?.recordedCameras || []) if (!names.has(camera.channel)) names.set(camera.channel, camera.name);
    for (const [channel, name] of names) select.add(new Option(CameraCollection.label(viewer.settings, channel, name), String(channel)));
  }
  function updateCameraTabs() {
    const panel = el('recording-camera-panel');
    panel.hidden = currentChannel === null;
    el('recording-camera-settings').disabled = panel.hidden;
    el('recording-camera-tabs').hidden = panel.hidden;
    el('recording-no-cameras').hidden = !panel.hidden;
    for (const tab of el('recording-camera-tabs').children) {
      const selected = Number(tab.dataset.channel) === currentChannel;
      tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
      if (selected) panel.setAttribute('aria-labelledby', tab.id);
    }
  }
  function renderCameraTabs() {
    const tabs = el('recording-camera-tabs'); tabs.replaceChildren();
    const available = channels();
    const selectCamera = (channel, focus = false) => {
      if (saving) return;
      if (currentChannel !== channel) { saveCamera(); currentChannel = channel; updateCameraTabs(); loadCamera(); }
      if (focus) el(`recording-camera-tab-${channel}`).focus();
    };
    for (const [index, channel] of available.entries()) {
      const tab = button(CameraCollection.label(viewer.settings, channel), () => selectCamera(channel));
      tab.id = `recording-camera-tab-${channel}`; tab.dataset.channel = String(channel);
      tab.setAttribute('role', 'tab'); tab.setAttribute('aria-controls', 'recording-camera-panel');
      tab.onkeydown = event => {
        let nextIndex;
        if (event.key === 'ArrowRight') nextIndex = (index + 1) % available.length;
        else if (event.key === 'ArrowLeft') nextIndex = (index + available.length - 1) % available.length;
        else if (event.key === 'Home') nextIndex = 0;
        else if (event.key === 'End') nextIndex = available.length - 1;
        else return;
        event.preventDefault(); selectCamera(available[nextIndex], true);
      };
      tabs.append(tab);
    }
    updateCameraTabs();
  }
  document.querySelectorAll('[data-close]').forEach(b => b.onclick = () => { if (!saving) el(b.dataset.close).close(); });
  settingsDialog.addEventListener('cancel', event => { if (saving) event.preventDefault(); });
  let motionHelpLoaded = false;
  el('motion-help').onclick = async () => {
    el('motion-help-dialog').showModal();
    if (motionHelpLoaded) return;
    try {
      const response = await fetch('motion-guide.html');
      if (!response.ok) throw new Error('Guide unavailable');
      const page = new DOMParser().parseFromString(await response.text(), 'text/html');
      const article = page.querySelector('article');
      if (!article) throw new Error('Guide unavailable');
      el('motion-help-content').replaceChildren(document.importNode(article, true));
      motionHelpLoaded = true;
    } catch { el('motion-help-content').firstElementChild.textContent = 'Could not load the guide. Try the separate-tab link below.'; }
  };
  el('motion-help-dialog').addEventListener('close', () => el('motion-help').focus());
  for (const dialog of [settingsDialog, libraryDialog]) dialog.addEventListener('close', () => el('menu-toggle').focus());

  async function refreshStatus() {
    try {
      latest = await request('recording');
      const active = latest.cameras.filter(c => c.status === 'recording').length;
      const retries = latest.cameras.filter(c => ['retrying', 'analysis-error'].includes(c.status)).length;
      el('recording-status').textContent = (latest.settings.enabled ? `${active} recording · ${latest.cameras.length} enabled cameras${retries ? ` · ${retries} need attention` : ''}` : 'Recording and motion actions are disabled.') + (latest.notice ? ` ${latest.notice}` : '');
      for (const tile of document.querySelectorAll('.tile')) {
        const camera = latest.cameras.find(c => c.channel === Number(tile.dataset.channel));
        const badge = tile.querySelector('.recording-badge');
        if (badge) { badge.textContent = camera?.status === 'recording' ? '● REC' : camera?.motion ? 'MOTION' : ['retrying', 'analysis-error'].includes(camera?.status) ? 'REC ERROR' : ''; badge.title = camera?.message || ''; }
      }
      if (settingsDialog.open) {
        updateStorageStatus();
        const camera = latest.cameras.find(c => c.channel === currentChannel);
        el('motion-meter').textContent = camera && latest.settings.cameras.find(c => c.channel === currentChannel)?.recordWhenStreaming ? camera.message : camera ? `${camera.message} · changed area ${camera.changedPercent.toFixed(1)}%${camera.ai ? ` · ${camera.ai.model} / ${camera.ai.device}: ${camera.ai.message} (${camera.ai.milliseconds.toFixed(0)} ms)` : ''} · RAM video ${camera.bufferSeconds.toFixed(1)}s / ${(camera.bufferBytes / 1048576).toFixed(1)} MB. Uses saved settings.` : 'This camera is not armed. Save and enable recording to see detection status.';
      }
      if (settingsDialog.open) {
        const diagnostics = latest.cameras.find(camera => camera.channel === currentChannel)?.adaptiveDiagnostics || [];
        const reasons = { inactive: 'No active detection area', warmup: 'Warming up', 'lighting-change': 'Recovering from lighting change',
          confirmed: 'Motion confirmed', 'region-persistence': 'Waiting for region persistence', 'below-area': 'Below moving-area threshold', debouncing: 'Waiting for motion confirmation' };
        for (const diagnostic of diagnostics) el('motion-meter').textContent += ` · ${diagnostic.templateName || 'Adaptive'}: ${reasons[diagnostic.reason] || diagnostic.reason}, ${diagnostic.changedPercent.toFixed(2)}% / ${diagnostic.minimumAreaPercent}% threshold, ${diagnostic.debounceFrames}/${diagnostic.requiredFrames} frames`;
      }
      if (libraryDialog.open) {
        const activeDirectory = latest.storageLocations?.find(location => location.active)?.directory;
        const limit = latest.settings.mirrors?.find(location => location.directory === activeDirectory)?.maximumStorageGigabytes ?? latest.settings.maximumStorageGigabytes;
        el('recordings-storage').textContent = `${latest.eventCount} recordings · ${(latest.usedBytes / 1e9).toFixed(2)} GB stored · ${limit} GB limit`;
      }
    } catch { el('recording-status').textContent = 'Recording status unavailable.'; }
  }
  async function poll() { await refreshStatus(); setTimeout(poll, 2000); }
  poll();

  el('recording-settings').onclick = async () => {
    hideMenu();
    try {
      [latest, viewer] = await Promise.all([request('recording'), request('state')]);
      draft = structuredClone(latest.settings);
      draft.mirrors ??= [];
      draft.actions ??= [{ id: 'record', name: 'Record', type: 'record', enabled: true }];
      for (const i of channels()) if (!draft.cameras.some(c => c.channel === i)) draft.cameras.push({ channel: i, enabled: true, detectionSubstream: true, sensitivity: 60, minimumAreaPercent: 1, zones: [] });
      for (const camera of draft.cameras) camera.defaultActionIds ??= ['record'];
      form.elements.namedItem('enabled').checked = draft.enabled;
      for (const field of fields) form.elements.namedItem(field).value = draft[field];
      form.elements.namedItem('directory').oninput = () => {
        draft.registerPrimaryReplacement = false;
        updateStorageStatus();
      };
      currentChannel = channels()[0] ?? null; renderCameraTabs();
      points = []; drag = null; cursor = null; picture = null; previewVersion++;
      el('detector-name').textContent = `Detector: ${latest.detector}`;
      el('detector-name').hidden = currentChannel === null;
      el('recording-result').textContent = '';
      settingsDialog.showModal(); renderStorageRows(); renderActions(); if (currentChannel !== null) loadCamera();
    } catch (error) { el('recording-status').textContent = errorText(error); el('viewer-menu').hidden = false; }
  };
  function renderStorageRows() {
    const list = el('recording-mirror-list');
    list.replaceChildren();
    for (const mirror of draft.mirrors) {
      const row = document.createElement('div');
      const fields = document.createElement('div'); fields.className = 'form-row';
      const path = document.createElement('input'); path.required = true; path.value = mirror.directory;
      const quota = document.createElement('input'); quota.type = 'number'; quota.required = true;
      quota.min = '0.000000001'; quota.step = 'any'; quota.value = mirror.maximumStorageGigabytes;
      const pathLabel = document.createElement('label'); pathLabel.append('Mirror folder on the server', path);
      const quotaLabel = document.createElement('label'); quotaLabel.append('Maximum storage (GB)', quota);
      fields.append(pathLabel, quotaLabel);
      const actions = document.createElement('div'); actions.className = 'form-row';
      const status = document.createElement('p'); status.className = 'help';
      const controls = document.createElement('div');
      actions.append(status, controls);
      row.append(fields, actions); list.append(row);
      path.oninput = () => { mirror.directory = path.value.trim(); mirror.registerReplacement = false; updateStorageStatus(); };
      quota.oninput = () => { mirror.maximumStorageGigabytes = Number(quota.value); };
    }
    updateStorageStatus();
  }
  function updateStorageStatus() {
    const locations = latest.storageLocations ?? [];
    const descriptions = { ready: 'Up to date', 'catching-up': 'Catching up', checking: 'Checking', offline: 'Offline',
      'wrong-folder': 'Wrong folder', stalled: 'Drive stopped responding', full: 'Full', error: 'Storage error' };
    const primaryPath = form.elements.namedItem('directory').value.trim();
    updateRow(primaryPath, el('recording-primary-status'), el('recording-primary-actions'), null);
    for (const [index, row] of [...el('recording-mirror-list').children].entries()) {
      const [status, controls] = row.lastElementChild.children;
      updateRow(draft.mirrors[index].directory, status, controls, draft.mirrors[index]);
    }
    const state = latest.storageState;
    const failover = state?.lastFailoverAt ? `Last failover ${new Date(state.lastFailoverAt).toLocaleString()} from ${state.previousDirectory}.` : '';
    const gap = state?.lastFailoverAt ? (state.copiedThroughAtFailover
      ? ` The replacement had footage through ${new Date(state.copiedThroughAtFailover).toLocaleString()}. More recent footage may be missing.`
      : ' Check the recording timeline for missing footage.') : '';
    el('recording-failover-status').textContent = failover + gap;

    function updateRow(directory, status, controls, mirror) {
      const location = locations.find(value => value.directory === directory);
      const replacement = mirror ? mirror.registerReplacement : draft.registerPrimaryReplacement;
      const lag = location?.lagSeconds > 0 && !location.active ? ` · ${Math.ceil(location.lagSeconds)}s behind` : '';
      status.textContent = replacement ? 'Replacement will be registered when you save.' : location
        ? `${location.active ? 'Active · ' : ''}${descriptions[location.status] ?? location.status}${lag}` : 'Save to register this folder.';
      const key = JSON.stringify([directory, mirror !== null, replacement, location?.status, location?.canActivate, saving]);
      if (controls.dataset.storageKey === key) return;
      controls.dataset.storageKey = key; controls.replaceChildren();
      if (location?.canActivate) controls.append(button('Make active', async () => {
        if (saving) return;
        try {
          await request('recording/storage/activate', { directory, configurationId: draft.configurationId }, 'POST');
          await refreshStatus();
        } catch (error) { el('recording-result').textContent = errorText(error); }
      }));
      if (!replacement && ['offline', 'wrong-folder'].includes(location?.status)) controls.append(button('Register replacement', () => {
        if (saving) return;
        if (mirror) mirror.registerReplacement = true;
        else draft.registerPrimaryReplacement = true;
        updateStorageStatus();
      }));
      if (mirror) controls.append(button('Remove', () => {
        if (saving) return;
        draft.mirrors = draft.mirrors.filter(value => value !== mirror); renderStorageRows();
      }));
      for (const control of controls.children) control.disabled = saving;
    }
  }
  el('recording-mirror-add').onclick = () => {
    if (saving) return;
    draft.mirrors.push({ directory: '', maximumStorageGigabytes: Number(form.elements.namedItem('maximumStorageGigabytes').value) });
    renderStorageRows();
    el('recording-mirror-list').lastElementChild.querySelector('input').focus();
  };
  el('schedule-local-timezone').onclick = () => { form.elements.namedItem('timeZoneId').value = Intl.DateTimeFormat().resolvedOptions().timeZone; };
  function saveCamera() {
    const camera = cameraDraft();
    if (!camera) return;
    camera.enabled = el('camera-recording-enabled').checked;
    camera.mode = el('camera-recording-mode').value;
    camera.recordWhenStreaming = camera.mode === 'available-video';
    camera.detectDuringContinuousRecording = el('continuous-detection').checked;
    camera.detectionSubstream = el('detection-substream').checked;
    camera.detectionTemplateId = el('camera-detection-template').value || null;
  }
  function loadCamera() {
    const camera = cameraDraft();
    el('camera-recording-enabled').checked = camera.enabled;
    el('camera-recording-mode').value = camera.recordWhenStreaming ? 'available-video' : camera.mode ?? 'event';
    const availableOption = [...el('camera-recording-mode').options].find(option => option.value === 'available-video');
    if (availableOption) availableOption.disabled = !viewer.settings.sources?.find(c => c.channel === currentChannel)?.motionActivated;
    el('continuous-detection').checked = !!camera.detectDuringContinuousRecording;
    renderStreamSchedule();
    el('detection-substream').checked = camera.detectionSubstream;
    renderTemplateAssignment();
    points = []; drag = null; cursor = null; picture = null;
    renderActionSelections(); refreshPicture(); refreshStatus();
  }
  function renderTemplateAssignment() {
    const select = el('camera-detection-template');
    DetectionTemplateAssignments.populate(select, draft.detectionTemplates, cameraDraft().detectionTemplateId);
    if (cameraDraft().detectionTemplateId == null) {
      select.add(new Option('Existing camera settings (unassigned)', '')); select.value = '';
    }
    el('camera-detection-template-edit').hidden = !cameraDraft().detectionTemplateId;
  }
  el('camera-detection-template').onchange = () => { cameraDraft().detectionTemplateId = el('camera-detection-template').value || null; renderTemplateAssignment(); renderZones(); };
  el('detection-templates-open').onclick = async () => {
    hideMenu();
    try { await templateDialog.open(); }
    catch (error) { el('recording-status').textContent = errorText(error); el('viewer-menu').hidden = false; }
  };
  async function editTemplate(templateId, returnFocus) {
    saveCamera();
    try { await templateDialog.open(templateId, draft, returnFocus); }
    catch (error) { el('recording-result').textContent = errorText(error); }
  }
  el('camera-detection-template-edit').onclick = event => {
    event.preventDefault();
    return editTemplate(el('camera-detection-template').value, () => el('camera-detection-template-edit').focus());
  };
  function renderStreamSchedule() {
    const camera = cameraDraft();
    const mode = el('camera-recording-mode').value;
    const enabled = mode !== 'event';
    const detection = mode === 'event' || mode === 'continuous' && el('continuous-detection').checked;
    el('stream-schedule-panel').hidden = !enabled;
    el('software-motion-settings').hidden = !detection;
    el('software-motion-settings').disabled = !detection;
    el('detector-name').hidden = !detection;
    el('stream-record-help').hidden = mode !== 'available-video';
    el('continuous-detection-label').hidden = mode !== 'continuous';

    const container = el('stream-schedules'); container.replaceChildren();
    const rules = camera.streamSchedule ??= [];
    rules.forEach((rule, index) => {
      const row = document.createElement('fieldset'); row.className = 'zone-schedule';
      const legend = document.createElement('legend'); legend.textContent = `Schedule ${index + 1}`; row.append(legend);
      const days = document.createElement('div'); days.className = 'schedule-days';
      for (const day of [1, 2, 3, 4, 5, 6, 0]) {
        const label = document.createElement('label'); label.className = 'check';
        const input = document.createElement('input'); input.type = 'checkbox'; input.checked = (rule.days ?? [0,1,2,3,4,5,6]).includes(day);
        input.onchange = () => { rule.days ??= [0,1,2,3,4,5,6]; rule.days = input.checked ? [...rule.days, day] : rule.days.filter(d => d !== day); };
        label.append(input, ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'][day]); days.append(label);
      }
      row.append(days);
      for (const key of ['start', 'end']) {
        const label = document.createElement('label'); label.textContent = key === 'start' ? 'Start' : 'End';
        const input = document.createElement('input'); input.type = 'time'; input.value = rule[key]; input.required = enabled && !rule.allDay;
        input.disabled = !!rule.allDay; input.oninput = () => { rule[key] = input.value; }; label.append(input); row.append(label);
      }
      const label = document.createElement('label'); label.className = 'check';
      const input = document.createElement('input'); input.type = 'checkbox'; input.checked = !!rule.allDay;
      input.onchange = () => { rule.allDay = input.checked; renderStreamSchedule(); }; label.append(input, 'All day'); row.append(label);
      row.append(button('Remove schedule', () => { rules.splice(index, 1); renderStreamSchedule(); })); container.append(row);
    });
    el('stream-schedule-add').disabled = rules.length >= 16;
  }
  el('camera-recording-mode').onchange = renderStreamSchedule;
  el('continuous-detection').onchange = renderStreamSchedule;
  el('stream-schedule-add').onclick = () => { cameraDraft().streamSchedule.push({ start: '08:00', end: '18:00', days: [0,1,2,3,4,5,6] }); renderStreamSchedule(); };
  form.onsubmit = async event => {
    event.preventDefault(); if (saving || !form.reportValidity()) return;
    if (points.length || drag) { el('recording-result').textContent = 'Finish or cancel the current shape before saving.'; return; }
    saveCamera(); draft.enabled = form.elements.namedItem('enabled').checked;
    for (const field of fields) draft[field] = ['directory', 'timeZoneId'].includes(field) ? form.elements.namedItem(field).value.trim() : Number(form.elements.namedItem(field).value);
    saving = true; for (const control of form.elements) control.disabled = true;
    try {
      const snapshot = await request('recording');
      if (snapshot.settings.configurationId !== draft.configurationId) throw new Error('The camera configuration changed. Reopen Motion & recording settings.');
      draft.detectionTemplates = structuredClone(snapshot.settings.detectionTemplates);
      el('recording-result').textContent = 'Applying settings and finishing active clips…';
      await request('recording', draft, 'POST'); settingsDialog.close(); await refreshStatus();
    }
    catch (error) { el('recording-result').textContent = errorText(error); }
    finally {
      saving = false; for (const control of form.elements) control.disabled = false;
      updateCameraTabs();
      if (settingsDialog.open) {
        updateStorageStatus();
        renderActions();
        if (currentChannel !== null) {
          renderActionSelections(); renderStreamSchedule();
        }
      }
    }
  };

  async function refreshPicture() {
    const version = ++previewVersion;
    picture = null; draw();
    el('zone-preview-status').textContent = 'Loading camera picture…';
    try {
      const image = new Image(); image.src = `/api/cameras/${currentChannel}/snapshot?refresh=true&t=${Date.now()}`; await image.decode();
      if (version !== previewVersion) return;
      picture = image; canvas.height = Math.round(canvas.width * image.naturalHeight / image.naturalWidth);
      el('zone-preview-status').textContent = 'Snapshot for drawing zones. Refresh if the camera has moved.'; draw();
    } catch {
      if (version !== previewVersion) return;
      picture = null; canvas.height = 360;
      el('zone-preview-status').textContent = 'No live picture. Check the camera connection and try Refresh picture again; existing zones can still be edited.'; draw();
    }
  }
  el('zone-refresh').onclick = refreshPicture;
  function polygon(zone) { return zone.points || [{ x: zone.x, y: zone.y }, { x: zone.x + zone.width, y: zone.y }, { x: zone.x + zone.width, y: zone.y + zone.height }, { x: zone.x, y: zone.y + zone.height }]; }
  function path(vertices, close) {
    ctx.beginPath(); vertices.forEach((p, i) => ctx[i ? 'lineTo' : 'moveTo'](p.x * canvas.width, p.y * canvas.height)); if (close) ctx.closePath();
  }
  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (picture) ctx.drawImage(picture, 0, 0, canvas.width, canvas.height);
    ctx.lineWidth = 2; ctx.font = '15px sans-serif';
    for (const [i, zone] of (draft ? cameraDraft()?.zones || [] : []).entries()) {
      const vertices = polygon(zone); path(vertices, true);
      ctx.fillStyle = zone.exclude ? '#ff624c35' : '#6ce4c435'; ctx.strokeStyle = zone.exclude ? '#ff8c78' : '#6ce4c4'; ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#10151eee'; ctx.fillRect(vertices[0].x * canvas.width, vertices[0].y * canvas.height, Math.min(210, (zone.name || `Zone ${i + 1}`).length * 9 + 6), 21);
      ctx.fillStyle = '#fff'; ctx.fillText(zone.name || `Zone ${i + 1}`, vertices[0].x * canvas.width + 3, vertices[0].y * canvas.height + 16, 204);
    }
    let draftPoints = points;
    if (drag && cursor) draftPoints = polygon({ x: Math.min(drag.x, cursor.x), y: Math.min(drag.y, cursor.y), width: Math.abs(drag.x - cursor.x), height: Math.abs(drag.y - cursor.y) });
    else if (points.length && cursor) draftPoints = [...points, cursor];
    if (draftPoints.length) { path(draftPoints, !!drag); ctx.strokeStyle = '#ffdb78'; ctx.stroke(); }
    for (const p of points) { ctx.beginPath(); ctx.arc(p.x * canvas.width, p.y * canvas.height, 4, 0, Math.PI * 2); ctx.fillStyle = '#ffdb78'; ctx.fill(); }
  }
  const position = event => { const r = canvas.getBoundingClientRect(); return { x: Math.max(0, Math.min(1, (event.clientX - r.left) / r.width)), y: Math.max(0, Math.min(1, (event.clientY - r.top) / r.height)) }; };
  function addZone(zone) {
    if (cameraDraft().zones.length >= 32) { el('recording-result').textContent = 'A camera can have up to 32 zones.'; return; }
    cameraDraft().zones.push({ ...zone, name: `Zone ${cameraDraft().zones.length + 1}`, exclude: el('zone-purpose').value === 'exclude', times: [] });
    el('recording-result').textContent = ''; renderZones();
  }
  canvas.addEventListener('pointerdown', event => {
    if (saving) return; canvas.focus({ preventScroll: true }); cursor = position(event);
    if (el('zone-shape').value === 'rectangle') { drag = cursor; canvas.setPointerCapture(event.pointerId); }
    else if (points.length < 64) points.push(cursor);
    draw();
  });
  canvas.addEventListener('pointermove', event => { cursor = position(event); if (drag || points.length) draw(); });
  canvas.addEventListener('pointerup', event => {
    if (!drag) return;
    const end = position(event), zone = { x: Math.min(drag.x, end.x), y: Math.min(drag.y, end.y), width: Math.abs(drag.x - end.x), height: Math.abs(drag.y - end.y), points: null };
    drag = null; if (zone.width > 0.005 && zone.height > 0.005) addZone(zone); draw();
  });
  canvas.addEventListener('pointercancel', () => { drag = null; draw(); });
  function finishPolygon() {
    if (points.length < 3) { el('recording-result').textContent = 'Add at least three vertices to finish a polygon.'; return; }
    const area = points.reduce((sum, p, i) => { const q = points[(i + 1) % points.length]; return sum + p.x * q.y - q.x * p.y; }, 0);
    if (Math.abs(area) < 0.00001) { el('recording-result').textContent = 'Draw a polygon with a nonzero area.'; return; }
    addZone({ x: 0, y: 0, width: 0, height: 0, points: [...points] }); points = []; cursor = null; draw();
  }
  el('zone-finish').onclick = finishPolygon;
  el('zone-undo').onclick = () => { points.pop(); draw(); };
  el('zone-cancel').onclick = () => { points = []; drag = null; cursor = null; draw(); };
  el('zone-shape').onchange = () => {
    points = []; drag = null; draw();
    el('zone-help').textContent = el('zone-shape').value === 'polygon' ? 'Click around the boundary, then choose Finish polygon or press Enter. Escape cancels the drawing.' : 'Drag to draw a rectangle. Add several zones if needed. Exclusions always win.';
  };
  canvas.addEventListener('keydown', event => {
    if (event.key === 'Enter' && points.length) { event.preventDefault(); finishPolygon(); }
    if (event.key === 'Escape' && (points.length || drag)) { event.preventDefault(); event.stopPropagation(); points = []; drag = null; draw(); }
  });

  function actionChecks(container, selected, changed) {
    container.replaceChildren();
    for (const action of draft.actions) {
      const label = document.createElement('label');
      label.className = 'check';
      const input = document.createElement('input');
      input.type = 'checkbox';
      input.value = action.id;
      input.checked = selected.includes(action.id);
      input.onchange = () => changed([...container.querySelectorAll('input:checked')].map(item => item.value));
      label.append(input, `${action.name}${action.enabled === false ? ' (paused)' : ''}`);
      container.append(label);
    }
  }

  function renderActionSelections() {
    if (!cameraDraft()) return;
    actionChecks(el('camera-default-actions'), cameraDraft().defaultActionIds, ids => {
      cameraDraft().defaultActionIds = ids;
      renderZones();
    });
    renderZones();
  }

  function actionField(parent, text, input) {
    const label = document.createElement('label');
    label.textContent = text;
    label.append(input);
    parent.append(label);
    return input;
  }

  function renderActions() {
    el('motion-action-list').replaceChildren();
    for (const action of draft.actions) {
      const row = document.createElement('details');
      row.className = 'motion-action';
      const summary = document.createElement('summary');
      summary.textContent = `${action.name} · ${action.type === 'record' ? 'Record' : 'Webhook'}`;
      row.append(summary);

      const name = actionField(row, 'Action name', document.createElement('input'));
      name.value = action.name;
      name.required = true;
      name.maxLength = 80;
      name.oninput = () => {
        action.name = name.value;
        summary.textContent = `${action.name} · ${action.type === 'record' ? 'Record' : 'Webhook'}`;
        renderActionSelections();
      };

      const type = actionField(row, 'Action type', document.createElement('select'));
      type.add(new Option('Record', 'record'));
      type.add(new Option('Webhook', 'webhook'));
      type.value = action.type;
      type.disabled = action.id === 'record';
      type.onchange = () => {
        action.type = type.value;
        renderActions();
        renderActionSelections();
      };

      const enabledLabel = document.createElement('label');
      enabledLabel.className = 'check';
      const enabled = document.createElement('input');
      enabled.type = 'checkbox';
      enabled.checked = action.enabled !== false;
      enabled.onchange = () => {
        action.enabled = enabled.checked;
        renderActionSelections();
      };
      enabledLabel.append(enabled, 'Enable action');
      row.append(enabledLabel);

      if (action.type === 'webhook') {
        const url = actionField(row, 'Destination URL', document.createElement('input'));
        url.type = 'url';
        url.required = true;
        url.maxLength = 2048;
        url.autocomplete = 'off';
        url.placeholder = 'http://homeassistant.local:8123/api/webhook/your-id';
        url.value = action.url || '';
        url.oninput = () => { action.url = url.value.trim(); };

        const payload = actionField(row, 'JSON body (optional)', document.createElement('textarea'));
        payload.rows = 3;
        payload.maxLength = 16384;
        payload.spellcheck = false;
        payload.value = action.payload || '';
        payload.oninput = () => {
          action.payload = payload.value.trim() ? payload.value : null;
          try {
            if (action.payload) JSON.parse(action.payload);
            payload.setCustomValidity('');
          } catch {
            payload.setCustomValidity('Enter valid JSON or leave this empty.');
          }
        };

        const cooldown = actionField(row, 'Cooldown per camera and zone (seconds)', document.createElement('input'));
        cooldown.type = 'number';
        cooldown.min = '0';
        cooldown.max = '3600';
        cooldown.required = true;
        cooldown.value = action.cooldownSeconds ?? 30;
        cooldown.oninput = () => { action.cooldownSeconds = Number(cooldown.value); };

        const help = document.createElement('p');
        help.className = 'help';
        help.textContent = 'Sends an HTTP POST when motion starts in each assigned zone, including while another zone is recording. Leave the body empty to include camera and zone names. A custom body is sent exactly as entered. Continuous motion sends once; the cooldown limits later activations. Failed deliveries retry independently of recording.';
        row.append(help);
      }

      if (action.id !== 'record') {
        row.append(button('Remove action', () => {
          const assigned = draft.cameras.some(camera => camera.defaultActionIds.includes(action.id) ||
            camera.zones.some(zone => zone.actionIds?.includes(action.id)));
          if (assigned) {
            el('recording-result').textContent = `Remove “${action.name}” from camera defaults and zones before deleting it.`;
            return;
          }
          draft.actions = draft.actions.filter(item => item.id !== action.id);
          renderActions();
          renderActionSelections();
        }));
      }
      row.open = action.type === 'webhook' && !action.url;
      // Open invalid fields before the browser tries to focus them on submit.
      row.addEventListener('invalid', () => { row.open = true; }, true);
      el('motion-action-list').append(row);
    }
    el('motion-action-add').disabled = draft.actions.length >= 64;
  }

  el('motion-action-add').onclick = () => {
    const id = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
    draft.actions.push({ id, name: `Action ${draft.actions.length + 1}`, type: 'webhook', enabled: true, url: '', payload: null, cooldownSeconds: 30 });
    renderActions();
    renderActionSelections();
  };

  function renderZones() {
    el('zone-list').replaceChildren();
    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    cameraDraft().zones.forEach((zone, index) => {
      const item = document.createElement('li'); item.className = 'zone-entry';
      const title = document.createElement('label'); title.textContent = `${zone.exclude ? 'Ignore' : 'Detect'} · ${zone.points ? 'Polygon' : 'Rectangle'} name`;
      const name = document.createElement('input'); name.value = zone.name || `Zone ${index + 1}`; name.required = true; name.maxLength = 80;
      name.oninput = () => { zone.name = name.value; draw(); }; title.append(name); item.append(title);
      item.append(button('Remove zone', () => { cameraDraft().zones.splice(index, 1); renderZones(); }));
      if (!zone.exclude) {
        const detector = actionField(item, 'Detection template', document.createElement('select'));
        DetectionTemplateAssignments.populate(detector, draft.detectionTemplates, zone.detectionTemplateId, true, cameraDraft().detectionTemplateId);
        const edit = document.createElement('a'); edit.href = '#detection-template-dialog'; edit.textContent = 'Edit this template';
        edit.id = `zone-template-edit-${index}`; edit.className = 'template-edit-link';
        const updateLink = () => { edit.hidden = !(zone.detectionTemplateId || cameraDraft().detectionTemplateId); };
        edit.onclick = event => {
          event.preventDefault();
          return editTemplate(detector.value || el('camera-detection-template').value, () => el(edit.id).focus());
        };
        detector.onchange = () => { zone.detectionTemplateId = detector.value || null; updateLink(); };
        updateLink(); item.append(edit);
        const mode = actionField(item, 'Zone actions', document.createElement('select'));
        const inheritedNames = cameraDraft().defaultActionIds.map(id => draft.actions.find(action => action.id === id)?.name).filter(Boolean);
        mode.add(new Option(`Use camera defaults: ${inheritedNames.join(', ') || 'None'}`, 'inherit'));
        mode.add(new Option('Custom actions', 'custom'));
        mode.value = zone.actionIds == null ? 'inherit' : 'custom';
        mode.onchange = () => {
          zone.actionIds = mode.value === 'inherit' ? null : [...cameraDraft().defaultActionIds];
          renderZones();
        };
        if (zone.actionIds != null) {
          const options = document.createElement('div');
          options.className = 'action-options';
          actionChecks(options, zone.actionIds, ids => {
            zone.actionIds = ids;
            renderZones();
          });
          item.append(options);
          if (!zone.actionIds.length) {
            const note = document.createElement('p');
            note.className = 'help';
            note.textContent = 'No actions: detects motion without recording or sending zone webhooks.';
            item.append(note);
          }
        }
      }
      const rules = zone.times || (zone.times = []);
      if (!rules.length) { const note = document.createElement('p'); note.className = 'help'; note.textContent = 'Active all day, every day.'; item.append(note); }
      rules.forEach((rule, ruleIndex) => {
        const row = document.createElement('fieldset'); row.className = 'zone-schedule';
        const legend = document.createElement('legend'); legend.textContent = `Schedule ${ruleIndex + 1}`; row.append(legend);
        const days = document.createElement('div'); days.className = 'schedule-days';
        rule.days ||= [0, 1, 2, 3, 4, 5, 6];
        for (const day of [1, 2, 3, 4, 5, 6, 0]) {
          const label = document.createElement('label'); label.className = 'check';
          const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = rule.days.includes(day);
          checkbox.onchange = () => { rule.days = checkbox.checked ? [...rule.days, day] : rule.days.filter(d => d !== day); };
          label.append(checkbox, dayNames[day]); days.append(label);
        }
        row.append(days);
        const allLabel = document.createElement('label'); allLabel.className = 'check';
        const all = document.createElement('input'); all.type = 'checkbox'; all.checked = rule.allDay || false;
        allLabel.append(all, 'All day on selected days'); row.append(allLabel);
        const times = document.createElement('div'); times.className = 'form-row';
        for (const [key, text] of [['start', 'From'], ['end', 'Until']]) {
          const label = document.createElement('label'); label.textContent = text;
          const input = document.createElement('input'); input.type = 'time'; input.value = rule[key]; input.required = !all.checked; input.disabled = all.checked;
          input.onchange = () => { rule[key] = input.value; };
          label.append(input); times.append(label);
        }
        all.onchange = () => { rule.allDay = all.checked; times.querySelectorAll('input').forEach(input => { input.disabled = all.checked; input.required = !all.checked; }); };
        row.append(times, button('Remove schedule', () => { rules.splice(ruleIndex, 1); renderZones(); })); item.append(row);
      });
      if (rules.length < 16) item.append(button('Add weekly schedule', () => { rules.push({ start: '00:00', end: '06:00', days: [0, 1, 2, 3, 4, 5, 6], allDay: false }); renderZones(); }));
      el('zone-list').append(item);
    });
    draw();
  }

  el('recordings').onclick = async () => {
    hideMenu();
    try {
      [viewer, latest] = await Promise.all([request('state'), request('recording')]);
      const selectedCamera = el('recordings-camera').value;
      populateCameras(el('recordings-camera'), true); el('recordings-camera').value = selectedCamera;
      libraryDialog.showModal(); await libraryFilters.initialize(viewer, latest); await loadEvents(false); await refreshStatus();
    }
    catch (error) { el('recording-status').textContent = errorText(error); }
  };
  el('recordings-refresh').onclick = () => loadEvents(false);
  el('recordings-more').onclick = () => loadEvents(true);
  libraryDialog.addEventListener('close', () => { libraryRequestVersion++; archivePlayer.clear(); });
  async function loadEvents(append) {
    const version = ++libraryRequestVersion;
    const filter = libraryFilters.snapshot();
    if (!append) archivePlayer.coverage(filter, latest.settings.configurationId);
    if (!append) { lastEventTime = null; el('recordings-more').hidden = true; }
    el('recordings-more').disabled = true;
    try {
      el('recordings-result').textContent = 'Loading…';
      const query = { ...(filter.savedReplayOnly ? { savedReplayOnly: true } : {}), channel: filter.channel, limit: 50, zones: filter.zones.map(({ channel, id }) => ({ channel, id })) };
      if (filter.date) {
        const dayStart = new Date(`${filter.date}T00:00:00`);
        const next = new Date(dayStart); next.setDate(next.getDate() + 1);
        query.since = dayStart.toISOString(); query.before = next.toISOString();
      }
      if (append && lastEventTime) query.before = lastEventTime;
      const items = await request('recordings/search', query, 'POST');
      if (version !== libraryRequestVersion) return;
      if (!append) el('recording-list').replaceChildren();
      for (const item of items) renderEvent(item);
      lastEventTime = items.at(-1)?.triggeredAt;
      el('recordings-more').hidden = items.length < 50;
      el('recordings-result').textContent = el('recording-list').children.length ? 'Times are shown in your browser’s timezone. Refresh to see newly finished recordings. Select a camera and date for the coverage timeline.' : 'No recordings match. Choose a recording mode and enable recording in the menu.';
    } catch (error) { if (version === libraryRequestVersion) el('recordings-result').textContent = errorText(error); }
    finally { if (version === libraryRequestVersion) el('recordings-more').disabled = false; }
  }
  function renderEvent(item) {
    const row = document.createElement('article'); row.className = 'recording-event';
    if (item.status === 'ready') { const image = document.createElement('img'); image.src = `/api/recordings/${item.id}/thumbnail`; image.alt = `${item.cameraName} event`; image.loading = 'lazy'; image.onerror = () => image.hidden = true; row.append(image); }
    const detail = document.createElement('div'); detail.className = 'event-detail';
    const title = document.createElement('strong'); title.textContent = `${item.cameraName} · ${new Date(item.triggeredAt).toLocaleString()}`;
    const info = document.createElement('p'); info.className = 'help'; info.textContent = `${({ event: "Event", continuous: "Continuous", "available-video": "Available video" })[item.mode] || "Event"} · ${Math.round(item.durationSeconds)}s · ${(item.sizeBytes / 1e6).toFixed(1)} MB · ${item.status}${item.interrupted ? ' · interrupted/recovered' : ''}${item.protected ? ' · protected' : ''}`;
    const actions = document.createElement('div'); actions.className = 'event-actions';
    if (item.status === 'ready') {
      actions.append(button('Play', () => archivePlayer.play(item)));
      const download = document.createElement('a'); download.href = `/api/recordings/${item.id}/video?download=true`; download.textContent = 'Download MP4'; actions.append(download);
    }
    if (item.status === 'failed') { const download = document.createElement('a'); download.href = `/api/recordings/${item.id}/video?download=true`; download.textContent = 'Download captured data'; actions.append(download); }
    actions.append(button(item.protected ? 'Unprotect' : 'Protect', async () => {
      try { await request(`recordings/${item.id}/protect`, { protected: !item.protected }, 'POST'); await loadEvents(false); }
      catch (error) { el('recordings-result').textContent = errorText(error); }
    }));
    if (!item.protected && ['ready', 'failed'].includes(item.status)) actions.append(button('Delete', async () => {
      if (!confirm(`Delete the recording from ${item.cameraName} at ${new Date(item.triggeredAt).toLocaleString()}?`)) return;
      try { await request(`recordings/${item.id}`, undefined, 'DELETE'); row.remove(); await refreshStatus(); }
      catch (error) { el('recordings-result').textContent = errorText(error); }
    }));
    const cause = document.createElement('p'); cause.className = 'help';
    cause.textContent = item.trigger ? `${item.trigger.source}: ${item.trigger.reason}${item.additionalTriggers?.length ? ` · ${item.additionalTriggers.length} additional triggers` : ''}` : 'Motion detected';
    const zoneNames = [...new Set([item.trigger, ...(item.additionalTriggers || [])].flatMap(trigger => (trigger?.zones || []).map(zone => zone.name)))];
    if (zoneNames.length) cause.textContent += ` · Zones: ${zoneNames.join(', ')}`;
    detail.append(title, info, cause, actions); row.append(detail); el('recording-list').append(row);
  }
})();
