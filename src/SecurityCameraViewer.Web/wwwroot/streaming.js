'use strict';
const StreamingServiceForm = Object.freeze({
  settings(previous, read, profiles) {
    return { ...previous, enabled: read('enabled'), onvifEnabled: read('onvif-enabled'),
      discoveryEnabled: read('discovery-enabled'), discoveryMode: read('discovery-mode'),
      advertisedHost: read('host').trim(), onvifBaseUrl: read('base-url').trim(),
      rtspPort: Number(read('rtsp-port')), udpEnabled: read('udp-enabled'),
      rtpPort: Number(read('rtp-port')), rtcpPort: Number(read('rtcp-port')), apiPort: Number(read('api-port')),
      mediaMtxExecutable: read('executable').trim(), username: read('username').trim(), profiles };
  }
});

(() => {
  const el = name => document.getElementById(`streaming-${name}`);
  const dialog = el('dialog');
  if (!dialog) return;
  let current, timer, loading = false;
  const overrideRows = [];
  async function request(path = '/api/streaming', method = 'GET', body) {
    const response = await fetch(path, { method, headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.message || `Request failed (${response.status}). Sign in with an administrator token if required.`);
    return data;
  }
  function address(label, value, container) {
    if (!value) return;
    const row = document.createElement('label'); row.textContent = label;
    const field = document.createElement('input'); field.value = value; field.readOnly = true;
    field.addEventListener('click', () => field.select());
    row.append(field); container.append(row);
  }
  function renderStatus(data) {
    el('status').textContent = `${data.status.state}: ${data.status.message} Active feeds: ${data.status.readyStreams}; clients: ${data.status.readers}.`;
    el('discovery-status').textContent = data.discovery.message;
    const addresses = el('addresses'); addresses.replaceChildren();
    address('All cameras — ONVIF recorder', data.aggregateAddress, addresses);
    for (const camera of data.cameras) {
      const group = document.createElement('fieldset'); const legend = document.createElement('legend');
      legend.textContent = camera.name; group.append(legend);
      address('ONVIF camera', camera.onvifAddress, group);
      for (const profile of camera.profiles) address(`${profile.substream ? 'Substream' : 'Main stream'} — RTSP (${profile.media.codec}, ${profile.media.width} × ${profile.media.height})`, profile.rtspAddress, group);
      addresses.append(group);
    }
    if (data.cameras.length === 0) addresses.textContent = 'Enable cameras in the active configuration to share them.';
  }
  function renderOverrides(data) {
    overrideRows.length = 0;
    el('profile-overrides').replaceChildren();
    for (const camera of data.cameras) for (const profile of camera.profiles) {
      const saved = data.settings.profiles.find(p => p.cameraId === camera.id && p.substream === profile.substream);
      const values = saved || profile.media;
      const row = document.createElement('fieldset'), legend = document.createElement('legend');
      legend.textContent = `${camera.name} — ${profile.substream ? 'Substream' : 'Main'}`; row.append(legend);
      const source = document.createElement('p'); source.className = 'help';
      source.textContent = `Descriptor source: ${profile.media.metadataSource}.`;
      if (profile.media.metadataSource === 'fallback') source.textContent += ' These are fallback values; refresh metadata or enter an override.';
      row.append(source);
      const check = document.createElement('input'); check.type = 'checkbox'; check.checked = Boolean(saved);
      const label = document.createElement('label'); label.className = 'check'; label.append(check, document.createTextNode('Override advertised metadata')); row.append(label);
      const fields = {};
      for (const [key, title, min, max] of [['codec', 'Codec'], ['width', 'Width', 1, 16384], ['height', 'Height', 1, 16384],
        ['frameRate', 'Frames per second', 0.1, 240], ['bitrateKbps', 'Bitrate (kbps)', 1, 200000]]) {
        const field = document.createElement(key === 'codec' ? 'select' : 'input');
        if (key === 'codec') for (const codec of ['H264', 'H265', 'JPEG']) { const option = document.createElement('option'); option.value = codec; option.textContent = codec; field.append(option); }
        else { field.type = 'number'; field.min = String(min); field.max = String(max); field.step = key === 'frameRate' ? '0.1' : '1'; }
        field.value = values[key]; field.disabled = !check.checked;
        const fieldLabel = document.createElement('label'); fieldLabel.textContent = title; fieldLabel.append(field); row.append(fieldLabel); fields[key] = field;
      }
      check.addEventListener('change', () => Object.values(fields).forEach(field => { field.disabled = !check.checked; }));
      overrideRows.push({ cameraId: camera.id, substream: profile.substream, check, fields });
      el('profile-overrides').append(row);
    }
  }
  async function load(fill = false) {
    if (loading) return;
    loading = true;
    try {
      const data = await request(); renderStatus(data);
      if (fill) {
        current = data.settings;
        for (const [name, key] of Object.entries({ enabled: 'enabled', 'onvif-enabled': 'onvifEnabled', 'discovery-enabled': 'discoveryEnabled',
          'discovery-mode': 'discoveryMode', host: 'advertisedHost', 'base-url': 'onvifBaseUrl', 'rtsp-port': 'rtspPort',
          'udp-enabled': 'udpEnabled', 'rtp-port': 'rtpPort', 'rtcp-port': 'rtcpPort', 'api-port': 'apiPort', executable: 'mediaMtxExecutable', username: 'username' })) {
          const field = el(name); if (field.type === 'checkbox') field.checked = current[key]; else field.value = current[key];
        }
        if (!current.advertisedHost) el('host').value = location.hostname;
        if (!current.onvifBaseUrl) el('base-url').value = `${location.origin}/`;
        el('password').value = '';
        el('password-help').textContent = data.hasPassword ? 'Password is saved. Leave blank to keep it, or enter a replacement.' : 'Set a password of at least 8 characters before enabling the service.';
        renderOverrides(data);
      }
    } finally { loading = false; }
  }
  function error(value) { el('result').textContent = value.message; }
  el('open').addEventListener('click', async () => {
    document.getElementById('viewer-menu').hidden = true;
    el('result').textContent = '';
    dialog.showModal();
    try { await load(true); } catch (value) { error(value); }
    clearInterval(timer); timer = setInterval(() => { if (dialog.open) load().catch(error); }, 5000);
  });
  dialog.addEventListener('close', () => { clearInterval(timer); el('password').value = ''; });
  el('refresh').addEventListener('click', () => load().catch(error));
  el('refresh-media').addEventListener('click', async () => {
    try { el('result').textContent = (await request('/api/streaming/refresh-media', 'POST')).message; } catch (value) { error(value); }
  });
  el('form').addEventListener('submit', async event => {
    event.preventDefault();
    if (!current) return;
    const submit = el('form').querySelector('[type=submit]'); submit.disabled = true;
    try {
      const read = name => el(name).type === 'checkbox' ? el(name).checked : el(name).value;
      const profiles = overrideRows.filter(row => row.check.checked).map(row => ({ cameraId: row.cameraId, substream: row.substream,
        ...Object.fromEntries(Object.entries(row.fields).map(([key, field]) => [key, key === 'codec' ? field.value : Number(field.value)])) }));
      const settings = StreamingServiceForm.settings(current, read, profiles);
      const password = el('password').value || null;
      el('result').textContent = (await request('/api/streaming', 'PUT', { settings, password })).message;
      await load(true);
    } catch (value) { error(value); } finally { submit.disabled = false; }
  });
})();
