'use strict';

const AiModelSettings = (() => {
  const name = value => value[0].toUpperCase() + value.slice(1);
  const optionValue = (camera, option) => option.id === 'confidence' ? camera.aiConfidence ?? option.default :
    option.id === 'framesPerSecond' ? camera.aiFramesPerSecond ?? option.default : camera.aiOptions?.[option.id] ?? option.default;
  function setOption(camera, id, value) {
    if (id === 'confidence') camera.aiConfidence = value;
    else if (id === 'framesPerSecond') camera.aiFramesPerSecond = value;
    else (camera.aiOptions ??= {})[id] = value;
  }
  function applyPreset(camera, model, preset) {
    camera.aiModel = model.id; camera.aiDevice = preset.device;
    camera.aiClasses = [...preset.triggerClasses]; camera.aiIgnoreClasses = [...preset.ignoreClasses]; camera.aiOptions = {};
    for (const option of model.options) setOption(camera, option.id, preset.options[option.id] ?? option.default);
  }
  function matchingPreset(camera, model) {
    const same = (a, b) => a.length === b.length && a.every(value => b.includes(value));
    return model.presets.find(preset => camera.aiDevice === preset.device &&
      same(camera.aiClasses || [], preset.triggerClasses) && same(camera.aiIgnoreClasses || [], preset.ignoreClasses) &&
      model.options.every(option => optionValue(camera, option) === (preset.options[option.id] ?? option.default)))?.id || 'custom';
  }
  return { name, optionValue, setOption, applyPreset, matchingPreset };
})();

// The view renders descriptors; it contains no model names, class indices or adapter-specific controls.
class AiModelSettingsView {
  constructor(preset, options, classes, search, status) {
    this.preset = preset; this.optionsRoot = options; this.classesRoot = classes; this.search = search; this.status = status;
    this.document = options.ownerDocument; this.inputs = new Map(); this.rows = []; this.enabled = true;
    preset.onchange = () => {
      if (preset.value === 'custom' || !this.model) return;
      const selected = this.model.presets.find(item => item.id === preset.value);
      if (selected) { AiModelSettings.applyPreset(this.camera, this.model, selected); this.load(this.camera, this.model); this.changed?.(); }
    };
    search.oninput = () => this.filter();
  }
  load(camera, model) {
    this.camera = camera; this.model = model; this.inputs.clear(); this.rows = []; this.optionsRoot.replaceChildren(); this.classesRoot.replaceChildren();
    this.preset.replaceChildren(); this.search.value = '';
    if (!model) {
      this.status.textContent = 'The saved model definition is unavailable. Refresh models or choose a registered model.';
      this.setEnabled(this.enabled); return;
    }
    camera.aiClasses ??= [...model.presets[0].triggerClasses];
    const custom = this.document.createElement('option'); custom.value = 'custom'; custom.textContent = 'Custom settings'; this.preset.append(custom);
    for (const preset of model.presets) {
      const option = this.document.createElement('option'); option.value = preset.id; option.textContent = preset.name; this.preset.append(option);
    }
    for (const option of model.options) {
      const label = this.document.createElement('label'); label.textContent = option.label;
      const input = this.document.createElement('input'); input.type = 'number';
      input.min = String(option.minimum); input.max = String(option.maximum); input.step = String(option.step);
      input.value = String(AiModelSettings.optionValue(camera, option)); input.setAttribute('aria-label', option.label);
      input.oninput = () => { AiModelSettings.setOption(camera, option.id, Number(input.value)); this.showPreset(); };
      const help = this.document.createElement('small'); help.className = 'help'; help.textContent = option.help;
      label.append(input, help); this.optionsRoot.append(label); this.inputs.set(option.id, input);
    }
    for (const object of model.classes) {
      const row = this.document.createElement('label'); row.className = 'ai-object-rule';
      const text = this.document.createElement('span'); text.textContent = AiModelSettings.name(object);
      const select = this.document.createElement('select'); select.setAttribute('aria-label', `Rule for ${AiModelSettings.name(object)}`);
      for (const [value, caption] of [['none', 'No action'], ['trigger', 'Trigger'], ['ignore', 'Ignore']]) {
        const option = this.document.createElement('option'); option.value = value; option.textContent = caption; select.append(option);
      }
      select.value = camera.aiIgnoreClasses?.includes(object) ? 'ignore' : camera.aiClasses.includes(object) ? 'trigger' : 'none';
      select.onchange = () => {
        camera.aiClasses = (camera.aiClasses || []).filter(value => value !== object);
        camera.aiIgnoreClasses = (camera.aiIgnoreClasses || []).filter(value => value !== object);
        if (select.value === 'trigger') camera.aiClasses.push(object);
        if (select.value === 'ignore') camera.aiIgnoreClasses.push(object);
        this.showPreset(); this.showStatus();
      };
      row.append(text, select); this.classesRoot.append(row); this.rows.push({ row, select, object });
    }
    this.showPreset(); this.showStatus(); this.setEnabled(this.enabled);
  }
  save() { if (this.model) for (const [id, input] of this.inputs) AiModelSettings.setOption(this.camera, id, Number(input.value)); }
  showPreset() { if (this.model) this.preset.value = AiModelSettings.matchingPreset(this.camera, this.model); }
  showStatus() {
    const selected = [...(this.camera.aiClasses || []), ...(this.camera.aiIgnoreClasses || [])];
    const missing = selected.filter(value => !this.model.classes.includes(value));
    this.status.textContent = missing.length ? `Saved object rules are unavailable in this model: ${missing.join(', ')}. Choose a preset to replace them.` :
      `${this.camera.aiClasses?.length || 0} trigger, ${this.camera.aiIgnoreClasses?.length || 0} ignore, ${this.model.classes.length} supported classes. Ignored objects do not suppress other objects.`;
  }
  filter() {
    const query = this.search.value.trim().toLocaleLowerCase();
    for (const { row, object } of this.rows) row.hidden = !object.toLocaleLowerCase().includes(query);
  }
  setEnabled(value) {
    this.enabled = value;
    this.preset.disabled = !value || !this.model; this.search.disabled = !value || !this.model;
    for (const input of this.inputs.values()) { input.disabled = !value; input.required = value; }
    for (const { select } of this.rows) select.disabled = !value;
  }
}
