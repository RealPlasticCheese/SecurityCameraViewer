'use strict';

// Shared detection policy editor; camera capture and zone geometry remain in recording.js.
class DetectionTemplateEditor {
  constructor(request, assignmentsChanged) {
    this.request = request; this.assignmentsChanged = assignmentsChanged;
    this.el = id => document.getElementById(id);
    this.ai = new AiModelSettingsView(this.el('ai-model-preset'), this.el('ai-model-options'), this.el('ai-classes'), this.el('ai-class-search'), this.el('ai-rule-status'));
    this.ai.changed = () => { this.el('ai-device').value = this.current().settings.aiDevice; this.renderMode(); };
    this.el('detection-template-select').onchange = () => { this.save(); this.selected = this.el('detection-template-select').value; this.render(); };
    this.el('detection-template-name').oninput = () => {
      this.current().name = this.el('detection-template-name').value;
      this.renderNames(); this.assignmentsChanged();
    };
    this.el('detection-template-add').onclick = () => this.add(false);
    this.el('detection-template-clone').onclick = () => this.add(true);
    this.el('detection-template-delete').onclick = () => {
      if (this.usage().length || this.settings.detectionTemplates.length <= 1) return;
      this.settings.detectionTemplates = this.settings.detectionTemplates.filter(template => template.id !== this.selected);
      this.selected = this.settings.detectionTemplates[0].id; this.render(); this.assignmentsChanged();
    };
    this.el('detection-method').onchange = () => this.renderMode();
    this.el('ai-model').onchange = () => {
      this.save();
      const model = this.catalog.models.find(item => item.id === this.el('ai-model').value);
      if (model) AiModelSettings.applyPreset(this.current().settings, model, model.presets[0]);
      this.render();
    };
    this.el('ai-device').onchange = () => { this.current().settings.aiDevice = this.el('ai-device').value; this.ai.showPreset(); this.renderMode(); };
    this.el('ai-model-refresh').onclick = async () => {
      if (this.refreshing) return;
      const settings = this.settings;
      this.save(); this.refreshing = true; this.el('ai-model-refresh').disabled = true;
      try {
        const catalog = await request('recording/detection/reload', undefined, 'POST');
        if (settings === this.settings) { this.catalog = catalog; this.render(); }
      }
      catch (error) { if (settings === this.settings) this.el('ai-model-notices').textContent = error.message; }
      finally { if (settings === this.settings) { this.refreshing = false; this.el('ai-model-refresh').disabled = false; } }
    };
    this.el('adaptive-learn-noise').onchange = () => this.renderMode();
    this.el('adaptive-shadows').onchange = () => this.renderMode();
    this.el('adaptive-options').addEventListener('invalid', () => { this.el('adaptive-options').open = true; }, true);
    this.el('detection-template-install').onclick = () => {
      if (this.settings.detectionTemplates.length >= 64) return;
      this.save();
      const starter = this.catalog.templates.find(template => template.id === this.el('detection-template-starter').value);
      if (!starter) return;
      const template = { ...structuredClone(starter), id: DetectionTemplateFile.createId() };
      this.settings.detectionTemplates.push(template); this.selected = template.id; this.render(); this.assignmentsChanged();
    };
    this.el('detection-template-export').onclick = () => this.export(false);
    this.el('detection-template-export-all').onclick = () => this.export(true);
    this.el('detection-template-import').onchange = async () => {
      const file = this.el('detection-template-import').files?.[0];
      if (!file || this.importing) return;
      const settings = this.settings;
      this.importing = true; this.el('detection-template-import').disabled = true;
      try {
        if (file.size > 8 * 1024 * 1024) throw new Error('Template files must be at most 8 MB.');
        const contents = await file.text();
        if (settings !== this.settings) return;
        const imported = DetectionTemplateFile.read(JSON.parse(contents), this.catalog);
        if (this.settings.detectionTemplates.length + imported.length > 64) throw new Error('A configuration supports up to 64 templates.');
        this.save(); this.settings.detectionTemplates.push(...imported); this.selected = imported[0].id;
        this.render(); this.assignmentsChanged();
        this.el('detection-template-file-status').textContent = `Imported ${imported.length} templates. Save templates to make them available for assignment.`;
      } catch (error) {
        if (settings === this.settings) this.el('detection-template-file-status').textContent = `Import failed: ${error.message}`;
      } finally {
        if (settings === this.settings) {
          this.importing = false; this.el('detection-template-import').disabled = false; this.el('detection-template-import').value = '';
        }
      }
    };
  }
  current() { return this.settings.detectionTemplates.find(template => template.id === this.selected); }
  load(settings, catalog, templateId = null, pendingCameras = []) {
    this.settings = settings; this.catalog = catalog; settings.detectionTemplates ??= [];
    this.pendingCameras = pendingCameras || [];
    this.refreshing = false; this.el('ai-model-refresh').disabled = false;
    this.importing = false; this.el('detection-template-import').disabled = false;
    if (!settings.detectionTemplates.length) {
      settings.detectionTemplates.push({ id: DetectionTemplateFile.createId(), name: 'Default detection', settings: {} });
    }
    this.selected = templateId || settings.detectionTemplates[0].id;
    this.el('detection-template-starter').replaceChildren(...(catalog.templates || []).map(template => new Option(template.name, template.id)));
    this.el('detection-template-file-status').textContent = '';
    this.el('detection-method').replaceChildren(...catalog.methods.map(method => new Option(method.name, method.id)));
    this.render();
  }
  add(duplicate) {
    if (this.settings.detectionTemplates.length >= 64) return;
    this.save();
    const template = { id: DetectionTemplateFile.createId(), name: duplicate ? `${this.current().name.slice(0, 70)} copy` : 'New detection',
      settings: duplicate ? structuredClone(this.current().settings) : {} };
    this.settings.detectionTemplates.push(template); this.selected = template.id; this.render(); this.assignmentsChanged();
  }
  usage() {
    return [...new Set([...this.settings.cameras, ...this.pendingCameras].flatMap(camera => [
      ...(camera.detectionTemplateId === this.selected ? [`Camera ${camera.channel}`] : []),
      ...camera.zones.filter(zone => zone.detectionTemplateId === this.selected).map(zone => `Camera ${camera.channel}: ${zone.name}`)
    ]))];
  }
  refreshUsage() {
    const assignments = this.usage();
    this.el('detection-template-usage').textContent = assignments.length ? `Assigned to ${assignments.join(', ')}. Zones without an override inherit their camera's template.` : 'No assignments yet.';
    this.el('detection-template-delete').disabled = assignments.length > 0 || this.settings.detectionTemplates.length <= 1;
    for (const id of ['detection-template-add', 'detection-template-clone', 'detection-template-install']) this.el(id).disabled = this.settings.detectionTemplates.length >= 64;
  }
  renderNames() {
    DetectionTemplateAssignments.populate(this.el('detection-template-select'), this.settings.detectionTemplates, this.selected); this.refreshUsage();
  }
  render() {
    const template = this.current(), policy = template.settings;
    this.renderNames(); this.el('detection-template-name').value = template.name;
    for (const [id, key, fallback] of [
      ['motion-sensitivity', 'sensitivity', 60], ['motion-area', 'minimumAreaPercent', 1], ['detection-method', 'detectionMethod', 'adaptive'],
      ['analysis-width', 'analysisWidth', 320], ['analysis-rate', 'analysisFramesPerSecond', 5], ['ai-device', 'aiDevice', 'cpu'],
      ['motion-gate-sensitivity', 'motionGateSensitivity', 80], ['motion-gate-area', 'motionGateMinimumAreaPercent', 0.1]
    ]) this.el(id).value = policy[key] ?? fallback;
    this.el('ai-model').replaceChildren(...this.catalog.models.map(model => new Option(`${model.name}${model.installed ? '' : ' — not installed'}`, model.id)));
    const modelId = policy.aiModel ?? 'yolox-nano';
    if (!this.catalog.models.some(model => model.id === modelId)) this.el('ai-model').append(new Option(`${modelId} — definition unavailable`, modelId));
    this.el('ai-model').value = modelId;
    policy.aiDevice ??= 'cpu'; policy.aiClasses ??= ['person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck', 'boat'];
    policy.aiIgnoreClasses ??= []; policy.aiOptions ??= {}; policy.aiConfidence ??= 0.45; policy.aiFramesPerSecond ??= 1;
    this.ai.load(policy, this.catalog.models.find(model => model.id === modelId));
    const adaptive = policy.adaptive || {};
    for (const [id, key] of [['adaptive-learn-noise', 'learnNoise'], ['adaptive-smooth', 'smoothFrames'], ['adaptive-shadows', 'suppressShadows'], ['adaptive-diagnostics', 'includeDiagnostics']]) this.el(id).checked = !!adaptive[key];
    for (const [id, key, fallback] of [['adaptive-noise-allowance', 'noiseAllowance', 3], ['adaptive-persistence', 'regionPersistenceFrames', 1],
      ['adaptive-confirmation', 'confirmationFrames', 3], ['adaptive-illumination', 'illuminationCompensation', 'mean'],
      ['adaptive-shadow-ratio', 'shadowMinimumBrightnessRatio', 0.6], ['adaptive-shadow-tolerance', 'shadowColorTolerance', 0.08], ['adaptive-candidate-pixels', 'candidateMinimumPixels', 4]]) this.el(id).value = adaptive[key] ?? fallback;
    this.renderMode();
  }
  save() {
    if (!this.settings) return;
    const template = this.current(), policy = template.settings;
    template.name = this.el('detection-template-name').value;
    for (const [id, key] of [['motion-sensitivity', 'sensitivity'], ['motion-area', 'minimumAreaPercent'], ['analysis-width', 'analysisWidth'],
      ['analysis-rate', 'analysisFramesPerSecond'], ['motion-gate-sensitivity', 'motionGateSensitivity'], ['motion-gate-area', 'motionGateMinimumAreaPercent']]) policy[key] = Number(this.el(id).value);
    policy.detectionMethod = this.el('detection-method').value; policy.aiModel = this.el('ai-model').value; policy.aiDevice = this.el('ai-device').value;
    this.ai.save(); policy.adaptive ??= {};
    for (const [id, key] of [['adaptive-learn-noise', 'learnNoise'], ['adaptive-smooth', 'smoothFrames'], ['adaptive-shadows', 'suppressShadows'], ['adaptive-diagnostics', 'includeDiagnostics']]) policy.adaptive[key] = this.el(id).checked;
    for (const [id, key] of [['adaptive-noise-allowance', 'noiseAllowance'], ['adaptive-persistence', 'regionPersistenceFrames'], ['adaptive-confirmation', 'confirmationFrames'],
      ['adaptive-shadow-ratio', 'shadowMinimumBrightnessRatio'], ['adaptive-shadow-tolerance', 'shadowColorTolerance'], ['adaptive-candidate-pixels', 'candidateMinimumPixels']]) policy.adaptive[key] = Number(this.el(id).value);
    policy.adaptive.illuminationCompensation = this.el('adaptive-illumination').value;
  }
  renderMode() {
    const mode = this.el('detection-method').value, ai = mode !== 'adaptive', adaptive = mode !== 'ai', hybrid = mode === 'adaptive-ai';
    this.el('ai-detection-settings').hidden = !ai; this.el('adaptive-detection-settings').hidden = mode !== 'adaptive';
    this.el('motion-gate-settings').hidden = !hybrid; this.el('adaptive-options').hidden = !adaptive;
    for (const id of ['ai-model', 'ai-device']) { this.el(id).disabled = !ai; this.el(id).required = ai; }
    this.ai.setEnabled(ai);
    for (const id of ['motion-gate-sensitivity', 'motion-gate-area']) { this.el(id).disabled = !hybrid; this.el(id).required = hybrid; }
    for (const id of ['motion-sensitivity', 'motion-area']) { this.el(id).disabled = mode !== 'adaptive'; this.el(id).required = mode === 'adaptive'; }
    for (const id of ['adaptive-learn-noise', 'adaptive-smooth', 'adaptive-persistence', 'adaptive-confirmation', 'adaptive-illumination', 'adaptive-shadows', 'adaptive-candidate-pixels', 'adaptive-diagnostics']) this.el(id).disabled = !adaptive;
    for (const [id, enabled] of [['adaptive-noise-allowance', adaptive && this.el('adaptive-learn-noise').checked],
      ['adaptive-shadow-ratio', adaptive && this.el('adaptive-shadows').checked], ['adaptive-shadow-tolerance', adaptive && this.el('adaptive-shadows').checked]]) {
      this.el(id).disabled = !enabled; this.el(id).required = enabled;
    }
    this.el('detection-method-help').textContent = { adaptive: 'Detects changing pixels. Optional filters reduce background noise; it cannot classify objects.',
      'adaptive-ai': 'Motion gates AI checks in the same zone. Missed motion cannot be recovered by AI.', ai: 'Selected object presence can trigger actions even without motion.' }[mode] || '';
    const model = this.catalog.models.find(item => item.id === this.el('ai-model').value);
    this.el('ai-model-help').textContent = model ? `${model.license}. ${model.installed ? 'Model installed.' : 'Model is not installed.'} Server model folder: ${this.catalog.modelDirectory}. See ? for setup.` : '';
    this.el('ai-model-notices').textContent = (this.catalog.notices || []).join(' ');
    this.el('ai-device-help').textContent = this.catalog.gpuDevices.length ? `Available Intel devices: ${this.catalog.gpuDevices.join(', ')}. Automatic falls back to CPU.` : 'Intel GPU is unavailable. CPU works without OpenVINO; see ? for GPU setup.';
    const gpu = [...this.el('ai-device').options].find(option => option.value === 'gpu');
    gpu.disabled = this.catalog.gpuDevices.length === 0 && this.el('ai-device').value !== 'gpu';
  }
  export(all) {
    this.save();
    const data = DetectionTemplateFile.write(all ? this.settings.detectionTemplates : [this.current()]);
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
    const link = document.createElement('a'); link.href = url; link.download = all ? 'detection-templates.json' : 'detection-template.json';
    document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    this.el('detection-template-file-status').textContent = 'Exported detection settings. Camera credentials, assignments and zones are not included.';
  }
}

const DetectionTemplateAssignments = {
  populate(select, templates, value, inherit = false, cameraId = null) {
    const options = templates.map(template => new Option(template.name, template.id));
    if (inherit) options.unshift(new Option(`Use camera template: ${templates.find(template => template.id === cameraId)?.name || 'Existing camera settings'}`, ''));
    select.replaceChildren(...options); select.value = value ?? (inherit ? '' : templates[0]?.id ?? '');
  }
};

const DetectionTemplateFile = {
  // getRandomValues is available on LAN HTTP servers as well as HTTPS/localhost.
  createId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''); },
  fields: ['detectionMethod', 'analysisWidth', 'analysisFramesPerSecond', 'sensitivity', 'minimumAreaPercent', 'adaptive',
    'aiModel', 'aiDevice', 'aiFramesPerSecond', 'aiConfidence', 'aiClasses', 'aiIgnoreClasses', 'aiOptions', 'motionGateSensitivity', 'motionGateMinimumAreaPercent'],
  write(templates) {
    return { version: 1, templates: templates.map(template => ({ name: template.name, settings: structuredClone(Object.fromEntries(
      this.fields.filter(key => template.settings[key] !== undefined).map(key => [key, template.settings[key]]))) })) };
  },
  read(data, catalog) {
    if (data?.version !== 1 || !Array.isArray(data.templates) || data.templates.length < 1 || data.templates.length > 64) throw new Error('Choose a version 1 template file containing 1–64 templates.');
    return data.templates.map(template => {
      const policy = template?.settings;
      if (typeof template?.name !== 'string' || !template.name.trim() || template.name.length > 80 || /[\u0000-\u001f]/.test(template.name) ||
        !policy || typeof policy !== 'object' || Array.isArray(policy) || Object.keys(policy).some(key => !this.fields.includes(key))) throw new Error('Invalid template name or detection settings.');
      if (policy.detectionMethod !== undefined && !catalog.methods.some(method => method.id === policy.detectionMethod) ||
        policy.aiModel !== undefined && !catalog.models.some(model => model.id === policy.aiModel) ||
        policy.aiDevice !== undefined && !['cpu', 'gpu', 'auto'].includes(policy.aiDevice) ||
        policy.analysisWidth !== undefined && ![320, 640, 960].includes(policy.analysisWidth)) throw new Error('A template uses an unsupported method, model, device or analysis width.');
      const ranges = { sensitivity: [1,100,true], minimumAreaPercent: [0.1,50], analysisFramesPerSecond: [1,10], aiFramesPerSecond: [0.2,5],
        aiConfidence: [0.1,0.95], motionGateSensitivity: [1,100,true], motionGateMinimumAreaPercent: [0.1,50] };
      this.numbers(policy, ranges);
      for (const key of ['aiClasses', 'aiIgnoreClasses']) if (policy[key] !== undefined && (!Array.isArray(policy[key]) || policy[key].length > 1000 ||
        policy[key].some(value => typeof value !== 'string' || !value.trim() || value.length > 80 || /[\u0000-\u001f]/.test(value)))) throw new Error('Invalid AI object rules.');
      const rules = [...(policy.aiClasses ?? ['person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck', 'boat']), ...(policy.aiIgnoreClasses ?? [])];
      const model = catalog.models.find(item => item.id === (policy.aiModel ?? 'yolox-nano'));
      if (!rules.length || rules.length > 1000 || new Set(rules).size !== rules.length ||
        model?.classes && rules.some(rule => !model.classes.includes(rule))) throw new Error('Choose supported, distinct trigger and ignore object rules.');
      if (policy.adaptive !== undefined) {
        const adaptive = policy.adaptive;
        if (!adaptive || typeof adaptive !== 'object' || Array.isArray(adaptive)) throw new Error('Invalid adaptive options.');
        const numeric = { noiseAllowance: [1,6], regionPersistenceFrames: [1,10,true], confirmationFrames: [1,10,true],
          shadowMinimumBrightnessRatio: [0.3,0.95], shadowColorTolerance: [0.01,0.3], candidateMinimumPixels: [4,10000,true] };
        const booleans = ['learnNoise', 'smoothFrames', 'suppressShadows', 'includeDiagnostics'];
        if (Object.keys(adaptive).some(key => ![...Object.keys(numeric), ...booleans, 'illuminationCompensation'].includes(key)) ||
          booleans.some(key => adaptive[key] !== undefined && typeof adaptive[key] !== 'boolean') ||
          adaptive.illuminationCompensation !== undefined && !['mean', 'robust'].includes(adaptive.illuminationCompensation)) throw new Error('Invalid adaptive options.');
        this.numbers(adaptive, numeric);
      }
      if (policy.aiOptions !== undefined && (!policy.aiOptions || typeof policy.aiOptions !== 'object' || Array.isArray(policy.aiOptions) ||
        Object.keys(policy.aiOptions).some(key => !['iouThreshold', 'maxDetections'].includes(key)))) throw new Error('Invalid AI options.');
      if (policy.aiOptions) this.numbers(policy.aiOptions, { iouThreshold: [0.1,0.9], maxDetections: [1,300,true] });
      if (model?.options) {
        if (Object.keys(policy.aiOptions || {}).some(key => !model.options.some(option => option.id === key))) throw new Error('An AI option is unsupported by this model.');
        for (const option of model.options) {
          const value = option.id === 'confidence' ? policy.aiConfidence ?? 0.45 : option.id === 'framesPerSecond' ? policy.aiFramesPerSecond ?? 1 : policy.aiOptions?.[option.id] ?? option.default;
          if (value < option.minimum || value > option.maximum) throw new Error(`${option.label} is outside this model's supported range.`);
        }
      }
      return { id: this.createId(), name: template.name.trim(), settings: structuredClone(policy) };
    });
  },
  numbers(values, ranges) {
    for (const [key, [minimum, maximum, integer]] of Object.entries(ranges)) if (values[key] !== undefined &&
      (typeof values[key] !== 'number' || !Number.isFinite(values[key]) || values[key] < minimum || values[key] > maximum || integer && !Number.isInteger(values[key]))) throw new Error(`Invalid ${key}.`);
  }
};
