'use strict';

// Playback filter controls own presentation and preset requests; the library owns playback and paging.
class RecordingFilters {
  constructor(request, changed, reportError) {
    this.request = request;
    this.changed = changed;
    this.reportError = reportError;
    this.selectedZones = new Map();
    this.saved = [];
    this.groups = [];
    this.configurationId = null;
    this.saving = false;
    this.el = id => document.getElementById(id);
    this.el('recordings-camera').onchange = () => this.change();
    this.el('recordings-date').onchange = () => this.change();
    this.el('recordings-replay-only').onchange = () => this.change();
    this.el('recordings-clear').onclick = () => this.clear();
    this.el('recordings-saved-filter').onchange = event => {
      const saved = this.saved.find(filter => filter.id === event.target.value);
      if (saved) { this.apply(saved.filter); this.changed(); }
    };
    this.el('recordings-save-filter').onclick = () => this.openSave();
    this.el('recordings-save-target').onchange = () => this.updateSaveTarget();
    this.el('recordings-filter-form').onsubmit = event => { event.preventDefault(); this.save(); };
    this.el('recordings-filter-save-dialog').addEventListener('cancel', event => { if (this.saving) event.preventDefault(); });
    this.el('recordings-filter-save-dialog').addEventListener('close', () => this.el('recordings-save-filter').focus());
  }

  static key(zone) { return `${zone.channel}:${zone.id}`; }

  snapshot() {
    return {
      channel: this.el('recordings-camera').value ? Number(this.el('recordings-camera').value) : null,
      date: this.el('recordings-date').value || null,
      ...(this.el('recordings-replay-only').checked ? { savedReplayOnly: true } : {}),
      zones: [...this.selectedZones.values()].map(zone => ({ ...zone }))
    };
  }

  async initialize(viewer, recording) {
    this.viewerSettings = viewer.settings;
    const configurationId = recording.settings.configurationId;
    if (this.configurationId !== null && this.configurationId !== configurationId) this.apply({});
    this.configurationId = configurationId;
    const [recordedGroups, saved] = await Promise.all([this.request('recordings/zones'), this.request('recordings/filters')]);
    this.saved = saved;
    const groups = new Map();
    const group = (channel, name) => {
      if (!groups.has(channel)) groups.set(channel, { channel, cameraName: name || `Camera ${channel}`, zones: new Map() });
      return groups.get(channel);
    };
    for (const camera of recordedGroups) {
      const entry = group(camera.channel, camera.cameraName);
      for (const zone of camera.zones) entry.zones.set(zone.id, { ...zone, recorded: true });
    }
    for (const camera of recording.settings.cameras) {
      const name = CameraCollection.entries(viewer.settings).find(entry => entry.source.channel === camera.channel)?.source.name;
      const entry = group(camera.channel, name);
      entry.cameraName = name || entry.cameraName;
      for (const zone of camera.zones) if (!zone.exclude) entry.zones.set(zone.id, { id: zone.id, name: zone.name });
    }
    this.groups = [...groups.values()].map(entry => ({ ...entry, zones: [...entry.zones.values()] }));
    this.renderSaved(this.el('recordings-saved-filter').value);
    this.renderZones();
  }

  apply(filter) {
    const camera = this.el('recordings-camera');
    if (filter.channel && ![...camera.options].some(option => option.value === String(filter.channel))) {
      camera.add(new Option(`Camera ${filter.channel} (saved)`, String(filter.channel)));
    }
    camera.value = filter.channel ? String(filter.channel) : '';
    this.el('recordings-date').value = filter.date || '';
    this.el('recordings-replay-only').checked = !!filter.savedReplayOnly;
    this.selectedZones = new Map((filter.zones || []).map(zone => [RecordingFilters.key(zone), { ...zone }]));
    this.renderZones();
  }

  change() {
    this.el('recordings-saved-filter').value = '';
    this.changed();
  }

  clear() {
    this.apply({});
    this.el('recordings-zone-picker').open = false;
    this.change();
  }

  renderZones() {
    const container = this.el('recordings-zone-tree');
    container.replaceChildren();
    // Preserve saved selections even when a zone or camera has since been removed.
    const groups = new Map(this.groups.map(group => [group.channel, { ...group, zones: [...group.zones] }]));
    for (const zone of this.selectedZones.values()) {
      if (!groups.has(zone.channel)) groups.set(zone.channel, { channel: zone.channel, cameraName: `Camera ${zone.channel}`, zones: [] });
      const group = groups.get(zone.channel);
      if (!group.zones.some(item => item.id === zone.id)) group.zones.push({ ...zone, saved: true });
    }
    const position = channel => this.viewerSettings ? CameraCollection.number(this.viewerSettings, channel) ?? Infinity : channel;
    for (const group of [...groups.values()].sort((a, b) => position(a.channel) - position(b.channel) || a.channel - b.channel)) {
      if (!group.zones.length) continue;
      const branch = document.createElement('details');
      branch.className = 'recording-zone-camera'; branch.open = true;
      const summary = document.createElement('summary'); summary.textContent = this.viewerSettings
        ? CameraCollection.label(this.viewerSettings, group.channel, group.cameraName) : group.cameraName;
      const children = document.createElement('div'); children.className = 'recording-zone-children';
      const choices = group.zones.map(zone => ({ channel: group.channel, id: zone.id, name: zone.name }));
      const count = choices.filter(zone => this.selectedZones.has(RecordingFilters.key(zone))).length;
      const all = this.checkbox('Select all zones', count === choices.length, checked => {
        for (const zone of choices) this.select(zone, checked);
        this.updateZoneChecks(container); this.change();
      });
      all.input.indeterminate = count > 0 && count < choices.length;
      all.input.dataset.camera = String(group.channel);
      children.append(all.label);
      for (const zone of group.zones.sort((a, b) => a.name.localeCompare(b.name))) {
        const selection = { channel: group.channel, id: zone.id, name: zone.name };
        const key = RecordingFilters.key(selection);
        const choice = this.checkbox(`${zone.name}${zone.saved ? ' (saved zone)' : zone.recorded ? ' (recorded zone)' : ''}`,
          this.selectedZones.has(key), checked => {
            this.select(selection, checked); this.updateZoneChecks(container); this.change();
          });
        choice.input.dataset.zone = key; choice.input.dataset.channel = String(group.channel);
        children.append(choice.label);
      }
      branch.append(summary, children); container.append(branch);
    }
    if (!container.children.length) {
      const empty = document.createElement('p'); empty.className = 'help'; empty.textContent = 'No detection zones are configured or recorded yet.';
      container.append(empty);
    }
    this.updateZoneSummary();
  }

  checkbox(text, checked, change) {
    const label = document.createElement('label'); label.className = 'check';
    const input = document.createElement('input'); input.type = 'checkbox'; input.checked = checked;
    input.onchange = () => change(input.checked);
    label.append(input, text); return { label, input };
  }

  select(zone, checked) {
    const key = RecordingFilters.key(zone);
    if (checked) this.selectedZones.set(key, zone); else this.selectedZones.delete(key);
  }

  updateZoneChecks(container) {
    for (const input of container.querySelectorAll('[data-zone]')) input.checked = this.selectedZones.has(input.dataset.zone);
    for (const input of container.querySelectorAll('[data-camera]')) {
      const children = [...container.querySelectorAll('[data-zone]')].filter(child => child.dataset.channel === input.dataset.camera);
      const count = children.filter(child => child.checked).length;
      input.checked = count === children.length; input.indeterminate = count > 0 && count < children.length;
    }
    this.updateZoneSummary();
  }

  updateZoneSummary() {
    const count = this.selectedZones.size;
    this.el('recordings-zone-summary').textContent = count ? `Triggered zones: ${count} selected` : 'Triggered zones: All';
  }

  renderSaved(selected = '') {
    const select = this.el('recordings-saved-filter');
    select.replaceChildren(new Option('Choose a saved filter', ''));
    for (const filter of [...this.saved].sort((a, b) => a.name.localeCompare(b.name))) select.add(new Option(filter.name, filter.id));
    select.value = selected;
  }

  async openSave() {
    try {
      this.saved = await this.request('recordings/filters');
      const select = this.el('recordings-save-target');
      select.replaceChildren(new Option('Create a new filter', ''));
      for (const filter of [...this.saved].sort((a, b) => a.name.localeCompare(b.name))) select.add(new Option(`Replace: ${filter.name}`, filter.id));
      select.value = this.el('recordings-saved-filter').value;
      this.el('recordings-filter-name').value = '';
      this.el('recordings-filter-save-result').textContent = '';
      this.updateSaveTarget();
      this.el('recordings-filter-save-dialog').showModal();
    } catch (error) { this.reportError(error); }
  }

  updateSaveTarget() {
    const existing = !!this.el('recordings-save-target').value;
    const name = this.el('recordings-filter-name'); name.disabled = existing; name.required = !existing;
    this.el('recordings-filter-name-label').hidden = existing;
  }

  async save() {
    const form = this.el('recordings-filter-form');
    if (this.saving || !form.reportValidity()) return;
    const id = this.el('recordings-save-target').value || null;
    const name = this.el('recordings-filter-name').value.trim();
    if (!id && !name) { this.el('recordings-filter-save-result').textContent = 'Enter a filter name.'; return; }
    this.saving = true;
    for (const control of form.elements) control.disabled = true;
    try {
      const saved = await this.request('recordings/filters', { id, name: id ? null : name, filter: this.snapshot() }, 'POST');
      this.saved = [...this.saved.filter(filter => filter.id !== saved.id), saved];
      this.renderSaved(saved.id);
      this.el('recordings-filter-save-dialog').close();
      this.el('recordings-result').textContent = `Filter “${saved.name}” saved.`;
    } catch (error) { this.el('recordings-filter-save-result').textContent = error.message || 'Could not save the filter.'; }
    finally { this.saving = false; for (const control of form.elements) control.disabled = false; this.updateSaveTarget(); }
  }
}
