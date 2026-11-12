'use strict';

// Owns the template form and persists policies independently of the recording form.
class DetectionTemplateSettingsDialog {
  constructor(request, saved) {
    this.request = request; this.saved = saved;
    this.el = id => document.getElementById(id);
    this.dialog = this.el('detection-template-dialog'); this.form = this.el('detection-template-form');
    this.editor = new DetectionTemplateEditor(request, () => {});
    this.openVersion = 0; this.saving = false;
    for (const id of ['detection-template-close', 'detection-template-cancel']) this.el(id).onclick = () => {
      if (!this.saving) this.dialog.close();
    };
    this.dialog.addEventListener('cancel', event => { if (this.saving) event.preventDefault(); });
    this.dialog.addEventListener('close', () => {
      this.openVersion++; this.editor.settings = null;
      this.returnFocus?.();
    });
    this.form.onsubmit = event => { event.preventDefault(); return this.save(); };
  }
  async open(templateId = null, context = null, returnFocus = () => this.el('menu-toggle').focus()) {
    if (this.saving || this.dialog.open) return;
    const version = ++this.openVersion;
    const [snapshot, catalog] = await Promise.all([this.request('recording'), this.request('recording/detection')]);
    if (version !== this.openVersion) return;
    if (context && context.configurationId !== snapshot.settings.configurationId) throw new Error('The camera configuration changed. Reopen Motion & recording settings.');
    if (templateId && !snapshot.settings.detectionTemplates.some(template => template.id === templateId)) throw new Error('This template is no longer available. Reopen Motion & recording settings.');
    this.configurationId = snapshot.settings.configurationId;
    this.baseline = JSON.stringify(snapshot.settings.detectionTemplates);
    this.returnFocus = returnFocus;
    this.editor.load(structuredClone(snapshot.settings), catalog, templateId, context?.cameras);
    this.el('detection-template-result').textContent = '';
    this.dialog.showModal(); this.el('detection-template-select').focus();
  }
  async save() {
    if (this.saving || !this.form.reportValidity()) return;
    const status = this.el('detection-template-result');
    if (this.editor.refreshing || this.editor.importing) {
      status.textContent = 'Wait for the model catalog refresh or template import to finish before saving.'; return;
    }
    this.editor.save();
    this.saving = true; for (const control of this.form.elements) control.disabled = true;
    try {
      const snapshot = await this.request('recording');
      if (snapshot.settings.configurationId !== this.configurationId) throw new Error('The camera configuration changed. Close and reopen template settings.');
      if (JSON.stringify(snapshot.settings.detectionTemplates) !== this.baseline) throw new Error('Templates changed elsewhere. Close and reopen template settings before saving.');
      // Use current saved camera settings, never the other form's unsaved draft.
      const settings = structuredClone(snapshot.settings);
      settings.detectionTemplates = structuredClone(this.editor.settings.detectionTemplates);
      status.textContent = 'Applying templates and finishing active clips…';
      await this.request('recording', settings, 'POST');
      this.saved(settings);
      this.dialog.close();
    } catch (error) { status.textContent = error.message || 'Could not reach the server.'; }
    finally {
      this.saving = false; for (const control of this.form.elements) control.disabled = false;
      if (this.dialog.open) { this.editor.renderMode(); this.editor.refreshUsage(); }
    }
  }
}
