'use strict';

// One synchronized player per listening camera. Camera credentials never enter this class.
class LiveCameraPlayer {
  constructor(changed) {
    this.changed = changed;
    this.clock = new CameraFrameClock();
    this.channel = null;
    this.volume = 1;
    this.muted = false;
    this.status = 'stopped';
    this.message = 'Use Audio OFF on a camera to turn sound on.';
    this.version = 0;
  }

  notify(status, message) {
    this.status = status; this.message = message;
    this.changed(this);
  }

  start(channel, tile, compatibility = false) {
    this.stop();
    this.compatibility = compatibility;
    const Media = window.MediaSource || window.ManagedMediaSource;
    if (!Media) { this.notify('unsupported', 'Live sound is not supported in this browser.'); return; }
    this.channel = channel; this.tile = tile; this.Media = Media;
    tile.detections?.setPlaybackTime(null);
    this.video = document.createElement('video');
    this.video.className = 'live-camera-video';
    this.video.playsInline = true; this.video.autoplay = true;
    this.video.disableRemotePlayback = true; this.video.disablePictureInPicture = true;
    this.video.volume = this.volume; this.video.muted = this.muted || !!this.talking;
    this.video.setAttribute('aria-label', `${tile.name} live video and sound`);
    tile.root.querySelector('.surface').append(this.video);
    const version = ++this.version;
    this.video.onplaying = () => {
      if (this.version !== version) return;
      tile.root.classList.add('live-media-ready');
      tile.playback?.setLiveMedia({ mimeType: this.mimeType, hasAudio: this.hasAudio });
      this.paint();
      this.notify(this.hasAudio === false ? 'no-audio' : 'playing',
        this.hasAudio === false ? 'This camera stream has no audio.' : this.muted ? 'Live sound muted.' : `Listening to ${tile.name}.`);
    };
    this.video.onerror = () => {
      if (this.version === version) this.fail('This browser could not play the live stream.');
    };
    this.createMedia();
    // Invoke play during the Audio OFF gesture, before the first network/media data arrives.
    this.play();
    this.connect(version);
    this.watchdog = setInterval(() => {
      if (this.fragmentAt && performance.now() - this.fragmentAt > 4000) {
        this.resetMedia();
        this.notify('retrying', 'Camera stopped sending live sound. Reconnecting…');
        this.socket?.close();
      }
    }, 500);
  }

  createMedia() {
    const media = this.media = new this.Media();
    this.sourceReady = new Promise(resolve => {
      this.resolveSource = resolve;
      media.addEventListener('sourceopen', () => resolve(media), { once: true });
    });
    this.url = URL.createObjectURL(media);
    this.video.src = this.url;
    this.buffer = null; this.initialized = false; this.fragmentAt = 0;
  }

  resetMedia() {
    LiveCameraClock.shared.remove(this);
    this.paintVersion = (this.paintVersion || 0) + 1;
    this.clock.reset(); this.timing = null;
    if (this.callbackUsesVideo) this.video?.cancelVideoFrameCallback?.(this.callback);
    else cancelAnimationFrame(this.callback);
    this.painting = false;
    this.tile?.detections?.setPlaybackTime(this.tile?.playback?.method === 'server' ? undefined : null);
    this.tile?.root.classList.remove('live-media-ready');
    this.resolveSource?.(null); this.resolveSource = null;
    this.video?.pause();
    this.video?.removeAttribute('src');
    this.video?.load();
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = null; this.media = null; this.buffer = null;
    this.initialized = false; this.fragmentAt = 0;
    this.mimeType = null;
    this.tile?.playback?.setLiveMedia(null);
  }

  play() {
    const video = this.video, version = this.version;
    video?.play().catch(error => {
      if (this.version !== version || video !== this.video || error.name === 'AbortError') return;
      this.notify('blocked', 'Tap Audio OFF to allow sound playback.');
    });
  }

  connect(version) {
    if (this.version !== version) return;
    this.notify('connecting', 'Connecting live sound…');
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/cameras/${this.channel}/live${this.compatibility ? '?compatibility=true' : ''}`);
    this.socket = ws; ws.binaryType = 'arraybuffer';
    let work = Promise.resolve(), queuedBytes = 0, queuedMessages = 0;
    ws.onmessage = event => {
      const bytes = typeof event.data === 'string' ? event.data.length : event.data.byteLength;
      queuedBytes += bytes; queuedMessages++;
      if (queuedBytes > 4 * 1024 * 1024 || queuedMessages > 8) {
        this.resetMedia(); ws.close(); return;
      }
      work = work.then(async () => {
        if (this.version !== version || this.socket !== ws || ws.readyState !== WebSocket.OPEN) return;
        await this.receive(event.data, version, ws);
      }).catch(() => {
        if (this.version === version && this.socket === ws) this.fail('Live playback could not continue. Choose Audio OFF to retry.');
      }).finally(() => { queuedBytes -= bytes; queuedMessages--; });
    };
    ws.onerror = () => ws.close();
    ws.onclose = () => {
      if (this.version !== version || this.socket !== ws) return;
      this.resetMedia();
      this.notify('retrying', 'Live sound disconnected. Reconnecting…');
      this.retry = setTimeout(() => { if (this.version === version) this.connect(version); }, 2000);
    };
  }

  async receive(data, version, ws) {
    if (typeof data === 'string') {
      const message = JSON.parse(data);
      if (message.type === 'state') {
        // A stream epoch ended: discard buffered sound before waiting or retrying.
        if (this.initialized) this.resetMedia();
        this.notify(message.status, message.message);
      } else if (message.type === 'init') {
        if (!this.Media.isTypeSupported(message.mimeType)) {
          this.fail('Live sound is not supported by this browser’s media decoder.'); return;
        }
        if (this.initialized) this.resetMedia();
        if (!this.media) this.createMedia();
        const media = await this.sourceReady;
        if (!media || this.version !== version || this.socket !== ws) return;
        this.hasAudio = message.hasAudio;
        this.mimeType = message.mimeType;
        this.video.muted = this.muted || !!this.talking || !message.hasAudio;
        this.buffer = media.addSourceBuffer(message.mimeType);
        this.initialized = true;
        if (!message.hasAudio) this.notify('no-audio', 'This camera stream has no audio.');
        else this.changed(this);
      } else if (message.type === 'fragment') this.timing = message;
      return;
    }
    const buffer = this.buffer;
    if (!buffer) return;
    this.clock.update(this.timing); this.timing = null;
    await this.update(buffer, () => buffer.appendBuffer(data));
    if (this.version !== version || this.socket !== ws || buffer !== this.buffer) return;
    const ranges = this.video.buffered;
    if (!ranges.length) return; // Initialization data contains no media samples.
    this.fragmentAt = performance.now();
    const start = ranges.start(ranges.length - 1), end = ranges.end(ranges.length - 1);
    const synchronized = this.synchronize();
    if (synchronized === null && (this.video.currentTime < start || end - this.video.currentTime > 3)) {
      this.video.currentTime = Math.max(start, end - 1.2);
    }
    if (this.status !== 'blocked' && synchronized === null && this.video.paused) this.play();
    this.paint();
    if (this.video.currentTime > 10 && buffer.buffered.length && buffer.buffered.start(0) < this.video.currentTime - 8) {
      await this.update(buffer, () => buffer.remove(0, this.video.currentTime - 8));
    }
  }

  synchronize() {
    if (this.status === 'blocked') { LiveCameraClock.shared.remove(this); return false; }
    return LiveCameraClock.shared.synchronize(this, this.clock, this.video, () => this.play());
  }

  paint() {
    if (this.painting || !this.video) return;
    this.painting = true;
    const video = this.video, version = this.paintVersion;
    const paint = (_, metadata) => {
      if (!this.painting || video !== this.video || version !== this.paintVersion) return;
      if (video.readyState >= 2 && (!video.paused || metadata) && this.tile?.root.classList.contains('live-media-ready'))
        this.tile.detections?.setPlaybackTime(this.clock.time(metadata?.mediaTime ?? video.currentTime));
      this.synchronize();
      this.callbackUsesVideo = !!video.requestVideoFrameCallback && !video.paused;
      this.callback = this.callbackUsesVideo ? video.requestVideoFrameCallback(paint) : requestAnimationFrame(paint);
    };
    paint();
  }

  update(buffer, action) {
    return new Promise((resolve, reject) => {
      const finish = () => { cleanup(); resolve(); };
      const failure = () => { cleanup(); reject(new Error('Media buffer failed.')); };
      const cleanup = () => {
        buffer.removeEventListener('updateend', finish);
        buffer.removeEventListener('error', failure);
        buffer.removeEventListener('abort', finish);
      };
      buffer.addEventListener('updateend', finish, { once: true });
      buffer.addEventListener('error', failure, { once: true });
      buffer.addEventListener('abort', finish, { once: true });
      try { action(); } catch (error) { cleanup(); reject(error); }
    });
  }

  setMuted(muted) {
    this.muted = muted;
    if (this.video) this.video.muted = muted || !!this.talking || this.hasAudio === false;
    if (this.status === 'playing') this.notify('playing', muted ? 'Live sound muted.' : `Listening to ${this.tile.name}.`);
    else this.changed(this);
  }

  setVolume(volume) {
    this.volume = Math.max(0, Math.min(1, volume));
    if (this.video) this.video.volume = this.volume;
  }

  setTalkbackActive(active) {
    if (this.talking === active) return;
    this.talking = active;
    if (this.video) this.video.muted = this.muted || active || this.hasAudio === false;
  }

  fail(message) {
    this.stop(); this.notify('error', message);
  }

  stop() {
    ++this.version;
    clearTimeout(this.retry); clearInterval(this.watchdog);
    const socket = this.socket; this.socket = null; socket?.close();
    this.resetMedia(); this.video?.remove(); this.video = null;
    this.channel = null; this.hasAudio = null; this.tile = null;
    this.notify('stopped', 'Use Audio OFF on a camera to turn sound on.');
  }
}

// Coordinates listening choices; each player owns its media, buffering and reconnection.
class LiveCameraAudioController {
  constructor(changed, createPlayer = callback => new LiveCameraPlayer(callback),
    storage = typeof localStorage === 'undefined' ? null : localStorage) {
    this.changed = changed;
    this.createPlayer = createPlayer;
    this.storage = storage;
    this.compatibility = false;
    try { this.compatibility = storage?.getItem('scv.live-audio-compatibility') === 'true'; } catch {}
    this.players = new Map();
    this.audioAvailability = new Map();
    this.volume = 1;
    this.talking = false;
  }

  get active() { return [...this.players.values()].some(player => player.channel !== null); }
  available(channel) { return this.audioAvailability.get(channel) !== false; }

  listen(channel, tile, keepOthers = false) {
    if (!tile || !this.available(channel)) return;
    let player = this.players.get(channel);
    if (!player) {
      player = this.createPlayer(() => {
        if (player.hasAudio !== null && player.hasAudio !== undefined)
          this.audioAvailability.set(channel, player.hasAudio);
        if (player.hasAudio === false && player.channel !== null) {
          // Release silent media and return to the tile's ordinary picture playback.
          player.stop(); player.notify('no-audio', 'This camera stream has no audio.');
        }
        this.changed();
      });
      this.players.set(channel, player);
      player.setVolume(this.volume);
      player.setTalkbackActive(this.talking);
    }
    if (player.channel !== null && !player.muted && player.status !== 'blocked') {
      player.stop(); return;
    }
    player.setMuted(false);
    if (player.channel === null) player.start(channel, tile, this.compatibility);
    else player.play();
    if (player.channel !== null && !keepOthers) {
      for (const other of this.players.values())
        if (other !== player && other.channel !== null) other.setMuted(true);
    }
    this.changed();
  }

  setCompatibility(value) {
    value = !!value;
    if (this.compatibility === value) return;
    this.compatibility = value;
    try { this.storage?.setItem('scv.live-audio-compatibility', String(value)); } catch {}
    for (const player of this.players.values()) {
      if (player.channel !== null) player.start(player.channel, player.tile, value);
    }
    this.changed();
  }

  setMuted(channel, muted) { this.players.get(channel)?.setMuted(muted); }

  setVolume(volume) {
    this.volume = Math.max(0, Math.min(1, volume));
    for (const player of this.players.values()) player.setVolume(this.volume);
  }

  setTalkbackActive(active) {
    this.talking = active;
    for (const player of this.players.values()) player.setTalkbackActive(active);
  }

  stop() {
    for (const player of this.players.values()) player.stop();
    this.players.clear(); this.audioAvailability.clear();
    this.changed();
  }
}
