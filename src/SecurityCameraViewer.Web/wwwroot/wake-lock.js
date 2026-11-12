'use strict';
(() => {
  const button = document.getElementById('keep-awake');
  const status = document.getElementById('wake-lock-status');
  const supported = window.isSecureContext && typeof navigator.wakeLock?.request === 'function';
  let enabled = false, sentinel = null, requesting = false, releasing = false;
  let revision = 0, pageActive = true, failure = '';

  function render() {
    const active = sentinel && !sentinel.released;
    button.disabled = !supported || releasing;
    button.setAttribute('aria-pressed', String(enabled));
    button.textContent = `Keep screen awake: ${!supported ? 'Unavailable' : releasing ? 'Turning off…' : !enabled ? 'Off' : active ? 'On' : requesting ? 'Starting…' : 'Paused'}`;
    status.textContent = !window.isSecureContext
      ? 'Requires HTTPS or localhost. Open this viewer over HTTPS to keep a remote device awake.'
      : !supported ? 'This browser does not support keeping the screen awake.'
      : failure || (active ? 'Keeping this screen awake while the viewer is visible.'
        : !enabled ? 'Allow the screen to sleep normally.'
        : requesting ? 'Requesting permission to keep this screen awake…'
        : 'Screen wake lock paused. It will be requested again when you return to this tab.');
  }

  async function acquire() {
    if (!supported || !enabled || requesting || releasing || sentinel || !pageActive || document.visibilityState !== 'visible') return;
    const requestRevision = ++revision;
    requesting = true;
    failure = '';
    render();
    try {
      const acquired = await navigator.wakeLock.request('screen');
      // A request can finish after the user turns the toggle off or leaves the page.
      if (requestRevision !== revision || !enabled || !pageActive || document.visibilityState !== 'visible') {
        await acquired.release();
        return;
      }
      sentinel = acquired.released ? null : acquired;
      acquired.addEventListener('release', () => {
        if (sentinel !== acquired) return;
        sentinel = null;
        render();
      });
    } catch {
      if (requestRevision === revision) {
        enabled = false;
        failure = 'The browser could not keep the screen awake. Check its permissions or power-saving settings, then try again.';
      }
    } finally {
      if (requestRevision === revision) {
        requesting = false;
        render();
      }
    }
  }

  async function release() {
    ++revision;
    requesting = false;
    const acquired = sentinel;
    if (!acquired) { render(); return; }
    releasing = true;
    render();
    try {
      await acquired.release();
      if (sentinel === acquired) sentinel = null;
    } catch {
      enabled = !acquired.released;
      failure = 'The browser could not release the screen wake lock. Try turning it off again or close this tab.';
    } finally {
      releasing = false;
      render();
      void acquire();
    }
  }

  button.addEventListener('click', () => {
    if (!supported || releasing) return;
    enabled = !enabled;
    failure = '';
    if (enabled) { render(); void acquire(); }
    else void release();
  });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') void acquire();
    else void release();
  });
  window.addEventListener('pagehide', () => {
    pageActive = false;
    void release();
  });
  window.addEventListener('pageshow', () => {
    pageActive = true;
    void acquire();
  });
  render();
})();
