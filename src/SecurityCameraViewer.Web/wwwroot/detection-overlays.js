'use strict';

// Presentation consumes the same observation/evidence contract for every detector and model.
const DetectionOverlay = (() => {
  const title = value => String(value || '').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]/g, ' ').replace(/^./, c => c.toUpperCase());
  function attribute(value) {
    if (value.text != null) return value.name === 'reason' ? ({ 'below-area': 'Below moving-area threshold', debouncing: 'Waiting for motion confirmation',
      'region-persistence': 'Waiting for region persistence', confirmed: 'Motion confirmed' }[value.text] || String(value.text)) : String(value.text);
    if (!Number.isFinite(value.number)) return '';
    if (value.unit === 'ratio') return `${(value.number * 100).toFixed(0)}%`;
    return `${Number(value.number.toFixed(2))}${value.unit === '%' ? '%' : value.unit ? ` ${value.unit}` : ''}`;
  }
  function label(observation, methods) {
    const values = observation.sources.flatMap(source => source.attributes || []);
    const object = values.find(value => value.name === 'objectType');
    const confidence = values.find(value => value.name === 'confidence');
    const names = [...new Set(observation.sources.map(source => methods.find(method => method.id === source.method)?.shortName || title(source.method)))];
    return [object ? title(object.text) : observation.kind === 'motion' ? 'Movement' : title(observation.kind), confidence ? attribute(confidence) : null,
      names.join(' + '), observation.disposition === 'ignore' ? 'ignored' : observation.confirmed ? null : 'candidate'].filter(Boolean).join(' · ');
  }
  function details(observation, methods, models, age) {
    const rows = [['Status', observation.disposition === 'ignore' ? 'Ignored object — cannot trigger actions' : observation.confirmed ? 'Detection confirmed' : 'Candidate / motion gate'],
      ['Zones', observation.zones.map(zone => zone.name).join(', ') || 'Whole active image'],
      ['Detection age', `${age.toFixed(1)} s`], ['Captured', new Date(observation.capturedAt).toLocaleString()]];
    for (const source of observation.sources) {
      const name = methods.find(method => method.id === source.method)?.name || title(source.method);
      rows.push(['Method', name], ['Evidence captured', new Date(source.capturedAt).toLocaleString()]);
      if (source.model) rows.push(['Model', models.find(model => model.id === source.model)?.name || source.model]);
      for (const value of source.attributes || []) rows.push([title(value.name), attribute(value)]);
    }
    return rows;
  }
  function geometry(box, picture, viewport, crop) {
    if (!picture || !box || ![box.x, box.y, box.width, box.height].every(Number.isFinite) ||
        box.x < 0 || box.y < 0 || box.width <= 0 || box.height <= 0 || box.x + box.width > 1.000001 || box.y + box.height > 1.000001) return null;
    const transform = crop ? CameraZoom.projection(crop, picture, viewport.width, viewport.height) : { x: 0, y: 0, scale: 1 };
    const x = transform.x + (picture.left + box.x * picture.width) * transform.scale;
    const y = transform.y + (picture.top + box.y * picture.height) * transform.scale;
    const right = Math.min(viewport.width, x + box.width * picture.width * transform.scale);
    const bottom = Math.min(viewport.height, y + box.height * picture.height * transform.scale);
    const left = Math.max(0, x), top = Math.max(0, y);
    return right > left && bottom > top ? { left, top, width: right - left, height: bottom - top } : null;
  }
  return { title, attribute, label, details, geometry };
})();

class CameraDetectionOverlay {
  constructor(root, zoom) {
    this.root = root; this.zoom = zoom; this.surface = root.querySelector('.surface');
    this.document = root.ownerDocument; this.entries = []; this.items = []; this.enabled = false; this.available = false;
    this.methods = []; this.models = []; this.selected = null; this.metadataVisible = true;
    this.frames = []; this.playbackTime = undefined;
    this.layer = this.document.createElement('div'); this.layer.className = 'detection-overlay'; this.layer.hidden = true;
    this.panel = this.document.createElement('section'); this.panel.className = 'detection-details'; this.panel.hidden = true;
    this.panel.setAttribute('aria-label', 'Detection details');
    const heading = this.document.createElement('strong'); heading.textContent = 'Detection details';
    const close = this.document.createElement('button'); close.type = 'button'; close.textContent = '×';
    close.setAttribute('aria-label', 'Close detection details');
    close.onclick = () => this.closeDetails();
    this.panel.addEventListener('dblclick', event => event.stopPropagation());
    this.panel.addEventListener('keydown', event => { if (event.key === 'Escape') { event.stopPropagation(); this.closeDetails(); } });
    this.definition = this.document.createElement('dl'); this.panel.append(heading, close, this.definition);
    this.surface.append(this.layer, this.panel);
    this.observer = new ResizeObserver(() => this.refresh()); this.observer.observe(this.surface);
  }
  setEnabled(value) { this.enabled = value; if (!value) this.clear(); else this.refresh(); }
  setMetadataVisible(value) {
    if (this.metadataVisible === value) return;
    this.metadataVisible = value; if (!value) this.selected = null; this.refresh();
  }
  setAvailable(value) { this.available = value; this.refresh(); }
  setPlaybackTime(value) {
    const previous = this.playbackTime;
    this.playbackTime = Number.isFinite(value) ? value : value === undefined ? undefined : null;
    const frame = this.currentFrame();
    if (frame !== this.presentedFrame || this.playbackTime === null && previous !== null ||
        this.nextFrameExpiry <= this.playbackTime) this.refresh();
  }
  updateHistory(history, methods, models, generatedAt, seconds, maximumSamples) {
    this.methods = methods || []; this.models = models || [];
    const generated = Date.parse(generatedAt);
    if (!history?.id || !Number.isFinite(generated) || !(seconds > 0) || !Number.isSafeInteger(maximumSamples) || maximumSamples <= 0) {
      this.clear(); return;
    }
    if (history.id !== this.historyId) { this.frames = []; this.selected = null; }
    this.historyId = history.id; this.hasHistory = true;
    this.serverTime = generated; this.serverReceived = performance.now();
    const frames = new Map(this.frames.map(frame => [frame.key, frame]));
    for (const value of history.frames || []) {
      const captured = Date.parse(value.capturedAt), published = Date.parse(value.publishedAt);
      if (!Number.isFinite(captured) || !Number.isFinite(published) || !Array.isArray(value.detections)) continue;
      const key = `${value.capturedAt}:${value.publishedAt}`;
      if (!frames.has(key)) frames.set(key, { ...value, captured, published, key });
    }
    this.frames = [...frames.values()].filter(frame => frame.published >= generated - seconds * 1000)
      .sort((a, b) => a.captured - b.captured || a.published - b.published).slice(-maximumSamples);
    this.refresh();
  }
  currentFrame() {
    if (!this.hasHistory || this.playbackTime === null) return null;
    const time = this.playbackTime ?? this.serverTime + performance.now() - this.serverReceived;
    let low = 0, high = this.frames.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if (this.frames[middle].captured <= time) low = middle + 1; else high = middle;
    }
    return low ? this.frames[low - 1] : null;
  }
  update(entries, methods, models, elapsed) {
    this.frames = []; this.hasHistory = false; this.historyId = null;
    const received = performance.now(); this.methods = methods || []; this.models = models || [];
    this.entries = (entries || []).slice(0, 128).filter(entry => Number.isFinite(entry.remainingSeconds) && entry.remainingSeconds > 0)
      .map(entry => ({ ...entry, expires: received + entry.remainingSeconds * 1000 - elapsed,
        age: Math.max(0, entry.ageSeconds || 0) + elapsed / 1000, received }));
    this.refresh();
  }
  refresh() {
    clearTimeout(this.expiry);
    const now = performance.now(), viewport = this.surface.getBoundingClientRect();
    this.presentedFrame = this.currentFrame(); this.nextFrameExpiry = Infinity;
    if (this.hasHistory) {
      const time = this.playbackTime ?? this.serverTime + now - this.serverReceived;
      this.entries = (this.presentedFrame?.detections || []).filter(observation => {
        const captured = Date.parse(observation.capturedAt), expires = Date.parse(observation.expiresAt);
        if (!Number.isFinite(captured) || !Number.isFinite(expires) || captured > time || expires <= time) return false;
        this.nextFrameExpiry = Math.min(this.nextFrameExpiry, expires); return true;
      }).map(observation => ({ observation, expires: Infinity, age: Math.max(0, (time - Date.parse(observation.capturedAt)) / 1000), received: now }));
    }
    const picture = this.enabled && this.available && this.playbackTime !== null && !this.document.hidden ? this.zoom.picture(viewport) : null;
    const visible = picture ? this.entries.filter(entry => entry.expires > now).map(entry => ({ entry,
      box: DetectionOverlay.geometry(entry.observation.bounds, picture, viewport, this.zoom.crop) })).filter(item => item.box) : [];
    this.layer.hidden = visible.length === 0;
    while (this.items.length < visible.length) {
      const region = this.document.createElement('div'); region.className = 'detection-box';
      const button = this.document.createElement('button'); button.type = 'button'; button.className = 'detection-label';
      const slot = this.items.length;
      button.onclick = () => { this.selected = this.selected === slot ? null : slot; this.refresh(); };
      button.addEventListener('dblclick', event => event.stopPropagation());
      button.addEventListener('keydown', event => { if (event.key === 'Escape') { event.stopPropagation(); this.closeDetails(); } });
      region.append(button); this.layer.append(region); this.items.push({ region, button });
    }
    this.items.forEach((item, index) => {
      const value = visible[index]; item.region.hidden = !value;
      if (!value) return;
      const { entry, box } = value, observation = entry.observation;
      const key = `${observation.disposition || 'trigger'}:${observation.kind}:${observation.sources.flatMap(source => source.attributes || []).find(attribute => attribute.name === 'objectType')?.text || ''}:${observation.zones.map(zone => zone.id).join(',')}`;
      if (item.key !== key && this.selected === index) this.selected = null;
      item.key = key; item.region.className = `detection-box ${observation.kind === 'object' ? 'object-detection' : 'motion-detection'}${observation.disposition === 'ignore' ? ' ignored-detection' : observation.confirmed ? '' : ' candidate-detection'}`;
      for (const field of ['left', 'top', 'width', 'height']) item.region.style[field] = `${box[field]}px`;
      item.button.hidden = !this.metadataVisible;
      item.button.style.top = `${box.top > 20 ? -20 : 0}px`; item.button.style.maxWidth = `${viewport.width - box.left}px`;
      item.button.textContent = DetectionOverlay.label(observation, this.methods);
      item.button.setAttribute('aria-expanded', String(this.selected === index));
      const rows = DetectionOverlay.details(observation, this.methods, this.models, entry.age + (now - entry.received) / 1000);
      item.button.title = rows.map(([name, value]) => `${name}: ${value}`).join('\n');
      item.button.setAttribute('aria-label', `${item.button.textContent}, show detection details`);
    });
    const selected = this.metadataVisible ? visible[this.selected] : null; this.panel.hidden = !selected;
    if (selected) {
      const entry = selected.entry;
      this.definition.replaceChildren(...DetectionOverlay.details(entry.observation, this.methods, this.models,
        entry.age + (now - entry.received) / 1000).flatMap(([name, value]) => {
        const term = this.document.createElement('dt'), detail = this.document.createElement('dd');
        term.textContent = name; detail.textContent = value; return [term, detail];
      }));
    } else this.selected = null;
    if (visible.length && !this.hasHistory) this.expiry = setTimeout(() => this.refresh(), Math.max(10, Math.min(...visible.map(item => item.entry.expires - now)) + 10));
  }
  closeDetails() { const selected = this.selected; this.selected = null; this.refresh(); this.items[selected]?.button.focus(); }
  clear() { this.entries = []; this.frames = []; this.hasHistory = false; this.historyId = null; this.selected = null; this.refresh(); }
  dispose() { this.enabled = false; this.clear(); this.observer.disconnect(); this.layer.remove(); this.panel.remove(); }
}

class DetectionOverlayController {
  constructor(toggle, metadataToggle, status, getTiles, getContext) {
    this.toggle = toggle; this.metadataToggle = metadataToggle; this.status = status; this.getTiles = getTiles; this.getContext = getContext; this.version = 0;
    try { toggle.checked = localStorage.getItem('scv.detection-overlays') === 'true'; } catch {}
    metadataToggle.checked = true;
    try { metadataToggle.checked = localStorage.getItem('scv.detection-overlay-metadata') !== 'false'; } catch {}
    this.events = new AbortController();
    toggle.onchange = () => { try { localStorage.setItem('scv.detection-overlays', String(toggle.checked)); } catch {} this.invalidate(); this.refresh(); };
    metadataToggle.onchange = () => { try { localStorage.setItem('scv.detection-overlay-metadata', String(metadataToggle.checked)); } catch {} this.refresh(); };
    document.addEventListener('visibilitychange', () => { this.invalidate(); this.refresh(); }, { signal: this.events.signal });
    window.addEventListener('pagehide', () => { this.suspended = true; this.invalidate(); }, { signal: this.events.signal });
    window.addEventListener('pageshow', () => { this.suspended = false; this.refresh(); }, { signal: this.events.signal });
    this.refresh();
  }
  canPoll() { return this.toggle.checked && !document.hidden && !this.suspended && this.getContext().online; }
  invalidate() {
    this.version++; this.since = null; clearTimeout(this.timer); this.timer = null; this.request?.abort();
    for (const tile of this.getTiles().values()) tile.detections?.clear();
  }
  refresh() {
    const context = this.getContext(), key = `${context.generation}:${context.configurationId}:${context.online}`;
    if (key !== this.key) { this.key = key; this.invalidate(); }
    const enabled = this.canPoll();
    this.metadataToggle.disabled = !this.toggle.checked;
    for (const tile of this.getTiles().values()) {
      tile.detections?.setMetadataVisible(this.metadataToggle.checked);
      tile.detections?.setEnabled(enabled);
    }
    if (!enabled) {
      if (this.timer || this.request && !this.request.signal.aborted) this.invalidate();
      this.status.textContent = this.toggle.checked ? 'Connect live viewing to see existing detector results. Detection must be enabled in Motion & recording.' : 'Detection overlays are off.';
    } else if (!this.timer && !this.request) this.poll();
  }
  async poll() {
    this.timer = null;
    if (!this.canPoll() || this.request) return;
    const request = this.request = new AbortController(), version = this.version, context = this.getContext(), started = performance.now();
    const timeout = setTimeout(() => request.abort(), 5000);
    try {
      const response = await fetch('/api/detections' + (this.since ? `?since=${encodeURIComponent(this.since)}` : ''), { signal: request.signal, cache: 'no-store' });
      if (response.status === 401) document.dispatchEvent(new Event('scv-sign-in-required'));
      if (!response.ok) throw new Error('Detection results unavailable.');
      const data = await response.json();
      if (version !== this.version || request.signal.aborted || !this.canPoll()) return;
      if (data.configurationId !== context.configurationId) { for (const tile of this.getTiles().values()) tile.detections?.clear(); return; }
      for (const [channel, tile] of this.getTiles()) {
        const camera = data.cameras.find(camera => camera.channel === channel);
        if (camera?.history) tile.detections?.updateHistory(camera.history, data.methods, data.models, data.generatedAt, data.historySeconds, data.maximumHistorySamples);
        else tile.detections?.update(camera?.detections || [], data.methods, data.models, performance.now() - started);
      }
      this.since = Number.isFinite(Date.parse(data.generatedAt)) ? data.generatedAt : null;
      this.status.textContent = data.enabled ? 'Candidate boxes have dashed outlines; positions update at the detector rate. Enable metadata text to see method, object confidence and detection details.' : 'Enable detection in Motion & recording to see overlays.';
    } catch {
      if (version === this.version && this.canPoll()) { this.since = null; for (const tile of this.getTiles().values()) tile.detections?.clear(); this.status.textContent = 'Detection results unavailable; retrying.'; }
    } finally {
      clearTimeout(timeout); if (this.request === request) this.request = null;
      if (this.canPoll()) this.timer = setTimeout(() => this.poll(), 500);
    }
  }
  dispose() { this.suspended = true; this.invalidate(); this.events.abort(); this.toggle.onchange = null; this.metadataToggle.onchange = null; }
}
