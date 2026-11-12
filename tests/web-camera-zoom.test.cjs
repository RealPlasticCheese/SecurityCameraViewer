const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-zoom.js'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));
const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 0.000001, `${actual} != ${expected}`);

function viewer({ width = 1000, height = 600, mediaWidth = 1600, mediaHeight = 900, aspectRatios = {} } = {}) {
  const handlers = new Map(), classes = new Set(['has-frame']), captures = new Set();
  const document = { hidden: false, defaultView: { addEventListener(name, fn) { handlers.set(name, fn); } },
    addEventListener(name, fn) { handlers.set(name, fn); }, createElement: element };
  function element() {
    return { ownerDocument: document, style: {}, children: [], hidden: false,
      classList: { contains: value => classes.has(value), toggle(value, on) { if (on) classes.add(value); else classes.delete(value); } },
      append(...children) { this.children.push(...children); }, setAttribute() {},
      addEventListener(name, fn) { this[name] = fn; } };
  }
  const img = { tagName: 'IMG', naturalWidth: mediaWidth, naturalHeight: mediaHeight, style: {} };
  const video = { videoWidth: mediaWidth, videoHeight: mediaHeight, style: {}, classList: { contains: name => name === 'live-camera-video' } };
  let videoAdded = false, observed, disconnected = false;
  const viewport = { left: 10, top: 20, width, height };
  const surface = { ...element(), getBoundingClientRect: () => ({ ...viewport }),
    querySelector: name => name === 'img' ? img : videoAdded ? video : null,
    querySelectorAll: () => videoAdded ? [img, video] : [img],
    setPointerCapture(id) { captures.add(id); }, hasPointerCapture: id => captures.has(id),
    releasePointerCapture(id) { captures.delete(id); surface.lostpointercapture({ pointerId: id }); } };
  const root = { ...element(), querySelector: () => surface };
  const sandbox = { AbortController, ResizeObserver: class {
    constructor(fn) { observed = fn; } observe() {} disconnect() { disconnected = true; }
  } };
  const { CameraZoom, CameraZoomView } = runInNewContext(`${source}\n({ CameraZoom, CameraZoomView })`, sandbox);
  const view = new CameraZoomView(root, undefined, aspectRatios);
  const pointer = (x, y, extra = {}) => ({ pointerId: 1, button: 0, isPrimary: true,
    clientX: x + viewport.left, clientY: y + viewport.top, target: { closest: () => null }, preventDefault() {}, ...extra });
  const touch = (id, x, y, extra = {}) => pointer(x, y, { pointerId: id, pointerType: 'touch', isPrimary: id === 1, ...extra });
  const drag = (start = [250, 200], end = [500, 350]) => {
    surface.pointerdown(pointer(...start)); surface.pointermove(pointer(...end)); surface.pointerup(pointer(...end));
  };
  const wheel = (deltaY, x = width / 2, y = height / 2, extra = {}) => {
    let prevented = false;
    surface.wheel(pointer(x, y, { deltaY, deltaMode: 0, preventDefault() { prevented = true; }, ...extra }));
    return prevented;
  };
  return { view, CameraZoom, root, surface, img, video, viewport, classes, captures, handlers, pointer, touch, drag, wheel,
    resize(w, h) { viewport.width = w; viewport.height = h; observed(); },
    live() { videoAdded = true; classes.add('live-media-ready'); surface.playing(); },
    jpeg() { classes.delete('live-media-ready'); view.refresh(); }, disconnected: () => disconnected };
}

test('aspect ratio overrides stretch the full picture into a centered, contained rectangle', () => {
  for (const [width, height] of [[1000, 600], [400, 900], [1600, 400]]) {
    for (const ratio of ['4:3', '16:9', '1:1', '9:16', '32:9', '1.85:1']) {
      const v = viewer({ width, height, mediaWidth: 640, mediaHeight: 480, aspectRatios: { preview: ratio } });
      v.view.refresh(); const bounds = v.view.picture(v.viewport);
      const [rw, rh] = ratio.split(':').map(Number); close(bounds.width / bounds.height, rw / rh);
      close(parseFloat(v.img.style.width), bounds.width); close(parseFloat(v.img.style.height), bounds.height);
      close(parseFloat(v.img.style.left), (width - bounds.width) / 2);
      close(parseFloat(v.img.style.top), (height - bounds.height) / 2);
      assert.ok(bounds.width <= width && bounds.height <= height);
      assert.equal(v.img.style.objectFit, 'fill'); v.view.dispose();
    }
  }
});

test('Auto restores intrinsic sizing and invalid preferences safely preserve it', () => {
  const v = viewer({ mediaWidth: 640, mediaHeight: 480, aspectRatios: { preview: '16:9' } });
  v.view.refresh(); assert.equal(v.img.style.objectFit, 'fill');
  for (const ratio of ['auto', undefined, null, 'invalid']) {
    v.view.aspectRatios.preview = ratio; v.view.refresh();
    close(v.view.picture(v.viewport).width / v.view.picture(v.viewport).height, 4 / 3);
    for (const property of ['position', 'inset', 'objectFit', 'left', 'top', 'width', 'height', 'transform']) assert.equal(v.img.style[property], '');
  }
  v.img.naturalWidth = 0; v.view.refresh(); assert.equal(v.view.picture(v.viewport), null); v.view.dispose();
});

test('custom ratios accept positive decimal dimensions and reject malformed, zero or nonfinite inputs', () => {
  const { CameraZoom } = viewer();
  for (const ratio of ['4:3', '16:9', '1:1', '9:16', '32:9', '1.85:1', '.5:1', '1000000:1']) {
    const [width, height] = ratio.split(':').map(Number); close(CameraZoom.ratio(ratio), width / height);
  }
  for (const ratio of [null, '', 'auto', 'custom', '0:1', '1:0', '-1:1', '+1:1', '1:2:3', '16/9', '1e3:1', 'NaN:1', 'Infinity:1', ' 1:1', '1,5:1', '9'.repeat(310) + ':1']) {
    assert.equal(CameraZoom.ratio(ratio), null, String(ratio));
  }
});

test('JPEG, decoded grid, main-stream sound and enhancements select the actual stream preference', () => {
  const v = viewer({ aspectRatios: { main: '16:9', preview: '4:3', jpeg: 'auto' } });
  const grid = { width: 640, height: 480 };
  const enhanced = { width: 640, height: 480, classList: { contains: name => name === 'camera-enhanced-picture' } };
  const aspect = media => { const b = v.view.bounds(media, v.viewport); return b.width / b.height; };
  close(aspect(v.img), 16 / 9);
  close(aspect(grid), 4 / 3); close(aspect(enhanced), 4 / 3);
  v.classes.add('grid-media-ready'); close(aspect(enhanced), 4 / 3);
  v.live(); close(aspect(v.video), 16 / 9); close(aspect(grid), 4 / 3); close(aspect(enhanced), 16 / 9);
  v.jpeg(); close(aspect(enhanced), 4 / 3); v.view.dispose();
});

test('corrected pictures keep crop coordinates aligned through zoom, pan and resizing', () => {
  const v = viewer({ mediaWidth: 640, mediaHeight: 480, aspectRatios: { preview: '16:9' } });
  v.view.setFocused(true); v.drag(); assert.ok(v.view.crop);
  const verify = () => {
    const bounds = v.view.picture(v.viewport), area = v.view.crop;
    const [tx, ty, scale] = v.img.style.transform.match(/-?[\d.]+/g).map(Number);
    close(bounds.left + tx + area.left * bounds.width * scale,
      (v.viewport.width - area.width * bounds.width * scale) / 2);
    close(bounds.top + ty + area.top * bounds.height * scale,
      (v.viewport.height - area.height * bounds.height * scale) / 2);
  };
  verify(); v.resize(450, 900); verify(); v.wheel(-120, 225, 450); verify();
  v.view.reset(); assert.equal(v.img.style.transform, ''); close(parseFloat(v.img.style.width) / parseFloat(v.img.style.height), 16 / 9);
  v.view.dispose();
});

test('rewind loading and gaps cannot select retained live pictures for zoom', () => {
  const v = viewer(); v.view.setFocused(true); v.classes.add('rewind-active');
  assert.equal(v.view.picture(v.viewport), null);
  v.live(); assert.ok(v.view.picture(v.viewport));
  v.jpeg(); assert.equal(v.view.picture(v.viewport), null);
  v.classes.delete('rewind-active'); assert.ok(v.view.picture(v.viewport)); v.view.dispose();
});

test('selection follows the viewport aspect in every direction and stays inside letterboxed pictures', () => {
  const { CameraZoom } = viewer();
  for (const [width, height, mw, mh] of [[1000, 600, 1600, 900], [600, 1000, 1600, 900], [1200, 600, 640, 480]]) {
    const bounds = CameraZoom.picture(width, height, mw, mh);
    const start = { x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 };
    for (const [dx, dy] of [[100, 200], [-100, 200], [100, -200], [-100, -200], [10000, 10000], [-10000, -10000]]) {
      const area = CameraZoom.selection(start, { x: start.x + dx, y: start.y + dy }, bounds, width / height);
      close(area.width / area.height, width / height);
      assert.ok(area.left >= bounds.left - 0.000001 && area.top >= bounds.top - 0.000001);
      assert.ok(area.left + area.width <= bounds.left + bounds.width + 0.000001);
      assert.ok(area.top + area.height <= bounds.top + bounds.height + 0.000001);
    }
  }
});

test('dragging only a focused picture draws a constrained box and zooms on release', () => {
  const v = viewer(); v.drag(); assert.equal(v.view.crop, null);
  v.view.setFocused(true);
  v.surface.pointerdown(v.pointer(250, 200));
  v.surface.pointermove(v.pointer(500, 350));
  assert.equal(v.view.crop, null); assert.equal(v.view.selection.hidden, false);
  close(parseFloat(v.view.selection.style.width) / parseFloat(v.view.selection.style.height), 1000 / 600);
  assert.ok(v.captures.has(1));
  v.surface.pointerup(v.pointer(500, 350));
  assert.equal(v.captures.size, 0); assert.equal(v.view.selection.hidden, true);
  assert.ok(v.view.crop); assert.match(v.img.style.transform, /scale\(4\)/);
  assert.equal(v.view.resetButton.hidden, false);
  assert.equal(v.view.reset(), true); assert.equal(v.img.style.transform, '');
  assert.equal(v.view.focused, true); assert.equal(v.view.reset(), false);
});

test('click jitter, letterbox margins, controls, secondary buttons and non-primary touches cannot zoom', () => {
  const v = viewer(); v.view.setFocused(true);
  for (const extra of [{ button: 2 }, { isPrimary: false }, { target: { closest: () => ({}) } }]) {
    v.surface.pointerdown(v.pointer(250, 200, extra)); assert.equal(v.view.drag, undefined);
  }
  v.drag([250, 1], [500, 100]); assert.equal(v.view.crop, null);
  for (let i = 0; i < 2; i++) v.drag([250, 200], [252, 201]);
  assert.equal(v.view.crop, null); assert.equal(v.img.style.transform, '');
});

test('pointer cancellation, lost capture, blur, hidden tab and resizing discard incomplete selections', () => {
  for (const cancel of [v => v.surface.pointercancel(v.pointer(500, 350)),
    v => v.surface.lostpointercapture(v.pointer(500, 350)), v => v.handlers.get('blur')(),
    v => { v.root.ownerDocument.hidden = true; v.handlers.get('visibilitychange')(); }, v => v.resize(800, 600)]) {
    const v = viewer(); v.view.setFocused(true);
    v.surface.pointerdown(v.pointer(250, 200)); v.surface.pointermove(v.pointer(500, 350));
    v.surface.pointercancel(v.pointer(500, 350, { pointerId: 2 })); assert.ok(v.view.drag);
    cancel(v); assert.equal(v.view.crop, null); assert.equal(v.view.drag, null);
    assert.equal(v.view.selection.hidden, true); assert.equal(v.captures.size, 0);
  }
});

test('zoom centers the exact selected camera area without stretching through viewport and media changes', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  const crop = plain(v.view.crop);
  for (const [width, height] of [[1000, 600], [600, 1000], [1600, 600]]) {
    v.resize(width, height); assert.deepEqual(plain(v.view.crop), crop);
    // A single uniform scale preserves camera pixels; the crop center lands at the viewport center.
    const bounds = v.CameraZoom.picture(width, height, 1600, 900);
    const numbers = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
    const [x, y, scale] = numbers;
    close(x + (bounds.left + (crop.left + crop.width / 2) * bounds.width) * scale, width / 2);
    close(y + (bounds.top + (crop.top + crop.height / 2) * bounds.height) * scale, height / 2);
  }
  v.live(); assert.equal(v.video.style.transform, v.img.style.transform);
  v.jpeg(); assert.deepEqual(plain(v.view.crop), crop);
  v.view.resetButton.click(); assert.equal(v.img.style.transform, ''); assert.equal(v.video.style.transform, '');
});

test('dragging a zoomed picture pans immediately at the same scale and ignores click jitter', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  const original = plain(v.view.crop), transform = v.img.style.transform;
  assert.ok(v.classes.has('zoom-pan-enabled'));
  v.drag([500, 300], [502, 301]); assert.deepEqual(plain(v.view.crop), original);
  v.surface.pointerdown(v.pointer(500, 300));
  assert.ok(v.classes.has('zoom-panning')); assert.ok(v.captures.has(1));
  v.surface.pointermove(v.pointer(620, 360, { pointerId: 2 })); assert.equal(v.img.style.transform, transform);
  v.surface.pointermove(v.pointer(620, 360));
  assert.equal(v.view.selection.hidden, true);
  close(v.view.crop.width, original.width); close(v.view.crop.height, original.height);
  close(v.view.crop.left, original.left - 120 / (1000 * 4));
  close(v.view.crop.top, original.top - 60 / (562.5 * 4));
  const values = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  const before = transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  close(values[0] - before[0], 120); close(values[1] - before[1], 60); close(values[2], before[2]);
  v.surface.pointerup(v.pointer(620, 360));
  assert.equal(v.captures.size, 0); assert.equal(v.classes.has('zoom-panning'), false);
  assert.ok(v.classes.has('zoom-pan-enabled')); assert.equal(v.view.drag, null);
});

test('panning reaches every picture edge without exposing blank space, including after aspect changes', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  const initial = plain(v.view.crop);
  for (const [width, height] of [[1000, 600], [600, 1000], [1600, 600]]) {
    v.resize(width, height);
    const bounds = v.CameraZoom.picture(width, height, 1600, 900);
    for (const [dx, dy] of [[1e6, 1e6], [-1e6, -1e6], [1e6, -1e6], [-1e6, 1e6]]) {
      v.view.crop = v.CameraZoom.pan(initial, dx, dy, bounds, width, height); v.view.refresh();
      const [x, y, scale] = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
      const left = x + bounds.left * scale, top = y + bounds.top * scale;
      const right = left + bounds.width * scale, bottom = top + bounds.height * scale;
      assert.ok(left <= 0.000001 && top <= 0.000001 && right >= width - 0.000001 && bottom >= height - 0.000001);
      close(dx > 0 ? left : right, dx > 0 ? 0 : width);
      close(dy > 0 ? top : bottom, dy > 0 ? 0 : height);
      close(v.view.crop.width, initial.width); close(v.view.crop.height, initial.height);
    }
  }
  // Pan to the edge of a narrow viewport, then widen it: the new viewport must stay filled.
  v.resize(400, 1000); v.drag([200, 500], [1e6, 1e6]); v.resize(1600, 600);
  const bounds = v.CameraZoom.picture(1600, 600, 1600, 900);
  const [x, y, scale] = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  assert.ok(x + bounds.left * scale <= 0.000001 && y + bounds.top * scale <= 0.000001);
  assert.ok(x + (bounds.left + bounds.width) * scale >= 1600 - 0.000001);
  assert.ok(y + (bounds.top + bounds.height) * scale >= 600 - 0.000001);
});

test('pan capture is released on cancellation, blur, hidden tab and resize while retaining the current view', () => {
  for (const cancel of [v => v.surface.pointercancel(v.pointer(620, 360)),
    v => v.surface.lostpointercapture(v.pointer(620, 360)), v => v.handlers.get('blur')(),
    v => { v.root.ownerDocument.hidden = true; v.handlers.get('visibilitychange')(); }, v => v.resize(800, 600)]) {
    const v = viewer(); v.view.setFocused(true); v.drag();
    v.surface.pointerdown(v.pointer(500, 300)); v.surface.pointermove(v.pointer(620, 360));
    const crop = plain(v.view.crop); cancel(v);
    assert.deepEqual(plain(v.view.crop), crop); assert.equal(v.view.drag, null);
    assert.equal(v.captures.size, 0); assert.equal(v.classes.has('zoom-panning'), false);
    assert.equal(v.view.selection.hidden, true);
  }
});

test('panning ignores controls and secondary pointers, follows live video, and resets cleanly', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  const initial = plain(v.view.crop);
  for (const extra of [{ button: 2 }, { isPrimary: false }, { target: { closest: () => ({}) } }]) {
    v.surface.pointerdown(v.pointer(500, 300, extra)); assert.equal(v.view.drag, null);
  }
  assert.deepEqual(plain(v.view.crop), initial);
  v.live(); v.drag([500, 300], [400, 250]);
  assert.equal(v.video.style.transform, v.img.style.transform);
  v.jpeg(); assert.equal(v.video.style.transform, v.img.style.transform);
  // The zoomed picture covers the old letterbox margins, so dragging there also pans.
  const before = plain(v.view.crop); v.drag([250, 1], [200, 1]);
  assert.ok(v.view.crop.left > before.left);
  v.view.resetButton.click();
  assert.equal(v.view.crop, null); assert.equal(v.img.style.transform, ''); assert.equal(v.video.style.transform, '');
  assert.equal(v.classes.has('zoom-pan-enabled'), false); assert.equal(v.classes.has('zoom-panning'), false);
});

test('wheel zoom keeps the pointed camera pixel stationary and preserves aspect ratio', () => {
  const { CameraZoom } = viewer();
  for (const [width, height, mw, mh] of [[1000, 600, 1600, 900], [400, 1000, 1600, 900], [1600, 600, 640, 480]]) {
    const bounds = CameraZoom.picture(width, height, mw, mh);
    const anchor = { x: bounds.left + bounds.width * 0.6, y: bounds.top + bounds.height * 0.4 };
    let area = null;
    for (const [factor, expectedScale] of [[6, 6], [1.5, 9], [2 / 3, 6]]) {
      area = CameraZoom.zoom(area, anchor, factor, bounds, width, height);
      const [x, y, scale] = CameraZoom.transform(area, bounds, width, height).match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
      close(scale, expectedScale);
      close(x + (bounds.left + bounds.width * 0.6) * scale, anchor.x);
      close(y + (bounds.top + bounds.height * 0.4) * scale, anchor.y);
    }
  }
});

test('wheel zoom works from the full picture, supports panning, and scrolls back to the unzoomed focused view', () => {
  const v = viewer(); v.view.setFocused(true);
  assert.equal(v.wheel(-100), true); assert.ok(v.view.crop);
  assert.ok(v.classes.has('zoom-pan-enabled'));
  const before = plain(v.view.crop);
  v.drag([500, 300], [520, 310]); assert.ok(v.view.crop.left < before.left);
  assert.equal(v.wheel(100), true);
  assert.equal(v.view.crop, null); assert.equal(v.img.style.transform, ''); assert.equal(v.view.focused, true);
  assert.ok(v.classes.has('zoom-enabled')); assert.equal(v.classes.has('zoom-pan-enabled'), false);
  assert.equal(v.view.resetButton.hidden, true);
  assert.equal(v.wheel(240), true); assert.equal(v.view.crop, null);
});

test('small wheel zooms keep letterboxed axes centered and allow panning only on enlarged axes', () => {
  for (const [width, height, mw, mh] of [[400, 1000, 1600, 900], [1600, 600, 640, 480]]) {
    const v = viewer({ width, height, mediaWidth: mw, mediaHeight: mh }); v.view.setFocused(true);
    v.wheel(-100);
    const bounds = v.CameraZoom.picture(width, height, mw, mh);
    v.drag([width / 2, height / 2], [width / 2 + 50, height / 2 + 50]);
    const [x, y, scale] = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
    close(scale, Math.exp(0.2));
    if (bounds.width * scale < width) close(x + (bounds.left + bounds.width / 2) * scale, width / 2);
    if (bounds.height * scale < height) close(y + (bounds.top + bounds.height / 2) * scale, height / 2);
    v.wheel(100); assert.equal(v.view.crop, null);
  }
});

test('wheel pixel, line and page deltas normalize consistently and zoom limits prevent runaway magnification', () => {
  const pixel = viewer(), line = viewer(), page = viewer(), capped = viewer();
  for (const v of [pixel, line, page, capped]) v.view.setFocused(true);
  pixel.wheel(-16); line.wheel(-1, 500, 300, { deltaMode: 1 });
  close(pixel.view.crop.width, line.view.crop.width); close(pixel.view.crop.height, line.view.crop.height);
  page.wheel(-1, 500, 300, { deltaMode: 2 }); capped.wheel(-1e8);
  close(page.view.crop.width, capped.view.crop.width);
  for (let i = 0; i < 100; i++) capped.wheel(-240);
  const scale = Number(capped.img.style.transform.match(/scale\(([^)]+)\)/)[1]); close(scale, 32);
  capped.wheel(240); assert.ok(Number(capped.img.style.transform.match(/scale\(([^)]+)\)/)[1]) < 32);
  const bounds = capped.CameraZoom.picture(1000, 600, 1600, 900);
  const strong = { left: 0.49, top: 0.49, width: 0.01, height: 0.01 };
  assert.equal(capped.CameraZoom.zoom(strong, { x: 500, y: 300 }, 2, bounds, 1000, 600), strong);
  const smaller = capped.CameraZoom.zoom(strong, { x: 500, y: 300 }, 0.5, bounds, 1000, 600);
  close(smaller.width, strong.width * 2);
});

test('wheel ignores grid, missing pictures, controls, browser zoom gestures, horizontal scroll and active drags', () => {
  const v = viewer(); assert.equal(v.wheel(-100), false); assert.equal(v.view.crop, null);
  v.view.setFocused(true);
  for (const extra of [{ ctrlKey: true }, { metaKey: true }, { target: { closest: () => ({}) } }]) {
    assert.equal(v.wheel(-100, 500, 300, extra), false); assert.equal(v.view.crop, null);
  }
  for (const delta of [0, NaN, Infinity, -Infinity]) assert.equal(v.wheel(delta), false);
  assert.equal(v.wheel(-100, 500, 1), false); assert.equal(v.wheel(-100, -1, 300), false);
  v.classes.delete('has-frame'); assert.equal(v.wheel(-100), false); v.classes.add('has-frame');
  v.surface.pointerdown(v.pointer(250, 200)); v.surface.pointermove(v.pointer(500, 350));
  assert.equal(v.wheel(-100), false); assert.equal(v.view.crop, null); assert.ok(v.view.drag);
  v.surface.pointerup(v.pointer(500, 350));
  const crop = plain(v.view.crop); v.surface.pointerdown(v.pointer(500, 300));
  assert.equal(v.wheel(-100), false); assert.deepEqual(plain(v.view.crop), crop);
  v.surface.pointercancel(v.pointer(500, 300));
});

test('wheel adjusts rectangle zoom, keeps JPEG and live video aligned, and resets when leaving the camera', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  const original = plain(v.view.crop); v.wheel(-100, 450, 300);
  assert.ok(v.view.crop.width < original.width); v.live();
  assert.equal(v.video.style.transform, v.img.style.transform);
  v.wheel(100, 450, 300); close(v.view.crop.width, original.width); close(v.view.crop.height, original.height);
  assert.equal(v.video.style.transform, v.img.style.transform);
  v.view.setFocused(false); assert.equal(v.view.crop, null); assert.equal(v.view.help.hidden, true);
  assert.equal(v.img.style.transform, ''); assert.equal(v.video.style.transform, '');
});

test('leaving a camera, removing its picture and disposing clear zoom and release observers', () => {
  const v = viewer(); v.view.setFocused(true); v.drag();
  v.surface.pointerdown(v.pointer(500, 300)); v.surface.pointermove(v.pointer(620, 360));
  v.view.setFocused(false); assert.equal(v.view.crop, null); assert.equal(v.view.help.hidden, true);
  assert.equal(v.captures.size, 0); assert.equal(v.classes.has('zoom-panning'), false);
  v.view.setFocused(true); v.drag(); v.classes.delete('has-frame'); v.view.refresh();
  assert.equal(v.view.crop, null); assert.equal(v.view.help.hidden, true);
  assert.equal(v.img.style.transform, '');
  v.classes.add('has-frame'); v.view.refresh(); v.drag(); v.surface.pointerdown(v.pointer(500, 300)); v.view.dispose();
  assert.equal(v.view.crop, null); assert.equal(v.view.events.signal.aborted, true); assert.equal(v.disconnected(), true);
  assert.equal(v.captures.size, 0); assert.equal(v.classes.has('zoom-panning'), false);
});

test('one touch keeps rectangle selection and drag panning with all captures released on lift', () => {
  const v = viewer(); v.view.setFocused(true);
  v.surface.pointerdown(v.touch(1, 250, 200)); v.surface.pointermove(v.touch(1, 500, 350));
  assert.equal(v.view.selection.hidden, false); assert.equal(v.view.pinch, null);
  v.surface.pointerup(v.touch(1, 500, 350)); assert.ok(v.view.crop); assert.equal(v.view.touches.size, 0);
  const before = plain(v.view.crop);
  v.surface.pointerdown(v.touch(1, 500, 300)); v.surface.pointermove(v.touch(1, 620, 360));
  assert.ok(v.view.crop.left < before.left); close(v.view.crop.width, before.width);
  v.surface.pointerup(v.touch(1, 620, 360)); assert.equal(v.captures.size, 0); assert.equal(v.view.touches.size, 0);
});

test('a second touch cancels the unfinished rectangle and pinches around the fingers midpoint', () => {
  const v = viewer(); v.view.setFocused(true);
  v.surface.pointerdown(v.touch(1, 400, 300)); v.surface.pointermove(v.touch(1, 420, 300));
  assert.equal(v.view.selection.hidden, false);
  v.surface.pointerdown(v.touch(2, 620, 300));
  assert.equal(v.view.selection.hidden, true); assert.equal(v.view.drag, null); assert.ok(v.view.pinch);
  assert.equal(v.view.crop, null); assert.deepEqual([...v.captures], [1, 2]);
  const anchor = { x: 520, y: 300 };
  v.surface.pointermove(v.touch(1, 320, 300)); v.surface.pointermove(v.touch(2, 720, 300));
  const [x, y, scale] = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  close(scale, 2); close(x + anchor.x * scale, anchor.x); close(y + anchor.y * scale, anchor.y);
  assert.equal(v.view.selection.hidden, true);
  v.surface.pointermove(v.touch(1, 420, 300)); v.surface.pointermove(v.touch(2, 620, 300));
  assert.equal(v.view.crop, null); assert.equal(v.img.style.transform, '');
  v.surface.pointerup(v.touch(2, 620, 300));
  assert.equal(v.view.pinch, null); assert.equal(v.view.touches.size, 0); assert.equal(v.captures.size, 0);
});

test('moving a pinching midpoint pans the same camera detail and both media transforms remain aligned', () => {
  const v = viewer(); v.view.setFocused(true); v.drag(); v.live();
  const before = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  v.surface.pointerdown(v.touch(1, 400, 300)); v.surface.pointerdown(v.touch(2, 600, 300));
  v.surface.pointermove(v.touch(1, 430, 320)); v.surface.pointermove(v.touch(2, 630, 320));
  const values = v.img.style.transform.match(/-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/g).map(Number);
  close(values[2], before[2]); close(values[0] - before[0], 30); close(values[1] - before[1], 20);
  assert.equal(v.video.style.transform, v.img.style.transform);
  v.surface.pointermove(v.touch(1, 330, 320)); v.surface.pointermove(v.touch(2, 730, 320));
  assert.ok(Number(v.img.style.transform.match(/scale\(([^)]+)\)/)[1]) > before[2]);
  assert.equal(v.video.style.transform, v.img.style.transform);
  v.surface.pointerup(v.touch(1, 330, 320)); const crop = plain(v.view.crop);
  v.surface.pointermove(v.touch(2, 800, 400)); v.surface.pointerup(v.touch(2, 800, 400));
  assert.deepEqual(plain(v.view.crop), crop); assert.equal(v.view.selection.hidden, true);
});

test('pinch cancellation, lost capture, blur, hidden tabs and resizing release both pointers', () => {
  for (const cancel of [v => v.surface.pointercancel(v.touch(2, 650, 300)),
    v => v.surface.lostpointercapture(v.touch(1, 350, 300)), v => v.handlers.get('blur')(),
    v => { v.root.ownerDocument.hidden = true; v.handlers.get('visibilitychange')(); }, v => v.resize(800, 600)]) {
    const v = viewer(); v.view.setFocused(true);
    v.surface.pointerdown(v.touch(1, 400, 300)); v.surface.pointerdown(v.touch(2, 600, 300));
    v.surface.pointermove(v.touch(1, 350, 300)); v.surface.pointermove(v.touch(2, 650, 300));
    const crop = plain(v.view.crop); cancel(v);
    assert.deepEqual(plain(v.view.crop), crop); assert.equal(v.view.pinch, null); assert.equal(v.view.drag, null);
    assert.equal(v.view.touches.size, 0); assert.equal(v.captures.size, 0); assert.equal(v.view.selection.hidden, true);
  }
});

test('pinch ignores controls and extra fingers, blocks wheel input, and handles coincident touches safely', () => {
  const v = viewer();
  v.surface.pointerdown(v.touch(1, 400, 300)); assert.equal(v.view.touches.size, 0);
  v.view.setFocused(true); v.surface.pointerdown(v.touch(1, 400, 300));
  v.surface.pointerdown(v.touch(2, 600, 300, { target: { closest: () => ({}) } }));
  assert.equal(v.view.touches.size, 1); assert.equal(v.view.pinch, null);
  v.surface.pointerdown(v.touch(2, 600, 300));
  v.surface.pointerdown(v.touch(3, 700, 300)); assert.equal(v.view.touches.size, 2); assert.equal(v.captures.has(3), false);
  const pinch = v.view.pinch; assert.equal(v.wheel(-100), false); assert.equal(v.view.pinch, pinch);
  v.surface.pointermove(v.touch(3, 800, 300)); assert.equal(v.view.crop, null);
  v.view.reset(); assert.equal(v.captures.size, 0);
  v.surface.pointerdown(v.touch(1, 500, 300)); v.surface.pointerdown(v.touch(2, 500, 300));
  v.surface.pointermove(v.touch(2, 600, 300));
  assert.ok(Number.isFinite(v.view.crop.width)); assert.ok(Number.isFinite(v.view.crop.left));
  const scale = Number(v.img.style.transform.match(/scale\(([^)]+)\)/)[1]); assert.ok(scale <= 32.000001);
});

test('reset, camera changes, picture expiry, disposal and capture failures leave no pinch state', () => {
  for (const stop of [v => { assert.equal(v.view.reset(), true); }, v => v.view.setFocused(false),
    v => { v.classes.delete('has-frame'); v.view.refresh(); }, v => v.view.dispose()]) {
    const v = viewer(); v.view.setFocused(true);
    v.surface.pointerdown(v.touch(1, 400, 300)); v.surface.pointerdown(v.touch(2, 600, 300));
    stop(v); assert.equal(v.view.pinch, null); assert.equal(v.view.crop, null);
    assert.equal(v.view.touches.size, 0); assert.equal(v.captures.size, 0);
  }
  const v = viewer(); v.view.setFocused(true); v.surface.pointerdown(v.touch(1, 400, 300));
  v.surface.setPointerCapture = () => { throw new Error('Pointer no longer active'); };
  v.surface.pointerdown(v.touch(2, 600, 300));
  assert.equal(v.view.pinch, null); assert.equal(v.view.touches.size, 0); assert.equal(v.captures.size, 0);
});


test('focused zoom survives Android hiding the viewport for persistent-video PiP', () => {
  const v = viewer(); v.view.setFocused(true); v.drag(); const crop = v.view.crop;
  assert.ok(crop); v.resize(0, 0); assert.equal(v.view.crop, crop);
  v.resize(1000, 600); assert.ok(v.view.crop);
  assert.equal(v.view.resetButton.hidden, false);
  v.resize(0, 0); v.view.setFocused(false); assert.equal(v.view.crop, null);
  v.view.dispose();
});
