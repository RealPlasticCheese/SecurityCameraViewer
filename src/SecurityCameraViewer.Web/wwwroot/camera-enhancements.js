'use strict';

const CameraEnhancements = (() => {
  const controls = [
    { key: 'brightness', name: 'Brightness', min: -50, max: 50, step: 1 },
    { key: 'contrast', name: 'Contrast', min: -50, max: 50, step: 1 },
    { key: 'gamma', name: 'Gamma / shadows', min: 0.5, max: 2, step: 0.05 },
    { key: 'denoise', name: 'Noise reduction', min: 0, max: 100, step: 1 },
    { key: 'sharpness', name: 'Sharpening', min: 0, max: 100, step: 1 },
    { key: 'radius', name: 'Sharpening radius', min: 0.5, max: 4, step: 0.1 },
    { key: 'saturation', name: 'Saturation', min: -100, max: 50, step: 1 },
    { key: 'vibrance', name: 'Vibrance', min: -50, max: 50, step: 1 }
  ];
  const presets = {
    balanced: { brightness: 0, contrast: 8, gamma: 1, denoise: 0, sharpness: 30, radius: 1.2, saturation: 0, vibrance: 0 },
    lowLight: { brightness: 5, contrast: 8, gamma: 1.35, denoise: 60, sharpness: 20, radius: 1, saturation: -10, vibrance: 0 },
    detail: { brightness: 0, contrast: 12, gamma: 1, denoise: 20, sharpness: 65, radius: 1.5, saturation: 0, vibrance: 5 }
  };

  function normalize(values) {
    return Object.fromEntries(controls.map(control => {
      const value = Number(values[control.key]);
      return [control.key, Number.isFinite(value) ? Math.max(control.min, Math.min(control.max, value)) : presets.balanced[control.key]];
    }));
  }

  function apply(canvas, texture, values) {
    const settings = normalize(values);
    canvas.draw(texture);
    // Remove noise before sharpening so grain is not amplified with the edges.
    if (settings.denoise > 0) canvas.denoise(50 - settings.denoise * 0.4);
    if (settings.gamma !== 1) {
      canvas.curves(Array.from({ length: 9 }, (_, i) => [i / 8, Math.pow(i / 8, 1 / settings.gamma)]));
    }
    if (settings.brightness || settings.contrast) canvas.brightnessContrast(settings.brightness / 100, settings.contrast / 100);
    if (settings.saturation) canvas.hueSaturation(0, settings.saturation / 100);
    if (settings.vibrance) canvas.vibrance(settings.vibrance / 100);
    if (settings.sharpness > 0) canvas.unsharpMask(settings.radius, settings.sharpness / 50);
    canvas.update();
  }

  return { controls, presets, normalize, apply };
})();

// Persistence owns save ordering and protects local edits from older polling responses.
class CameraEnhancementPreferences {
  constructor(value = {}, save = null, { delayMilliseconds = 200, setTimer = (callback, delay) => setTimeout(callback, delay), clearTimer = id => clearTimeout(id) } = {}) {
    this.value = { enabled: !!value.enabled, ...CameraEnhancements.normalize(value) };
    this.save = save;
    this.delayMilliseconds = delayMilliseconds;
    this.setTimer = setTimer;
    this.clearTimer = clearTimer;
    this.revision = 0;
    this.pending = {};
    this.saving = false;
    this.error = false;
  }

  update(values) {
    const next = { ...this.value, ...values };
    const normalized = { enabled: !!next.enabled, ...CameraEnhancements.normalize(next) };
    const changes = Object.fromEntries(Object.entries(normalized).filter(([key, value]) => value !== this.value[key]));
    if (Object.keys(changes).length) {
      this.value = normalized;
      ++this.revision;
      if (this.save) Object.assign(this.pending, changes);
    }
    if (this.save && Object.keys(this.pending).length) {
      this.clearTimer(this.timer);
      this.timer = this.setTimer(() => this.flush(), this.delayMilliseconds);
    }
    this.changed?.();
  }

  async flush() {
    this.clearTimer(this.timer);
    this.timer = null;
    if (this.saving || !this.save || !Object.keys(this.pending).length) return;
    this.saving = true;
    this.error = false;
    this.changed?.();
    try {
      while (Object.keys(this.pending).length) {
        const patch = this.pending;
        this.pending = {};
        try { await this.save(patch); }
        catch {
          this.pending = { ...patch, ...this.pending };
          this.error = true;
          break;
        }
      }
    } finally {
      this.saving = false;
      this.changed?.();
    }
  }

  synchronize(value, revision) {
    if (revision !== this.revision || this.saving || Object.keys(this.pending).length) return false;
    const next = { enabled: !!value?.enabled, ...CameraEnhancements.normalize(value || {}) };
    if (JSON.stringify(next) === JSON.stringify(this.value)) return false;
    this.value = next;
    ++this.revision;
    return true;
  }
}

// History belongs to one open options session; saved camera preferences have no undo history.
class CameraEnhancementHistory {
  reset(snapshot) { this.entries = [this.copy(snapshot)]; this.index = 0; }
  copy(snapshot) { return { settings: { ...snapshot.settings }, preset: snapshot.preset }; }
  get current() { return this.entries[this.index]; }
  get canUndo() { return this.index > 0; }
  get canRedo() { return this.index < this.entries.length - 1; }
  record(snapshot) {
    if (JSON.stringify(snapshot) === JSON.stringify(this.current)) return;
    this.entries.splice(this.index + 1);
    this.entries.push(this.copy(snapshot));
    ++this.index;
  }
  undo() { return this.canUndo ? this.copy(this.entries[--this.index]) : null; }
  redo() { return this.canRedo ? this.copy(this.entries[++this.index]) : null; }
}

// Load the pinned local library only for cameras with enhancements enabled.
const CameraEnhancementLibrary = {
  load(document) {
    if (document.defaultView.fx) return Promise.resolve(document.defaultView.fx);
    if (!this.pending) {
      this.pending = new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = '/vendor/glfx/glfx.js';
        script.onload = () => {
          if (document.defaultView.fx) resolve(document.defaultView.fx);
          else { this.pending = null; script.remove(); reject(new Error('Image processing library unavailable.')); }
        };
        script.onerror = () => { this.pending = null; script.remove(); reject(new Error('Image processing library unavailable.')); };
        document.head.append(script);
      });
    }
    return this.pending;
  }
};

// Pixel processing has no responsibility for camera playback, zoom state or controls.
class CameraEnhancementProcessor {
  constructor(library, document) {
    this.canvas = library.canvas();
    this.input = document.createElement('canvas');
    this.context = this.input.getContext('2d', { alpha: false });
    this.texture = null;
  }

  render(media, settings) {
    const width = media.videoWidth || media.naturalWidth || media.width;
    const height = media.videoHeight || media.naturalHeight || media.height;
    // Bound work to 1280 pixels on either axis and 720p total pixels, preserving aspect ratio.
    const scale = Math.min(1, 1280 / Math.max(width, height), Math.sqrt(1280 * 720 / (width * height)));
    const w = Math.max(1, Math.round(width * scale)), h = Math.max(1, Math.round(height * scale));
    if (this.input.width !== w || this.input.height !== h) { this.input.width = w; this.input.height = h; }
    this.context.drawImage(media, 0, 0, w, h);
    if (!this.texture) this.texture = this.canvas.texture(this.input);
    else this.texture.loadContentsOf(this.input);
    CameraEnhancements.apply(this.canvas, this.texture, settings);
  }

  dispose() {
    this.texture?.destroy();
    this.texture = null;
    // Release the entire context, including filter shaders and internal buffers.
    this.canvas.getContext('experimental-webgl')?.getExtension('WEBGL_lose_context')?.loseContext();
    this.canvas.remove();
    this.input.width = this.input.height = 1;
  }
}

class CameraEnhancementView {
  constructor(root, zoom, loadLibrary = document => CameraEnhancementLibrary.load(document),
    createProcessor = (library, document) => new CameraEnhancementProcessor(library, document),
    preferences = new CameraEnhancementPreferences()) {
    this.root = root;
    this.zoom = zoom;
    this.surface = root.querySelector('.surface');
    this.document = root.ownerDocument;
    this.window = this.document.defaultView;
    this.loadLibrary = loadLibrary;
    this.createProcessor = createProcessor;
    this.preferences = preferences;
    this.enabled = preferences.value.enabled;
    this.history = new CameraEnhancementHistory();
    this.optionsOpen = false;
    this.focused = false;
    this.visible = true;
    this.settings = CameraEnhancements.normalize(preferences.value);
    this.version = 0;
    this.frame = null;
    this.processor = null;
    this.inputs = new Map();
    this.events = new AbortController();
    const options = { signal: this.events.signal };
    const document = this.document;

    const toggleLabel = document.createElement('div');
    toggleLabel.className = 'camera-enhancement-toggle';
    this.toggle = document.createElement('button');
    this.toggle.type = 'button';
    this.toggle.className = 'camera-enhancement-disclosure';
    this.toggle.setAttribute('aria-label', 'Show enhancement options');
    this.toggle.setAttribute('aria-expanded', 'false');
    const chevron = document.createElement('span');
    chevron.className = 'camera-enhancement-chevron';
    chevron.setAttribute('aria-hidden', 'true');
    this.toggle.append(chevron, document.createTextNode('Enhance'));
    this.panel = document.createElement('div');
    this.panel.className = 'camera-enhancement-options';
    this.panel.hidden = true;
    this.panel.setAttribute('role', 'group');
    this.panel.setAttribute('aria-label', 'Picture enhancement settings');
    const enableLabel = document.createElement('label');
    enableLabel.className = 'camera-enhancement-enable';
    this.enabledToggle = document.createElement('input');
    this.enabledToggle.type = 'checkbox';
    this.enabledToggle.setAttribute('role', 'switch');
    this.enabledToggle.setAttribute('aria-label', 'Enable picture enhancements');
    enableLabel.append(this.enabledToggle);
    toggleLabel.append(this.toggle, enableLabel);
    zoom.toolbar.append(toggleLabel);
    const presets = document.createElement('label');
    presets.className = 'camera-enhancement-preset';
    presets.append(document.createTextNode('Preset'));
    this.preset = document.createElement('select');
    for (const [value, text] of [['balanced', 'Balanced'], ['lowLight', 'Low light'], ['detail', 'Detail'], ['custom', 'Custom']]) {
      const option = document.createElement('option'); option.value = value; option.textContent = text; this.preset.append(option);
    }
    this.preset.setAttribute('aria-label', 'Enhancement preset');
    presets.append(this.preset);
    this.panel.append(presets);
    for (const control of CameraEnhancements.controls) {
      const label = document.createElement('label');
      const caption = document.createElement('span'); caption.textContent = control.name;
      const output = document.createElement('output');
      const input = document.createElement('input'); input.type = 'range';
      input.min = control.min; input.max = control.max; input.step = control.step;
      input.setAttribute('aria-label', control.name);
      label.append(caption, output, input);
      this.panel.append(label);
      this.inputs.set(control.key, { input, output });
      input.addEventListener('input', () => {
        if (this.sliderEditing && this.sliderEditing !== control.key) this.commitSlider();
        this.settings = CameraEnhancements.normalize({ ...this.settings, [control.key]: input.value });
        this.sliderEditing = control.key;
        this.preset.value = 'custom'; this.updateControls(); this.invalidate();
        this.preferences.update(this.settings);
        this.updateHistoryButtons();
      }, options);
      input.addEventListener('change', () => { this.commitSlider(); this.preferences.flush(); }, options);
      input.addEventListener('blur', () => { this.commitSlider(); this.preferences.flush(); }, options);
    }
    const reset = document.createElement('button'); reset.type = 'button'; reset.textContent = 'Reset settings';
    reset.setAttribute('aria-label', 'Reset enhancement settings');
    this.resetButton = reset;
    this.undoButton = document.createElement('button'); this.undoButton.type = 'button'; this.undoButton.textContent = 'Undo';
    this.undoButton.setAttribute('aria-label', 'Undo enhancement change');
    this.redoButton = document.createElement('button'); this.redoButton.type = 'button'; this.redoButton.textContent = 'Redo';
    this.redoButton.setAttribute('aria-label', 'Redo enhancement change');
    const actions = document.createElement('div'); actions.className = 'camera-enhancement-actions';
    actions.append(reset, this.undoButton, this.redoButton);
    this.panel.append(actions);
    this.status = document.createElement('p'); this.status.className = 'camera-enhancement-status'; this.status.hidden = true;
    this.status.setAttribute('role', 'status');
    this.saveStatus = document.createElement('p'); this.saveStatus.className = 'camera-enhancement-status';
    this.saveStatus.setAttribute('role', 'status');
    this.retryButton = document.createElement('button'); this.retryButton.type = 'button'; this.retryButton.textContent = 'Retry saving';
    this.retryButton.setAttribute('aria-label', 'Retry saving');
    this.retryButton.addEventListener('click', () => this.preferences.flush(), options);
    zoom.help.append(this.panel, this.status, this.saveStatus, this.retryButton);
    preferences.changed = () => this.updateSaveStatus();
    this.toggle.addEventListener('click', () => this.setOptionsOpen(!this.optionsOpen), options);
    this.enabledToggle.addEventListener('change', () => this.setEnabled(this.enabledToggle.checked, true), options);
    this.preset.addEventListener('change', () => {
      const preset = CameraEnhancements.presets[this.preset.value];
      if (preset) this.editSettings(preset, this.preset.value);
    }, options);
    reset.addEventListener('click', () => {
      this.editSettings(CameraEnhancements.presets.balanced, 'balanced');
    }, options);
    this.undoButton.addEventListener('click', () => this.restoreHistory('undo'), options);
    this.redoButton.addEventListener('click', () => this.restoreHistory('redo'), options);
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.preferences.flush(); this.refresh(); }, options);
    this.window.addEventListener('pagehide', () => { this.preferences.flush(); this.stop(); }, options);
    this.window.addEventListener('pageshow', () => this.refresh(), options);
    this.preset.value = this.matchingPreset();
    this.enabledToggle.checked = this.enabled;
    this.history.reset(this.snapshot());
    this.updateControls();
    this.updateHistoryButtons();
    this.updateSaveStatus();
  }

  matchingPreset() {
    return Object.entries(CameraEnhancements.presets).find(([, values]) =>
      CameraEnhancements.controls.every(({ key }) => values[key] === this.settings[key]))?.[0] || 'custom';
  }

  snapshot() { return { settings: { ...this.settings }, preset: this.preset.value }; }

  commitSlider() {
    if (this.sliderEditing && this.optionsOpen) this.history.record({ settings: { ...this.settings }, preset: 'custom' });
    this.sliderEditing = false;
    this.updateHistoryButtons();
  }

  editSettings(settings, preset) {
    this.commitSlider();
    this.settings = CameraEnhancements.normalize(settings);
    this.preset.value = preset;
    if (this.optionsOpen) this.history.record(this.snapshot());
    this.updateControls(); this.updateHistoryButtons(); this.invalidate();
    this.preferences.update(this.settings);
    this.preferences.flush();
  }

  restoreHistory(direction) {
    this.commitSlider();
    const snapshot = this.history[direction]();
    if (!snapshot) return;
    this.settings = snapshot.settings;
    this.preset.value = snapshot.preset;
    this.updateControls(); this.updateHistoryButtons(); this.invalidate();
    this.preferences.update(this.settings);
    this.preferences.flush();
  }

  updateHistoryButtons() {
    const uncommitted = this.sliderEditing && JSON.stringify(this.snapshot()) !== JSON.stringify(this.history.current);
    this.undoButton.disabled = !this.history.canUndo && !uncommitted;
    this.redoButton.disabled = !this.history.canRedo || !!uncommitted;
  }

  updateSaveStatus() {
    const pending = this.preferences.saving || Object.keys(this.preferences.pending).length > 0;
    this.saveStatus.textContent = this.preferences.error ? 'Settings could not be saved. Retry to keep these changes after leaving this page.'
      : pending ? 'Saving settings…' : '';
    this.saveStatus.hidden = !this.saveStatus.textContent;
    this.retryButton.hidden = !this.preferences.error;
  }

  synchronizePreferences(value, revision) {
    if (this.optionsOpen || !this.preferences.synchronize(value, revision)) return;
    this.settings = CameraEnhancements.normalize(this.preferences.value);
    this.enabled = this.preferences.value.enabled;
    this.enabledToggle.checked = this.enabled;
    this.preset.value = this.matchingPreset();
    this.updateControls(); this.invalidate();
  }

  updateControls() {
    for (const [key, { input, output }] of this.inputs) {
      input.value = this.settings[key]; output.value = String(this.settings[key]);
    }
  }

  message(text) {
    if (this.status.textContent !== text) this.status.textContent = text;
    this.status.hidden = !text;
  }

  setFocused(focused, visible = true) {
    this.focused = focused;
    this.visible = visible;
    if (!focused) this.setOptionsOpen(false);
    this.refresh();
  }

  setOptionsOpen(open) {
    const next = !!open && this.focused;
    if (this.optionsOpen && !next) { this.commitSlider(); this.preferences.flush(); }
    if (!this.optionsOpen && next) { this.history.reset(this.snapshot()); this.updateHistoryButtons(); }
    this.optionsOpen = next;
    this.toggle.setAttribute('aria-expanded', String(this.optionsOpen));
    this.toggle.setAttribute('aria-label', this.optionsOpen ? 'Hide enhancement options' : 'Show enhancement options');
    this.panel.hidden = !this.optionsOpen;
    this.zoom.help.classList.toggle('enhancement-options-open', this.optionsOpen);
  }

  setEnabled(enabled, persist = false) {
    this.enabled = !!enabled && this.focused;
    this.enabledToggle.checked = this.enabled;
    this.message('');
    this.refresh();
    if (persist) { this.preferences.update({ enabled: this.enabled }); this.preferences.flush(); }
  }

  media() {
    if (this.root.classList.contains('rewind-active') && !this.root.classList.contains('live-media-ready')) return null;
    const media = this.root.classList.contains('live-media-ready') ? this.surface.querySelector('.live-camera-video') || this.surface.querySelector('video') :
      this.root.classList.contains('grid-media-ready') ? (this.surface.querySelector('.grid-camera-canvas') || this.surface.querySelector('.grid-camera-video')) :
      this.root.classList.contains('has-frame') ? this.surface.querySelector('img') : null;
    return (media?.videoWidth || media?.naturalWidth || media?.width) > 0 && (media?.videoHeight || media?.naturalHeight || media?.height) > 0 ? media : null;
  }

  hasViewport() {
    const { width, height } = this.surface.getBoundingClientRect();
    return width > 0 && height > 0;
  }

  refresh() {
    if (!this.visible || !this.enabled || this.document.hidden || !this.hasViewport() || !this.media()) { this.stop(); return; }
    this.position();
    if (!this.processor) { this.start(); return; }
    this.schedule();
  }

  async start() {
    if (this.loading) return;
    const version = this.version;
    this.loading = true;
    this.message('Loading enhancements…');
    try {
      const library = await this.loadLibrary(this.document);
      if (version !== this.version || !this.enabled || !this.visible || this.document.hidden || !this.hasViewport() || !this.media()) return;
      const processor = this.processor = this.createProcessor(library, this.document);
      const canvas = processor.canvas;
      canvas.className = 'camera-enhanced-picture';
      canvas.setAttribute('aria-hidden', 'true');
      canvas.hidden = true;
      canvas.addEventListener('webglcontextlost', () => {
        if (this.processor === processor) this.fail();
      }, { signal: this.events.signal });
      this.surface.append(canvas);
      this.dirty = true;
      this.schedule();
    } catch {
      if (version === this.version) this.fail();
    } finally {
      if (version === this.version) this.loading = false;
    }
  }

  schedule() {
    if (this.frame === null) this.frame = this.window.requestAnimationFrame(time => this.draw(time));
  }

  invalidate() {
    this.dirty = true;
    this.refresh();
  }

  draw(time) {
    this.frame = null;
    const media = this.media();
    if (!this.enabled || !this.visible || this.document.hidden || !this.hasViewport() || !media) { this.stop(); return; }
    const video = media.tagName === 'VIDEO';
    const canvas = media.tagName === 'CANVAS';
    const ready = video ? media.readyState >= 2 : canvas || media.complete;
    const key = video ? media.currentTime : canvas ? media.frameVersion : media.currentSrc || media.src;
    if (ready && (this.dirty || media !== this.lastMedia || key !== this.lastKey) &&
        (this.lastTime === undefined || time - this.lastTime >= 100)) {
      try {
        this.processor.render(media, this.settings);
        this.processor.canvas.hidden = false;
        this.root.classList.add('enhancement-ready');
        this.lastMedia = media; this.lastKey = key; this.lastTime = time; this.dirty = false;
        this.message('');
        this.position();
      } catch { this.fail(); return; }
    }
    // JPEG updates arrive through the zoom view's load handler. Video needs its own clock.
    if (video || canvas || this.dirty || ready && (media !== this.lastMedia || key !== this.lastKey)) this.schedule();
  }

  position() {
    if (!this.processor) return;
    const canvas = this.processor.canvas, viewport = this.surface.getBoundingClientRect();
    this.zoom.position(canvas, viewport);
  }

  stop() {
    ++this.version;
    this.loading = false;
    if (this.frame !== null) this.window.cancelAnimationFrame(this.frame);
    this.frame = null;
    const processor = this.processor; this.processor = null;
    this.root.classList.remove('enhancement-ready');
    processor?.dispose();
    this.lastMedia = this.lastKey = this.lastTime = undefined;
  }

  fail() {
    this.setEnabled(false);
    this.message('Enhancements unavailable. Original picture shown; you can try enabling again.');
  }

  dispose() {
    this.setEnabled(false);
    this.setOptionsOpen(false);
    this.preferences.changed = null;
    this.preferences.flush();
    this.events.abort();
  }
}
