'use strict';

class CameraVideoRecovery {
  static defaultDelays = '1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32';
  constructor(schedule, now = () => performance.now(), random = () => Math.random()) {
    this.delays = (schedule || CameraVideoRecovery.defaultDelays).split(',').map(value => Number(value.trim()));
    if (!this.delays.length || this.delays.some(value => !Number.isInteger(value) || value <= 0 || value > 2147483647)) throw new Error('Invalid camera retry schedule.');
    Object.assign(this, { now, random, failures: 0, retryAt: null, healthySince: null, lastFrame: null });
  }
  start() { this.retryAt = this.healthySince = this.lastFrame = null; }
  cancel() {
    if (this.retryAt === null) return;
    this.failures--;
    this.start();
  }
  failed() {
    if (this.retryAt !== null) return;
    const delay = this.delays[Math.min(this.failures++, this.delays.length - 1)] * 1000;
    // Spread simultaneous camera reconnects without retrying sooner than the configured delay.
    this.retryAt = this.now() + delay * (1 + .2 * this.random());
    this.healthySince = null;
  }
  frame() {
    if (this.retryAt !== null) return;
    if (this.lastFrame !== null && this.now() - this.lastFrame >= 4000) this.healthySince = null;
    this.lastFrame = this.now();
    this.healthySince ??= this.now();
    if (this.now() - this.healthySince >= 30000) this.failures = 0;
  }
  get remainingSeconds() { return this.retryAt === null ? null : Math.max(0, Math.ceil((this.retryAt - this.now()) / 1000)); }
}

class CameraVideoTransport {
  constructor(channel, consumer, failure, status, budget) {
    Object.assign(this, { channel, consumer, failure, status, budget });
    this.version = 0; this.closed = false;
    this.recovery = new CameraVideoRecovery(budget.retryDelaysSeconds);
    this.diagnostics = { reconnects: 0, lastAcknowledgementMilliseconds: 0, maximumAcknowledgementMilliseconds: 0,
      webSocketTransport: location.protocol === 'https:' ? 'wss' : 'ws',
      pageProtocol: performance.getEntriesByType?.('navigation')[0]?.nextHopProtocol || null };
    this.lastProgress = performance.now();
    this.events = new AbortController();
    const options = { signal: this.events.signal };
    window.addEventListener('online', () => this.resume('network-restored', true), options);
    window.addEventListener('pageshow', event => { if (event.persisted) this.resume('page-restored', true); }, options);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) this.resume('page-resumed'); }, options);
    document.addEventListener('resume', () => this.resume('page-resumed'), options);
    this.connect();
  }
  connect() {
    if (this.closed) return;
    clearTimeout(this.retry); this.recovery.start();
    const version = ++this.version;
    let socket;
    try { socket = this.socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/cameras/${this.channel}/video`); }
    catch { this.reconnect('connection-error', 'Camera connection failed.'); return; }
    socket.binaryType = 'arraybuffer';
    let metadata, busy = false;
    const acknowledge = () => {
      if (version !== this.version || socket.readyState !== WebSocket.OPEN) return;
      try { socket.send('ok'); }
      catch { this.reconnect('send-error', 'Camera connection interrupted.'); }
    };
    socket.onmessage = event => {
      if (version !== this.version) return;
      if (typeof event.data === 'string') {
        try {
          const message = JSON.parse(event.data);
          if (message.serverProtocol) this.diagnostics.serverProtocol = message.serverProtocol;
          if (message.type === 'init' || message.type === 'fragment') { metadata = message; return; }
          if (message.type !== 'state') throw new Error('Unexpected camera message.');
          // Older servers also mark queue overflow as fallback. It remains a transient transport failure.
          if (message.reason === 'queue-overflow' || message.fallback && message.status !== 'unsupported') {
            acknowledge(); this.reconnect('queue-overflow', 'Viewer fell behind.', { queue: message.queue || null }); return;
          }
          if (message.fallback) { acknowledge(); this.failure(message.message, message.status === 'unsupported'); return; }
          if (['retrying', 'waiting'].includes(message.status)) this.consumer.reset();
          this.status(message); acknowledge(); return;
        } catch { this.reconnect('protocol-error', 'Camera sent an invalid message.'); return; }
      }
      if (busy || event.data.byteLength > this.budget.maximumQueuedBytes) {
        this.reconnect('client-backpressure', 'Viewer exceeded its video queue budget.', { receivedBytes: event.data.byteLength }); return;
      }
      busy = true;
      const initialization = metadata; metadata = null;
      const started = performance.now();
      this.processingDeadline = setTimeout(() => {
        if (version === this.version) this.reconnect('consumer-timeout', 'Viewer took too long to accept video.',
          { pendingAcknowledgementMilliseconds: performance.now() - started });
      }, (this.budget.acknowledgementTimeoutSeconds || 10) * 1000);
      Promise.resolve().then(() => initialization?.type === 'init' ? this.consumer.initialize(initialization, event.data) : this.consumer.fragment(event.data, initialization))
        .then(() => {
          if (version !== this.version) return;
          clearTimeout(this.processingDeadline); busy = false;
          this.diagnostics.lastAcknowledgementMilliseconds = performance.now() - started;
          this.diagnostics.maximumAcknowledgementMilliseconds = Math.max(this.diagnostics.maximumAcknowledgementMilliseconds, this.diagnostics.lastAcknowledgementMilliseconds);
          acknowledge();
        })
        .catch(error => {
          if (version !== this.version) return;
          clearTimeout(this.processingDeadline); this.failure(error.message || 'Camera decoding failed.', false, error.unsupportedMimeType);
        });
    };
    socket.onerror = () => socket.close();
    socket.onclose = event => {
      if (version !== this.version || this.closed) return;
      this.reconnect('disconnected', 'Camera connection interrupted.', { closeCode: event?.code ?? null });
    };
  }
  reconnect(reason, message, details = {}) {
    if (this.closed) return;
    ++this.version; clearTimeout(this.retry); clearTimeout(this.processingDeadline);
    this.socket?.close(); this.socket = null; this.consumer.reset();
    Object.assign(this.diagnostics, details, { reason, reconnects: this.diagnostics.reconnects + 1 });
    this.recovery.failed();
    const retry = () => {
      if (this.closed) return;
      const seconds = this.recovery.remainingSeconds;
      if (!seconds) { this.connect(); return; }
      this.status({ status: 'retrying', reason, retryInSeconds: seconds,
        message: `${message} Reconnecting in ${seconds} ${seconds === 1 ? 'second' : 'seconds'}.` });
      // Chunk long user-configured delays to stay within the browser timer's range and update the countdown.
      this.retry = setTimeout(retry, Math.min(1000, this.recovery.retryAt - performance.now()));
    };
    retry();
  }
  frame() { this.lastProgress = performance.now(); this.recovery.frame(); }
  resume(reason, force = false) {
    if (this.closed || !force && this.recovery.retryAt === null && performance.now() - this.lastProgress < 4000) return;
    // Foreground transitions do not interrupt healthy background/PiP playback.
    this.reconnect(reason, 'Refreshing live camera video.');
  }
  snapshot() { return { ...this.diagnostics, retryInSeconds: this.recovery.remainingSeconds, failures: this.recovery.failures }; }
  stop() {
    this.closed = true; ++this.version; this.events.abort(); clearTimeout(this.retry); clearTimeout(this.processingDeadline);
    this.socket?.close(); this.socket = null;
  }
}

class NativeCameraVideo {
  static retainedSeconds = 8;
  constructor(tile, frame, activity = () => {}, probing = false) {
    this.tile = tile; this.frame = frame; this.activity = activity; this.version = 0;
    this.probing = probing;
    this.clock = new CameraFrameClock();
    if (!probing && !this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(null);
    this.video = document.createElement('video');
    this.video.className = probing ? 'camera-decoder-probe' : 'grid-camera-video'; this.video.muted = true; this.video.autoplay = true;
    this.video.playsInline = true; this.video.disableRemotePlayback = true; this.video.disablePictureInPicture = true;
    // Background/PiP playback can advance without compositor or animation callbacks.
    this.video.ontimeupdate = () => {
      if (!this.media || this.video.readyState < 2 || this.video.paused || this.video.seeking || this.video.currentTime === this.progressTime) return;
      this.progressTime = this.video.currentTime; this.showFrame(); this.activity(this.progressTime);
    };
    this.video.setAttribute('aria-label', `${tile.name} native live video`);
    tile.root.querySelector('.surface').append(this.video);
  }
  async initialize(metadata) {
    this.reset();
    const Media = window.MediaSource || window.ManagedMediaSource;
    if (!Media?.isTypeSupported(metadata.mimeType)) {
      const error = new Error(`Native playback unavailable for ${metadata.mimeType}.`);
      error.unsupportedMimeType = metadata.mimeType;
      throw error;
    }
    this.media = new Media();
    const version = this.version;
    const ready = new Promise(resolve => {
      this.resolveReady = resolve;
      this.media.addEventListener('sourceopen', () => resolve(this.media), { once: true });
    });
    this.url = URL.createObjectURL(this.media); this.video.src = this.url;
    const media = await ready;
    if (!media || version !== this.version) return;
    this.buffer = media.addSourceBuffer(metadata.mimeType);
    this.video.onerror = () => { if (version === this.version) this.failure?.('The native decoder could not play this stream.'); };
    this.video.play().catch(error => { if (version === this.version && error.name !== 'AbortError') this.failure?.('Native autoplay was blocked by this browser.'); });
  }
  async append(bytes) {
    if (!this.buffer) return;
    const buffer = this.buffer;
    await new Promise((resolve, reject) => {
      const finish = error => { buffer.removeEventListener('updateend', end); buffer.removeEventListener('error', failed); this.finishAppend = null; error ? reject(error) : resolve(); };
      const end = () => finish(); const failed = () => finish(new Error('Native video buffer failed.'));
      this.finishAppend = finish;
      buffer.addEventListener('updateend', end, { once: true }); buffer.addEventListener('error', failed, { once: true });
      try { buffer.appendBuffer(bytes); } catch (error) { finish(error); }
    });
  }
  async fragment(bytes, timing) {
    this.clock.update(timing);
    await this.append(bytes);
    if (!this.buffer || !this.video.buffered.length) return;
    const ranges = this.video.buffered, start = ranges.start(0), end = ranges.end(ranges.length - 1);
    const synchronized = this.synchronize();
    if (synchronized === null && (this.video.currentTime < start || end - this.video.currentTime > 3)) this.video.currentTime = Math.max(start, end - 1);
    if (!this.painting) this.paint();
    if (this.buffer.buffered.length && this.video.currentTime - Math.max(this.buffer.buffered.start(0), this.prunedThrough || 0) >= NativeCameraVideo.retainedSeconds * 2 && !this.buffer.updating) {
      // Native eviction can take a full fragment to finish. Batch one retention window instead of
      // holding every acknowledgement behind cleanup; MSE preserves the retained keyframe dependencies.
      this.buffer.remove(0, this.video.currentTime - NativeCameraVideo.retainedSeconds);
      this.prunedThrough = this.video.currentTime - NativeCameraVideo.retainedSeconds;
      await new Promise(resolve => { this.finishRemoval = resolve; this.buffer.addEventListener('updateend', resolve, { once: true }); });
    }
  }
  synchronize() {
    if (this.probing) return null;
    if (this.tile.root.classList.contains('live-media-ready')) { LiveCameraClock.shared.remove(this); return false; }
    return LiveCameraClock.shared.synchronize(this, this.clock, this.video, () => this.video.play().catch(error => {
      if (error.name !== 'AbortError') this.failure?.('Native autoplay was blocked by this browser.');
    }));
  }
  paint() {
    this.painting = true;
    const version = this.version;
    const paint = (_, metadata) => {
      if (version !== this.version) return;
      if (this.video.readyState >= 2 && (!this.video.paused || metadata) && this.video.currentTime !== this.lastTime) {
        this.lastTime = this.video.currentTime;
        this.showFrame();
        const mediaTime = metadata?.mediaTime ?? this.video.currentTime;
        if (!this.probing && !this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(this.clock.time(mediaTime));
        this.frame(0, mediaTime);
        if (version !== this.version) return;
      }
      this.synchronize();
      // Paused cameras emit no compositor callbacks. Keep checking the common
      // cursor so a held picture resumes without waiting for another fragment.
      this.callbackUsesVideo = !!this.video.requestVideoFrameCallback && !this.video.paused;
      if (this.callbackUsesVideo) this.callback = this.video.requestVideoFrameCallback(paint);
      else this.callback = requestAnimationFrame(paint);
    };
    paint();
  }
  showFrame() {
    if (this.probing) return;
    this.frozen?.remove(); this.frozen = null;
    this.video.style.visibility = '';
    this.tile.root.classList.add('grid-media-ready');
  }
  reset() {
    LiveCameraClock.shared.remove(this);
    this.clock.reset();
    if (!this.probing && !this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(null);
    if (!this.probing && this.tile.root.classList.contains('grid-media-ready') && this.video.readyState >= 2 && this.video.videoWidth && !this.frozen) {
      const frozen = document.createElement('canvas');
      frozen.className = 'grid-camera-canvas'; frozen.width = this.video.videoWidth; frozen.height = this.video.videoHeight;
      frozen.getContext('2d').drawImage(this.video, 0, 0);
      this.tile.root.querySelector('.surface').append(frozen); this.frozen = frozen;
      this.video.style.visibility = 'hidden';
      this.tile.zoom?.refresh();
    }
    ++this.version; this.resolveReady?.(null); this.resolveReady = null;
    this.finishAppend?.(); this.finishRemoval?.(); this.finishRemoval = null;
    if (this.callbackUsesVideo) this.video.cancelVideoFrameCallback?.(this.callback);
    else cancelAnimationFrame(this.callback);
    this.painting = false; this.lastTime = this.progressTime = this.prunedThrough = undefined;
    this.video.pause(); this.video.removeAttribute('src'); this.video.load();
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = this.buffer = this.media = null;
    if (!this.probing && !this.frozen) this.tile.root.classList.remove('grid-media-ready');
  }
  promote() {
    this.probing = false; this.video.className = 'grid-camera-video'; this.showFrame();
  }
  expire() { this.frozen?.remove(); this.frozen = null; if (!this.probing) this.tile.root.classList.remove('grid-media-ready'); }
  stop(preserve = false) {
    this.reset(); this.video.remove();
    if (!preserve) this.expire();
    return preserve ? this.frozen : null;
  }
}

class SoftwareCameraVideo {
  constructor(tile, frame, failure, probing = false) {
    this.tile = tile; this.requests = new Map(); this.sequence = 0;
    this.probing = probing;
    this.clock = new CameraFrameClock();
    if (!probing && !this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(null);
    this.canvas = document.createElement('canvas'); this.canvas.className = probing ? 'camera-decoder-probe' : 'grid-camera-canvas';
    this.canvas.setAttribute('aria-label', `${tile.name} browser decoded live video`);
    try {
      this.worker = new Worker('/camera-decoder-worker.js');
      const canvas = this.canvas.transferControlToOffscreen();
      this.worker.postMessage({ type: 'canvas', canvas }, [canvas]);
      tile.root.querySelector('.surface').append(this.canvas);
    } catch (error) {
      this.worker?.terminate();
      throw error;
    }
    this.worker.onmessage = event => {
      const message = event.data;
      if (message.type === 'frame') {
        const resized = this.canvas.videoWidth !== message.width || this.canvas.videoHeight !== message.height;
        this.canvas.videoWidth = message.width; this.canvas.videoHeight = message.height;
        this.canvas.frameVersion = (this.canvas.frameVersion || 0) + 1;
        if (!this.probing) {
          this.tile.root.classList.add('grid-media-ready');
          if (resized) this.tile.zoom?.refresh();
          if (!this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(this.clock.time(message.mediaTime));
        }
        frame(message.decodeMilliseconds, message.mediaTime);
      }
      else if (message.type === 'drop') this.dropped?.(message.count);
      else if (message.type === 'done') { this.requests.get(message.id)?.resolve(); this.requests.delete(message.id); }
      else if (message.type === 'error') { const request = this.requests.get(message.id); if (request) { request.reject(new Error(message.message)); this.requests.delete(message.id); } else failure(message.message); }
    };
    this.worker.onerror = () => failure('Browser software decoder could not start.');
  }
  send(type, data, timing) {
    const id = ++this.sequence;
    return new Promise((resolve, reject) => {
      this.requests.set(id, { resolve, reject });
      this.worker.postMessage({ type, data, id, timing }, data ? [data] : []);
    });
  }
  initialize(metadata, bytes) { return this.send('initialize', bytes); }
  fragment(bytes, timing) {
    this.clock.update(timing);
    const position = this.probing || this.tile.root.classList.contains('live-media-ready') ? (LiveCameraClock.shared.remove(this), null)
      : LiveCameraClock.shared.update(this, this.clock, timing?.endSeconds);
    return this.send('fragment', bytes, position === null ? null : { samples: this.clock.samples, position, rate: LiveCameraClock.shared.rate,
      sentAt: (performance.timeOrigin || 0) + performance.now(), toleranceMilliseconds: LiveCameraClock.shared.toleranceMilliseconds });
  }
  reset() {
    LiveCameraClock.shared.remove(this);
    this.clock.reset();
    if (!this.probing && !this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(null);
    this.worker?.postMessage({ type: 'reset' });
  }
  promote() {
    this.probing = false; this.canvas.className = 'grid-camera-canvas'; this.tile.root.classList.add('grid-media-ready');
  }
  expire() { if (!this.probing) this.tile.root.classList.remove('grid-media-ready'); }
  stop(preserve = false) {
    LiveCameraClock.shared.remove(this);
    this.worker.onmessage = this.worker.onerror = null;
    this.worker.terminate(); for (const request of this.requests.values()) request.resolve(); this.requests.clear();
    if (!preserve) { this.canvas.remove(); this.expire(); }
    return preserve ? this.canvas : null;
  }
}

class CameraPlayback {
  static reports = new Map();
  static probeWarmupMilliseconds = 2000;
  static capabilities() {
    return { native: !!(window.MediaSource || window.ManagedMediaSource),
      browser: typeof WebAssembly === 'object' && typeof Worker === 'function' && typeof OffscreenCanvas === 'function' && !!HTMLCanvasElement.prototype.transferControlToOffscreen };
  }
  constructor(channel, tile, preference, budget, changed, demo = false) {
    Object.assign(this, { channel, tile, preference, budget, changed, demo });
    this.failed = new Set(); this.stats = new PlaybackStatistics(); this.disposed = false; this.suspended = false;
    this.unsupported = new Map();
    this.decoderRetry = new CameraVideoRecovery(budget.retryDelaysSeconds);
    this.upgradeRetry = new CameraVideoRecovery('30, 60, 120, 300');
    this.resolve();
  }
  get player() { return this.active?.player ?? null; }
  get transport() { return this.active?.transport ?? null; }
  get capabilities() { return this.demo ? { native: false, browser: false } : CameraPlayback.capabilities(); }
  resolve(reason = '') {
    if (this.disposed || this.suspended) return;
    this.stopActive();
    const choice = PlaybackPolicy.choose(this.preference,
      { ...this.capabilities, reason: this.demo ? 'Demo cameras supply JPEG pictures.' : reason }, this.failed);
    this.mimeType = null; this.nativeSupported = null; this.connectionMessage = ''; this.reconnecting = false;
    this.method = choice.method; this.message = choice.message; this.confirmed = choice.method === 'server'; this.stats.reset(); this.report();
    if (!this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(choice.method === 'server' ? undefined : null);
    if (choice.method && choice.method !== 'server') this.startDecoder(choice.method);
    this.updateMethodRetry();
    this.changed();
  }
  startDecoder(method, probing = false) {
    const session = { method };
    if (probing) this.probe = session;
    else this.active = session;
    const current = () => this.active === session || this.probe === session;
    const frame = (decodeTime, mediaTime) => {
      if (!current() || session.transport?.recovery.retryAt != null) return;
      session.transport?.frame();
      if (this.probe === session) {
        this.probeFrame(session, mediaTime, decodeTime);
        return;
      }
      this.activeFrame(decodeTime);
    };
    const failure = (message, unsupported, mimeType) => {
      if (this.probe === session) {
        this.markUnsupported(unsupported, mimeType);
        this.finishProbe();
      } else if (this.active === session) this.fail(message, unsupported, mimeType);
    };
    try {
      if (method === 'native') {
        session.player = new NativeCameraVideo(this.tile, frame, mediaTime => frame(undefined, mediaTime), probing);
        session.player.failure = failure;
      } else {
        session.player = new SoftwareCameraVideo(this.tile, frame, failure, probing);
        session.player.dropped = count => {
          if (this.active !== session) return;
          this.stats.drop(count);
          if (!this.reportedAt || performance.now() - this.reportedAt >= 1000) this.report();
        };
      }
      const player = session.player, initialize = player.initialize.bind(player);
      player.initialize = async (metadata, bytes) => {
        if (!current()) return;
        session.mimeType = metadata.mimeType;
        session.nativeSupported = !!(window.MediaSource || window.ManagedMediaSource)?.isTypeSupported(metadata.mimeType);
        if (this.active === session) {
          this.mimeType = session.mimeType; this.nativeSupported = session.nativeSupported;
          const previousMimeType = this.unsupported.get('native');
          if (previousMimeType && previousMimeType !== this.mimeType && this.nativeSupported) {
            this.unsupported.delete('native');
            this.updateMethodRetry();
          }
          this.report();
        }
        await initialize(metadata, bytes);
        if (current() && method === 'native') await player.append(bytes);
      };
      session.transport = new CameraVideoTransport(this.channel, player, failure, message => {
        if (this.probe === session) {
          if (message.status === 'retrying' || message.status === 'waiting') session.firstFrameAt = session.lastFrameAt = session.mediaTime = undefined;
          return;
        }
        if (this.active !== session) return;
        if (['connecting', 'waiting', 'retrying'].includes(message.status)) {
          this.mimeType = null; this.nativeSupported = null; this.confirmed = false;
        }
        this.reconnecting = message.status === 'retrying';
        this.connectionMessage = message.status === 'connecting' ? 'Connecting camera video…' : message.status === 'waiting' ? 'Waiting for camera activity.' : message.message;
        this.report();
      }, this.budget);
    } catch (error) { failure(error.message); }
  }
  activeFrame(decodeTime) {
    this.tile.lastFrame = performance.now();
    if (decodeTime !== undefined) this.stats.frame(decodeTime);
    const recovered = !this.confirmed || this.reconnecting;
    this.confirmed = true; this.reconnecting = false; this.connectionMessage = '';
    this.retained?.remove(); this.retained = null;
    if (!this.retryCandidate()) this.decoderRetry.frame();
    this.updateMethodRetry();
    if (decodeTime !== undefined && this.tile.zoom?.focused) this.tile.zoom.refresh();
    if (recovered || !this.reportedAt || performance.now() - this.reportedAt >= 1000) this.report();
  }
  markUnsupported(streamUnsupported, mimeType) {
    if (streamUnsupported) {
      this.unsupported.set('native', null); this.unsupported.set('browser', null);
      this.failed.add('native'); this.failed.add('browser');
    } else if (mimeType) this.unsupported.set('native', mimeType);
  }
  fail(reason, unsupported = false, mimeType) {
    if (this.disposed || this.suspended) return;
    this.cancelProbe();
    this.clearMethodRetry();
    this.failed.add(this.method);
    this.markUnsupported(unsupported, mimeType);
    this.lastProbeMethod = null;
    this.resolve(reason);
  }
  retryCandidate() {
    const methods = ['native', 'browser', 'server'];
    const preferred = this.preference === 'automatic' ? methods.slice(0, methods.indexOf(this.method)) : [this.preference];
    const capabilities = this.capabilities;
    const candidates = preferred.filter(method => capabilities[method] && this.failed.has(method) && !this.unsupported.has(method));
    return candidates.find(method => method !== this.lastProbeMethod) ?? candidates[0];
  }
  updateMethodRetry() {
    if (this.disposed || this.suspended || this.probe) return;
    const candidate = this.retryCandidate();
    if (!candidate) {
      this.clearMethodRetry();
      return;
    }
    const fresh = this.confirmed && this.stats.frames > 0 && performance.now() - this.tile.lastFrame < 4000;
    const policy = fresh ? this.upgradeRetry : this.decoderRetry;
    if (policy === this.retryPolicy) return;
    this.clearMethodRetry();
    this.retryPolicy = policy;
    policy.failed();
    this.waitForMethodRetry();
  }
  waitForMethodRetry() {
    const remaining = this.retryPolicy.remainingSeconds;
    if (remaining) {
      if (!this.method) {
        const label = PlaybackPolicy.labels[this.preference];
        this.connectionMessage = `Retrying ${label.toLowerCase()} in ${remaining} ${remaining === 1 ? 'second' : 'seconds'}.`;
        this.report();
      }
      // Chunk long collection delays for browser timers and keep a blank tile's countdown current.
      const delay = this.retryPolicy.retryAt - performance.now();
      this.methodTimer = setTimeout(() => this.waitForMethodRetry(), this.method ? Math.min(delay, 2147483647) : Math.min(delay, 1000));
      return;
    }
    this.retryPolicy.start();
    this.clearMethodRetry();
    const candidate = this.retryCandidate();
    if (!candidate) return;
    if (!this.method) {
      this.failed.delete(candidate);
      this.resolve();
      return;
    }
    this.lastProbeMethod = candidate;
    const timeout = (this.budget.acknowledgementTimeoutSeconds || 10) * 1000 + CameraPlayback.probeWarmupMilliseconds;
    this.probeTimer = setTimeout(() => this.finishProbe(), timeout);
    this.startDecoder(candidate, true);
  }
  clearMethodRetry() {
    clearTimeout(this.methodTimer);
    this.retryPolicy?.cancel();
    this.retryPolicy = null;
  }
  probeFrame(session, mediaTime, decodeTime) {
    if (!Number.isFinite(mediaTime) || mediaTime <= session.mediaTime) return;
    const now = performance.now();
    if (session.lastFrameAt === undefined || now - session.lastFrameAt >= 4000) session.firstFrameAt = now;
    session.lastFrameAt = now;
    session.mediaTime = mediaTime;
    if (now - session.firstFrameAt < CameraPlayback.probeWarmupMilliseconds) return;
    this.probe = null;
    clearTimeout(this.probeTimer);
    this.stopActive();
    this.active = session;
    this.failed.delete(session.method);
    this.method = session.method;
    this.message = PlaybackPolicy.choose(this.preference, this.capabilities, this.failed).message;
    this.mimeType = session.mimeType; this.nativeSupported = session.nativeSupported;
    this.stats.reset();
    this.upgradeRetry.failures = 0;
    session.player.promote();
    if (!this.tile.root.classList.contains('live-media-ready')) this.tile.detections?.setPlaybackTime(session.player.clock.time(mediaTime));
    this.tile.zoom?.refresh();
    this.confirmed = false;
    this.activeFrame(decodeTime);
    this.changed();
  }
  finishProbe() {
    this.cancelProbe();
    this.updateMethodRetry();
  }
  cancelProbe() {
    clearTimeout(this.probeTimer);
    const probe = this.probe;
    this.probe = null;
    probe?.transport?.stop();
    probe?.player?.stop();
  }
  stopActive(preserve = false) {
    const active = this.active;
    this.active = null;
    active?.transport?.stop();
    return active?.player?.stop(preserve);
  }
  setLiveMedia(metadata) {
    this.liveMedia = metadata;
    if (!this.disposed) this.report();
  }
  report() {
    this.reportedAt = performance.now();
    const report = { channel: this.channel, preference: this.preference, method: this.method, confirmed: this.confirmed, message: this.message,
      connectionMessage: this.connectionMessage || '', capabilities: CameraPlayback.capabilities(), failedMethods: [...this.failed], ...this.stats.snapshot() };
    report.mimeType = this.mimeType || null; report.nativeSupported = this.nativeSupported ?? null;
    report.liveMedia = this.liveMedia || null;
    report.recovery = this.transport?.snapshot() || null;
    const method = this.liveMedia ? 'native' : this.method;
    const mimeType = this.liveMedia ? this.liveMedia.mimeType : this.mimeType;
    const codecs = CameraMediaCodecs.describe(mimeType);
    report.videoCodec = method === 'server' ? 'JPEG' : method ? codecs.video : null;
    report.audioCodec = this.liveMedia?.hasAudio ? codecs.audio : null;
    CameraPlayback.reports.set(this.channel, report);
    const warning = method === 'server' ? ' · server CPU' : method === 'browser' ? ' · client CPU' : '';
    const details = [method !== 'server' ? report.videoCodec : null].filter(Boolean);
    const media = details.length ? ` · ${details.join(' · ')}` : '';
    const automatic = !this.liveMedia && this.preference === 'automatic' ? 'Auto: ' : '';
    const checking = this.confirmed || this.liveMedia ? '' : 'Checking ';
    this.tile.playbackStatus.textContent = method ? `${automatic}${checking}${PlaybackPolicy.labels[method]}${media}${warning}${this.reconnecting ? ` · ${this.connectionMessage}` : ''}` : 'Playback unavailable';
    const description = this.liveMedia ? 'Live video and sound.' : `${this.message} ${this.connectionMessage || ''}`;
    this.tile.playbackStatus.title = description + (mimeType && method && method !== 'server' ? ` Stream: ${mimeType}.` : '');
    this.tile.playbackStatus.classList.toggle('playback-warning', method === 'server' || method === 'browser' || !method);
    document.dispatchEvent(new CustomEvent('scv-playback-method', { detail: report }));
  }
  frame(milliseconds) {
    if (this.retained) { this.retained.remove(); this.retained = null; this.tile.root.classList.remove('grid-media-ready'); }
    this.stats.frame(milliseconds);
    this.updateMethodRetry();
    if (!this.reportedAt || performance.now() - this.reportedAt >= 1000) this.report();
  }
  expire() { this.retained?.remove(); this.retained = null; this.player?.expire(); this.tile.root.classList.remove('grid-media-ready'); }
  suspend(suspended) {
    if (this.suspended === suspended || this.disposed) return;
    this.suspended = suspended;
    if (suspended) {
      this.clearMethodRetry(); this.cancelProbe();
      this.retained = this.stopActive(true) || this.retained;
    }
    else this.resolve();
    this.changed();
  }
  stop() {
    this.disposed = true;
    this.clearMethodRetry(); this.cancelProbe(); this.stopActive(); this.expire();
    CameraPlayback.reports.delete(this.channel);
  }
}
