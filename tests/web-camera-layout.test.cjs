const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const source = readFileSync(join(__dirname, '../src/SecurityCameraViewer.Web/wwwroot/camera-layout.js'), 'utf8');
const { CameraLayout: layout, CameraLayoutView } = runInNewContext(`${source}\n({ CameraLayout, CameraLayoutView })`);
const ids = ['front', 'drive', 'yard', 'garage'];
const plain = value => JSON.parse(JSON.stringify(value));
const divider = (cells, axis, a, b) => layout.dividers(cells).find(d => d.axis === axis && d.a === a && d.b === b);
const drag = (cells, axis, a, b, segment, delta) => layout.move(cells, layout.movement(cells, divider(cells, axis, a, b), segment), delta, 0.08);

test('sideways profiles keep the width breakpoint identity and permit a compact grid', () => {
  assert.equal(layout.profile(393).stacked, true);
  assert.equal(layout.profile(393, true).id, 'mobile-sideways');
  assert.equal(layout.profile(580, true).id, 'wide-mobile-sideways');
  assert.equal(layout.profile(393, true).stacked, false);
});

test('portrait, automatic split-screen and forced sideways layouts retain separate saved proportions', () => {
  const saved = new Map(), grid = { addEventListener() {}, querySelectorAll: () => [], clientWidth: 393, clientHeight: 852 };
  const storage = { getItem: key => saved.get(key), setItem: (key, value) => saved.set(key, value), removeItem: key => saved.delete(key) };
  const view = new CameraLayoutView(grid, storage, () => {}); view.render = () => {};
  const tiles = new Map(ids.map((id, i) => [i + 1, { root: {} }]));
  const settings = { configurationId: 'home', sources: ids.map((id, i) => ({ id, channel: i + 1 })) };
  view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), plain(layout.create(ids, 1)));
  view.cells = layout.swap(view.cells, 0, 3); view.save(); const portrait = plain(view.cells);
  grid.clientHeight = 393; view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), portrait, 'A square camera area retains its portrait layout.');
  grid.clientHeight = 392; view.configure(settings, tiles, 1, null);
  assert.equal(view.profile.sideways, true);
  grid.clientHeight = 350; view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), plain(layout.create(ids, 2)));
  view.cells = drag(view.cells, 'x', 0, 1, false, 0.1); view.save(); const sideways = plain(view.cells);
  grid.clientHeight = 852; view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), portrait);
  view.sideways = true; view.configure(settings, tiles, 1, 2);
  assert.deepEqual(plain(view.cells), sideways); assert.equal(view.focus, 'drive');
  view.configure(settings, tiles, 1, null); assert.deepEqual(plain(view.cells), sideways);
  view.reset(); view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), plain(layout.create(ids, 2)));
  view.sideways = false; view.configure(settings, tiles, 1, null);
  assert.deepEqual(plain(view.cells), portrait); assert.equal(saved.size, 1);
});

test('landscape touch viewports use the compact grid without changing wide desktop defaults', () => {
  let coarse = true;
  const grid = { addEventListener() {}, querySelectorAll: () => [], clientWidth: 844, clientHeight: 320,
    ownerDocument: { addEventListener() {}, defaultView: { addEventListener() {}, matchMedia: () => ({ matches: coarse }) } } };
  const view = new CameraLayoutView(grid, { getItem: () => null }, () => {}); view.render = () => {};
  const tiles = new Map(ids.map((id, i) => [i + 1, { root: {} }]));
  view.configure({}, tiles, 4, null); assert.equal(view.profile.id, 'desktop-sideways');
  assert.deepEqual(plain(view.cells), plain(layout.create(['channel-1', 'channel-2', 'channel-3', 'channel-4'], 2)));
  coarse = false; view.configure({}, tiles, 4, null);
  assert.equal(view.profile.id, 'desktop'); assert.equal(view.cells[0].right, 0.25);
});

test('sideways handles empty and arbitrary camera counts when browser storage is unavailable', () => {
  const grid = { addEventListener() {}, querySelectorAll: () => [], clientWidth: 390, clientHeight: 800 };
  const view = new CameraLayoutView(grid, { getItem() { throw new Error('Storage blocked'); } }, () => {});
  view.render = () => {}; view.sideways = true;
  for (const count of [0, 1, 3, 4, 7, 17]) {
    const tiles = new Map(Array.from({ length: count }, (_, i) => [i + 1, { root: {} }]));
    view.configure({}, tiles, 1, null);
    if (count) assert.ok(layout.valid(view.cells, [...tiles.keys()].map(channel => `channel-${channel}`)));
    else assert.equal(view.cells.length, 0);
    assert.equal(view.profile.sideways, true);
  }
});

test('normal drags move complete vertical and horizontal bars', () => {
  const initial = layout.create(ids, 2);
  const vertical = drag(initial, 'x', 0, 1, false, 0.1);
  assert.equal(vertical[0].right, 0.6);
  assert.equal(vertical[2].right, 0.6);
  assert.equal(vertical[1].left, 0.6);
  assert.equal(vertical[3].left, 0.6);
  const horizontal = drag(initial, 'y', 0, 2, false, -0.1);
  assert.equal(horizontal[0].bottom, 0.4);
  assert.equal(horizontal[1].bottom, 0.4);
  assert.equal(horizontal[2].top, 0.4);
  assert.equal(horizontal[3].top, 0.4);
  assert.ok(layout.valid(vertical, ids));
  assert.ok(layout.valid(horizontal, ids));
});

test('Control isolates the clicked segment in either orientation', () => {
  const initial = layout.create(ids, 2);
  const vertical = drag(initial, 'x', 0, 1, true, 0.1);
  assert.deepEqual(plain(vertical.slice(2)), plain(initial.slice(2)));
  assert.equal(vertical[0].right, 0.6);
  const horizontal = drag(initial, 'y', 0, 2, true, -0.1);
  assert.deepEqual(plain(horizontal[1]), plain(initial[1]));
  assert.deepEqual(plain(horizontal[3]), plain(initial[3]));
  assert.equal(horizontal[0].bottom, 0.4);
  assert.ok(layout.valid(vertical, ids));
  assert.ok(layout.valid(horizontal, ids));
});

test('T junctions stay rectangular and repeated mixed moves never overlap or leave holes', () => {
  let cells = drag(layout.create(ids, 2), 'x', 0, 1, true, 0.1);
  cells = drag(cells, 'y', 0, 2, true, 0.1);
  assert.ok(layout.valid(cells, ids));
  assert.equal(cells[1].bottom, 0.6);
  // Deterministic varied movements cover staggered boundaries, clamping, and both axes.
  let seed = 17;
  const random = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 2 ** 32);
  for (let i = 0; i < 1500; i++) {
    const dividers = layout.dividers(cells);
    const selected = dividers[Math.floor(random() * dividers.length)];
    cells = layout.move(cells, layout.movement(cells, selected, random() > 0.5), random() - 0.5, 0.08);
    assert.ok(layout.valid(cells, ids), `valid partition after move ${i}`);
  }
});

test('large collections, including blank cells, retain full coverage', () => {
  for (const count of [...Array.from({ length: 16 }, (_, index) => index + 1), 17, 32, 64]) {
    const cameras = Array.from({ length: count }, (_, i) => `camera-${i}`);
    for (let columns = 1; columns <= count; columns++) {
      const cells = layout.create(cameras, columns);
      assert.ok(layout.valid(cells, cameras));
    }
  }
  const cells = layout.create([...ids, 'porch'], 3);
  const swapped = layout.swap(cells, 0, 5);
  assert.equal(swapped[0].id, null);
  assert.equal(swapped[5].id, 'front');
  assert.ok(layout.valid(swapped, [...ids, 'porch']));
});

test('swapping changes identities only; proportional geometry survives serialization', () => {
  const cells = drag(layout.create(ids, 2), 'x', 0, 1, true, 0.1);
  const swapped = layout.swap(cells, 0, 3);
  assert.equal(swapped[0].id, 'garage');
  assert.equal(swapped[3].id, 'front');
  const geometry = items => plain(items.map(({ id, ...bounds }) => bounds));
  assert.deepEqual(geometry(cells), geometry(swapped));
  const saved = plain(swapped);
  for (const width of [360, 1366, 2560]) assert.equal(saved[0].right * width / width, 0.6);
  assert.ok(layout.valid(saved, ids));
  assert.ok(!layout.valid(saved, [...ids, 'new-camera']));
});

test('invalid or stale saved layouts are rejected', () => {
  const cells = layout.create(ids, 2);
  for (const invalid of [null, {}, [], [null], cells.map(c => ({ ...c, id: 'duplicate' })),
    cells.map(c => ({ ...c, left: NaN })), cells.map(c => ({ ...c, right: 2 })),
    cells.map(c => ({ ...c, left: c.left + 0.01 })), [...cells, cells[0]]]) {
    assert.equal(layout.valid(invalid, ids), false);
  }
});

test('controller saves immediately and reports storage failure without throwing', () => {
  const reports = [], writes = [];
  const grid = { addEventListener() {} };
  const view = new CameraLayoutView(grid, { setItem: (...args) => writes.push(args) }, (...args) => reports.push(args));
  view.key = 'test'; view.cells = layout.create(ids, 2);
  view.profile = layout.profile(1366);
  view.save();
  assert.equal(writes.length, 1);
  assert.equal(JSON.parse(writes[0][1]).version, 1);
  assert.deepEqual(JSON.parse(writes[0][1]).cells, plain(view.cells));
  view.storage.setItem = () => { throw new Error('Quota exceeded'); };
  assert.doesNotThrow(() => view.save());
  assert.equal(reports.at(-1)[1], true);
});

test('mobile stack and desktop grid persist independently across resizes, reloads and resets', () => {
  const saved = new Map();
  const storage = { getItem: key => saved.get(key) ?? null, setItem: (key, value) => saved.set(key, value), removeItem: key => saved.delete(key) };
  const grid = { addEventListener() {}, querySelectorAll: () => [], clientWidth: 1366 };
  const tiles = new Map(ids.map((id, index) => [index + 1, { root: { id } }]));
  const settings = { configurationId: 'home', sources: ids.map((id, index) => ({ id, channel: index + 1 })) };
  const makeView = () => {
    const view = new CameraLayoutView(grid, storage, () => {});
    view.render = () => {};
    return view;
  };
  let view = makeView();
  view.configure(settings, tiles, 2, null);
  assert.equal(view.profile.id, 'desktop');
  view.cells = drag(view.cells, 'x', 0, 1, true, 0.1);
  view.cells = layout.swap(view.cells, 0, 3);
  view.save();
  const desktop = plain(view.cells);
  grid.clientWidth = 390;
  view.configure(settings, tiles, 2, null);
  assert.equal(view.profile.id, 'mobile');
  assert.ok(view.cells.every(c => c.left === 0 && c.right === 1));
  view.cells = drag(view.cells, 'y', 0, 1, false, 0.08);
  view.cells = layout.swap(view.cells, 0, 1);
  view.save();
  const mobile = plain(view.cells);
  view = makeView();
  view.configure(settings, tiles, 2, null);
  assert.deepEqual(plain(view.cells), mobile);
  grid.clientWidth = 1920;
  view.configure(settings, tiles, 3, null);
  assert.deepEqual(plain(view.cells), desktop);
  grid.clientWidth = 480;
  view.configure(settings, tiles, 2, null);
  assert.deepEqual(plain(view.cells), mobile);
  view.reset(); view.configure(settings, tiles, 2, null);
  assert.deepEqual(plain(view.cells), plain(layout.create(ids, 1)));
  grid.clientWidth = 761;
  view.configure(settings, tiles, 2, null);
  assert.deepEqual(plain(view.cells), desktop);
  view.configure({ ...settings, configurationId: 'office' }, tiles, 2, null);
  assert.deepEqual(plain(view.cells), plain(layout.create(ids, 2)));
});

test('every CSS breakpoint owns a separate saved layout and reset scope', () => {
  const saved = new Map();
  const grid = { addEventListener() {}, querySelectorAll: () => [], clientWidth: 390 };
  const storage = { getItem: key => saved.get(key) ?? null, setItem: (key, value) => saved.set(key, value), removeItem: key => saved.delete(key) };
  const view = new CameraLayoutView(grid, storage, () => {});
  view.render = () => {};
  const tiles = new Map(ids.map((id, i) => [i + 1, { root: {} }]));
  const settings = { configurationId: 'home', sources: ids.map((id, i) => ({ id, channel: i + 1 })) };
  const sizes = [480, 600, 760, 1366];
  const expected = [];
  for (const [i, width] of sizes.entries()) {
    view.configure(settings, tiles, 2, null, width);
    view.cells = layout.swap(view.cells, 0, i);
    view.save(); expected.push(plain(view.cells));
  }
  assert.equal(saved.size, 4);
  for (const [i, width] of sizes.entries()) {
    view.configure(settings, tiles, 3, null, width);
    assert.deepEqual(plain(view.cells), expected[i]);
  }
  view.reset();
  assert.equal(saved.size, 3);
  assert.equal(layout.profile(480).id, 'mobile');
  assert.equal(layout.profile(481).id, 'wide-mobile');
  assert.equal(layout.profile(600).id, 'wide-mobile');
  assert.equal(layout.profile(601).id, 'tablet');
  assert.equal(layout.profile(760).id, 'tablet');
  assert.equal(layout.profile(761).id, 'desktop');
});

test('pointer dragging reads Ctrl, saves before release, and safely releases capture', () => {
  const writes = [];
  const grid = { addEventListener() {}, querySelectorAll: () => [],
    classList: { add() {}, remove() {} }, getBoundingClientRect: () => ({ width: 1000, height: 600 }),
    setPointerCapture(id) { this.pointer = id; }, hasPointerCapture(id) { return this.pointer === id; },
    releasePointerCapture() { this.pointer = null; } };
  const view = new CameraLayoutView(grid, { setItem: (...args) => writes.push(args) }, () => {});
  view.profile = layout.profile(1000); view.key = 'test'; view.cells = layout.create(ids, 2);
  view.tiles = new Map(ids.map(id => [id, { style: {} }])); view.positionHandles = () => {}; view.render = () => {};
  const handle = { divider: divider(view.cells, 'x', 0, 1), classList: { add() {} },
    setPointerCapture: id => grid.setPointerCapture(id), hasPointerCapture: id => grid.hasPointerCapture(id), releasePointerCapture: () => grid.releasePointerCapture() };
  const target = { closest: () => handle };
  view.beginResize({ target, button: 0, pointerId: 9, clientX: 500, clientY: 100, preventDefault() {} });
  view.resize({ pointerId: 9, clientX: 600, clientY: 100, ctrlKey: true });
  assert.equal(writes.length, 1);
  assert.equal(view.cells[0].right, 0.6);
  assert.equal(view.cells[2].right, 0.5);
  // Releasing Ctrl during the same drag switches to the full bar from its baseline.
  view.resize({ pointerId: 9, clientX: 600, clientY: 100, ctrlKey: false });
  assert.equal(view.cells[2].right, 0.6);
  assert.equal(writes.length, 2);
  view.endResize({ pointerId: 9 });
  assert.equal(grid.pointer, null);
  assert.equal(view.drag, null);
});

test('nearby parallel segments snap exactly and then drag as one complete bar', () => {
  for (const [axis, a, b] of [['x', 0, 1], ['y', 0, 2]]) {
    const cells = drag(layout.create(ids, 2), axis, a, b, true, 0.1);
    const selected = layout.movement(cells, divider(cells, axis, a, b), true);
    for (const extent of [400, 1000, 2400]) {
      const snapped = layout.snap(cells, selected, -0.1 + 9 / extent, 0.04, 10 / extent);
      assert.equal(snapped.target.position, 0.5);
      assert.ok(layout.valid(snapped.cells, ids));
      assert.deepEqual(plain(snapped.cells), plain(layout.create(ids, 2)));
      const full = layout.movement(snapped.cells, divider(snapped.cells, axis, a, b), false);
      assert.equal(full.edges.length, 4);
      assert.equal(layout.snap(cells, selected, -0.1 + 11 / extent, 0.04, 10 / extent).target, null);
    }
  }
});

test('snapping rejects perpendicular, moving and unreachable lines', () => {
  const cells = layout.create(['a', 'b', 'c'], 3);
  const selected = layout.movement(cells, divider(cells, 'x', 0, 1), false);
  // The next vertical divider is also the far edge of the middle tile: snapping
  // there would collapse that tile, so it cannot be a highlighted destination.
  assert.equal(layout.snap(cells, selected, 1 / 3, 0.04, 0.02).target, null);
  const grid = layout.create(ids, 2);
  const wholeBar = layout.movement(grid, divider(grid, 'x', 0, 1), false);
  assert.equal(layout.snap(grid, wholeBar, 0, 0.04, 1).target, null);
});

test('double-click alignment restores either axis without changing order or other dimensions', () => {
  for (const [axis, a, b] of [['x', 0, 1], ['y', 0, 2]]) {
    let cells = layout.swap(layout.create(ids, 2), 0, 3);
    const aligned = plain(cells);
    cells = drag(cells, axis, a, b, true, 0.13);
    const restored = layout.resetAlignment(cells, divider(cells, axis, a, b));
    assert.deepEqual(plain(restored), aligned);
    assert.deepEqual(plain(layout.resetAlignment(restored, divider(restored, axis, a, b))), aligned);
  }
  const pair = layout.create(ids.slice(0, 2), 2);
  const resized = drag(pair, 'x', 0, 1, false, 0.2);
  assert.deepEqual(plain(layout.resetAlignment(resized, divider(resized, 'x', 0, 1))), plain(pair));
});

function interactiveView(cells = layout.create(ids, 2)) {
  const writes = [], listeners = {};
  const grid = { addEventListener() {}, querySelectorAll: () => [],
    ownerDocument: { addEventListener: (name, handler) => { listeners[name] = handler; }, defaultView: { addEventListener() {} } },
    classList: { add() {}, remove() {} }, getBoundingClientRect: () => ({ width: 1000, height: 600 }) };
  const view = new CameraLayoutView(grid, { setItem: (...args) => writes.push(args) }, () => {});
  view.profile = layout.profile(1000); view.key = 'test'; view.cells = cells;
  view.tiles = new Map(ids.map(id => [id, { style: {} }]));
  view.handles = layout.dividers(cells).map(d => {
    const classes = new Set();
    return { divider: d, classes, style: {}, closest() { return this; },
      classList: { toggle: (name, on) => on ? classes.add(name) : classes.delete(name) },
      setPointerCapture(id) { this.pointer = id; }, hasPointerCapture(id) { return this.pointer === id; }, releasePointerCapture() { this.pointer = null; } };
  });
  return { view, writes, listeners };
}

test('hover covers the full bar and responds to Control without a pointer move', () => {
  for (const axis of ['x', 'y']) {
    const { view, listeners } = interactiveView();
    const handle = view.handles.find(h => h.divider.axis === axis);
    const active = () => view.handles.filter(h => h.classes.has('active'));
    view.hover({ target: handle, ctrlKey: false });
    assert.equal(active().length, 2);
    assert.ok(active().every(h => h.divider.axis === axis));
    listeners.keydown({ key: 'Control', type: 'keydown' });
    assert.equal(active().length, 1);
    assert.equal(active()[0], handle);
    listeners.keyup({ key: 'Control', type: 'keyup' });
    assert.equal(active().length, 2);
    view.hover({ target: null });
    assert.equal(active().length, 0);
  }
});

test('pointer snapping highlights the destination and persists the exact alignment before release', () => {
  const { view, writes } = interactiveView(drag(layout.create(ids, 2), 'x', 0, 1, true, 0.1));
  const handle = view.handles[0];
  view.beginResize({ target: handle, button: 0, pointerId: 7, clientX: 600, clientY: 100, ctrlKey: true, preventDefault() {} });
  view.resize({ pointerId: 7, clientX: 507, clientY: 100, ctrlKey: true });
  assert.equal(view.handles.filter(h => h.classes.has('active')).length, 1);
  assert.equal(view.handles.filter(h => h.classes.has('snap-target')).length, 1);
  assert.equal(JSON.parse(writes.at(-1)[1]).cells[0].right, 0.5);
  view.resize({ pointerId: 7, clientX: 520, clientY: 100, ctrlKey: true });
  assert.equal(view.handles.filter(h => h.classes.has('snap-target')).length, 0);
  assert.equal(view.cells[0].right, 0.52);
});

test('two stationary clicks keep the captured handle attached so double-click can realign it', () => {
  const { view, writes } = interactiveView(drag(layout.create(ids, 2), 'x', 0, 1, true, 0.1));
  const handle = view.handles[0];
  let renders = 0;
  view.render = () => { renders++; };
  handle.focus = () => {};
  for (let i = 0; i < 2; i++) {
    view.beginResize({ target: handle, button: 0, pointerId: 7, clientX: 600, clientY: 100, preventDefault() {} });
    assert.equal(handle.pointer, 7);
    view.endResize({ target: handle, pointerId: 7 });
    assert.equal(handle.pointer, null);
    assert.equal(renders, 0);
  }
  view.resetDivider({ target: handle, preventDefault() {} });
  assert.equal(renders, 1);
  assert.equal(view.cells[0].right, 0.5);
  assert.equal(writes.length, 1);
});

test('mixed snapping and realignment preserve a complete non-overlapping partition', () => {
  const cameras = [...ids, 'porch'];
  let cells = layout.create(cameras, 3), seed = 71;
  const random = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 2 ** 32);
  for (let i = 0; i < 500; i++) {
    const lines = layout.dividers(cells);
    const line = lines[Math.floor(random() * lines.length)];
    cells = i % 3 === 0 ? layout.resetAlignment(cells, line) :
      layout.snap(cells, layout.movement(cells, line, random() > 0.5), random() - 0.5, 0.04, 0.025).cells;
    assert.ok(layout.valid(cells, cameras), `valid partition after alignment ${i}`);
  }
});
