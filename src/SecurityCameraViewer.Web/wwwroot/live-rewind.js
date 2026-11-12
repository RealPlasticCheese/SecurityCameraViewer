'use strict';

// A single browser clock drives every visible historical camera, independently of server capture.
class LiveRewindClock {
  constructor(now = () => performance.now()) { this.now = now; this.live(); }
  live() { this.position = null; this.paused = false; this.rate = 1; this.anchor = this.now(); }
  current() {
    if (this.position === null) return null;
    const now = this.now();
    if (!this.paused) this.position += Math.max(0, now - this.anchor) * this.rate;
    this.anchor = now;
    return this.position;
  }
  seek(position) { if (!Number.isFinite(position)) return; this.position = position; this.anchor = this.now(); }
  pause(paused) { this.current(); this.paused = paused; }
  speed(rate) { if (!Number.isFinite(rate) || rate === 0) return; this.current(); this.rate = rate; }
  static rangeAt(ranges, at) { return ranges.find(range => range.start <= at && at < range.end); }
}

// Speed shortcuts change magnitude; direction changes are explicit transport actions.
class PlaybackSpeed {
  static next(rate) { return [0.5, 1, 2, 4].find(value => value > Math.abs(rate)) ?? Math.abs(rate) * 2; }
  static supported(rate, probe) {
    if (!Number.isFinite(rate) || rate === 0) return false;
    if (rate < 0) return true; // Reverse scanning uses seeks, never a negative native playbackRate.
    try { probe.playbackRate = rate; return probe.playbackRate === rate; }
    catch { return false; }
  }
}

// One response contains matching timing, initialization and compressed fragments.
// The server admits those immutable references together before eviction can occur.
class RewindWindow {
  static read(data) {
    const bytes = ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : new Uint8Array(data);
    const invalid = () => { throw new Error('Invalid buffered playback window.'); };
    if (bytes.length < 4) invalid();
    const length = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(0, true);
    if (!length || length > bytes.length - 4) invalid();
    let header;
    try { header = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(4, 4 + length))); }
    catch { invalid(); }
    const playlist = header?.playlist;
    if (!playlist || typeof playlist.epoch !== 'string' || !playlist.epoch || typeof playlist.mimeType !== 'string' || !playlist.mimeType ||
        typeof playlist.hasAudio !== 'boolean' ||
        !Array.isArray(playlist.fragments) || !playlist.fragments.length || !Array.isArray(header.fragmentLengths) ||
        header.fragmentLengths.length !== playlist.fragments.length) invalid();
    let at = 4 + length;
    const read = size => {
      if (!Number.isSafeInteger(size) || size <= 0 || size > bytes.length - at) invalid();
      const value = bytes.subarray(at, at + size); at += size; return value;
    };
    const initialization = read(header.initializationLength), media = new Map();
    for (let i = 0; i < playlist.fragments.length; i++) {
      const sequence = playlist.fragments[i]?.sequence;
      if (!Number.isSafeInteger(sequence) || sequence <= 0 || media.has(sequence)) invalid();
      media.set(sequence, read(header.fragmentLengths[i]));
    }
    if (at !== bytes.length) invalid();
    return { ...playlist, initialization, media };
  }
}

// Fetch a small keyframe window on demand. The browser never downloads the entire server history.
class RewindCameraPlayer {
  constructor(channel, tile, request, changed, audioBlocked = () => {}, coverageChanged = () => {}) {
    this.channel = channel; this.tile = tile; this.request = request; this.changed = changed;
    this.audioBlocked = audioBlocked;
    this.coverageChanged = coverageChanged;
    this.version = 0; this.busy = false; this.sequence = 0; this.muted = true; this.message = '';
    this.timingFragments = new Map();
    this.video = document.createElement('video');
    this.video.className = 'live-camera-video'; this.video.playsInline = true;
    this.video.disableRemotePlayback = true; this.video.muted = true;
    this.video.setAttribute('aria-label', `${tile.name} buffered playback`);
    this.waiting = true;
    this.video.addEventListener('loadeddata', () => this.show());
    this.video.addEventListener('seeked', () => this.show());
    this.video.addEventListener('error', () => this.gap('This browser could not decode buffered video.'));
    tile.root.querySelector('.surface').append(this.video);
    tile.root.classList.add('rewind-active');
  }

  reset() {
    ++this.version; this.abort?.abort(); this.abort = null;
    clearTimeout(this.sourceTimeout);
    this.sourceResolve?.(null); this.sourceResolve = null;
    this.video.pause(); this.video.removeAttribute('src'); this.video.load();
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = null; this.buffer = null; this.media = null; this.epoch = null; this.sequence = 0;
    this.timingFragments.clear(); this.base = this.mediaBase = undefined;
    this.waiting = true;
    this.tile.root.classList.remove('live-media-ready');
  }

  show() {
    if (this.disposed || this.waiting || !this.epoch || this.video.readyState < 2 || this.video.seeking) return;
    this.tile.root.classList.add('live-media-ready'); this.tile.zoom?.refresh(); this.changed();
  }

  waitForFootage() {
    this.waiting = true; this.video.pause();
    this.tile.root.classList.remove('live-media-ready', 'rewind-gap'); this.message = ''; this.changed();
  }

  covered(at) {
    const position = this.positionAt(at), buffered = this.video.buffered;
    return position && Array.from({ length: buffered.length }, (_, i) => i)
      .some(i => position.offset >= buffered.start(i) && position.offset < buffered.end(i));
  }

  end() {
    const last = [...this.timingFragments.values()].at(-1);
    return last ? last.end ?? last.start + last.durationSeconds * (last.clockRate ?? 1) * 1000 : -Infinity;
  }

  gap(message) {
    this.reset(); this.message = message;
    this.retryAt = performance.now() + 2000;
    this.tile.root.classList.add('rewind-gap');
    this.changed();
  }

  async append(action) {
    const buffer = this.buffer;
    const signal = this.abort?.signal;
    return new Promise((resolve, reject) => {
      const cleanup = () => { for (const [name, handler] of events) buffer.removeEventListener(name, handler); signal?.removeEventListener('abort', cancelled); };
      const success = () => { cleanup(); resolve(); };
      const failure = () => { cleanup(); reject(new Error('Buffered playback failed.')); };
      const cancelled = () => { cleanup(); reject(new DOMException('Playback changed', 'AbortError')); };
      const events = [['updateend', success], ['abort', failure], ['error', failure]];
      for (const [name, handler] of events) buffer.addEventListener(name, handler, { once: true });
      signal?.addEventListener('abort', cancelled, { once: true });
      if (signal?.aborted) { cancelled(); return; }
      try { action(); } catch (error) { cleanup(); reject(error); }
    });
  }

  async synchronize(range, at, paused, rate) {
    const previous = this.target;
    if (range && (!previous?.range || range.epoch !== previous.range.epoch || range.start > previous.range.start)) this.retryAt = 0;
    this.target = { range, at, paused, rate };
    // Stop native forward playback immediately, including while an older window is loading.
    if (paused || rate < 0) { this.video.pause(); if (rate < 0) this.video.muted = true; }
    if (!range) { if (this.epoch || !this.message) this.gap('No buffered footage at this time.'); return; }
    if (this.epoch && (range.epoch !== this.epoch || !this.covered(at))) this.waitForFootage();
    if (this.busy || performance.now() < (this.retryAt || 0)) return;
    this.busy = true;
    let operationVersion = this.version;
    try {
      let position = this.positionAt(at), offset = position?.offset ?? -1;
      const buffered = this.video.buffered;
      const inside = this.epoch === range.epoch && Array.from({ length: buffered.length }, (_, i) => i)
        .some(i => offset >= buffered.start(i) && offset < buffered.end(i));
      if (!inside) {
        const extend = this.epoch === range.epoch && this.buffer && at >= this.end();
        if (!extend) { this.reset(); this.abort = new AbortController(); }
        this.waitForFootage();
        operationVersion = this.version;
        const version = this.version, signal = this.abort.signal;
        const playlist = await this.playlist(range, at, rate, signal);
        if (version !== this.version) return;
        if (playlist.epoch !== range.epoch) throw new Error('Buffered playback belongs to another stream.');
        if (!extend) {
          const Media = window.MediaSource || window.ManagedMediaSource;
          if (!Media?.isTypeSupported(playlist.mimeType)) throw new Error('Buffered video is unsupported in this browser.');
          this.base = playlist.fragments[0].start; this.mediaBase = playlist.fragments[0].mediaStartSeconds;
          this.epoch = range.epoch; this.hasAudio = playlist.hasAudio;
          this.media = new Media();
          const ready = new Promise((resolve, reject) => {
            this.sourceResolve = resolve; this.media.addEventListener('sourceopen', () => resolve(true), { once: true });
            this.sourceTimeout = setTimeout(() => reject(new Error('Browser media initialization timed out.')), 10000);
          });
          this.url = URL.createObjectURL(this.media); this.video.src = this.url;
          if (!await ready || version !== this.version) return;
          clearTimeout(this.sourceTimeout);
          this.sourceResolve = null;
          this.buffer = this.media.addSourceBuffer(playlist.mimeType);
          this.buffer.timestampOffset = -playlist.fragments[0].mediaStartSeconds;
          await this.appendMedia(playlist.initialization, version);
        }
        await this.fragments(playlist, version);
        if (version !== this.version) return;
      } else if (rate > 0 && buffered.end(buffered.length - 1) - offset < 5 && range.end > this.end()) {
        const version = this.version;
        try {
          const playlist = await this.playlist(range, Math.max(at, Math.min(this.end(), range.end - 1)), rate, this.abort.signal);
          if (version !== this.version) return;
          await this.fragments(playlist, version);
        } catch (error) {
          if (error.status !== 410) throw error;
          this.coverageChanged(); // Keep the already decoded picture when optional read-ahead has expired.
        }
        if (version !== this.version) return;
      }
      if (!this.epoch || !this.buffer) return;
      // The clock may have moved while media was fetched; apply the newest target.
      const target = this.target;
      if (target.range?.epoch !== this.epoch) return;
      position = this.positionAt(target.at);
      if (!position) { this.waitForFootage(); return; }
      offset = position.offset;
      const ranges = this.video.buffered;
      if (!Array.from({ length: ranges.length }, (_, i) => i).some(i => offset >= ranges.start(i) && offset < ranges.end(i))) {
        this.waitForFootage(); return; // Keep the admitted media while a filling buffer supplies the newer target.
      }
      const scanning = target.rate < 0;
      if (!scanning) this.video.playbackRate = target.rate / position.clockRate;
      if (Math.abs(this.video.currentTime - offset) * position.clockRate > (target.paused || scanning ? 0.04 : 0.1)) this.video.currentTime = Math.max(0, offset);
      this.video.muted = scanning || this.muted || !this.hasAudio;
      this.waiting = false;
      if (target.paused || scanning) this.video.pause(); else this.play();
      this.message = ''; this.tile.root.classList.remove('rewind-gap'); this.changed();
      this.show();
      if (!scanning && offset > 15 && this.buffer.buffered.length && this.buffer.buffered.start(0) < offset - 10) {
        const buffer = this.buffer;
        await this.append(() => buffer.remove(0, offset - 10));
        for (const [sequence, fragment] of this.timingFragments) {
          if (fragment.mediaStartSeconds + fragment.durationSeconds - this.mediaBase < offset - 10) this.timingFragments.delete(sequence);
        }
      }
    } catch (error) {
      if (error.name !== 'AbortError' && operationVersion === this.version && !this.disposed) {
        this.gap(error.message || 'Buffered playback unavailable.');
        if (error.status === 410) this.coverageChanged();
      }
    } finally { this.busy = false; }
  }

  positionAt(at) {
    const fragments = [...this.timingFragments.values()];
    const fragment = fragments.findLast(item => item.start <= at && at <
      (item.end ?? item.start + item.durationSeconds * (item.clockRate ?? 1) * 1000));
    if (!fragment) return null;
    const clockRate = fragment.clockRate ?? 1;
    if (!Number.isFinite(clockRate) || clockRate <= 0) throw new Error('Invalid buffered timing metadata.');
    return { offset: fragment.mediaStartSeconds - this.mediaBase + (at - fragment.start) / (clockRate * 1000), clockRate };
  }

  async playlist(range, at, rate, signal) {
    const requestAt = async position => {
      const window = RewindWindow.read(await this.request(`/api/playback/${this.channel}/${range.epoch}/window?at=${Math.floor(position)}`, signal, true));
      if (window.epoch !== range.epoch) throw new Error('Buffered playback belongs to another stream.');
      return window;
    };
    // Retain the same ten-second look-behind used by forward playback's media cache.
    const playlist = await requestAt(rate < 0 ? Math.max(range.start, at - 10000) : at);
    const last = playlist.fragments.at(-1);
    if (rate < 0 && (last.end ?? last.start + last.durationSeconds * (last.clockRate ?? 1) * 1000) <= at) {
      const current = await requestAt(at);
      playlist.fragments = [...new Map([...playlist.fragments, ...current.fragments].map(fragment => [fragment.sequence, fragment])).values()];
      playlist.media = new Map([...playlist.media, ...current.media]);
    }
    return playlist;
  }

  async appendMedia(bytes, version) {
    if (version !== this.version) throw new DOMException('Playback changed', 'AbortError');
    const buffer = this.buffer;
    await this.append(() => buffer.appendBuffer(bytes));
  }

  async fragments(playlist, version) {
    for (const fragment of playlist.fragments) {
      if (version !== this.version) return;
      if (![fragment.start, fragment.durationSeconds, fragment.mediaStartSeconds, fragment.clockRate ?? 1].every(Number.isFinite) ||
          fragment.clockRate !== undefined && !Number.isFinite(fragment.clockRate) ||
          fragment.durationSeconds <= 0 || fragment.mediaStartSeconds < 0 || (fragment.clockRate ?? 1) <= 0 ||
          fragment.end !== undefined && (!Number.isFinite(fragment.end) || fragment.end <= fragment.start))
        throw new Error('Invalid buffered timing metadata.');
      this.timingFragments.set(fragment.sequence, fragment);
      if (fragment.sequence <= this.sequence) continue;
      const bytes = playlist.media.get(fragment.sequence);
      if (!bytes) throw new Error('Invalid buffered playback window.');
      await this.appendMedia(bytes, version);
      if (version !== this.version) return;
      this.sequence = fragment.sequence;
    }
  }

  play() {
    if (this.waiting || this.target?.paused || this.target?.rate < 0 || !this.video.paused) return;
    const version = this.version;
    this.video.play().catch(error => {
      if (version !== this.version || this.disposed) return;
      if (error.name === 'NotAllowedError' && !this.video.muted) {
        this.muted = true; this.video.muted = true;
        this.audioBlocked(); this.play();
      }
    });
  }

  sound(enabled, volume) { this.muted = !enabled; this.video.muted = !enabled || !this.hasAudio || this.target?.rate < 0; this.video.volume = volume; if (enabled && !this.target?.paused && this.target?.rate > 0) this.play(); }
  dispose() { this.disposed = true; this.reset(); this.video.remove(); this.tile.root.classList.remove('rewind-active', 'rewind-gap'); this.tile.zoom?.refresh(); }
}

class LiveRewindController {
  constructor(context, enteringPlayback, changed) {
    this.context = context; this.enteringPlayback = enteringPlayback; this.changed = changed;
    this.clock = new LiveRewindClock(); this.players = new Map(); this.snapshot = null;
    this.status = ''; this.speedError = ''; this.sound = false; this.polling = false;
    const el = id => document.getElementById(id);
    this.bar = el('playback-controls'); this.slider = el('playback-position'); this.label = el('playback-status');
    this.pauseButton = el('playback-pause'); this.liveButton = el('playback-live'); this.soundButton = el('playback-sound');
    this.reverseButton = el('playback-reverse'); this.playButton = el('playback-play');
    this.slowerButton = el('playback-slower'); this.fasterButton = el('playback-faster');
    this.presets = [0.5, 1, 2, 4].map(rate => ({ rate, button: el(`playback-speed-${rate}`) }));
    this.speed = el('playback-speed'); this.enable = el('rewind-enabled'); this.seconds = el('rewind-seconds');
    this.memory = el('rewind-memory');
    this.saveButton = el('playback-save'); this.cancelSaveButton = el('playback-save-cancel');
    this.saveLabel = el('playback-save-status'); this.selection = null; this.saveMessage = ''; this.saveMessageTimer = null; this.saving = false;
    this.saveButton.onclick = () => this.saveRecording();
    this.cancelSaveButton.onclick = () => { this.selection = null; this.clearSaveMessage(); this.render(); };
    this.controls = new PlaybackControlsView({ root: el('playback-overlay'), bar: this.bar,
      tabArea: el('playback-tab-area'), tab: el('playback-tab'),
      dock: el('playback-dock'), focusTarget: el('cameras'), pin: el('playback-pin'), compactStatus: el('playback-compact-status'),
      compactLive: el('playback-compact-live') }, () => this.goLive());
    this.rateProbe = document.createElement('video');
    this.pauseButton.onclick = () => {
      if (!this.historical) { this.clock.pause(true); this.seek(this.latest()); }
      else this.clock.pause(true);
      this.speedError = ''; this.speed.value = '0';
      this.tick();
    };
    this.reverseButton.onclick = () => this.playDirection(-1);
    this.playButton.onclick = () => this.playDirection(1);
    this.slowerButton.onclick = () => this.setMagnitude(Math.abs(this.clock.rate) / 2);
    this.fasterButton.onclick = () => this.setMagnitude(Math.abs(this.clock.rate) * 2);
    for (const { rate, button } of this.presets) button.onclick = () => this.setMagnitude(rate);
    el('playback-back').onclick = () => this.seek((this.clock.current() ?? this.latest()) - 10000);
    el('playback-forward').onclick = () => this.seek((this.clock.current() ?? this.latest()) + 10000);
    this.liveButton.onclick = () => this.goLive();
    this.slider.oninput = () => this.seek(Number(this.slider.value));
    this.speed.onchange = () => {
      const rate = Number(this.speed.value);
      if (!this.speed.value.trim() || !Number.isFinite(rate)) {
        this.speedError = 'Enter a finite playback speed; use 0 to pause.'; this.speed.value = String(this.signedRate); this.render(); return;
      }
      if (rate === 0) this.pauseButton.onclick(); else this.setRate(rate);
    };
    this.soundButton.onclick = () => { this.status = ''; this.sound = !this.sound; this.applySound(); this.render(); };
    this.enable.onchange = this.seconds.onchange = this.memory.onchange = () => this.configure();
    this.bar.addEventListener('dblclick', event => event.stopPropagation());
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.clearPlayers(); else this.poll(); });
    window.addEventListener('pagehide', () => { this.suspended = true; this.clearPlayers(); });
    window.addEventListener('pageshow', () => { this.suspended = false; this.poll(); });
    setInterval(() => this.tick(), 250); setInterval(() => this.poll(), 1000); this.poll();
  }

  get historical() { return this.clock.position !== null; }
  get signedRate() { return this.clock.paused ? 0 : this.clock.rate; }
  playDirection(direction) {
    const active = this.historical && !this.clock.paused && Math.sign(this.clock.rate) === direction;
    this.setRate(direction * (active ? PlaybackSpeed.next(this.clock.rate) : 1));
  }
  setMagnitude(magnitude) { this.setRate(Math.sign(this.clock.rate) * magnitude); }
  setRate(rate) {
    if (!PlaybackSpeed.supported(rate, this.rateProbe)) {
      this.speedError = `This browser does not support ${rate}× playback. Speed remains ${this.signedRate}×.`;
      this.speed.value = String(this.signedRate); this.render(); return;
    }
    this.speedError = ''; this.status = '';
    this.clock.speed(rate); this.clock.pause(false);
    if (!this.historical) this.seek(rate < 0 ? this.latest() : this.latest() - 1000);
    else if (rate > 0 && this.clock.current() >= this.latest()) this.seek(this.latest() - 1000);
    this.speed.value = String(this.signedRate); this.tick();
  }
  latest() { return Math.max(...(this.snapshot?.cameras || []).flatMap(camera => camera.ranges.map(range => range.end))) - 1200; }
  oldest() { return Math.min(...(this.snapshot?.cameras || []).flatMap(camera => camera.ranges.map(range => range.start))); }
  supported() { return !!(window.MediaSource || window.ManagedMediaSource); }

  visibleChannels() {
    const { tiles, focus } = this.context();
    return [...tiles.keys()].filter(channel => focus === null || channel === focus);
  }

  clearSaveMessage() {
    clearTimeout(this.saveMessageTimer); this.saveMessageTimer = null; this.saveMessage = '';
  }

  async saveRecording() {
    if (this.saveButton.disabled || this.saving || !this.historical) return;
    const position = Math.round(this.clock.current());
    this.clock.pause(true); this.speedError = ''; this.speed.value = '0';
    if (!this.selection) {
      this.selection = { generation: this.snapshot.generation, start: position, channels: this.visibleChannels() };
      this.clearSaveMessage(); this.tick(); return;
    }
    const selection = this.selection;
    if (position === selection.start) {
      this.saveMessage = 'Choose a different end time with the slider or playback buttons.'; this.render(); return;
    }
    this.saving = true; this.saveMessage = 'Saving video…'; this.render();
    try {
      const response = await fetch('/api/playback/recordings', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...selection, end: position }) });
      if (response.status === 401) document.dispatchEvent(new Event('scv-sign-in-required'));
      const data = await response.json();
      if (!response.ok) throw new Error(data.message || 'Could not save the selected video.');
      if (this.selection === selection) {
        this.selection = null;
        this.saveMessage = `${data.length} recording${data.length === 1 ? '' : 's'} saved. Open View recordings to play ${data.length === 1 ? 'it' : 'them'}.`;
        this.saveMessageTimer = setTimeout(() => { this.clearSaveMessage(); this.render(); }, 10000);
      }
    } catch (error) {
      if (this.selection === selection) this.saveMessage = error.message || 'Could not save the selected video.';
    } finally {
      this.saving = false; this.render();
    }
  }

  async request(url, signal, binary = false) {
    const response = await fetch(url, { signal: AbortSignal.any([signal || new AbortController().signal, AbortSignal.timeout(10000)]) });
    if (!response.ok) throw Object.assign(new Error(response.status === 410 ? 'Footage has left the rewind buffer.' : 'Buffered playback is unavailable.'), { status: response.status });
    return binary ? response.arrayBuffer() : response.json();
  }

  async poll() {
    if (this.polling || this.configuring || this.suspended || document.hidden) return;
    this.polling = true;
    try {
      const next = await this.request('/api/playback');
      if (this.snapshot && this.snapshot.generation !== next.generation) this.reset();
      this.snapshot = next;
      this.enable.checked = next.settings.enabled;
      if (document.activeElement !== this.seconds) this.seconds.value = String(next.settings.seconds);
      if (document.activeElement !== this.memory) this.memory.value = String(next.settings.megabytesPerCamera);
      if (!next.settings.enabled || !next.connected) this.goLive();
      this.online = true; this.status = ''; this.tick();
    } catch { this.online = false; this.status = 'Playback server unavailable.'; this.clearPlayers(); this.render(); }
    finally { this.polling = false; }
  }

  async configure() {
    this.configuring = true;
    this.enable.disabled = this.seconds.disabled = this.memory.disabled = true;
    try {
      const response = await fetch('/api/playback', { method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: this.enable.checked, seconds: Number(this.seconds.value), megabytesPerCamera: Number(this.memory.value) }), signal: AbortSignal.timeout(20000) });
      if (!response.ok) throw new Error(response.status === 403 ? 'Changing the server buffer requires administrator access.' : (await response.json()).message || 'Could not save rewind settings.');
      this.snapshot = await response.json(); this.goLive();
    } catch (error) { this.status = error.message; }
    finally { this.configuring = false; this.enable.disabled = this.seconds.disabled = this.memory.disabled = false; this.render(); }
  }

  seek(at) {
    if (!this.supported() || !Number.isFinite(at) || !Number.isFinite(this.oldest())) return;
    if (!this.historical) this.enteringPlayback();
    this.clock.seek(Math.max(this.oldest(), Math.min(this.latest(), at)));
    this.tick();
  }

  goLive() { this.selection = null; this.clearSaveMessage(); this.clock.live(); this.speedError = ''; this.sound = false; this.clearPlayers(); this.render(); this.changed(); }
  reset() { this.snapshot = null; this.goLive(); }
  clearPlayers() { for (const player of this.players.values()) player.dispose(); this.players.clear(); }
  viewChanged() {
    const channels = this.visibleChannels();
    if (this.selection && (channels.length !== this.selection.channels.length ||
        channels.some(channel => !this.selection.channels.includes(channel)))) {
      this.selection = null; this.saveMessage = this.saving ? '' : 'Camera view changed; select the recording start again.';
    }
    this.tick();
  }
  applySound() {
    const { focus, volume } = this.context();
    for (const [channel, player] of this.players) player.sound(this.sound && this.clock.rate > 0 && channel === focus, volume);
  }

  tick() {
    if (this.suspended || document.hidden) return;
    const { tiles, focus, generation } = this.context();
    if (this.historical && this.snapshot?.generation === generation && this.online) {
      if (!Number.isFinite(this.oldest()) || !Number.isFinite(this.latest()) || this.latest() <= this.oldest()) { this.goLive(); return; }
      let at = this.clock.current();
      if (at <= this.oldest() && !this.clock.paused && this.clock.rate < 0) {
        if (Number(this.speed.value) === this.clock.rate) this.speed.value = '0';
        this.clock.seek(this.oldest()); this.clock.pause(true); at = this.oldest(); this.status = 'Start of available footage; playback paused.';
      } else if (at < this.oldest()) { this.clock.seek(this.oldest()); at = this.oldest(); this.status = 'Older footage expired; moved to the start of the buffer.'; }
      if (!this.clock.paused && this.clock.rate > 0 && at >= this.latest()) { this.goLive(); return; }
      const visible = new Map([...tiles].filter(([channel]) => focus === null || channel === focus));
      for (const [channel, player] of this.players) if (!visible.has(channel)) { player.dispose(); this.players.delete(channel); }
      for (const [channel, tile] of visible) {
        if (!this.players.has(channel)) this.players.set(channel, new RewindCameraPlayer(channel, tile, (...args) => this.request(...args), this.changed,
          () => { this.sound = false; this.status = 'Tap Sound to allow playback audio.'; this.render(); }, () => this.poll()));
        const camera = this.snapshot.cameras.find(camera => camera.channel === channel);
        this.players.get(channel).synchronize(LiveRewindClock.rangeAt(camera?.ranges || [], at), at, this.clock.paused, this.clock.rate);
      }
      this.applySound(); this.changed();
    }
    this.render();
  }

  render() {
    const available = this.snapshot?.settings.enabled;
    const oldest = this.oldest(), latest = this.latest(), usable = Number.isFinite(oldest) && latest > oldest && this.online && this.supported();
    for (const control of this.bar.querySelectorAll('.playback-buttons button:not(#playback-pin), .playback-buttons input, .playback-buttons select, #playback-position')) control.disabled = !usable;
    this.liveButton.disabled = !this.historical;
    this.saveButton.disabled = !usable || !this.historical || this.saving || this.visibleChannels().length === 0;
    this.saveButton.textContent = this.saving ? 'Saving…' : this.selection ? 'Save end' : 'Save video';
    this.saveButton.setAttribute('aria-label', this.saveButton.textContent);
    this.saveButton.title = this.selection ? 'Select this time as the end and save the recording' : 'Save video from this playback time';
    this.cancelSaveButton.hidden = !this.selection || this.saving;
    this.cancelSaveButton.disabled = this.saving;
    this.saveLabel.hidden = !this.selection && !this.saveMessage;
    this.saveLabel.textContent = this.saveMessage || (this.selection
      ? `Start selected: ${new Date(this.selection.start).toLocaleString()} · ${this.selection.channels.length} camera${this.selection.channels.length === 1 ? '' : 's'}. Use the slider or playback buttons to choose the end, then press Save end. Earlier end times are allowed.` : '');
    const rate = this.signedRate, magnitude = Math.abs(rate);
    this.pauseButton.setAttribute('aria-pressed', String(rate === 0));
    this.reverseButton.setAttribute('aria-pressed', String(this.historical && rate < 0));
    this.playButton.setAttribute('aria-pressed', String(this.historical && rate > 0));
    this.slowerButton.setAttribute('aria-pressed', String(magnitude > 0 && magnitude < 0.5));
    this.fasterButton.setAttribute('aria-pressed', String(magnitude > 4));
    for (const preset of this.presets) preset.button.setAttribute('aria-pressed', String(this.historical && magnitude === preset.rate));
    if (document.activeElement !== this.speed) this.speed.value = String(rate);
    this.slider.min = String(Number.isFinite(oldest) ? oldest : 0); this.slider.max = String(Number.isFinite(latest) ? Math.max(oldest, latest) : 0);
    this.slider.value = String(this.clock.current() ?? latest);
    this.soundButton.hidden = !this.historical || this.context().focus === null;
    this.soundButton.disabled = !usable || this.clock.rate < 0;
    const soundEnabled = this.sound && this.clock.rate > 0;
    this.soundButton.setAttribute('aria-pressed', String(soundEnabled));
    this.soundButton.setAttribute('aria-label', soundEnabled ? 'Mute playback' : 'Sound');
    this.soundButton.title = this.clock.rate < 0 ? 'Reverse scanning is muted' : this.sound ? 'Mute playback' : 'Sound';
    const position = this.clock.current();
    const secondsBehind = (latest - position) / 1000;
    this.controls.update({ available, historical: this.historical, paused: this.clock.paused, rate, secondsBehind });
    const text = this.speedError || this.status || (this.historical ? `${formatPlaybackState(this.clock.paused, rate)} · ${new Date(position).toLocaleString()} · ${formatPlaybackDelay(secondsBehind)} behind live`
      : !this.supported() ? 'Rewind is unsupported in this browser.' : usable ? 'LIVE' : 'Filling rewind buffer…');
    this.label.textContent = text; this.slider.setAttribute('aria-valuetext', text);
    const memory = (this.snapshot?.cameras || []).reduce((bytes, camera) => bytes + (camera.bytes || 0), 0) / 1048576;
    document.getElementById('rewind-settings-status').textContent = this.status || (available ? `${this.snapshot.cameras.length} buffered cameras · ${memory.toFixed(1)} MiB used. Available history depends on captured footage and the memory budget.` : 'Enable the server buffer to rewind live pictures.');
  }
}

if (typeof tiles !== 'undefined') {
  liveRewind = new LiveRewindController(() => ({ tiles, focus, generation, volume: liveAudio.volume }),
    () => { liveAudio.stop(); pushToTalk.stop(); }, () => renderStatus());
}
