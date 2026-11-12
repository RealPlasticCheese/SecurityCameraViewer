'use strict';

// Visibility is presentation state. Revealing a saved value never dispatches an
// input event or changes the draft's credentials.
class PasswordVisibility {
  static attach(input, loadPassword, onError = () => {}) {
    if (input.passwordVisibility) return input.passwordVisibility;
    const label = document.createElement('label'); label.className = 'check password-visibility';
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox';
    checkbox.setAttribute('aria-label', `Show password for ${input.getAttribute('aria-label') || input.closest('label')?.textContent || 'password field'}`);
    input.dataset.secret = 'true';
    label.append(checkbox, 'Show password');
    const parent = input.closest('label') || input;
    const wrapper = document.createElement('div'); wrapper.className = 'password-field';
    parent.replaceWith(wrapper); wrapper.append(parent, label);
    let revision = 0;
    const control = {
      reset() { revision++; checkbox.checked = false; input.type = 'password'; },
      setLoader(loader) { loadPassword = loader; }
    };
    input.passwordVisibility = control;
    input.closest('dialog')?.addEventListener('close', () => { control.reset(); input.value = ''; });
    checkbox.onchange = async () => {
      const currentRevision = ++revision;
      input.type = checkbox.checked ? 'text' : 'password';
      if (!checkbox.checked || input.value || !loadPassword) return;
      checkbox.disabled = true;
      try {
        const value = await loadPassword();
        if (revision === currentRevision && input.isConnected && checkbox.checked && !input.value) input.value = value ?? '';
      } catch (error) {
        if (revision === currentRevision) { control.reset(); onError(error); }
      } finally { checkbox.disabled = false; }
    };
    return control;
  }
}

document.querySelectorAll('input[type=password]').forEach(input => PasswordVisibility.attach(input));
