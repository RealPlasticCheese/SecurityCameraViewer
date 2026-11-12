const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/password-visibility.js'), 'utf8');

function field(value = '') {
  const dialog = new EventTarget();
  const elements = [];
  const document = {
    querySelectorAll: () => [],
    createElement(tag) {
      const element = { tag, children: [], checked: false, disabled: false, attributes: {},
        setAttribute(name, value) { this.attributes[name] = value; },
        append(...children) { this.children.push(...children); } };
      elements.push(element); return element;
    }
  };
  const label = { textContent: 'Camera password', replaceWith() {} };
  const input = { value, type: 'password', dataset: {}, isConnected: true,
    getAttribute: () => null, closest: selector => selector === 'label' ? label : dialog };
  const PasswordVisibility = runInNewContext(`${source}\nPasswordVisibility;`, { document });
  let requests = 0, inputEvents = 0;
  input.oninput = () => inputEvents++;
  const errors = [];
  const control = PasswordVisibility.attach(input, async () => { requests++; return 'saved-secret'; }, error => errors.push(error));
  const checkbox = elements.find(element => element.type === 'checkbox');
  return { input, checkbox, dialog, control, errors, get requests() { return requests; }, get inputEvents() { return inputEvents; },
    async show(checked) { checkbox.checked = checked; await checkbox.onchange(); } };
}

test('every typed password can be shown and hidden without changing its value', async () => {
  const f = field('unsaved-secret');
  await f.show(true);
  assert.equal(f.input.type, 'text'); assert.equal(f.input.value, 'unsaved-secret');
  assert.equal(f.requests, 0); assert.equal(f.inputEvents, 0);
  await f.show(false);
  assert.equal(f.input.type, 'password'); assert.equal(f.input.value, 'unsaved-secret');
});

test('saved passwords are loaded only on explicit reveal and do not mark an edit', async () => {
  const f = field();
  assert.equal(f.requests, 0);
  await f.show(true);
  assert.equal(f.input.value, 'saved-secret'); assert.equal(f.requests, 1); assert.equal(f.inputEvents, 0);
  await f.show(false); await f.show(true);
  assert.equal(f.requests, 1);
});

test('closing a dialog clears both revealed and masked passwords', async () => {
  for (const revealed of [false, true]) {
    const f = field('unsaved-secret');
    await f.show(revealed); f.dialog.dispatchEvent(new Event('close'));
    assert.equal(f.input.value, ''); assert.equal(f.input.type, 'password'); assert.equal(f.checkbox.checked, false);
  }
});

test('a late saved password cannot overwrite edits, a reset, or a closed dialog', async () => {
  for (const action of ['edit', 'reset', 'close']) {
    const f = field(); let resolve;
    f.control.setLoader(() => new Promise(done => { resolve = done; }));
    const revealing = f.show(true);
    if (action === 'edit') f.input.value = 'new-secret';
    if (action === 'reset') f.control.reset();
    if (action === 'close') f.dialog.dispatchEvent(new Event('close'));
    resolve('saved-secret'); await revealing;
    assert.equal(f.input.value, action === 'edit' ? 'new-secret' : '');
  }
});

test('failed password fetches return to masked state and permit retry', async () => {
  const f = field(); f.control.setLoader(async () => { throw new Error('Fetch failed'); });
  await f.show(true);
  assert.equal(f.input.type, 'password'); assert.equal(f.checkbox.checked, false); assert.equal(f.checkbox.disabled, false);
  assert.equal(f.errors.length, 1);
  f.control.setLoader(async () => 'retry-secret'); await f.show(true);
  assert.equal(f.input.value, 'retry-secret');
});

// Exercise the setup script itself so a visibility change cannot silently
// replace its draft, lose camera edits, or rebuild camera tabs.
function setup(videoTimeoutSeconds, pollIntervalSeconds, sources, retryDelaysSeconds) {
  const ids = new Map();
  class Element extends EventTarget {
    constructor(tag) { super(); this.tag = tag; this.children = []; this.dataset = {}; this.value = ''; this.type = ''; this.checked = false; this.disabled = false; this.attributes = {}; }
    set innerHTML(html) {
      for (const match of html.matchAll(/<(\w+)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
        const child = new Element(match[1]); ids.set(match[3], child);
        child.type = match[2].match(/type="([^"]+)"/)?.[1] || '';
        this.append(child);
      }
    }
    append(...children) { for (const child of children) { if (typeof child === 'object') child.parent = this; this.children.push(child); } }
    replaceWith(next) { const index = this.parent.children.indexOf(this); this.parent.children[index] = next; next.parent = this.parent; this.parent = null; }
    replaceChildren(...children) { this.children.forEach(child => { if (typeof child === 'object') child.parent = null; }); this.children = []; this.append(...children); }
    closest(tag) { for (let element = this; element; element = element.parent) if (element.tag === tag) return element; return null; }
    get isConnected() { return !!this.closest('body'); }
    setAttribute(name, value) { this.attributes[name] = value; }
    getAttribute(name) { return this.attributes[name]; }
    add(option) { this.append(option); }
    get options() { return this.children; }
    showModal() { this.open = true; }
    focus() { document.activeElement = this; }
    setCustomValidity(message) { this.validationMessage = message; }
    reportValidity() { return !this.validationMessage && this.value !== '' && (this.type !== 'number' || Number.isInteger(Number(this.value)) && Number(this.value) >= 0 && Number(this.value) <= 2147483647); }
    querySelectorAll(selector) {
      const matches = element => selector.split(',').some(part => {
        part = part.trim();
        if (part.startsWith('[role=')) return element.attributes.role === part.slice(6, -1);
        if (part === 'input[data-secret="true"]') return element.tag === 'input' && element.dataset.secret === 'true';
        if (part === 'input[type=password]') return element.tag === 'input' && element.type === 'password';
        return element.tag === part;
      });
      return this.children.flatMap(child => typeof child !== 'object' ? [] : [...(matches(child) ? [child] : []), ...child.querySelectorAll(selector)]);
    }
  }
  const body = new Element('body');
  const documentEvents = new EventTarget();
  const document = { addEventListener: documentEvents.addEventListener.bind(documentEvents), body, createElement: tag => new Element(tag), getElementById: id => {
    if (!ids.has(id)) { const element = new Element('button'); ids.set(id, element); body.append(element); }
    return ids.get(id);
  }, querySelectorAll: selector => body.querySelectorAll(selector) };
  const requests = [];
  const collection = { configurationId: 'default', configurationName: 'Test', videoTimeoutSeconds, retryDelaysSeconds, sources: sources || [
    { id: 'one', channel: 1, name: 'Garage', origin: 'Recorder template', username: 'admin', mainStream: 'rtsp://camera/live', motionActivated: true, pollIntervalSeconds },
    { id: 'two', channel: 2, name: 'Driveway', enabled: false, username: 'admin', mainStream: 'rtsp://camera/live2' }
  ] };
  let nextId = 0;
  const context = { document, structuredClone, AbortSignal, URL, crypto: { randomUUID: () => `01234567-89ab-cdef-0123-${String(++nextId).padStart(12, '0')}` },
    CameraPlayback: { reports: new Map(), capabilities: () => ({ native: true, browser: true }) },
    window: { confirm: () => { throw new Error('Visibility must not ask to discard edits'); } },
    Option: function(text, value) { const option = new Element('option'); option.textContent = text; option.value = value; return option; },
    fetch: async (path, options) => {
      requests.push({ path, options });
      if (path.endsWith('/configurations') && options.method === 'POST') Object.assign(collection, JSON.parse(options.body).settings);
      const data = path.endsWith('/passwords') ? { sharedPassword: 'shared-secret', cameraPasswords: { one: 'saved-secret' } } :
        path.endsWith('/interfaces') ? [] : { activeId: 'default', defaultRetryDelaysSeconds: '1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32', configurations: [structuredClone(collection)] };
      return { ok: true, headers: { get: () => 'application/json' }, json: async () => data };
    } };
  runInNewContext(readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-collection.js'), 'utf8') + '\n' +
    source + '\n' + readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-playback-policy.js'), 'utf8') + '\n' +
    readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-setup.js'), 'utf8'), context);
  const settle = () => new Promise(resolve => setImmediate(resolve));
  return { ids, requests, document, playback: context.CameraPlayback, async open() { ids.get('connection').onclick(); await settle(); },
    allowConfirmation(accepted = true) { context.window.confirm = () => accepted; },
    cameraTabs: () => ids.get('setup-cameras').querySelectorAll('[role=tab]'),
    cameraSections: () => ids.get('setup-cameras').querySelectorAll('[role=tabpanel]') };
}

function visibilityCheckbox(input) {
  const wrapper = input.parent.className === 'password-field' ? input.parent : input.parent.parent;
  return wrapper.children[1].children[0];
}

test('setup exposes an independent toggle beside shared, preset, ONVIF, and each camera password', async () => {
  const s = setup(); await s.open();
  const sections = s.cameraSections();
  for (const input of [s.ids.get('setup-password'), s.ids.get('preset-password'), s.ids.get('onvif-password'),
      ...sections.flatMap(section => section.querySelectorAll('input[type=password]'))]) {
    assert.ok(input.passwordVisibility);
    const checkbox = visibilityCheckbox(input);
    input.value = 'typed-secret'; checkbox.checked = true; await checkbox.onchange();
    assert.equal(input.type, 'text'); assert.equal(input.value, 'typed-secret');
    checkbox.checked = false; await checkbox.onchange(); assert.equal(input.type, 'password');
  }
  assert.equal(s.requests.filter(request => request.path.endsWith('/passwords')).length, 0);
});

test('camera reveal preserves unsaved edits and selected tab, including after reordering', async () => {
  const s = setup(); await s.open();
  const [first, second] = s.cameraSections();
  s.cameraTabs()[1].onclick();
  const name = second.querySelectorAll('input')[1]; name.value = 'Edited camera'; name.oninput();
  const password = second.querySelectorAll('input[type=password]')[0];
  password.value = 'edited-secret'; password.oninput();
  const checkbox = visibilityCheckbox(password); checkbox.checked = true; await checkbox.onchange();
  assert.equal(name.value, 'Edited camera'); assert.equal(password.value, 'edited-secret'); assert.equal(first.hidden, true);
  second.querySelectorAll('button').find(button => button.textContent === 'Move left').onclick();
  const reordered = s.cameraSections();
  assert.equal(reordered[1].dataset.cameraId, 'one'); assert.equal(reordered[1].hidden, true);
  assert.equal(reordered[0].hidden, false); assert.equal(s.cameraTabs()[0].getAttribute('aria-selected'), 'true');
  assert.equal(reordered[0].querySelectorAll('input')[1].value, 'Edited camera');
  assert.equal(reordered[0].querySelectorAll('input[type=password]')[0].value, 'edited-secret');
  assert.deepEqual(s.cameraTabs().map(tab => tab.textContent), ['1. Edited camera', '2. Garage']);
  assert.equal(reordered[0].querySelectorAll('h3')[0].textContent, '1. Edited camera - RTSP');
  assert.equal(reordered[0].querySelectorAll('button').find(button => button.textContent === 'Move left').disabled, true);
  assert.equal(reordered[1].querySelectorAll('button').find(button => button.textContent === 'Move right').disabled, true);
  const savedInput = reordered[1].querySelectorAll('input[type=password]')[0];
  const savedCheckbox = visibilityCheckbox(savedInput); savedCheckbox.checked = true; await savedCheckbox.onchange();
  assert.equal(savedInput.value, 'saved-secret');
  assert.equal(s.requests.filter(request => request.path.endsWith('/configurations')).length, 1);
});

test('camera tabs label names only, update headings, and strike through disabled cameras while keeping them editable', async () => {
  const s = setup(); await s.open();
  const tabs = s.cameraTabs(), sections = s.cameraSections();
  assert.deepEqual(tabs.map(tab => tab.textContent), ['1. Garage', '2. Driveway']);
  assert.equal(sections[0].querySelectorAll('h3')[0].textContent, '1. Garage - Recorder template');
  assert.equal(sections[1].querySelectorAll('h3')[0].textContent, '2. Driveway - RTSP');
  for (let i = 0; i < tabs.length; i++) {
    assert.equal(tabs[i].getAttribute('aria-controls'), sections[i].id);
    assert.equal(sections[i].getAttribute('aria-labelledby'), tabs[i].id);
  }
  assert.match(tabs[1].className, /camera-disabled/); assert.equal(tabs[1].disabled, false);
  tabs[1].onclick(); assert.equal(sections[0].hidden, true); assert.equal(sections[1].hidden, false);
  const inputs = sections[1].querySelectorAll('input');
  inputs[0].checked = true; inputs[0].onchange(); assert.doesNotMatch(tabs[1].className, /camera-disabled/);
  inputs[1].value = 'Side gate'; inputs[1].oninput();
  assert.equal(tabs[1].textContent, '2. Side gate');
  assert.equal(sections[1].querySelectorAll('h3')[0].textContent, '2. Side gate - RTSP');
  inputs[0].checked = false; inputs[0].onchange(); assert.match(tabs[1].className, /camera-disabled/);
  tabs[0].onclick(); tabs[1].onclick();
  assert.equal(sections[1].querySelectorAll('input')[1].value, 'Side gate');
  assert.equal(s.requests.length, 2);
});

test('arrow keys wrap camera tabs and Home/End focus and show the selected camera', async () => {
  const s = setup(); await s.open();
  const tabs = s.cameraTabs(), sections = s.cameraSections();
  assert.equal(tabs[0].tabIndex, 0); assert.equal(tabs[1].tabIndex, -1);
  for (const [from, key, to] of [[0, 'ArrowLeft', 1], [1, 'ArrowRight', 0], [0, 'End', 1], [1, 'Home', 0]]) {
    let prevented = false;
    tabs[from].onkeydown({ key, preventDefault() { prevented = true; } });
    assert.equal(prevented, true); assert.equal(s.document.activeElement, tabs[to]);
    assert.equal(tabs[to].tabIndex, 0); assert.equal(tabs[1 - to].tabIndex, -1);
    assert.equal(tabs[to].getAttribute('aria-selected'), 'true'); assert.equal(tabs[1 - to].getAttribute('aria-selected'), 'false');
    assert.equal(sections[to].hidden, false); assert.equal(sections[1 - to].hidden, true);
  }
  tabs[0].onkeydown({ key: 'Tab', preventDefault() { assert.fail('Tab must retain normal keyboard navigation'); } });
});

test('adding RTSP and recorder cameras creates and selects tabs and removing the last camera restores the empty draft', async () => {
  const s = setup(); await s.open(); s.allowConfirmation();
  s.ids.get('setup-add').onclick();
  assert.equal(s.cameraTabs().length, 3); assert.equal(s.cameraTabs()[2].textContent, '3. Camera 3');
  assert.equal(s.cameraSections()[2].hidden, false); assert.equal(s.document.activeElement, s.cameraTabs()[2]);
  for (const [id, value] of [['preset-host', '192.0.2.1'], ['preset-port', '554'], ['preset-first', '5'], ['preset-count', '2'], ['preset-path', '/ip{channel:00}/{stream}']]) s.ids.get(id).value = value;
  s.ids.get('preset-add').onclick();
  assert.equal(s.cameraTabs().length, 5); assert.equal(s.cameraTabs()[4].textContent, '5. Camera 6');
  assert.equal(s.cameraSections()[4].querySelectorAll('h3')[0].textContent, '5. Camera 6 - Recorder template');
  while (s.cameraSections().length) {
    const sections = s.cameraSections(), selected = sections.find(section => !section.hidden);
    selected.querySelectorAll('button').find(button => button.textContent === 'Remove').onclick();
    assert.equal(s.cameraSections().length, sections.length - 1);
    if (sections.length > 1) {
      assert.equal(s.cameraSections().filter(section => !section.hidden).length, 1);
      assert.equal(s.document.activeElement.getAttribute('aria-selected'), 'true');
    }
  }
  assert.equal(s.cameraTabs().length, 0); assert.equal(s.document.activeElement, s.ids.get('setup-add'));
  s.ids.get('setup-add').onclick(); assert.equal(s.cameraTabs()[0].textContent, '1. Camera 1');
  assert.equal(s.cameraSections()[0].hidden, false);
});

test('setup loads old and saved video timeouts, and saves the edited value through the configuration API', async () => {
  for (const saved of [undefined, 60]) {
    const s = setup(saved); await s.open();
    const input = s.ids.get('setup-video-timeout'); assert.equal(input.value, saved ?? 0);
    input.value = '300'; input.dispatchEvent(new Event('input'));
    await s.ids.get('setup-save').onclick();
    const request = s.requests.find(request => request.options.method === 'POST');
    assert.equal(JSON.parse(request.options.body).settings.videoTimeoutSeconds, 300);
    assert.equal(input.value, 300);
  }
});

test('setup loads legacy defaults and custom retry schedules and saves the edited schedule', async () => {
  for (const saved of [undefined, '3, 6, 12']) {
    const s = setup(0, 1, undefined, saved); await s.open();
    const input = s.ids.get('setup-retry-delays');
    assert.equal(input.value, saved ?? '1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32');
    input.value = ' 2, 5, 9 '; input.dispatchEvent(new Event('input'));
    await s.ids.get('setup-save').onclick();
    const request = s.requests.find(request => request.options.method === 'POST');
    assert.equal(JSON.parse(request.options.body).settings.retryDelaysSeconds, '2, 5, 9');
    assert.equal(input.value, '2, 5, 9');
  }
});

test('invalid retry schedules block saving and restoring defaults clears validation and marks the draft dirty', async () => {
  const s = setup(0, 1, undefined, '9'); await s.open();
  const input = s.ids.get('setup-retry-delays');
  for (const invalid of ['', ' ', '0', '-1', '1.5', '1,,2', '1,', ',1', 'abc', '+1', '1e2', '2147483648']) {
    input.value = invalid; await s.ids.get('setup-save').onclick();
    assert.match(input.validationMessage, /positive whole seconds/);
  }
  assert.equal(s.requests.filter(request => request.options.method === 'POST').length, 0);
  s.ids.get('setup-retry-default').onclick();
  assert.equal(input.value, '1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32');
  assert.equal(input.validationMessage, '');
  // Unsaved defaults participate in the existing discard-changes protection.
  assert.throws(() => s.ids.get('setup-close').onclick(), /must not ask to discard/);
  await s.ids.get('setup-save').onclick();
  assert.equal(JSON.parse(s.requests.find(request => request.options.method === 'POST').options.body).settings.retryDelaysSeconds, input.value);
});

test('editing after a retry validation failure clears the error and a single delay or long schedule saves', async () => {
  const s = setup(0); await s.open(); const input = s.ids.get('setup-retry-delays');
  input.value = 'bad'; await s.ids.get('setup-save').onclick();
  for (const valid of ['7', '2147483647', Array(1000).fill('1').join(',')]) {
    input.value = valid; input.dispatchEvent(new Event('input'));
    assert.equal(input.validationMessage, '');
    await s.ids.get('setup-save').onclick(); assert.equal(input.value, valid);
  }
  assert.equal(s.requests.filter(request => request.options.method === 'POST').length, 3);
});

test('duplicating and importing a configuration preserve retry schedules and old imports receive defaults', async () => {
  const s = setup(0, 1, undefined, '9'); await s.open();
  s.ids.get('setup-retry-delays').value = '2, 6'; s.ids.get('setup-copy').onclick();
  assert.equal(s.ids.get('setup-retry-delays').value, '2, 6');
  await s.ids.get('setup-save').onclick();
  const saved = JSON.parse(s.requests.find(request => request.options.method === 'POST').options.body).settings;
  assert.equal(saved.retryDelaysSeconds, '2, 6');
  s.allowConfirmation();
  for (const retryDelaysSeconds of ['4, 10', undefined]) {
    await s.ids.get('setup-import-config').onchange({ target: { files: [{ size: 1000, text: async () => JSON.stringify({ ...saved, retryDelaysSeconds }) }] } });
    assert.equal(s.ids.get('setup-retry-delays').value, retryDelaysSeconds ?? '1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32');
  }
});

test('setup loads default and fractional poll intervals and saves seconds without truncation', async () => {
  for (const [saved, edited] of [[undefined, 0.1], [1, 0.5], [0.5, 1.6]]) {
    const s = setup(0, saved); await s.open();
    const input = s.cameraSections()[0].querySelectorAll('input').find(input => input.parent.textContent?.startsWith('Poll interval'));
    assert.equal(input.value, saved ?? 1);
    assert.equal(input.min, '0.1'); assert.equal(input.max, '3600'); assert.equal(input.step, '0.1');
    assert.equal(input.disabled, false);
    input.value = String(edited); input.oninput();
    await s.ids.get('setup-save').onclick();
    const request = s.requests.find(request => request.options.method === 'POST');
    assert.equal(JSON.parse(request.options.body).settings.sources[0].pollIntervalSeconds, edited);
    const reloaded = s.cameraSections()[0].querySelectorAll('input').find(input => input.parent.textContent?.startsWith('Poll interval'));
    assert.equal(reloaded.value, edited);
  }
});

test('setup rejects blank, negative, fractional and overflowing video timeouts before saving', async () => {
  const s = setup(); await s.open();
  for (const invalid of ['', '-1', '1.5', '2147483648']) {
    s.ids.get('setup-video-timeout').value = invalid;
    await s.ids.get('setup-save').onclick();
  }
  assert.equal(s.requests.filter(request => request.options.method === 'POST').length, 0);
});

test('duplicating a collection retains an unsaved video timeout in the new draft', async () => {
  const s = setup(60); await s.open();
  s.ids.get('setup-video-timeout').value = '300'; s.ids.get('setup-copy').onclick();
  assert.equal(s.ids.get('setup-video-timeout').value, 300);
  await s.ids.get('setup-save').onclick();
  const saved = JSON.parse(s.requests.find(request => request.options.method === 'POST').options.body).settings;
  assert.equal(saved.videoTimeoutSeconds, 300); assert.notEqual(saved.configurationId, 'default');
});

test('sparse saved channels display consecutive positions and removal renumbers without retargeting cameras', async () => {
  const cameras = [3, 2, 4, 5, 1, 6].map(channel => ({ id: `id-${channel}`, channel, name: `Feed ${channel}`,
    mainStream: `rtsp://camera/ip${channel}/0`, enabled: channel !== 1 && channel !== 6 }));
  const s = setup(0, 1, cameras); await s.open(); s.allowConfirmation();
  assert.deepEqual(s.cameraTabs().map(tab => tab.textContent), cameras.map((camera, i) => `${i + 1}. ${camera.name}`));
  s.cameraSections()[0].querySelectorAll('button').find(button => button.textContent === 'Remove').onclick();
  assert.equal(s.cameraTabs()[0].textContent, '1. Feed 2');
  s.ids.get('setup-add').onclick();
  assert.equal(s.cameraTabs()[5].textContent, '6. Camera 6');
  await s.ids.get('setup-save').onclick();
  const saved = JSON.parse(s.requests.find(request => request.options.method === 'POST').options.body).settings;
  assert.deepEqual(saved.sources.slice(0, 5).map(camera => [camera.id, camera.channel]), cameras.slice(1).map(camera => [camera.id, camera.channel]));
  assert.equal(saved.sources[5].channel, 3);
});

test('RTSP addition, recorder batches and portable imports exceed sixteen cameras', async () => {
  const s = setup(0); await s.open();
  for (let i = 0; i < 18; i++) s.ids.get('setup-add').onclick();
  assert.equal(s.cameraTabs().length, 20); assert.equal(s.cameraTabs()[19].textContent, '20. Camera 20');
  for (const [id, value] of [['preset-host', '192.0.2.1'], ['preset-port', '554'], ['preset-first', '1'], ['preset-count', '20'], ['preset-path', '/ip{channel:00}/{stream}']]) s.ids.get(id).value = value;
  s.ids.get('preset-add').onclick();
  assert.equal(s.cameraTabs().length, 40); assert.equal(s.cameraTabs()[39].textContent, '40. Camera 20');
  await s.ids.get('setup-save').onclick();
  const saved = JSON.parse(s.requests.find(request => request.options.method === 'POST').options.body).settings;
  assert.equal(saved.sources.length, 40);
  assert.equal(new Set(saved.sources.map(camera => camera.channel)).size, 40);
  s.allowConfirmation();
  await s.ids.get('setup-import-config').onchange({ target: { files: [{ size: 10000, text: async () => JSON.stringify(saved) }] } });
  assert.equal(s.cameraTabs().length, 40);
  assert.equal(s.cameraTabs()[39].textContent, '40. Camera 20');
  assert.match(s.ids.get('setup-result').textContent, /Imported as a new draft/);
});

test('each camera saves all four playback preferences independently without losing secrets or timeout settings', async () => {
  for (const method of ['automatic', 'native', 'browser', 'server']) {
    const s = setup(120); await s.open();
    const select = s.cameraSections()[0].querySelectorAll('select').find(element => element.dataset.playbackChannel === '1');
    assert.deepEqual(select.options.map(option => option.value), ['automatic', 'native', 'browser', 'server']);
    select.value = method; select.onchange();
    await s.ids.get('setup-save').onclick(); await new Promise(resolve => setImmediate(resolve));
    const settings = JSON.parse(s.requests.find(request => request.path.endsWith('/configurations') && request.options.method === 'POST').options.body).settings;
    assert.equal(settings.sources[0].playbackMethod, method); assert.equal(settings.videoTimeoutSeconds, 120);
    assert.equal(settings.sources[1].playbackMethod, undefined);
  }
});
test('Automatic explains the planned method and CPU warning using actual stream support', async () => {
  const s = setup(120);
  s.playback.reports.set(1, { nativeSupported: false, confirmed: true, message: 'Automatic to Browser software decoding. Warning: extra client CPU.', framesPerSecond: 10, dropped: 2 });
  await s.open();
  const section = s.cameraSections()[0], select = section.querySelectorAll('select').find(element => element.dataset.playbackChannel === '1');
  assert.match(select.options[1].textContent, /unavailable/);
  assert.match(select.options[2].textContent, /client CPU/);
  const help = section.querySelectorAll('p').find(element => element.className === 'help camera-playback-help');
  assert.match(help.textContent, /On this device.*Automatic.*Browser software decoding/);
  assert.match(help.textContent, /client CPU/); assert.match(help.textContent, /10.0 fps; 2 frames skipped/);
});
test('unavailable browser APIs are labeled and inactive cameras explain benchmark requirements', async () => {
  const s = setup(120); s.playback.capabilities = () => ({ native: false, browser: false }); await s.open();
  const section = s.cameraSections()[0], select = section.querySelectorAll('select').find(element => element.dataset.playbackChannel === '1');
  assert.match(select.options[1].textContent, /unavailable/); assert.match(select.options[2].textContent, /unavailable/);
  assert.match(section.querySelectorAll('p').find(element => element.className === 'help camera-playback-help').textContent, /Automatic.*Server JPEG.*server CPU/);
  section.querySelectorAll('button').find(button => button.textContent === 'Export playback benchmark').onclick();
  assert.match(s.ids.get('setup-result').textContent, /Activate and view/);
});

test('Automatic retains known decoder failures when explaining the next available method', async () => {
  const s = setup(120);
  s.playback.reports.set(1, { nativeSupported: true, confirmed: true, failedMethods: ['native', 'browser'],
    message: 'Automatic uses Server JPEG previews because both decoders failed.', framesPerSecond: 10, dropped: 0 });
  await s.open();
  const section = s.cameraSections()[0], select = section.querySelectorAll('select').find(element => element.dataset.playbackChannel === '1');
  assert.match(select.options[1].textContent, /unavailable/); assert.match(select.options[2].textContent, /failed/);
  const help = section.querySelectorAll('p').find(element => element.className === 'help camera-playback-help');
  assert.match(help.textContent, /On this device.*Automatic.*Server JPEG previews/);
});
