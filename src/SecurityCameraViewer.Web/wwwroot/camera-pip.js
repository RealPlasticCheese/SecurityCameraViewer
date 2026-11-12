'use strict';

// A background video supplies the grid to Android PiP while the viewer owns fullscreen.
// Its compositor callback drives capture even when page animation callbacks are suspended.
class CameraPictureInPictureView {
  constructor(tiles, viewport, failure = () => {}) {
    this.tiles = tiles; this.viewport = viewport; this.failure = failure;
  }

  media(tile) {
    const surface = tile.root.querySelector('.surface');
    const media = tile.root.classList.contains('live-media-ready') ? surface.querySelector('.live-camera-video') :
      tile.root.classList.contains('grid-media-ready') ? surface.querySelector('.grid-camera-canvas') || surface.querySelector('.grid-camera-video') :
      tile.root.classList.contains('has-frame') ? tile.img : null;
    if (!media || media.tagName === 'VIDEO' && media.readyState < 2 || media.tagName === 'IMG' && !media.complete) return null;
    return (media.videoWidth || media.naturalWidth || media.width) > 0 && (media.videoHeight || media.naturalHeight || media.height) > 0 ? media : null;
  }

  picture(tile, media, width, height) {
    if (tile.zoom) return tile.zoom.bounds(media, { width, height });
    const mw = media.videoWidth || media.naturalWidth || media.width, mh = media.videoHeight || media.naturalHeight || media.height;
    const scale = Math.min(width / mw, height / mh);
    return { left: (width - mw * scale) / 2, top: (height - mh * scale) / 2, width: mw * scale, height: mh * scale };
  }

  start() {
    this.stop();
    try {
      const tiles = this.tiles();
      if (!tiles.length) throw new Error('There are no cameras in this view.');
      this.canvas = document.createElement('canvas');
      this.context = this.canvas.getContext('2d');
      this.video = document.createElement('video');
      if (!this.context || !this.canvas.captureStream || !this.video.requestVideoFrameCallback)
        throw new Error('This browser cannot capture the camera grid for picture-in-picture.');
      const { width, scale = 1 } = this.viewport();
      const tile = tiles.find(tile => this.media(tile));
      const picture = tile && this.picture(tile, this.media(tile), 1, 1);
      const aspect = picture ? picture.width / picture.height : 16 / 9;
      const columns = Math.ceil(Math.sqrt(tiles.length)), rows = Math.ceil(tiles.length / columns);
      this.canvas.width = Math.max(1, Math.round(width * scale));
      this.canvas.height = Math.max(1, Math.round(this.canvas.width * rows / (columns * aspect)));
      this.stream = this.canvas.captureStream(0); this.track = this.stream.getVideoTracks()[0];
      if (!this.track?.requestFrame) throw new Error('This browser cannot request camera grid video frames.');
      const video = this.video;
      video.className = 'camera-pip-video'; video.muted = true; video.autoplay = true; video.playsInline = true;
      video.disableRemotePlayback = true; video.setAttribute('aria-label', 'Live camera view');
      video.srcObject = this.stream;
      video.onerror = () => { if (this.video === video) this.fail(new Error('The browser could not play the camera grid video.')); };
      video.onplay = () => {
        if (this.video !== video) return;
        try { this.draw(); this.track.requestFrame(); } catch (error) { this.fail(error); }
      };
      document.body.prepend(video);
      this.events = new AbortController();
      document.addEventListener('visibilitychange', () => this.refresh(), { signal: this.events.signal });
      window.addEventListener('resize', () => this.refresh(), { signal: this.events.signal });
      this.schedule(); this.draw(); this.track.requestFrame();
      this.playing = video.play();
      return video;
    } catch (error) { this.stop(); throw error; }
  }

  async enterFullscreen() {
    const video = this.start();
    try {
      // Request fullscreen in the original gesture; do not await media startup first.
      await Promise.all([document.documentElement.requestFullscreen(), this.playing]);
    } catch (error) { if (this.video === video) this.stop(); throw error; }
  }

  draw() {
    const tiles = this.tiles(), { width, height } = this.canvas, context = this.context;
    context.fillStyle = '#0c121d'; context.fillRect(0, 0, width, height);
    if (!tiles.length) return;
    const columns = Math.ceil(Math.sqrt(tiles.length)), rows = Math.ceil(tiles.length / columns);
    const cellWidth = width / columns, cellHeight = height / rows;
    const fontSize = Math.max(1, Math.round(cellWidth / 24));
    context.font = `${fontSize}px system-ui`; context.textBaseline = 'bottom';
    for (const [index, tile] of tiles.entries()) {
      const left = index % columns * cellWidth, top = Math.floor(index / columns) * cellHeight;
      context.save(); context.beginPath(); context.rect(left, top, cellWidth, cellHeight); context.clip();
      const media = this.media(tile);
      if (media) {
        const picture = this.picture(tile, media, cellWidth, cellHeight);
        if (picture) context.drawImage(media, left + picture.left, top + picture.top, picture.width, picture.height);
      } else {
        context.fillStyle = '#9baac0';
        context.fillText(tile.message?.textContent || 'Waiting for camera', left + fontSize, top + cellHeight / 2, cellWidth - 2 * fontSize);
      }
      context.fillStyle = '#10151ed9'; context.fillRect(left, top + cellHeight - 2 * fontSize, cellWidth, 2 * fontSize);
      context.fillStyle = '#edf3fa'; context.fillText(tile.name, left + fontSize / 2, top + cellHeight - fontSize / 2, cellWidth - fontSize);
      context.restore();
    }
  }

  isPresented() {
    // Android's legacy persistent-video mode hides the foreground viewer without
    // setting pictureInPictureElement. Its zero-width viewport identifies that mode.
    return !!this.video && (document.pictureInPictureElement === this.video || this.viewport().width === 0);
  }

  cancelFrame() {
    if (this.frameUsesVideo) this.video?.cancelVideoFrameCallback?.(this.frame);
    else cancelAnimationFrame(this.frame);
    this.frame = null;
  }

  refresh() {
    if (!this.video) return;
    this.cancelFrame();
    try { this.draw(); this.schedule(); this.track.requestFrame(); }
    catch (error) { this.fail(error); }
  }

  schedule() {
    const video = this.video;
    const paint = () => {
      if (this.video !== video) return;
      this.frame = null;
      try { this.draw(); this.schedule(); this.track.requestFrame(); }
      catch (error) { this.fail(error); }
    };
    // The underlay is occluded by the normal viewer, so foreground capture uses
    // its animation clock. Visible PiP uses its own unthrottled compositor clock.
    this.frameUsesVideo = document.hidden || this.isPresented();
    this.frame = this.frameUsesVideo ? video.requestVideoFrameCallback(paint) : requestAnimationFrame(paint);
  }

  fail(error) { this.stop(); this.failure(error); }

  stop() {
    const video = this.video; this.cancelFrame(); this.video = null;
    this.events?.abort(); this.events = null;
    if (video) { video.onerror = video.onplay = null; video.pause(); video.srcObject = null; video.remove(); }
    for (const track of this.stream?.getTracks() || []) track.stop();
    this.stream = this.track = this.canvas = this.context = this.playing = null;
  }
}
