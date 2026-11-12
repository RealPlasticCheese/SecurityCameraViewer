'use strict';
(() => {
  const el = id => document.getElementById(id);
  let config, cameras = [];
  const scopes = { read: 'Read cameras, recordings and events', trigger: 'Trigger recordings', control: 'Arm/disarm and override schedules', admin: 'Administrator — all permissions' };
  const uuid = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  async function request(path, method = 'GET', body, headers = {}) {
    const response = await fetch(path, { method, headers: { ...headers, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
    const data = response.headers.get('content-type')?.includes('json') ? await response.json() : null;
    if (!response.ok) { if (response.status === 401) showLogin(); throw new Error(data?.message || `Request failed (${response.status}).`); }
    return data;
  }
  function showLogin() { if (!el('auth-dialog').open) el('auth-dialog').showModal(); }
  document.addEventListener('scv-sign-in-required', showLogin);
  function handle(id, action, submit = false) {
    el(id).addEventListener(submit ? 'submit' : 'click', async event => {
      event.preventDefault(); const button = submit ? event.submitter : event.currentTarget; button.disabled = true;
      el('integration-result').textContent = ''; el('auth-result').textContent = '';
      try { await action(); } catch (error) { el('integration-result').textContent = error.message; el('auth-result').textContent = error.message; }
      finally { button.disabled = false; }
    });
  }
  function button(text, action) {
    const b = document.createElement('button'); b.type = 'button'; b.textContent = text;
    b.onclick = async () => { b.disabled = true; try { await action(); } catch (e) { el('integration-result').textContent = e.message; } finally { b.disabled = false; } }; return b;
  }
  function checks(container, choices, selected) {
    el(container).replaceChildren();
    for (const [value, label] of Object.entries(choices)) {
      const l = document.createElement('label'); l.className = 'check'; const input = document.createElement('input'); input.type = 'checkbox'; input.value = value; input.checked = selected.includes(value);
      l.append(input, document.createTextNode(label)); el(container).append(l);
    }
  }
  const checked = id => [...el(id).querySelectorAll('input:checked')].map(i => i.value);
  const selected = id => [...el(id).selectedOptions].map(o => o.value);
  function secret(value) { el('integration-secret-panel').hidden = false; el('integration-secret').value = value; el('integration-secret-panel').scrollIntoView({ block: 'nearest' }); }
  async function authStatus() {
    const status = await request('/api/auth/status');
    el('auth-open').textContent = status.signedIn ? 'Sign-in & sign out' : 'Sign in';
    if (status.enabled && !status.signedIn) showLogin();
  }
  async function load() {
    config = await request('/api/integrations');
    el('integration-setup').hidden = config.enabled; el('integration-config').hidden = !config.enabled;
    if (!config.enabled) return;
    el('integration-base').textContent = `${location.origin}/api/v1`;
    cameras = await request('/api/v1/cameras');
    const previousCamera = el('integration-camera').value;
    el('integration-camera').replaceChildren(...cameras.map(c => new Option(c.name, c.id)));
    if (cameras.some(c => c.id === previousCamera)) el('integration-camera').value = previousCamera;
    renderCamera();
    el('integration-hook-cameras').replaceChildren(...cameras.map(c => new Option(c.name, c.id)));
    const zoneGroups = await Promise.all(cameras.map(c => request(`/api/v1/cameras/${c.id}/zones`)));
    el('integration-hook-zones').replaceChildren(new Option('Full frame (no inclusion zones)', 'full-frame'));
    zoneGroups.forEach((group, i) => group.zones.filter(z => !z.exclude).forEach(z => el('integration-hook-zones').add(new Option(`${cameras[i].name} / ${z.name}`, z.id))));
    el('integration-token-list').replaceChildren();
    for (const token of config.tokens) {
      const row = document.createElement('p'); row.textContent = `${token.name} · ${token.scopes.join(', ')} · ${token.expiresAt ? 'expires ' + new Date(token.expiresAt).toLocaleString() : 'no expiry'} `;
      row.append(button('Revoke', async () => { await request(`/api/integrations/tokens/${token.id}`, 'DELETE'); await load(); })); el('integration-token-list').append(row);
    }
    el('integration-hook-list').replaceChildren();
    for (const hook of config.webhooks) {
      const row = document.createElement('p'); row.textContent = `${hook.name} · ${hook.enabled ? 'enabled' : 'paused'} `;
      row.append(button('Edit', () => edit(hook)), button('Test', async () => { await request(`/api/integrations/webhooks/${hook.id}/test`, 'POST'); el('integration-result').textContent = 'Test queued. Refresh deliveries to see its result.'; await deliveries(); }),
        button('Delete', async () => { await request(`/api/integrations/webhooks/${hook.id}`, 'DELETE'); await load(); edit(); })); el('integration-hook-list').append(row);
    }
    await deliveries();
  }
  function edit(hook = {}) {
    el('integration-hook-id').value = hook.id || uuid(); el('integration-hook-name').value = hook.name || 'Home Assistant';
    el('integration-hook-url').value = hook.url || ''; el('integration-hook-enabled').checked = hook.enabled !== false;
    el('integration-hook-cooldown').value = hook.cooldownSeconds ?? 2; el('integration-hook-rotate').checked = false;
    checks('integration-events', Object.fromEntries(config.eventTypes.map(e => [e, e])), hook.events || ['motion.started', 'motion.ended', 'recording.ready', 'camera.offline', 'camera.online']);
    for (const option of el('integration-hook-cameras').options) option.selected = (hook.cameraIds || []).includes(option.value);
    for (const option of el('integration-hook-zones').options) option.selected = (hook.zoneIds || []).includes(option.value);
  }
  async function deliveries() {
    const [items, summary] = await Promise.all([request('/api/integrations/deliveries'), request('/api/integrations')]);
    el('integration-queue-status').textContent = `${summary.pendingCount} pending · ${summary.failedCount} failed · ${summary.droppedEvents} events dropped before persistence. ${summary.notice}`;
    el('integration-deliveries').replaceChildren();
    if (!items.length) el('integration-deliveries').textContent = 'No deliveries yet.';
    for (const item of items) {
      const row = document.createElement('div'); row.className = 'integration-delivery';
      const title = document.createElement('strong'); title.textContent = `${item.actionName || config.webhooks.find(h => h.id === item.subscriptionId)?.name || 'Deleted webhook'} · ${item.eventType} · ${item.status}`;
      const detail = document.createElement('p'); detail.textContent = `${new Date(item.createdAt).toLocaleString()} · ${item.attempts} attempts${item.error ? ' · ' + item.error : ''}${item.status === 'pending' ? ' · next attempt ' + new Date(item.nextAttempt).toLocaleString() : ''}`;
      const id = document.createElement('code'); id.textContent = item.id;
      row.append(title, detail, id);
      if (item.status !== 'delivered') row.append(button('Retry now', async () => { await request(`/api/integrations/deliveries/${item.id}/retry`, 'POST'); await deliveries(); }), button('Dismiss', async () => { await request(`/api/integrations/deliveries/${item.id}`, 'DELETE'); await deliveries(); }));
      el('integration-deliveries').append(row);
    }
  }
  function renderCamera() {
    const c = cameras.find(c => c.id === el('integration-camera').value);
    el('integration-camera-state').textContent = c ? `${c.id} · ${c.recording?.status || 'disabled'} · ${c.arming.effectiveMode}${c.arming.expiresAt ? ' (until ' + new Date(c.arming.expiresAt).toLocaleString() + ')' : ''}` : '';
    el('integration-arming-mode').value = c?.arming.effectiveMode || 'schedule';
  }
  el('integration-camera').onchange = renderCamera;
  handle('auth-open', showLogin);
  handle('auth-form', async () => { await request('/api/auth/session', 'POST', { token: el('auth-token').value }); el('auth-token').value = ''; el('auth-dialog').close(); await authStatus(); }, true);
  handle('auth-logout', async () => { await request('/api/auth/session', 'DELETE'); location.reload(); });
  handle('integrations-open', async () => { el('integrations-dialog').showModal(); await load(); if (config.enabled) edit(); });
  handle('integration-enable', async () => { const result = await request('/api/integrations/setup', 'POST'); secret(result.token); await load(); edit(); await authStatus(); });
  handle('integration-copy', async () => { if (navigator.clipboard) await navigator.clipboard.writeText(el('integration-secret').value); else { el('integration-secret').select(); document.execCommand('copy'); } el('integration-result').textContent = 'Secret copied.'; });
  handle('integration-secret-dismiss', () => { el('integration-secret').value = ''; el('integration-secret-panel').hidden = true; });
  handle('integration-token-form', async () => {
    const result = await request('/api/integrations/tokens', 'POST', { name: el('integration-token-name').value, scopes: checked('integration-scopes'), expiresAt: el('integration-token-expiry').value ? new Date(el('integration-token-expiry').value).toISOString() : null }); secret(result.token); await load();
  }, true);
  handle('integration-hook-new', () => edit());
  handle('integration-hook-form', async () => {
    const result = await request('/api/integrations/webhooks', 'PUT', { subscription: { id: el('integration-hook-id').value, name: el('integration-hook-name').value,
      url: el('integration-hook-url').value, enabled: el('integration-hook-enabled').checked, events: checked('integration-events'), cameraIds: selected('integration-hook-cameras'), zoneIds: selected('integration-hook-zones'), cooldownSeconds: +el('integration-hook-cooldown').value }, rotateSecret: el('integration-hook-rotate').checked });
    secret(result.secret); await load(); edit(result.subscription); el('integration-result').textContent = 'Webhook saved. Home Assistant accepts the JSON payload without checking the signature; other receivers can verify it with this secret.';
  }, true);
  handle('integration-refresh', deliveries);
  handle('integration-arming-form', async () => { const mode = el('integration-arming-mode').value; await request(`/api/v1/cameras/${el('integration-camera').value}/arming`, 'PUT', { mode, expiresAt: mode === 'schedule' ? null : new Date(Date.now() + +el('integration-arming-minutes').value * 60000).toISOString() }); await load(); el('integration-result').textContent = 'Arming mode updated.'; }, true);
  handle('integration-trigger-form', async () => { const result = await request('/api/v1/recordings/trigger', 'POST', { cameraId: el('integration-camera').value, source: 'browser', reason: el('integration-trigger-reason').value, durationSeconds: +el('integration-trigger-seconds').value, overrideSchedule: el('integration-trigger-override').checked }, { 'Idempotency-Key': uuid() }); el('integration-result').textContent = `Recording request ${result.status}. ID: ${result.id}`; }, true);
  for (const id of ['auth-dialog', 'integrations-dialog']) {
    document.querySelector(`[data-close="${id}"]`).onclick = () => el(id).close();
  }
  el('integrations-dialog').addEventListener('close', () => { el('integration-secret').value = ''; el('integration-secret-panel').hidden = true; });
  checks('integration-scopes', scopes, ['read', 'trigger']);
  authStatus().catch(() => {});
})();
