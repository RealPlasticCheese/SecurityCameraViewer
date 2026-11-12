'use strict';

// All geometry uses CSS pixels. Crops use fractions of the camera picture so
// resizing the viewport or switching between JPEG and live video keeps the area.
const CameraZoom = (() => {
  function ratio(value) {
    if (typeof value !== 'string' || !/^(\d+(?:\.\d*)?|\.\d+):(\d+(?:\.\d*)?|\.\d+)$/.test(value)) return null;
    const [width, height] = value.split(':').map(Number), aspect = width / height;
    return width > 0 && height > 0 && Number.isFinite(width) && Number.isFinite(height) && Number.isFinite(aspect) && aspect > 0 ? aspect : null;
  }

  function picture(width, height, mediaWidth, mediaHeight, aspectRatio = 'auto') {
    if (!(width > 0 && height > 0 && mediaWidth > 0 && mediaHeight > 0)) return null;
    const aspect = ratio(aspectRatio) || mediaWidth / mediaHeight;
    const w = Math.min(width, height * aspect), h = w / aspect;
    return { left: (width - w) / 2, top: (height - h) / 2, width: w, height: h };
  }

  function selection(start, end, bounds, aspect) {
    const right = end.x >= start.x, down = end.y >= start.y;
    const availableWidth = right ? bounds.left + bounds.width - start.x : start.x - bounds.left;
    const availableHeight = down ? bounds.top + bounds.height - start.y : start.y - bounds.top;
    const width = Math.max(0, Math.min(Math.max(Math.abs(end.x - start.x), Math.abs(end.y - start.y) * aspect),
      availableWidth, availableHeight * aspect));
    const height = width / aspect;
    return { left: right ? start.x : start.x - width, top: down ? start.y : start.y - height, width, height };
  }

  function crop(area, bounds) {
    return { left: (area.left - bounds.left) / bounds.width, top: (area.top - bounds.top) / bounds.height,
      width: area.width / bounds.width, height: area.height / bounds.height };
  }

  function zoomScale(area, bounds, width, height) {
    return Math.max(width / (area.width * bounds.width), height / (area.height * bounds.height));
  }

  function pan(area, dx, dy, bounds, width, height) {
    const scale = zoomScale(area, bounds, width, height);
    const visibleWidth = width / (bounds.width * scale), visibleHeight = height / (bounds.height * scale);
    const halfWidth = Math.min(0.5, visibleWidth / 2), halfHeight = Math.min(0.5, visibleHeight / 2);
    // Keep the visible picture inside its edges even after the viewport changes aspect.
    // An axis that still has letterbox margins stays centered until it fills the view.
    const centerX = Math.max(halfWidth, Math.min(1 - halfWidth,
      area.left + area.width / 2 - dx / (bounds.width * scale)));
    const centerY = Math.max(halfHeight, Math.min(1 - halfHeight,
      area.top + area.height / 2 - dy / (bounds.height * scale)));
    return { ...area, left: centerX - area.width / 2, top: centerY - area.height / 2 };
  }

  function zoom(area, anchor, factor, bounds, width, height, destination = anchor) {
    const current = area || { left: (1 - width / bounds.width) / 2, top: (1 - height / bounds.height) / 2,
      width: width / bounds.width, height: height / bounds.height };
    const scale = zoomScale(current, bounds, width, height);
    // Bound gesture magnification, without reducing an existing stronger rectangle zoom.
    const nextScale = Math.max(1, Math.min(Math.max(32, scale), scale * factor));
    if (nextScale <= 1.000001) return null;
    if (nextScale === scale && anchor.x === destination.x && anchor.y === destination.y) return area;
    const ratio = scale / nextScale, w = current.width * ratio, h = current.height * ratio;
    // Keep the camera pixel under the pointer or moving pinch midpoint, subject to picture edges.
    const centerX = current.left + current.width / 2 + (anchor.x - width / 2) / (bounds.width * scale) -
      (destination.x - width / 2) / (bounds.width * nextScale);
    const centerY = current.top + current.height / 2 + (anchor.y - height / 2) / (bounds.height * scale) -
      (destination.y - height / 2) / (bounds.height * nextScale);
    return pan({ left: centerX - w / 2, top: centerY - h / 2, width: w, height: h }, 0, 0, bounds, width, height);
  }

  function projection(area, bounds, width, height) {
    // Fill the viewport without stretching, including after an aspect-ratio change.
    const scale = zoomScale(area, bounds, width, height);
    const x = width / 2 - (bounds.left + (area.left + area.width / 2) * bounds.width) * scale;
    const y = height / 2 - (bounds.top + (area.top + area.height / 2) * bounds.height) * scale;
    return { x, y, scale };
  }

  function transform(area, bounds, width, height) {
    const { x, y, scale } = projection(area, bounds, width, height);
    return `translate(${x}px, ${y}px) scale(${scale})`;
  }

  return { ratio, picture, selection, crop, pan, zoom, projection, transform };
})();

class CameraZoomView {
  constructor(root, changed = () => {}, aspectRatios = {}) {
    this.root = root;
    this.changed = changed;
    this.aspectRatios = aspectRatios;
    this.surface = root.querySelector('.surface');
    this.focused = false;
    this.crop = null;
    this.touches = new Map();
    this.pinch = null;
    const document = root.ownerDocument;
    this.selection = document.createElement('div');
    this.selection.className = 'camera-zoom-selection';
    this.selection.hidden = true;
    this.selection.setAttribute('aria-hidden', 'true');
    this.help = document.createElement('div');
    this.help.className = 'camera-zoom-help';
    this.help.hidden = true;
    this.resetButton = document.createElement('button');
    this.resetButton.type = 'button';
    this.resetButton.className = 'camera-zoom-reset';
    this.resetButton.textContent = 'Reset';
    this.resetButton.setAttribute('aria-label', 'Reset camera zoom');
    this.toolbar = document.createElement('div');
    this.toolbar.className = 'camera-view-toolbar';
    this.toolbar.append(this.resetButton);
    this.help.append(this.toolbar);
    this.surface.append(this.selection, this.help);

    this.events = new AbortController();
    const options = { signal: this.events.signal };
    this.surface.addEventListener('pointerdown', event => this.begin(event), options);
    this.surface.addEventListener('pointermove', event => this.move(event), options);
    this.surface.addEventListener('pointerup', event => this.finish(event), options);
    this.surface.addEventListener('wheel', event => this.wheel(event), { ...options, passive: false });
    for (const name of ['pointercancel', 'lostpointercapture']) {
      this.surface.addEventListener(name, event => {
        if (this.drag?.pointerId === event.pointerId || this.touches.has(event.pointerId)) this.cancelSelection();
      }, options);
    }
    for (const name of ['load', 'loadedmetadata', 'playing', 'resize']) {
      this.surface.addEventListener(name, () => this.refresh(), { ...options, capture: true });
    }
    this.help.addEventListener('dblclick', event => event.stopPropagation(), options);
    this.resetButton.addEventListener('click', () => this.reset(), options);
    document.defaultView.addEventListener('blur', () => this.cancelSelection(), options);
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.cancelSelection(); }, options);
    this.observer = new ResizeObserver(() => { this.cancelSelection(); this.refresh(); });
    this.observer.observe(this.surface);
  }

  setFocused(focused) {
    this.focused = focused;
    this.refresh();
  }

  bounds(media, viewport) {
    return CameraZoom.picture(viewport.width, viewport.height,
      media?.videoWidth || media?.naturalWidth || media?.width, media?.videoHeight || media?.naturalHeight || media?.height,
      this.aspectRatio(media));
  }

  aspectRatio(media) {
    const enhanced = media?.classList?.contains('camera-enhanced-picture');
    if (media?.classList?.contains('live-camera-video') || enhanced && this.root.classList.contains('live-media-ready'))
      return this.aspectRatios.main || 'auto';
    if (media?.tagName === 'IMG' || enhanced && !this.root.classList.contains('grid-media-ready'))
      return this.aspectRatios.jpeg ?? this.aspectRatios.preview ?? 'auto';
    return this.aspectRatios.preview || 'auto';
  }

  position(media, viewport) {
    const bounds = this.bounds(media, viewport), ratio = this.aspectRatio(media);
    const forced = bounds && CameraZoom.ratio(ratio) !== null;
    // Fit the corrected rectangle first, then apply the same crop as overlays.
    // Auto keeps the browser's intrinsic video/pixel-aspect handling.
    Object.assign(media.style, {
      position: forced ? 'absolute' : '', inset: forced ? 'auto' : '', objectFit: forced ? 'fill' : '',
      left: forced ? `${bounds.left}px` : '', top: forced ? `${bounds.top}px` : '',
      width: forced ? `${bounds.width}px` : '', height: forced ? `${bounds.height}px` : ''
    });
    if (this.crop && bounds && forced) {
      const { x, y, scale } = CameraZoom.projection(this.crop, bounds, viewport.width, viewport.height);
      media.style.transform = `translate(${x + (scale - 1) * bounds.left}px, ${y + (scale - 1) * bounds.top}px) scale(${scale})`;
    } else {
      media.style.transform = this.crop && bounds ? CameraZoom.transform(this.crop, bounds, viewport.width, viewport.height) : '';
    }
  }

  picture(viewport) {
    if (this.root.classList.contains('rewind-active') && !this.root.classList.contains('live-media-ready')) return null;
    const media = this.root.classList.contains('live-media-ready') ? this.surface.querySelector('.live-camera-video') || this.surface.querySelector('video') :
      this.root.classList.contains('grid-media-ready') ? (this.surface.querySelector('.grid-camera-canvas') || this.surface.querySelector('.grid-camera-video')) :
      this.root.classList.contains('has-frame') ? this.surface.querySelector('img') : null;
    return this.bounds(media, viewport);
  }

  refresh() {
    const viewport = this.surface.getBoundingClientRect();
    if (this.focused && (viewport.width === 0 || viewport.height === 0)) return;
    const picture = this.picture(viewport);
    const available = this.focused && !!picture;
    if (!available) { this.cancelSelection(); this.crop = null; }
    else if (this.crop) this.crop = CameraZoom.pan(this.crop, 0, 0, picture, viewport.width, viewport.height);
    for (const media of this.surface.querySelectorAll('img, video, .grid-camera-canvas')) {
      this.position(media, viewport);
    }
    this.surface.classList.toggle('zoom-enabled', available && !this.crop);
    this.surface.classList.toggle('zoom-pan-enabled', available && !!this.crop);
    this.help.hidden = !available;
    this.resetButton.hidden = !this.crop;
    this.changed();
  }

  wheel(event) {
    if (!this.focused || this.drag || this.pinch || event.ctrlKey || event.metaKey || !Number.isFinite(event.deltaY) || !event.deltaY ||
        event.target.closest('button, input, select, textarea, a, [contenteditable], .live-sound-overlay, .camera-zoom-help, .detection-details')) return;
    const viewport = this.surface.getBoundingClientRect(), bounds = this.picture(viewport);
    if (!bounds) return;
    const anchor = { x: event.clientX - viewport.left, y: event.clientY - viewport.top };
    if (anchor.x < 0 || anchor.x > viewport.width || anchor.y < 0 || anchor.y > viewport.height) return;
    if (!this.crop && (anchor.x < bounds.left || anchor.x > bounds.left + bounds.width ||
        anchor.y < bounds.top || anchor.y > bounds.top + bounds.height)) return;
    const pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? viewport.height : 1);
    const factor = Math.exp(-Math.max(-240, Math.min(240, pixels)) * 0.002);
    event.preventDefault();
    this.crop = CameraZoom.zoom(this.crop, anchor, factor, bounds, viewport.width, viewport.height);
    this.refresh();
  }

  begin(event) {
    if (!this.focused || event.button !== 0 || this.touches.has(event.pointerId) ||
        event.target.closest('button, .live-sound-overlay, .camera-zoom-help, .detection-details')) return;
    const viewport = this.surface.getBoundingClientRect(), bounds = this.picture(viewport);
    if (!bounds) return;
    const start = { x: event.clientX - viewport.left, y: event.clientY - viewport.top };
    if (event.pointerType === 'touch' && this.touches.size === 1) {
      if (start.x < 0 || start.x > viewport.width || start.y < 0 || start.y > viewport.height) return;
      event.preventDefault();
      this.touches.set(event.pointerId, start);
      try { this.surface.setPointerCapture(event.pointerId); }
      catch { this.cancelSelection(); return; }
      // Preserve both captures while replacing the unfinished one-finger gesture.
      this.drag = null;
      this.selection.hidden = true;
      this.surface.classList.toggle('zoom-panning', false);
      const [first, second] = this.touches.values();
      this.pinch = { viewport, bounds, crop: this.crop ? { ...this.crop } : null,
        anchor: { x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 },
        distance: Math.max(1, Math.hypot(first.x - second.x, first.y - second.y)) };
      return;
    }
    if (this.drag || this.pinch || event.isPrimary === false) return;
    if (!this.crop && (start.x < bounds.left || start.x > bounds.left + bounds.width ||
        start.y < bounds.top || start.y > bounds.top + bounds.height)) return;
    event.preventDefault();
    if (event.pointerType === 'touch') this.touches.set(event.pointerId, start);
    this.drag = { pointerId: event.pointerId, viewport, bounds, start, crop: this.crop ? { ...this.crop } : null };
    this.surface.classList.toggle('zoom-panning', !!this.crop);
    try { this.surface.setPointerCapture(event.pointerId); }
    catch { this.cancelSelection(); }
  }

  move(event) {
    if (this.touches.has(event.pointerId)) {
      const viewport = this.pinch?.viewport || this.drag.viewport;
      this.touches.set(event.pointerId, { x: event.clientX - viewport.left, y: event.clientY - viewport.top });
    }
    if (this.pinch && this.touches.has(event.pointerId)) {
      event.preventDefault();
      const pinch = this.pinch, [first, second] = this.touches.values();
      const midpoint = { x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 };
      const factor = Math.hypot(first.x - second.x, first.y - second.y) / pinch.distance;
      this.crop = CameraZoom.zoom(pinch.crop, pinch.anchor, factor, pinch.bounds,
        pinch.viewport.width, pinch.viewport.height, midpoint);
      this.refresh();
      return;
    }
    const drag = this.drag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const end = { x: event.clientX - drag.viewport.left, y: event.clientY - drag.viewport.top };
    if (drag.crop) {
      const dx = end.x - drag.start.x, dy = end.y - drag.start.y;
      if (!drag.moved && Math.hypot(dx, dy) < 4) return;
      drag.moved = true;
      this.crop = CameraZoom.pan(drag.crop, dx, dy, drag.bounds, drag.viewport.width, drag.viewport.height);
      this.refresh();
      return;
    }
    const area = CameraZoom.selection(drag.start, end, drag.bounds, drag.viewport.width / drag.viewport.height);
    drag.area = area;
    this.selection.hidden = area.width < 12 || area.height < 12;
    for (const key of ['left', 'top', 'width', 'height']) this.selection.style[key] = `${area[key]}px`;
  }

  finish(event) {
    if (this.pinch && this.touches.has(event.pointerId)) {
      this.move(event);
      this.cancelSelection();
      return;
    }
    if (!this.drag || this.drag.pointerId !== event.pointerId) return;
    this.move(event);
    const { area, bounds, crop } = this.drag;
    this.cancelSelection();
    if (crop) return;
    // Ignore click jitter, including both clicks of the focus/reset gesture.
    if (area.width < 12 || area.height < 12) return;
    this.crop = CameraZoom.crop(area, bounds);
    this.refresh();
  }

  cancelSelection() {
    const drag = this.drag;
    const pointers = new Set(this.touches.keys());
    if (drag) pointers.add(drag.pointerId);
    this.drag = null;
    this.pinch = null;
    this.touches.clear();
    this.selection.hidden = true;
    this.surface.classList.toggle('zoom-panning', false);
    for (const pointerId of pointers) {
      if (this.surface.hasPointerCapture(pointerId)) this.surface.releasePointerCapture(pointerId);
    }
  }

  reset() {
    const changed = !!this.crop || !!this.drag || !!this.pinch;
    this.cancelSelection();
    this.crop = null;
    this.refresh();
    return changed;
  }

  dispose() {
    this.reset();
    this.events.abort();
    this.observer.disconnect();
  }
}
