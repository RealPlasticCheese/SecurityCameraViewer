'use strict';

// One browser preference controls overlay opacity without changing camera pixels.
(() => {
  const slider = document.getElementById('overlay-transparency');
  const output = document.getElementById('overlay-transparency-value');
  const storageKey = 'scv.overlay-transparency';

  function apply(value) {
    const number = Number(value);
    const transparency = Number.isFinite(number) ? Math.max(0, Math.min(100, number)) : 0;
    slider.value = String(transparency);
    output.value = `${transparency}%`;
    slider.setAttribute('aria-valuetext', `${transparency}% transparent`);
    document.documentElement.style.setProperty('--camera-overlay-opacity', String(1 - transparency / 100));
  }

  let stored = 0;
  try { stored = localStorage.getItem(storageKey); } catch {}
  apply(stored);
  slider.addEventListener('input', () => {
    apply(slider.value);
    try { localStorage.setItem(storageKey, slider.value); } catch {}
  });
})();
