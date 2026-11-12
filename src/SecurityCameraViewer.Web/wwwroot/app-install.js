'use strict';
(() => {
  const controls = document.getElementById('app-install-controls');
  const button = document.getElementById('install-app');
  const status = document.getElementById('app-install-status');
  const displayMode = window.matchMedia('(display-mode: standalone)');
  let installPrompt = null, prompting = false, installed = false;

  function isInstalled() {
    return installed || displayMode.matches || Boolean(navigator.standalone);
  }

  function render(message = '') {
    button.hidden = !window.isSecureContext || !installPrompt || isInstalled() || prompting;
    status.textContent = message;
    status.hidden = !message;
    controls.hidden = button.hidden && status.hidden;
  }

  window.addEventListener('beforeinstallprompt', event => {
    event.preventDefault();
    if (!window.isSecureContext || isInstalled()) return;
    installPrompt = event;
    render();
  });

  window.addEventListener('appinstalled', () => {
    installed = true;
    installPrompt = null;
    render('Security Camera Viewer is installed.');
  });

  displayMode.addEventListener('change', () => {
    if (isInstalled()) installPrompt = null;
    render();
  });

  button.addEventListener('click', async () => {
    if (!installPrompt || isInstalled() || prompting) return;
    const prompt = installPrompt;
    installPrompt = null;
    prompting = true;
    render();
    try {
      // Call within the click handler to preserve the browser's user gesture.
      const result = await prompt.prompt();
      const choice = result ?? (prompt.userChoice ? await prompt.userChoice : null);
      if (isInstalled()) render('Security Camera Viewer is installed.');
      else if (choice?.outcome === 'accepted') render('Install started. Finish the browser prompt to add the app to your device.');
      else if (choice?.outcome === 'dismissed') render('Install dismissed. You can also install from your browser menu.');
    } catch {
      render(isInstalled() ? 'Security Camera Viewer is installed.' : 'Use your browser menu to install this site. On Android, choose Install app or Add to Home screen.');
    } finally {
      prompting = false;
      render(status.textContent);
    }
  });

  render();
})();
