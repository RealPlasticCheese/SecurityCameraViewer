'use strict';

// Geometry is independent of the DOM, storage, and camera playback. Coordinates are
// fractions of the viewport; even an empty grid cell participates in the partition.
const CameraLayout = (() => {
  const epsilon = 0.000001;
  const same = (a, b) => Math.abs(a - b) < epsilon;
  const overlap = (a, b, start, end) => Math.min(a[end], b[end]) - Math.max(a[start], b[start]) > epsilon;

  // Match the existing CSS width breakpoints. Keys stay stable across camera or
  // viewport changes; stacked and sideways views retain separate saved layouts.
  function profile(width, sideways = false) {
    const base = width <= 480 ? { id: 'mobile', name: 'Mobile', stacked: true } :
      width <= 600 ? { id: 'wide-mobile', name: 'Wide mobile', stacked: true } :
      width <= 760 ? { id: 'tablet', name: 'Tablet', stacked: false } : { id: 'desktop', name: 'Desktop', stacked: false };
    return sideways ? { id: `${base.id}-sideways`, name: `${base.name} sideways`, stacked: false, sideways: true } : base;
  }

  function create(ids, columns) {
    const rows = Math.ceil(ids.length / columns);
    return Array.from({ length: rows * columns }, (_, i) => ({
      id: ids[i] ?? null, left: (i % columns) / columns, right: (i % columns + 1) / columns,
      top: Math.floor(i / columns) / rows, bottom: (Math.floor(i / columns) + 1) / rows
    }));
  }

  function valid(cells, ids) {
    if (!Array.isArray(cells) || cells.length < ids.length || cells.length > Math.max(1, ids.length * 2)) return false;
    const minimumSize = Math.min(0.02, 0.5 / Math.max(1, cells.length));
    const savedIds = cells.filter(c => c?.id !== null).map(c => c?.id);
    if (savedIds.length !== ids.length || new Set(savedIds).size !== ids.length || ids.some(id => !savedIds.includes(id))) return false;
    let area = 0;
    for (const [index, cell] of cells.entries()) {
      if (!cell || !['left', 'right', 'top', 'bottom'].every(key => Number.isFinite(cell[key]) && cell[key] >= 0 && cell[key] <= 1) ||
          cell.right - cell.left < minimumSize - epsilon || cell.bottom - cell.top < minimumSize - epsilon) return false;
      area += (cell.right - cell.left) * (cell.bottom - cell.top);
      if (cells.slice(0, index).some(other => overlap(cell, other, 'left', 'right') && overlap(cell, other, 'top', 'bottom'))) return false;
    }
    return same(area, 1);
  }

  function dividers(cells) {
    const result = [];
    for (const axis of ['x', 'y']) {
      const [start, end, low, high] = axis === 'x' ? ['top', 'bottom', 'left', 'right'] : ['left', 'right', 'top', 'bottom'];
      for (let a = 0; a < cells.length; a++) {
        for (let b = 0; b < cells.length; b++) {
          if (same(cells[a][high], cells[b][low]) && overlap(cells[a], cells[b], start, end)) {
            result.push({ axis, a, b, position: cells[a][high], start: Math.max(cells[a][start], cells[b][start]), end: Math.min(cells[a][end], cells[b][end]) });
          }
        }
      }
    }
    return result;
  }

  function movement(cells, divider, segmentOnly) {
    const [low, high, start, end] = divider.axis === 'x' ? ['left', 'right', 'top', 'bottom'] : ['top', 'bottom', 'left', 'right'];
    const edges = cells.flatMap((cell, index) => [low, high].filter(edge => same(cell[edge], divider.position)).map(edge => ({ index, edge })));
    let selected = segmentOnly ? edges.filter(e => e.index === divider.a || e.index === divider.b) : edges;
    // At a T junction, connected edges must travel together to keep every tile
    // rectangular. Otherwise the move would leave holes or cover another feed.
    for (let previous = -1; previous !== selected.length;) {
      previous = selected.length;
      selected = edges.filter(e => selected.some(s => s.index === e.index ||
        (s.edge !== e.edge && overlap(cells[s.index], cells[e.index], start, end))));
    }
    return { axis: divider.axis, position: divider.position, edges: selected };
  }

  function move(cells, movement, delta, minimum) {
    const [low, high] = movement.axis === 'x' ? ['left', 'right'] : ['top', 'bottom'];
    let min = -Infinity, max = Infinity;
    for (const { index, edge } of movement.edges) {
      const cell = cells[index];
      // Small viewports must not change saved proportions just by being opened.
      const size = Math.max(Math.min(0.02, 0.5 / cells.length), Math.min(minimum, (cell[high] - cell[low]) / 2));
      if (edge === low) max = Math.min(max, cell[high] - size - cell[low]);
      else min = Math.max(min, cell[low] + size - cell[high]);
    }
    delta = Math.max(min, Math.min(max, delta));
    const next = cells.map(cell => ({ ...cell }));
    for (const { index, edge } of movement.edges) next[index][edge] += delta;
    return next;
  }

  function swap(cells, source, target) {
    const next = cells.map(cell => ({ ...cell }));
    [next[source].id, next[target].id] = [next[target].id, next[source].id];
    return next;
  }

  function affects(movement, divider) {
    if (movement.axis !== divider.axis) return false;
    const [low, high] = divider.axis === 'x' ? ['left', 'right'] : ['top', 'bottom'];
    return movement.edges.some(e => (e.index === divider.a && e.edge === high) || (e.index === divider.b && e.edge === low));
  }

  function snap(cells, movement, delta, minimum, tolerance) {
    const proposed = movement.position + delta;
    const candidates = dividers(cells).filter(d => d.axis === movement.axis && !affects(movement, d) && Math.abs(d.position - proposed) <= tolerance)
      .sort((a, b) => Math.abs(a.position - proposed) - Math.abs(b.position - proposed) || a.position - b.position);
    for (const target of candidates) {
      const aligned = move(cells, movement, target.position - movement.position, minimum);
      if (!movement.edges.every(e => same(aligned[e.index][e.edge], target.position))) continue;
      // Store the exact target fraction so subsequent full-bar drags reunite the edges.
      for (const edge of movement.edges) aligned[edge.index][edge.edge] = target.position;
      return { cells: aligned, target };
    }
    return { cells: move(cells, movement, delta, minimum), target: null };
  }

  function resetAlignment(cells, divider) {
    const selected = movement(cells, divider, true);
    const aligned = snap(cells, selected, 0, 0.02, Infinity);
    if (aligned.target) return aligned.cells;
    // A single row/column has no other parallel segment: balance its adjacent space.
    const [low, high] = divider.axis === 'x' ? ['left', 'right'] : ['top', 'bottom'];
    const lower = Math.max(...selected.edges.filter(e => e.edge === high).map(e => cells[e.index][low]));
    const upper = Math.min(...selected.edges.filter(e => e.edge === low).map(e => cells[e.index][high]));
    return move(cells, selected, (lower + upper) / 2 - divider.position, 0.02);
  }

  return { profile, create, valid, dividers, movement, move, swap, affects, snap, resetAlignment };
})();

class CameraLayoutView {
  constructor(grid, storage, report) {
    this.grid = grid;
    this.storage = storage;
    this.report = report;
    this.cells = [];
    this.handles = [];
    this.empty = [];
    this.sideways = false;
    grid.addEventListener('pointerdown', event => this.beginResize(event));
    grid.addEventListener('pointermove', event => this.resize(event));
    grid.addEventListener('pointerover', event => this.hover(event));
    grid.addEventListener('pointerout', event => this.hover({ target: event.relatedTarget, ctrlKey: event.ctrlKey }));
    grid.addEventListener('pointerup', event => this.endResize(event));
    grid.addEventListener('pointercancel', event => this.endResize(event));
    grid.addEventListener('lostpointercapture', event => this.endResize(event));
    grid.addEventListener('dragstart', event => this.beginSwap(event));
    grid.addEventListener('dragover', event => this.overSwap(event));
    grid.addEventListener('drop', event => this.endSwap(event));
    grid.addEventListener('dragend', () => this.clearSwap());
    grid.addEventListener('keydown', event => this.keyResize(event));
    grid.addEventListener('dblclick', event => this.resetDivider(event));
    const keyboard = grid.ownerDocument;
    keyboard?.addEventListener('keydown', event => this.modifierChanged(event));
    keyboard?.addEventListener('keyup', event => this.modifierChanged(event));
    keyboard?.defaultView?.addEventListener('blur', () => { this.hovered = null; this.highlight(); });
  }

  configure(settings, tiles, columns, focus, viewportWidth = this.grid.clientWidth) {
    const cameras = settings.sources?.length ? settings.sources.filter(c => c.enabled !== false).map(c => ({ id: c.id, channel: c.channel })) :
      [...tiles.keys()].map(channel => ({ id: `channel-${channel}`, channel }));
    const ids = cameras.map(c => c.id);
    const mobile = CameraLayout.profile(viewportWidth).stacked || this.grid.ownerDocument?.defaultView?.matchMedia?.('(pointer:coarse)').matches;
    this.profile = CameraLayout.profile(viewportWidth, this.sideways || mobile && this.grid.clientWidth > this.grid.clientHeight);
    const key = `scv.camera-layout.v1:${settings.configurationId || 'default'}:${this.profile.id}`;
    const identity = JSON.stringify([key, ids]);
    if (identity !== this.identity) {
      this.cancelResize();
      this.clearSwap();
      this.identity = identity;
      this.key = key;
      this.ids = ids;
      this.custom = false;
      try {
        const saved = JSON.parse(this.storage.getItem(key));
        if (saved?.version === 1 && CameraLayout.valid(saved.cells, ids)) {
          this.cells = saved.cells;
          this.custom = true;
        }
      } catch { /* Missing, unavailable or invalid storage uses the automatic grid. */ }
    }
    this.tiles = new Map(cameras.map(c => [c.id, tiles.get(c.channel).root]));
    this.focus = focus === null ? null : cameras.find(c => c.channel === focus)?.id;
    if (!this.custom) this.cells = CameraLayout.create(ids, this.profile.sideways ? Math.max(1, Math.ceil(Math.sqrt(ids.length))) : this.profile.stacked ? 1 : columns);
    this.render();
  }

  save() {
    this.custom = true;
    try {
      this.storage.setItem(this.key, JSON.stringify({ version: 1, cells: this.cells }));
      this.report(`${this.profile.name} camera layout saved in this browser.`, false);
    } catch {
      this.report('Camera layout could not be saved. Allow browser storage to keep it after reloading.', true);
    }
  }

  reset() {
    if (!this.key) return;
    this.cancelResize();
    this.clearSwap();
    try { this.storage.removeItem(this.key); this.report(`Automatic ${this.profile.name.toLowerCase()} camera layout restored.`, false); }
    catch { this.report('Could not remove the saved layout from browser storage.', true); }
    this.custom = false;
  }

  position(element, cell) {
    element.style.left = `${cell.left * 100}%`;
    element.style.top = `${cell.top * 100}%`;
    element.style.width = `${(cell.right - cell.left) * 100}%`;
    element.style.height = `${(cell.bottom - cell.top) * 100}%`;
  }

  render() {
    this.cancelResize();
    this.clearSwap();
    this.hovered = null;
    this.handles.forEach(handle => handle.remove());
    this.empty.forEach(cell => cell.remove());
    this.handles = []; this.empty = [];
    for (const [id, tile] of this.tiles) {
      tile.hidden = this.focus !== null && id !== this.focus;
      tile.draggable = this.focus === null;
      tile.querySelector('img').draggable = false;
    }
    if (this.focus !== null) {
      const tile = this.tiles.get(this.focus);
      if (tile) this.position(tile, { left: 0, right: 1, top: 0, bottom: 1 });
      return;
    }
    for (const [index, cell] of this.cells.entries()) {
      let tile = this.tiles.get(cell.id);
      if (!tile) {
        tile = document.createElement('div'); tile.className = 'empty-cell';
        tile.setAttribute('aria-label', 'Empty camera position');
        this.empty.push(tile); this.grid.append(tile);
      }
      tile.dataset.slot = index;
      this.position(tile, cell);
    }
    for (const divider of CameraLayout.dividers(this.cells)) {
      const handle = document.createElement('div');
      handle.className = `camera-divider ${divider.axis === 'x' ? 'vertical' : 'horizontal'}`;
      handle.tabIndex = 0; handle.divider = divider;
      handle.setAttribute('role', 'separator');
      handle.setAttribute('aria-orientation', divider.axis === 'x' ? 'vertical' : 'horizontal');
      handle.setAttribute('aria-label', 'Resize cameras. Hold Control for this segment. Arrow keys resize; Enter realigns.');
      handle.setAttribute('aria-valuenow', String(Math.round(divider.position * 100)));
      handle.title = 'Drag to resize the full bar. Ctrl+drag resizes this segment. Nearby lines snap together. Double-click to realign.';
      this.handles.push(handle); this.grid.append(handle);
    }
    this.positionHandles();
  }

  positionHandles() {
    for (const handle of this.handles) {
      const d = handle.divider;
      const a = this.cells[d.a], b = this.cells[d.b];
      const vertical = d.axis === 'x';
      const start = Math.max(a[vertical ? 'top' : 'left'], b[vertical ? 'top' : 'left']);
      const end = Math.min(a[vertical ? 'bottom' : 'right'], b[vertical ? 'bottom' : 'right']);
      handle.style[vertical ? 'left' : 'top'] = `${a[vertical ? 'right' : 'bottom'] * 100}%`;
      handle.style[vertical ? 'top' : 'left'] = `${start * 100}%`;
      handle.style[vertical ? 'height' : 'width'] = `${(end - start) * 100}%`;
    }
  }

  beginResize(event) {
    const handle = event.target.closest('.camera-divider');
    if (!handle || event.button !== 0 || this.drag) return;
    event.preventDefault();
    this.clearSwap();
    const bounds = this.grid.getBoundingClientRect();
    this.drag = { pointer: event.pointerId, handle, cells: this.cells, divider: handle.divider,
      x: event.clientX, y: event.clientY, width: bounds.width, height: bounds.height, ctrlKey: event.ctrlKey };
    handle.setPointerCapture(event.pointerId);
    this.grid.classList.add('resizing');
    this.highlight(CameraLayout.movement(this.drag.cells, this.drag.divider, event.ctrlKey));
  }

  resize(event) {
    if (!this.drag) { this.hover(event); return; }
    if (event.pointerId !== this.drag.pointer) return;
    const d = this.drag, vertical = d.divider.axis === 'x';
    d.lastEvent = event;
    d.ctrlKey = event.ctrlKey;
    const extent = vertical ? d.width : d.height;
    if (!extent) return;
    const delta = (vertical ? event.clientX - d.x : event.clientY - d.y) / extent;
    const movement = CameraLayout.movement(d.cells, d.divider, event.ctrlKey);
    if (!d.changed && Math.abs(delta) * extent < 3) { this.highlight(movement); return; }
    d.changed = true;
    const snapped = CameraLayout.snap(d.cells, movement, delta, Math.max(0.02, Math.min(0.1, 80 / extent)), 10 / extent);
    this.cells = snapped.cells;
    this.cells.forEach((cell, i) => this.position(this.tiles.get(cell.id) || this.empty.find(e => Number(e.dataset.slot) === i), cell));
    this.positionHandles();
    this.highlight(movement, snapped.target);
    // Synchronous persistence also covers closing the tab partway through a drag.
    this.save();
  }

  cancelResize() {
    if (!this.drag) return;
    const { pointer, handle } = this.drag;
    this.drag = null;
    this.grid.classList.remove('resizing');
    if (handle.hasPointerCapture(pointer)) handle.releasePointerCapture(pointer);
  }

  endResize(event) {
    if (!this.drag || event.pointerId !== this.drag.pointer) return;
    const changed = this.drag.changed;
    this.cancelResize();
    // Keep the clicked DOM node for a second click so the browser can emit dblclick.
    if (changed) {
      this.render();
      this.hover({ target: this.grid.ownerDocument?.elementFromPoint(event.clientX, event.clientY), ctrlKey: event.ctrlKey });
    } else this.hover(event);
  }

  hover(event) {
    if (this.drag) return;
    const handle = event.target?.closest?.('.camera-divider');
    this.hovered = this.handles.includes(handle) ? handle : null;
    this.ctrlKey = !!event.ctrlKey;
    this.highlight(this.hovered ? CameraLayout.movement(this.cells, this.hovered.divider, this.ctrlKey) : null);
  }

  modifierChanged(event) {
    if (event.key !== 'Control') return;
    this.ctrlKey = event.type === 'keydown';
    if (this.drag) {
      if (this.drag.lastEvent) {
        const last = this.drag.lastEvent;
        this.resize({ pointerId: last.pointerId, clientX: last.clientX, clientY: last.clientY, ctrlKey: this.ctrlKey });
      } else this.highlight(CameraLayout.movement(this.drag.cells, this.drag.divider, this.ctrlKey));
    } else if (this.hovered) this.hover({ target: this.hovered, ctrlKey: this.ctrlKey });
  }

  highlight(movement = null, target = null) {
    for (const handle of this.handles) {
      handle.classList.toggle('active', !!movement && CameraLayout.affects(movement, handle.divider));
      handle.classList.toggle('snap-target', !!target && handle.divider.axis === target.axis &&
        Math.abs(handle.divider.position - target.position) < 0.000001 && !CameraLayout.affects(movement, handle.divider));
    }
  }

  resetDivider(event) {
    const handle = event.target.closest('.camera-divider');
    if (!handle) return;
    event.preventDefault();
    const index = this.handles.indexOf(handle);
    this.cells = CameraLayout.resetAlignment(this.cells, handle.divider);
    this.save(); this.render(); this.handles[index]?.focus();
  }

  keyResize(event) {
    const handle = event.target.closest('.camera-divider');
    if (!handle) return;
    if (event.key === 'Enter') { this.resetDivider(event); return; }
    const keys = handle.divider.axis === 'x' ? ['ArrowLeft', 'ArrowRight'] : ['ArrowUp', 'ArrowDown'];
    if (!keys.includes(event.key)) return;
    event.preventDefault();
    const index = this.handles.indexOf(handle);
    this.cells = CameraLayout.move(this.cells, CameraLayout.movement(this.cells, handle.divider, event.ctrlKey), event.key === keys[0] ? -0.01 : 0.01, 0.04);
    this.save(); this.render(); this.handles[index]?.focus();
  }

  beginSwap(event) {
    const tile = event.target.closest('.tile');
    if (!tile || this.focus !== null || this.drag || event.target.closest('button')) { event.preventDefault(); return; }
    this.source = Number(tile.dataset.slot);
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', 'Swap camera');
    tile.classList.add('dragging-camera');
  }

  overSwap(event) {
    if (this.source === undefined) return;
    const target = event.target.closest('[data-slot]');
    if (!target) return;
    event.preventDefault(); event.dataTransfer.dropEffect = 'move';
    this.grid.querySelectorAll('.drop-target').forEach(tile => tile.classList.remove('drop-target'));
    target.classList.add('drop-target');
  }

  endSwap(event) {
    if (this.source === undefined) return;
    const target = event.target.closest('[data-slot]');
    if (target) {
      event.preventDefault();
      this.cells = CameraLayout.swap(this.cells, this.source, Number(target.dataset.slot));
      this.save();
    }
    this.clearSwap(); this.render();
  }

  clearSwap() {
    this.source = undefined;
    this.grid.querySelectorAll('.drop-target,.dragging-camera').forEach(tile => tile.classList.remove('drop-target', 'dragging-camera'));
  }
}
