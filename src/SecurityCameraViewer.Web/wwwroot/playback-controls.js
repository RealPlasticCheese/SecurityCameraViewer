'use strict';

function formatPlaybackDelay(secondsBehind) {
  const seconds = Number.isFinite(secondsBehind) ? Math.max(0, Math.round(secondsBehind)) : 0;
  const minutes = Math.floor(seconds / 60);
  return minutes ? `${minutes}m ${seconds % 60}s` : `${seconds}s`;
}

function formatPlaybackState(paused, rate = 1) {
  return `${paused ? 'PAUSED' : rate < 0 ? 'REVERSE' : 'PLAYBACK'} · ${paused ? 0 : rate}×`;
}

// Keep playback above the bottom-row camera footers without resizing the pictures.
class PlaybackControlsLayout {
  constructor(grid, root, footerMarginPixels = 4) {
    this.grid = grid;
    this.root = root;
    this.footerMarginPixels = footerMarginPixels;
    this.offset = 0;
    this.observed = [];
    this.abort = new AbortController();
    this.resizeObserver = new ResizeObserver(() => this.scheduleRefresh());
    this.mutationObserver = new MutationObserver(() => this.scheduleRefresh());
    window.addEventListener('resize', () => this.scheduleRefresh(), { signal: this.abort.signal });
    this.refresh();
  }

  refresh() {
    const tiles = [...this.grid.querySelectorAll('.tile')];
    const headers = tiles.map(tile => tile.querySelector('.tile-header')).filter(Boolean);
    const observed = [this.grid, ...tiles, ...headers];
    if (observed.length !== this.observed.length || observed.some((element, index) => element !== this.observed[index])) {
      this.resizeObserver.disconnect();
      this.mutationObserver.disconnect();
      for (const element of observed) this.resizeObserver.observe(element);
      this.mutationObserver.observe(this.grid, { childList: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'] });
      for (const tile of tiles) this.mutationObserver.observe(tile, { attributes: true, attributeFilter: ['class', 'style', 'hidden'] });
      this.observed = observed;
    }

    const gridBottom = this.grid.getBoundingClientRect().bottom;
    let footerTop = window.innerHeight;
    for (const tile of tiles) {
      const bounds = tile.getBoundingClientRect();
      // Allow subpixel rounding at the grid edge; hidden and upper-row tiles do not reserve space.
      if (!bounds.width || !bounds.height || Math.abs(bounds.bottom - gridBottom) > 1 ||
          bounds.bottom <= 0 || bounds.top >= window.innerHeight) continue;
      const header = tile.querySelector('.tile-header')?.getBoundingClientRect();
      if (!header?.width || !header.height || header.top >= window.innerHeight) continue;
      footerTop = Math.min(footerTop, Math.max(0, bounds.top, header.top));
    }
    const offset = footerTop < window.innerHeight
      ? Math.min(window.innerHeight, window.innerHeight - footerTop + this.footerMarginPixels) : 0;
    this.offset = offset;
    this.root.style.setProperty('--playback-bottom-offset', `${offset}px`);
  }

  scheduleRefresh() {
    if (this.disposed || this.frame != null) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = null;
      this.refresh();
    });
  }

  dispose() {
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.resizeObserver.disconnect();
    this.mutationObserver.disconnect();
    this.abort.abort();
  }
}

// Presentation only: hiding the drawer never changes the playback clock or players.
class PlaybackControlsView {
  constructor(elements, goLive, { autoHideMilliseconds = 3000 } = {}) {
    Object.assign(this, elements);
    this.autoHideMilliseconds = autoHideMilliseconds;
    this.tabRevealed = false;
    this.tabHovered = false;
    this.tabTouched = false;
    this.available = false;
    this.open = false;
    this.pinned = false;
    this.hovered = false;
    this.touchOpened = false;
    this.pointers = new Set();
    this.revealPointers = new Set();
    this.layout = new PlaybackControlsLayout(this.focusTarget, this.root);
    this.abort = new AbortController();
    const options = { signal: this.abort.signal };

    this.root.addEventListener('focusin', event => {
      if (event.target !== this.root) return;
      this.show();
      this.bar.querySelector('button:not(:disabled), input:not(:disabled), select:not(:disabled)')?.focus();
    }, options);
    this.tab.addEventListener('focusin', () => this.revealTab(), options);
    this.tab.addEventListener('focusout', () => this.scheduleHide(), options);
    this.tab.addEventListener('pointerdown', event => { this.tabTouched = event.pointerType === 'touch'; }, options);
    this.tab.addEventListener('pointercancel', () => { this.tabTouched = false; }, options);
    this.tab.addEventListener('click', event => {
      const touch = event.pointerType === 'touch' || event.sourceCapabilities?.firesTouchEvents || (event.detail > 0 && this.tabTouched);
      this.tabTouched = false;
      this.show(touch);
      if (event.detail === 0 && this.open) {
        this.bar.querySelector('button:not(:disabled), input:not(:disabled), select:not(:disabled)')?.focus();
      }
    }, options);
    this.tabArea.addEventListener('pointerenter', event => {
      if (event.pointerType !== 'mouse' || event.buttons || this.open || !this.canReveal()) return;
      this.tabHovered = true;
      this.revealTab();
    }, options);
    this.tabArea.addEventListener('pointerleave', event => {
      if (event.pointerType !== 'mouse') return;
      this.tabHovered = false;
      if (!this.open && !this.tab.matches(':focus-visible')) this.close();
      else this.scheduleHide();
    }, options);
    this.pin.addEventListener('click', () => {
      this.pinned = !this.pinned;
      this.render();
      this.scheduleHide();
    }, options);
    this.compactLive.addEventListener('click', goLive, options);
    this.bar.addEventListener('pointerenter', event => {
      if (event.pointerType === 'touch') return;
      this.hovered = true;
      this.scheduleHide();
    }, options);
    this.bar.addEventListener('pointerleave', () => {
      this.hovered = false;
      this.scheduleHide();
    }, options);
    this.bar.addEventListener('pointerdown', event => {
      this.pointers.add(event.pointerId);
      this.render();
      this.scheduleHide();
    }, options);
    for (const name of ['pointerup', 'pointercancel']) {
      window.addEventListener(name, event => {
        if (this.pointers.delete(event.pointerId)) { this.render(); this.scheduleHide(); }
      }, options);
    }
    this.bar.addEventListener('lostpointercapture', event => {
      if (this.pointers.delete(event.pointerId)) { this.render(); this.scheduleHide(); }
    }, options);
    this.bar.addEventListener('focusin', () => this.scheduleHide(), options);
    this.bar.addEventListener('focusout', event => {
      if (!this.bar.contains(event.relatedTarget)) this.scheduleHide(false);
    }, options);
    for (const name of ['input', 'change', 'keydown']) {
      this.bar.addEventListener(name, () => this.scheduleHide(), options);
    }
    this.root.addEventListener('keydown', event => {
      if (event.key !== 'Escape' || (!this.open && !this.tabRevealed)) return;
      event.preventDefault();
      event.stopPropagation();
      this.close();
    }, options);
    this.root.addEventListener('dblclick', event => event.stopPropagation(), options);
    document.addEventListener('pointermove', event => {
      if (event.pointerType === 'mouse' && this.touchOpened) { this.touchOpened = false; this.render(); }
    }, options);
    const captureOptions = { ...options, capture: true };
    document.addEventListener('pointerdown', event => {
      // A new gesture must not inherit a cancelled or click-less reveal gesture.
      for (const pointerId of this.revealPointers) {
        if (!this.pointers.has(pointerId)) this.revealPointers.delete(pointerId);
      }
    }, captureOptions);
    this.tabArea.addEventListener('pointerdown', event => {
      if (event.pointerType !== 'touch' || this.open || !this.canReveal() ||
          (this.tabRevealed && this.tab.contains(event.target))) return;
      // The first touch in the vicinity reveals only the tab, including its compatibility click.
      event.preventDefault();
      event.stopImmediatePropagation();
      this.revealPointers.add(event.pointerId);
      this.pointers.add(event.pointerId);
      this.revealTab();
    }, captureOptions);
    for (const name of ['pointermove', 'pointerup', 'pointercancel', 'click']) {
      document.addEventListener(name, event => {
        // Older browsers deliver the compatibility click as a MouseEvent without a pointer ID.
        const legacyTouchClick = name === 'click' && event.pointerId == null && event.detail > 0 && this.revealPointers.size > 0;
        if (!this.revealPointers.has(event.pointerId) && !legacyTouchClick) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        if (name === 'pointerup' || name === 'pointercancel') {
          this.pointers.delete(event.pointerId);
          this.render();
          this.scheduleHide();
        }
        if (name === 'pointercancel' || name === 'click') {
          if (legacyTouchClick) this.revealPointers.clear();
          else this.revealPointers.delete(event.pointerId);
        }
      }, captureOptions);
    }
    window.addEventListener('blur', () => this.suspend(), options);
    window.addEventListener('pagehide', () => this.suspend(), options);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.suspend();
    }, options);
    this.render();
  }

  update({ available, historical, paused, rate = 1, secondsBehind }) {
    this.available = !!available;
    this.historical = !!historical;
    if (!historical && document.activeElement === this.compactLive) this.focusTarget.focus({ preventScroll: true });
    this.compactStatus.hidden = this.compactLive.hidden = !historical;
    this.compactStatus.textContent = historical
      ? `${formatPlaybackState(paused, rate)} · ${formatPlaybackDelay(secondsBehind)} behind live` : '';
    if (!this.available) {
      if (this.root.contains(document.activeElement)) document.activeElement.blur();
      this.open = this.pinned = this.hovered = this.touchOpened = this.tabRevealed = this.tabHovered = this.tabTouched = false;
      this.pointers.clear();
      this.revealPointers.clear();
      this.cancelHide();
    }
    this.render();
  }

  canReveal() {
    return this.available && !document.hidden && !document.querySelector('dialog[open]');
  }

  revealTab() {
    if (!this.canReveal() || this.open) return;
    this.layout.refresh();
    this.tabRevealed = true;
    this.render();
    this.scheduleHide();
  }

  show(touch = false) {
    if (!this.canReveal()) return;
    this.layout.refresh();
    if (document.activeElement === this.compactLive || document.activeElement === this.tab) this.focusTarget.focus({ preventScroll: true });
    this.open = true;
    this.tabRevealed = this.tabHovered = this.tabTouched = false;
    this.touchOpened ||= touch;
    this.render();
    this.scheduleHide();
  }

  close() {
    if (this.root.contains(document.activeElement)) this.focusTarget.focus({ preventScroll: true });
    this.open = this.pinned = this.hovered = this.touchOpened = this.tabRevealed = this.tabHovered = this.tabTouched = false;
    this.pointers.clear();
    this.revealPointers.clear();
    this.cancelHide();
    this.render();
  }

  suspend() {
    this.hovered = false;
    this.pointers.clear();
    this.close();
  }

  cancelHide() {
    clearTimeout(this.hideTimer);
    this.hideTimer = null;
  }

  scheduleHide(focused = this.root.contains(document.activeElement) && document.activeElement.matches(':focus-visible')) {
    this.cancelHide();
    if ((!this.open && !this.tabRevealed) || this.pinned || this.hovered || this.tabHovered || focused || this.pointers.size) return;
    this.hideTimer = setTimeout(() => this.close(), this.autoHideMilliseconds);
  }

  render() {
    this.root.hidden = !this.available;
    this.root.classList.toggle('is-open', this.open);
    this.root.classList.toggle('is-tab-revealed', this.tabRevealed);
    this.tabArea.hidden = !this.available || this.open;
    this.tab.setAttribute('aria-expanded', String(this.open));
    this.dock.hidden = !this.historical || this.open;
    this.bar.classList.toggle('is-interacting', this.touchOpened || this.pointers.size > 0);
    this.bar.inert = !this.open;
    this.bar.setAttribute('aria-hidden', String(!this.open));
    this.pin.setAttribute('aria-pressed', String(this.pinned));
    this.pin.setAttribute('aria-label', this.pinned ? 'Unpin playback controls' : 'Pin playback controls');
    this.pin.title = this.pinned ? 'Allow playback controls to hide' : 'Keep playback controls open';
  }

  dispose() {
    this.suspend();
    this.layout.dispose();
    this.abort.abort();
    this.available = false;
    this.render();
  }
}
