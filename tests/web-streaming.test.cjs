const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const script = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/streaming.js'), 'utf8');

class Element {
  constructor(tag = 'input') {
    this.tag = tag; this.type = ''; this.value = ''; this.checked = false; this.disabled = false;
    this.children = []; this.handlers = new Map(); this.textContent = ''; this.open = false;
  }
  addEventListener(name, callback) { this.handlers.set(name, [...this.handlers.get(name) || [], callback]); }
  async emit(name) { const event = { preventDefault() {} }; for (const handler of this.handlers.get(name) || []) await handler(event); }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  showModal() { this.open = true; }
  select() { this.selected = true; }
  querySelector() { return this.submit ||= new Element('button'); }
}

function harness() {
  const fields = new Map();
  const names = ['dialog', 'form', 'open', 'refresh', 'refresh-media', 'status', 'discovery-status', 'addresses', 'profile-overrides',
    'result', 'password-help', 'enabled', 'onvif-enabled', 'discovery-enabled', 'discovery-mode', 'host', 'base-url',
    'rtsp-port', 'udp-enabled', 'rtp-port', 'rtcp-port', 'api-port', 'executable', 'username', 'password'];
  for (const name of names) fields.set(`streaming-${name}`, new Element());
  for (const name of ['enabled', 'onvif-enabled', 'discovery-enabled', 'udp-enabled']) fields.get(`streaming-${name}`).type = 'checkbox';
  fields.set('viewer-menu', new Element());
  const cameraId = '0123456789abcdef0123456789abcdef';
  const data = {
    settings: { enabled: false, onvifEnabled: true, discoveryEnabled: true, discoveryMode: 'individual',
      advertisedHost: '', onvifBaseUrl: '', rtspPort: 8554, udpEnabled: true, rtpPort: 8000, rtcpPort: 8001,
      apiPort: 9997, mediaMtxExecutable: 'mediamtx', username: 'viewer', deviceId: 'stable-id', profiles: [] },
    hasPassword: true, status: { state: 'disabled', message: 'Disabled', readyStreams: 0, readers: 0 },
    discovery: { message: 'Discovery disabled' }, aggregateAddress: 'http://server:5080/onvif/device_service',
    cameras: [{ id: cameraId, name: '<script>unsafe label</script>', motionActivated: false,
      onvifAddress: `http://server:5080/onvif/cameras/${cameraId}/device_service`,
      profiles: [{ token: `${cameraId}_main`, substream: false,
        media: { codec: 'H264', width: 640, height: 360, frameRate: 10, bitrateKbps: 512, metadataSource: 'ffprobe' },
        rtspAddress: `rtsp://server:8554/cameras/${cameraId}/main` }] }]
  };
  const calls = []; let interval; let reject = false;
  const document = { getElementById: id => fields.get(id), createElement: tag => new Element(tag), createTextNode: text => ({ textContent: text }) };
  runInNewContext(script, { document, location: { hostname: 'server', origin: 'http://server:5080' },
    setInterval(callback) { interval = callback; return 1; }, clearInterval() {},
    async fetch(url, options) {
      calls.push({ url, ...options });
      if (reject) return { ok: false, status: 403, async json() { return { message: 'Administrator sign-in required.' }; } };
      if (options.method === 'GET') return { ok: true, async json() { return structuredClone(data); } };
      if (options.method === 'PUT') { const request = JSON.parse(options.body); data.settings = request.settings; data.hasPassword ||= Boolean(request.password); }
      return { ok: true, async json() { return { message: 'Saved or queued.' }; } };
    }
  });
  return { fields, data, calls, get: name => fields.get(`streaming-${name}`), async poll() { interval(); await new Promise(setImmediate); }, fail: () => { reject = true; } };
}

test('hamburger entry opens the service dialog and fills reachable defaults without returning a password', async () => {
  const ui = harness(); await ui.get('open').emit('click');
  assert.equal(ui.get('dialog').open, true);
  assert.equal(ui.fields.get('viewer-menu').hidden, true);
  assert.equal(ui.get('host').value, 'server');
  assert.equal(ui.get('base-url').value, 'http://server:5080/');
  assert.equal(ui.get('password').value, '');
  assert.match(ui.get('password-help').textContent, /Leave blank to keep/);
  assert.match(ui.get('status').textContent, /disabled/);
});

test('status polling preserves edits in the open form', async () => {
  const ui = harness(); await ui.get('open').emit('click');
  ui.get('host').value = 'my-edited-host'; ui.get('password').value = 'unsaved-secret';
  ui.data.status = { state: 'running', message: 'Ready', readyStreams: 2, readers: 3 };
  await ui.poll();
  assert.equal(ui.get('host').value, 'my-edited-host');
  assert.equal(ui.get('password').value, 'unsaved-secret');
  assert.match(ui.get('status').textContent, /running.*clients: 3/);
});

test('saving preserves the stable device ID, converts ports and keeps a saved password when blank', async () => {
  const ui = harness(); await ui.get('open').emit('click');
  ui.get('enabled').checked = true; ui.get('rtsp-port').value = '18554';
  await ui.get('form').emit('submit');
  const saved = JSON.parse(ui.calls.find(call => call.method === 'PUT').body);
  assert.equal(saved.password, null); assert.equal(saved.settings.deviceId, 'stable-id');
  assert.equal(saved.settings.enabled, true); assert.equal(saved.settings.rtspPort, 18554);
  assert.equal(ui.get('form').querySelector().disabled, false);
});

test('replacement passwords are sent only in the explicit save and then cleared', async () => {
  const ui = harness(); await ui.get('open').emit('click'); ui.get('password').value = 'replacement-secret';
  await ui.get('form').emit('submit');
  assert.equal(JSON.parse(ui.calls.find(call => call.method === 'PUT').body).password, 'replacement-secret');
  assert.equal(ui.get('password').value, '');
  ui.get('password').value = 'another-unsaved-secret'; ui.get('dialog').open = false; await ui.get('dialog').emit('close');
  assert.equal(ui.get('password').value, '');
});

test('camera addresses are read-only selectable fields and camera labels remain text', async () => {
  const ui = harness(); await ui.get('open').emit('click');
  const camera = ui.get('addresses').children.find(child => child.tag === 'fieldset');
  assert.equal(camera.children[0].textContent, '<script>unsafe label</script>');
  const rtsp = camera.children.at(-1).children[0];
  assert.equal(rtsp.readOnly, true); assert.match(rtsp.value, /^rtsp:\/\/server/);
  await rtsp.emit('click'); assert.equal(rtsp.selected, true);
});

test('profile overrides are disabled until selected and produce correctly typed settings', async () => {
  const ui = harness(); await ui.get('open').emit('click');
  const row = ui.get('profile-overrides').children[0];
  const checkbox = row.children.find(child => child.className === 'check').children[0];
  const width = row.children.find(child => child.textContent === 'Width').children[0];
  assert.equal(width.disabled, true);
  checkbox.checked = true; await checkbox.emit('change'); width.value = '1280';
  assert.equal(width.disabled, false);
  await ui.get('form').emit('submit');
  const overrides = JSON.parse(ui.calls.find(call => call.method === 'PUT').body).settings.profiles;
  assert.equal(overrides.length, 1); assert.equal(overrides[0].width, 1280);
  assert.equal(overrides[0].substream, false); assert.equal(overrides[0].codec, 'H264');
});

test('administration failures are actionable and always release the save button', async () => {
  const ui = harness(); await ui.get('open').emit('click'); ui.fail();
  await ui.get('form').emit('submit');
  assert.equal(ui.get('result').textContent, 'Administrator sign-in required.');
  assert.equal(ui.get('form').querySelector().disabled, false);
});

test('metadata refresh is an explicit POST, separate from status polling', async () => {
  const ui = harness(); await ui.get('open').emit('click'); await ui.get('refresh-media').emit('click');
  assert(ui.calls.some(call => call.url === '/api/streaming/refresh-media' && call.method === 'POST'));
  assert.equal(ui.get('result').textContent, 'Saved or queued.');
});
