const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/playback-controls.js'), 'utf8');

function harness() {
  let now = 0, nextTimer = 0, liveCalls = 0;
  const timers = new Map();
  const frames = new Map(), observers = {};
  const document = new EventTarget();
  document.activeElement = null;
  document.hidden = false;
  document.querySelector = () => document.modal ? {} : null;
  const window = new EventTarget();
  window.innerHeight = 600;
  class Element extends EventTarget {
    constructor() {
      super();
      this.children = [];
      this.attributes = {};
      this.rect = { top: 0, bottom: 600, width: 1000, height: 600 };
      this.style = { setProperty: (name, value) => this.attributes[name] = value };
      this.classes = new Set();
      this.classList = { toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name) };
    }
    setAttribute(name, value) { this.attributes[name] = value; }
    contains(element) { return this === element || this.children.some(child => child.contains(element)); }
    matches(selector) { assert.equal(selector, ':focus-visible'); return !!this.keyboardFocus; }
    getBoundingClientRect() { return this.rect; }
    querySelector(selector) {
      return selector === '.tile-header' ? this.children.find(child => child.classes.has('tile-header')) : this.children.find(child => !child.disabled);
    }
    querySelectorAll(selector) { assert.equal(selector, '.tile'); return this.children.filter(child => child.classes.has('tile')); }
    closest(selector) {
      if (selector === '.viewer-controls') return this.isViewerControl ? this : this.parent?.closest(selector);
      return this.isControl || this.classes.has('tile-header') ? this : this.parent?.closest(selector);
    }
    focus() { document.activeElement = this; }
    blur() { if (document.activeElement === this) document.activeElement = null; }
  }
  const elements = Object.fromEntries(['root', 'bar', 'dock', 'focusTarget', 'pin', 'compactStatus', 'compactLive', 'tabArea', 'tab'].map(name => [name, new Element()]));
  elements.bar.rect = { left: 0, right: 1000, top: 500, bottom: 600, width: 1000, height: 100 };
  elements.root.children.push(elements.bar, elements.dock, elements.tabArea);
  elements.tabArea.children.push(elements.tab);
  elements.dock.children.push(elements.compactStatus, elements.compactLive);
  const input = new Element();
  elements.bar.children.push(elements.pin, input);
  class Observer {
    constructor(callback, name) { this.callback = callback; this.elements = new Set(); observers[name] = this; }
    observe(element) { this.elements.add(element); }
    disconnect() { this.elements.clear(); }
  }
  const View = runInNewContext(`${source}\nPlaybackControlsView`, {
    document, window, AbortController,
    ResizeObserver: class extends Observer { constructor(callback) { super(callback, 'resize'); } },
    MutationObserver: class extends Observer { constructor(callback) { super(callback, 'mutation'); } },
    requestAnimationFrame(callback) { const id = ++nextTimer; frames.set(id, callback); return id; },
    cancelAnimationFrame(id) { frames.delete(id); },
    setTimeout(callback, milliseconds) { const id = ++nextTimer; timers.set(id, { at: now + milliseconds, callback }); return id; },
    clearTimeout(id) { timers.delete(id); }
  });
  const view = new View(elements, () => liveCalls++);
  const update = (overrides = {}) => view.update({ available: true, historical: false, paused: false, secondsBehind: 0, ...overrides });
  function emit(target, name, values = {}) {
    const event = new Event(name, { cancelable: true });
    if (values.target) Object.defineProperty(event, 'target', { value: values.target });
    Object.assign(event, { clientX: 500, pointerId: 1 }, Object.fromEntries(Object.entries(values).filter(([key]) => key !== 'target')));
    target.dispatchEvent(event);
    return event;
  }
  return { view, ...elements, input, document, window, update, emit, timers, frames, observers,
    addTile({ top = 300, bottom = 600, footerHeight = 22, width = 500 } = {}) {
      const tile = new Element(), header = new Element(), check = new Element();
      tile.classes.add('tile'); tile.rect = { top, bottom, width, height: bottom - top };
      header.classes.add('tile-header'); header.rect = { top: bottom - footerHeight, bottom, width, height: footerHeight };
      check.isControl = true; check.parent = header; header.parent = tile;
      tile.children.push(header); header.children.push(check); elements.focusTarget.children.push(tile);
      return { tile, header, check };
    },
    flushFrames() { for (const [id, callback] of [...frames]) { frames.delete(id); callback(); } },
    get liveCalls() { return liveCalls; },
    advance(milliseconds) {
      const end = now + milliseconds;
      while (true) {
        const pending = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!pending) break;
        now = pending[1].at;
        timers.delete(pending[0]);
        pending[1].callback();
      }
      now = end;
    }
  };
}

test('compact playback delay formats minutes and seconds in playing and paused states', () => {
  const h = harness();
  const cases = [[0, '0s'], [7, '7s'], [59, '59s'], [60, '1m 0s'], [61, '1m 1s'],
    [247, '4m 7s'], [3599, '59m 59s'], [3600, '60m 0s'], [86407, '1440m 7s'],
    [59.49, '59s'], [59.5, '1m 0s'], [119.5, '2m 0s'], [-1, '0s'],
    [NaN, '0s'], [Infinity, '0s'], [-Infinity, '0s'], [undefined, '0s']];
  for (const paused of [false, true]) {
    for (const [secondsBehind, expected] of cases) {
      h.update({ historical: true, paused, secondsBehind });
      assert.equal(h.compactStatus.textContent, `${paused ? 'PAUSED · 0×' : 'PLAYBACK · 1×'} · ${expected} behind live`);
    }
  }
  h.update();
  assert.equal(h.compactStatus.textContent, '');
});

test('compact reverse state shows the signed rate and pause reports zero', () => {
  const h = harness();
  h.update({ historical: true, rate: -8, secondsBehind: 30 });
  assert.equal(h.compactStatus.textContent, 'REVERSE · -8× · 30s behind live');
  h.update({ historical: true, rate: -8, paused: true, secondsBehind: 30 });
  assert.equal(h.compactStatus.textContent, 'PAUSED · 0× · 30s behind live');
});

test('layout reserves the tallest visible bottom footer plus four pixels', () => {
  const h = harness();
  h.addTile({ top: 0, bottom: 300, footerHeight: 120 });
  h.addTile({ footerHeight: 22 });
  h.addTile({ footerHeight: 48 });
  h.addTile({ footerHeight: 100, width: 0 });
  h.view.layout.refresh();
  assert.equal(h.view.layout.offset, 52);
  assert.equal(h.root.attributes['--playback-bottom-offset'], '52px');
});

test('layout includes the grid inset and accepts fractional footer geometry', () => {
  const h = harness();
  h.focusTarget.rect.bottom = 598.75;
  h.addTile({ bottom: 598.5, footerHeight: 20.25 });
  h.view.layout.refresh();
  assert.equal(h.view.layout.offset, 25.75);
  assert.equal(600 - h.view.layout.offset, 578.25 - 4);
});

test('empty grids, missing headers, hidden footers and offscreen tiles reserve no space', () => {
  const h = harness();
  assert.equal(h.view.layout.offset, 0);
  const missing = h.addTile(); missing.tile.children = [];
  const hidden = h.addTile(); hidden.header.rect.height = 0;
  h.addTile({ top: 600, bottom: 800 });
  h.view.layout.refresh();
  assert.equal(h.view.layout.offset, 0);
});

test('an oversized footer is clipped to the tile and available viewport height', () => {
  const h = harness();
  h.addTile({ top: 10, footerHeight: 900 });
  h.view.layout.refresh();
  assert.equal(h.view.layout.offset, 594);
  h.focusTarget.children[0].rect.top = 0;
  h.view.layout.refresh();
  assert.equal(h.view.layout.offset, 600);
});

test('footer wrapping and viewport resizing update the offset in one animation frame', () => {
  const h = harness(), { header } = h.addTile();
  h.view.layout.refresh();
  assert.equal(h.observers.resize.elements.has(header), true);
  header.rect.top = 550; header.rect.height = 50;
  h.observers.resize.callback(); h.observers.resize.callback(); h.emit(h.window, 'resize');
  assert.equal(h.frames.size, 1);
  assert.equal(h.view.layout.offset, 26);
  h.flushFrames();
  assert.equal(h.view.layout.offset, 54);
  h.window.innerHeight = 640;
  h.emit(h.window, 'resize'); h.flushFrames();
  assert.equal(h.view.layout.offset, 94);
});

test('adding, hiding, rearranging and removing cameras refreshes geometry and observation', () => {
  const h = harness(), first = h.addTile();
  h.observers.mutation.callback(); h.flushFrames();
  assert.equal(h.view.layout.offset, 26);
  const second = h.addTile({ footerHeight: 60 });
  h.observers.mutation.callback(); h.flushFrames();
  assert.equal(h.view.layout.offset, 64);
  assert.equal(h.observers.resize.elements.has(second.header), true);
  assert.equal(h.observers.mutation.elements.has(second.tile), true);
  second.tile.rect.width = 0;
  h.observers.mutation.callback(); h.flushFrames();
  assert.equal(h.view.layout.offset, 26);
  first.tile.rect.bottom = 300; first.header.rect.top = 278;
  h.observers.mutation.callback(); h.flushFrames();
  assert.equal(h.view.layout.offset, 0);
  h.focusTarget.children = [];
  h.observers.mutation.callback(); h.flushFrames();
  assert.equal(h.observers.resize.elements.size, 1);
  assert.equal(h.observers.resize.elements.has(first.header), false);
});

test('mouse and touch gestures on the bottom camera pictures leave the tab and drawer closed', () => {
  const h = harness(); h.update();
  for (const pointerType of ['mouse', 'touch', 'pen']) {
    for (const clientX of [100, 500, 999]) {
      for (const name of ['pointerdown', 'pointermove', 'pointerup', 'click', 'dblclick']) {
        assert.equal(h.emit(h.document, name, { pointerType, clientX, clientY: 550 }).defaultPrevented, false);
        assert.equal(h.view.open, false); assert.equal(h.view.tabRevealed, false);
      }
    }
  }
});

test('footer controls and other interactive elements never reveal the playback tab', () => {
  for (const pointerType of ['mouse', 'touch']) {
    const h = harness(), { header, check } = h.addTile();
    h.view.layout.refresh(); h.update(); h.input.isControl = true;
    for (const target of [header, check, h.input]) {
      h.emit(h.document, pointerType === 'mouse' ? 'pointermove' : 'pointerdown', { pointerType, clientY: pointerType === 'touch' ? 580 : 560, target });
      assert.equal(h.view.open, false);
    }
  }
});

test('showing the controls refreshes geometry before queued layout changes run', () => {
  const h = harness(); h.addTile({ footerHeight: 70 }); h.update();
  h.view.show();
  assert.equal(h.view.layout.offset, 74);
});

test('disposing layout cancels queued measurements and disconnects observers and resize events', () => {
  const h = harness();
  h.observers.resize.callback(); assert.equal(h.frames.size, 1);
  h.view.dispose();
  assert.equal(h.frames.size, 0);
  assert.equal(h.observers.resize.elements.size, 0);
  assert.equal(h.observers.mutation.elements.size, 0);
  h.emit(h.window, 'resize'); h.observers.resize.callback(); h.observers.mutation.callback();
  assert.equal(h.frames.size, 0);
});

test('available live playback leaves no visible dock and closed controls remain inert', () => {
  const h = harness();
  assert.equal(h.root.hidden, true);
  h.view.show();
  assert.equal(h.view.open, false);
  h.update();
  assert.equal(h.root.hidden, false);
  assert.equal(h.view.open, false);
  assert.equal(h.bar.inert, true);
  assert.equal(h.bar.attributes['aria-hidden'], 'true');
  assert.equal(h.dock.hidden, true);
  assert.equal(h.compactLive.hidden, true);
});

test('mouse hover reveals only the tab and a click opens the drawer without changing playback', () => {
  const h = harness(); h.update({ historical: true, paused: true, secondsBehind: 30 });
  const status = h.compactStatus.textContent;
  h.emit(h.tabArea, 'pointerenter', { pointerType: 'mouse' });
  assert.equal(h.view.tabRevealed, true); assert.equal(h.view.open, false);
  assert.equal(h.bar.inert, true); assert.equal(h.tab.attributes['aria-expanded'], 'false');
  h.advance(10000); assert.equal(h.view.tabRevealed, true);
  h.emit(h.tab, 'click', { pointerType: 'mouse', detail: 1 });
  assert.equal(h.view.open, true); assert.equal(h.bar.inert, false);
  assert.equal(h.tabArea.hidden, true); assert.equal(h.tab.attributes['aria-expanded'], 'true');
  assert.equal(h.compactStatus.textContent, status); assert.equal(h.liveCalls, 0);
  h.advance(3000); assert.equal(h.view.open, false); assert.equal(h.view.tabRevealed, false);
  assert.equal(h.tabArea.hidden, false); assert.equal(h.tab.attributes['aria-expanded'], 'false');
});

test('playback status polling does not postpone automatic hiding', () => {
  const h = harness(); h.update(); h.view.show();
  for (let i = 0; i < 12; i++) {
    h.advance(250);
    h.update({ historical: true, secondsBehind: 20 + i });
  }
  assert.equal(h.view.open, false);
  assert.equal(h.compactStatus.textContent, 'PLAYBACK · 1× · 31s behind live');
  assert.equal(h.compactLive.hidden, false);
});

test('leaving the mouse vicinity retracts the tab and another approach reveals it again', () => {
  const h = harness(); h.update();
  for (let attempt = 0; attempt < 3; attempt++) {
    h.emit(h.tabArea, 'pointerenter', { pointerType: 'mouse' });
    assert.equal(h.view.tabRevealed, true);
    h.emit(h.tabArea, 'pointerleave', { pointerType: 'mouse' });
    assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false);
    assert.equal(h.timers.size, 0);
  }
});

test('hovering keeps the drawer open and leaving starts a fresh inactivity period', () => {
  const h = harness(); h.update(); h.view.show();
  h.emit(h.bar, 'pointerenter', { pointerType: 'mouse' });
  h.advance(10000); assert.equal(h.view.open, true);
  h.emit(h.bar, 'pointerleave');
  h.advance(2999); assert.equal(h.view.open, true);
  h.advance(1); assert.equal(h.view.open, false);
});

test('touch pointer entry does not create a persistent hover hold', () => {
  const h = harness(); h.update(); h.view.show();
  h.emit(h.bar, 'pointerenter', { pointerType: 'touch' });
  h.advance(3000); assert.equal(h.view.open, false);
});

test('opening from a touch tab click keeps the drawer readable and switching to mouse restores transparency', () => {
  const h = harness(); h.update(); h.view.revealTab();
  h.emit(h.tab, 'click', { pointerType: 'touch', detail: 1 });
  assert.equal(h.view.open, true); assert.equal(h.bar.classes.has('is-interacting'), true);
  assert.equal(h.view.hovered, false);
  h.emit(h.document, 'pointermove', { pointerType: 'mouse' });
  assert.equal(h.bar.classes.has('is-interacting'), false);
  h.advance(3000); assert.equal(h.view.open, false);
});

test('legacy tab clicks retain touch readability without click pointer metadata', () => {
  const h = harness(); h.update(); h.view.revealTab();
  h.emit(h.tab, 'pointerdown', { pointerType: 'touch' });
  h.emit(h.tab, 'click', { detail: 1 });
  assert.equal(h.view.open, true); assert.equal(h.bar.classes.has('is-interacting'), true);
});

test('cancelled touch activation and keyboard activation cannot retain a stale touch mode', () => {
  for (const cancel of [h => h.emit(h.tab, 'pointercancel'), h => h.view.close()]) {
    const h = harness(); h.update(); h.view.revealTab();
    h.emit(h.tab, 'pointerdown', { pointerType: 'touch' }); cancel(h);
    h.emit(h.tab, 'click', { detail: 0 });
    assert.equal(h.view.open, true); assert.equal(h.bar.classes.has('is-interacting'), false);
  }
});

test('the first touch in the vicinity consumes the entire gesture and only reveals the tab', () => {
  const h = harness(); h.update(); let calls = 0;
  h.tabArea.addEventListener('pointerdown', () => calls++);
  for (const name of ['pointermove', 'pointerup', 'click']) h.document.addEventListener(name, () => calls++);
  assert.equal(h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch', target: h.tab }).defaultPrevented, true);
  for (const name of ['pointermove', 'pointerup', 'click']) {
    assert.equal(h.emit(h.document, name, { pointerType: 'touch' }).defaultPrevented, true, name);
  }
  assert.equal(calls, 0); assert.equal(h.view.tabRevealed, true); assert.equal(h.view.open, false);
  assert.equal(h.bar.inert, true); assert.equal(h.liveCalls, 0);
  assert.equal(h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch', target: h.tab }).defaultPrevented, false);
  h.emit(h.tab, 'click', { pointerType: 'touch', detail: 1 });
  assert.equal(h.view.open, true);
});

test('touching beside a revealed tab keeps it revealed without opening the drawer', () => {
  const h = harness(); h.update(); h.view.revealTab();
  assert.equal(h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' }).defaultPrevented, true);
  h.emit(h.document, 'pointerup'); h.emit(h.document, 'click');
  assert.equal(h.view.tabRevealed, true); assert.equal(h.view.open, false);
});

test('a touch reveal stays visible until release and retracts after three seconds of inactivity', () => {
  const h = harness(); h.update();
  h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' });
  h.emit(h.tabArea, 'pointerleave', { pointerType: 'touch' });
  h.advance(10000); assert.equal(h.view.tabRevealed, true);
  h.emit(h.document, 'pointerup');
  h.advance(2999); assert.equal(h.view.tabRevealed, true);
  h.advance(1); assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false);
});

test('status polling does not postpone tab retraction and revealing refreshes footer geometry', () => {
  const h = harness(); h.addTile({ footerHeight: 70 }); h.update(); h.view.revealTab();
  assert.equal(h.view.layout.offset, 74);
  for (let i = 0; i < 12; i++) { h.advance(250); h.update({ historical: true, secondsBehind: 30 }); }
  assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false);
});

test('unavailable playback, dialogs and hidden pages block mouse, touch and keyboard activation', () => {
  for (const block of [h => {}, h => { h.update(); h.document.modal = true; }, h => { h.update(); h.document.hidden = true; }]) {
    const h = harness(); block(h);
    h.emit(h.tabArea, 'pointerenter', { pointerType: 'mouse' });
    assert.equal(h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' }).defaultPrevented, false);
    h.emit(h.tab, 'focusin'); h.emit(h.tab, 'click', { detail: 0 });
    assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false);
    assert.equal(h.timers.size, 0);
  }
});

test('touch or pen hover and mouse dragging into the vicinity do not reveal the tab', () => {
  const h = harness(); h.update();
  for (const values of [{ pointerType: 'touch' }, { pointerType: 'pen' }, { pointerType: 'mouse', buttons: 1 }]) {
    h.emit(h.tabArea, 'pointerenter', values);
    assert.equal(h.view.tabRevealed, false);
  }
});

test('cancelled and click-less touch reveals do not consume the next tab activation', () => {
  for (const name of ['pointercancel', 'pointerup']) {
    const h = harness(); h.update();
    h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' });
    assert.equal(h.emit(h.document, name).defaultPrevented, true);
    h.emit(h.document, 'pointerdown', { pointerType: 'touch' });
    assert.equal(h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch', target: h.tab }).defaultPrevented, false);
    assert.equal(h.emit(h.document, 'click', { pointerType: 'touch' }).defaultPrevented, false);
    h.emit(h.tab, 'click', { pointerType: 'touch', detail: 1 });
    assert.equal(h.view.open, true);
  }
});

test('legacy reveal clicks are consumed while keyboard clicks and other pointers remain available', () => {
  const h = harness(); h.update();
  h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' });
  assert.equal(h.emit(h.document, 'pointermove', { pointerId: 2 }).defaultPrevented, false);
  assert.equal(h.emit(h.document, 'click', { pointerId: undefined, detail: 0 }).defaultPrevented, false);
  h.emit(h.document, 'pointerup');
  assert.equal(h.emit(h.document, 'click', { pointerId: undefined, detail: 1 }).defaultPrevented, true);
  assert.equal(h.view.open, false);
});

test('keyboard focus reveals the tab and activation moves focus to the first enabled drawer control', () => {
  const h = harness(); h.update(); h.pin.disabled = true; h.input.keyboardFocus = true;
  h.tab.keyboardFocus = true; h.tab.focus(); h.emit(h.tab, 'focusin');
  h.emit(h.tabArea, 'pointerleave', { pointerType: 'mouse' });
  h.advance(10000); assert.equal(h.view.tabRevealed, true); assert.equal(h.view.open, false);
  h.emit(h.tab, 'click', { detail: 0 });
  assert.equal(h.view.open, true); assert.equal(h.document.activeElement, h.input);
  h.emit(h.bar, 'focusin');
  h.advance(10000); assert.equal(h.view.open, true);
});

test('Escape retracts the focused tab and stops camera shortcuts', () => {
  const h = harness(); h.update(); h.tab.keyboardFocus = true; h.tab.focus(); h.emit(h.tab, 'focusin');
  assert.equal(h.emit(h.root, 'keydown', { key: 'Escape' }).defaultPrevented, true);
  assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false);
  assert.equal(h.document.activeElement, h.focusTarget);
  assert.equal(h.emit(h.root, 'keydown', { key: 'Escape' }).defaultPrevented, false);
});

test('closing, disabling, suspending and disposing clear the tab and its active reveal gesture', () => {
  for (const stop of [h => h.view.close(), h => h.update({ available: false }), h => h.emit(h.window, 'blur'), h => h.view.dispose()]) {
    const h = harness(); h.update(); h.emit(h.tabArea, 'pointerdown', { pointerType: 'touch' }); stop(h);
    assert.equal(h.view.tabRevealed, false); assert.equal(h.view.tabHovered, false);
    assert.equal(h.view.revealPointers.size, 0); assert.equal(h.view.pointers.size, 0);
    assert.equal(h.timers.size, 0); assert.equal(h.emit(h.document, 'click').defaultPrevented, false);
  }
});

test('keyboard entry reveals the drawer and focuses its first enabled control', () => {
  const h = harness(); h.update();
  h.pin.keyboardFocus = true;
  h.root.focus(); h.emit(h.root, 'focusin'); h.emit(h.bar, 'focusin');
  assert.equal(h.view.open, true);
  assert.equal(h.document.activeElement, h.pin);
  h.advance(10000); assert.equal(h.view.open, true);
});

test('drags stay readable until every pointer is released, including outside the drawer', () => {
  const h = harness(); h.update(); h.view.show();
  h.emit(h.bar, 'pointerdown', { pointerId: 1 });
  h.emit(h.bar, 'pointerdown', { pointerId: 2 });
  assert.equal(h.bar.classes.has('is-interacting'), true);
  h.advance(10000); assert.equal(h.view.open, true);
  h.emit(h.window, 'pointerup', { pointerId: 50 });
  h.emit(h.window, 'pointerup', { pointerId: 1 });
  h.advance(10000); assert.equal(h.view.open, true);
  h.emit(h.window, 'pointerup', { pointerId: 2 });
  assert.equal(h.bar.classes.has('is-interacting'), false);
  h.advance(3000); assert.equal(h.view.open, false);
});

test('cancelled pointers and lost capture release the drag hold', () => {
  for (const name of ['pointercancel', 'lostpointercapture']) {
    const h = harness(); h.update(); h.view.show();
    h.emit(h.bar, 'pointerdown', { pointerId: 1 });
    h.emit(name === 'pointercancel' ? h.window : h.bar, name, { pointerId: 1 });
    h.advance(3000);
    assert.equal(h.view.open, false, name);
    assert.equal(h.bar.classes.has('is-interacting'), false);
  }
});

test('keyboard focus holds the drawer and moving between controls preserves the hold', () => {
  const h = harness(); h.update(); h.view.show();
  h.input.keyboardFocus = true; h.input.focus(); h.emit(h.bar, 'focusin');
  h.advance(10000); assert.equal(h.view.open, true);
  h.pin.keyboardFocus = true; h.pin.focus();
  h.emit(h.bar, 'focusout', { relatedTarget: h.pin }); h.emit(h.bar, 'focusin');
  h.advance(10000); assert.equal(h.view.open, true);
  h.focusTarget.focus(); h.emit(h.bar, 'focusout', { relatedTarget: h.focusTarget });
  h.advance(3000); assert.equal(h.view.open, false);
});

test('mouse focus on a button allows hiding and returns focus to the camera grid', () => {
  const h = harness(); h.update(); h.view.show();
  h.pin.focus(); h.emit(h.bar, 'focusin');
  h.advance(3000);
  assert.equal(h.view.open, false);
  assert.equal(h.document.activeElement, h.focusTarget);
});

test('input activity resets the inactivity period', () => {
  for (const name of ['input', 'change', 'keydown']) {
    const h = harness(); h.update(); h.view.show();
    h.advance(2500); h.emit(h.bar, name);
    h.advance(2999); assert.equal(h.view.open, true, name);
    h.advance(1); assert.equal(h.view.open, false, name);
  }
});

test('pinning holds the drawer and unpinning restores automatic hiding', () => {
  const h = harness(); h.update(); h.view.show();
  h.emit(h.pin, 'click');
  assert.equal(h.pin.attributes['aria-pressed'], 'true');
  assert.equal(h.pin.attributes['aria-label'], 'Unpin playback controls');
  h.advance(10000); assert.equal(h.view.open, true);
  h.emit(h.pin, 'click');
  assert.equal(h.pin.attributes['aria-pressed'], 'false');
  h.advance(3000); assert.equal(h.view.open, false);
});

test('closing clears pinning and permits later automatic hiding', () => {
  const h = harness(); h.update(); h.view.show(); h.emit(h.pin, 'click');
  h.view.close();
  assert.equal(h.view.open, false); assert.equal(h.view.pinned, false);
  h.view.show(); h.advance(3000);
  assert.equal(h.view.open, false);
});

test('Escape closes the drawer, restores focus and prevents camera shortcuts', () => {
  const h = harness(); h.update(); h.view.show();
  h.input.keyboardFocus = true; h.input.focus();
  h.emit(h.pin, 'click');
  const event = h.emit(h.root, 'keydown', { key: 'Escape' });
  assert.equal(event.defaultPrevented, true);
  assert.equal(h.view.open, false); assert.equal(h.view.pinned, false);
  assert.equal(h.document.activeElement, h.focusTarget);
  assert.equal(h.emit(h.root, 'keydown', { key: 'Escape' }).defaultPrevented, false);
});

test('compact historical and paused state expose Go live without opening the drawer', () => {
  const h = harness(); h.update({ historical: true, secondsBehind: 30 });
  assert.equal(h.compactStatus.hidden, false);
  assert.equal(h.compactStatus.textContent, 'PLAYBACK · 1× · 30s behind live');
  h.update({ historical: true, paused: true, secondsBehind: 31 });
  assert.equal(h.dock.hidden, false);
  assert.equal(h.compactStatus.textContent, 'PAUSED · 0× · 31s behind live');
  h.compactLive.focus(); h.emit(h.compactLive, 'click');
  assert.equal(h.liveCalls, 1);
  h.update();
  assert.equal(h.compactStatus.hidden, true); assert.equal(h.compactLive.hidden, true);
  assert.equal(h.document.activeElement, h.focusTarget); assert.equal(h.view.open, false);
  assert.equal(h.dock.hidden, true);
});

test('disabling the buffer clears interaction, timers, focus and pinning', () => {
  const h = harness(); h.update(); h.view.show();
  h.input.focus(); h.emit(h.pin, 'click'); h.emit(h.bar, 'pointerdown', { pointerId: 1 });
  h.update({ available: false });
  assert.equal(h.root.hidden, true); assert.equal(h.bar.inert, true);
  assert.equal(h.view.pinned, false); assert.equal(h.view.pointers.size, 0);
  assert.equal(h.document.activeElement, null); assert.equal(h.timers.size, 0);
  h.update(); assert.equal(h.view.open, false);
});

test('page hiding and window blur clear holds without reopening on later status updates', () => {
  for (const name of ['blur', 'pagehide', 'visibilitychange']) {
    const h = harness(); h.update(); h.view.show();
    h.emit(h.pin, 'click'); h.emit(h.bar, 'pointerdown', { pointerId: 1 });
    if (name === 'visibilitychange') h.document.hidden = true;
    h.emit(name === 'visibilitychange' ? h.document : h.window, name);
    assert.equal(h.view.open, false, name); assert.equal(h.view.pointers.size, 0);
    h.document.hidden = false; h.update(); h.advance(10000);
    assert.equal(h.view.open, false, name);
  }
});

test('disposing removes listeners and pending timers', () => {
  const h = harness(); h.update(); h.view.show(); h.view.dispose();
  assert.equal(h.timers.size, 0); assert.equal(h.root.hidden, true);
  h.emit(h.root, 'focusin'); h.emit(h.document, 'pointermove', { pointerType: 'mouse', clientY: 599 });
  h.emit(h.document, 'pointerdown', { pointerType: 'touch', clientY: 599 });
  h.emit(h.tabArea, 'pointerenter', { pointerType: 'mouse' }); h.emit(h.tab, 'click');
  h.emit(h.compactLive, 'click');
  assert.equal(h.view.tabRevealed, false); assert.equal(h.view.open, false); assert.equal(h.liveCalls, 0);
});
