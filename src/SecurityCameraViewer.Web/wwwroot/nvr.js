'use strict';
(() => {
  const el = id => document.getElementById(id);
  const notice = message => { el('notice').textContent = message; };
  let pollTimer;
  const localDate = new Date();
  el('date').value = `${localDate.getFullYear()}-${String(localDate.getMonth() + 1).padStart(2, '0')}-${String(localDate.getDate()).padStart(2, '0')}`;
  async function request(path, method = 'GET', body) {
    const response = await fetch(path, { method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined });
    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(response.status === 401 ? 'Sign in from the camera viewer menu, then return to this page.' : error.message || `Request failed (${response.status}).`);
    }
    return response.status === 204 ? null : response.text().then(text => text ? JSON.parse(text) : null);
  }
  function handle(id, event, action) {
    el(id).addEventListener(event, async e => {
      e.preventDefault();
      const button = e.submitter || (e.currentTarget.tagName === 'BUTTON' ? e.currentTarget : null);
      if (button) button.disabled = true;
      try { await action(); } catch (error) { notice(error.message); }
      finally { if (button) button.disabled = false; }
    });
  }
  const displayTime = value => value.replace('T', ' ').replace(/\.\d+$/, '');
  const bytes = value => value >= 1024 * 1024 ? `${(value / 1024 / 1024).toFixed(1)} MiB` : `${Math.round(value / 1024)} KiB`;
  function button(label, action) {
    const element = document.createElement('button');
    element.type = 'button'; element.textContent = label;
    element.addEventListener('click', async () => {
      element.disabled = true;
      try { await action(); } catch (error) { notice(error.message); }
      finally { element.disabled = false; }
    });
    return element;
  }
  async function device() {
    notice('Connecting to the recorder…');
    const info = await request('/api/nvr/device');
    el('device-info').textContent = `${info.name || 'NVR'} · Firmware ${info.firmware} · ${info.channelCount} channels · Playback ${info.canPlayback ? 'permitted' : 'not permitted'} · Export ${info.canExport ? 'permitted' : 'not permitted'}`;
    const previous = el('channel').value;
    el('channel').replaceChildren(...Array.from({ length: info.channelCount }, (_, i) => {
      const option = document.createElement('option'); option.value = i + 1; option.textContent = `Channel ${i + 1}`; return option;
    }));
    if (previous && Number(previous) <= info.channelCount) el('channel').value = previous;
    notice('Connected. Choose a channel and date to browse its recordings.');
    try {
      const storage = await request('/api/nvr/storage');
      const disks = storage.disks.map(d => `Disk ${d.number}: ${(d.capacityBytes / 1024 ** 3).toFixed(1)} GiB total, ${(d.freeBytes / 1024 ** 3).toFixed(1)} GiB free`).join(' · ');
      el('storage-info').textContent = `${disks || 'No disk capacity reported'} · Automatic overwrite ${storage.automaticOverwrite ? 'on' : 'off'}`;
    } catch (error) { el('storage-info').textContent = `Disk information unavailable: ${error.message}`; }
  }
  handle('connection-form', 'submit', async () => {
    await request('/api/nvr/settings', 'PUT', { settings: { host: el('host').value.trim(), port: Number(el('port').value), username: el('username').value }, password: el('password').value || null });
    el('password').value = '';
    await device();
  });
  handle('refresh-device', 'click', device);
  handle('search-form', 'submit', async () => {
    notice('Searching the NVR…');
    const recordings = await request('/api/nvr/search', 'POST', { channel: Number(el('channel').value), begin: `${el('date').value}T00:00:00`, end: `${el('date').value}T23:59:59` });
    el('recordings').replaceChildren();
    for (const recording of recordings) {
      const row = document.createElement('tr');
      const kind = recording.kind === 1 ? 'Continuous' : recording.kind === 2 ? 'Motion' : `Type ${recording.kind}`;
      for (const text of [displayTime(recording.begin), displayTime(recording.end), kind, bytes(recording.sizeBytes)]) {
        const cell = document.createElement('td'); cell.textContent = text; row.append(cell);
      }
      const cell = document.createElement('td');
      cell.append(button('Select clip', () => {
        el('begin').value = recording.begin.slice(0, 19);
        const start = new Date(recording.begin + 'Z');
        const finish = new Date(Math.min(new Date(recording.end + 'Z').getTime(), start.getTime() + 60 * 1000));
        el('end').value = finish.toISOString().slice(0, 19);
        el('begin').focus();
        notice('Clip selected. Adjust its times, then retrieve it for playback.');
      }));
      row.append(cell); el('recordings').append(row);
    }
    if (!recordings.length) {
      const row = document.createElement('tr'); const cell = document.createElement('td'); cell.colSpan = 5; cell.textContent = 'No recordings found for this channel and date.'; row.append(cell); el('recordings').append(row);
    }
    notice(`${recordings.length} recording segments found.`);
  });
  handle('export-form', 'submit', async () => {
    if (!el('channel').value) throw new Error('Connect and select a channel first.');
    const job = await request('/api/nvr/exports', 'POST', { channel: Number(el('channel').value), begin: el('begin').value, end: el('end').value });
    notice(`Clip queued. Transfer status is shown below (${job.id.slice(0, 8)}).`);
    await exports();
  });
  function play(saved) {
    el('player').src = `/api/nvr/exports/${saved.id}/video`;
    el('playing-label').textContent = `Channel ${saved.recording.channel} · ${displayTime(saved.recording.begin)} to ${displayTime(saved.recording.end)} · Video only`;
    el('playback-error').textContent = '';
    el('player').play().catch(() => { el('playback-error').textContent = 'Press Play to start this clip.'; });
    el('player').scrollIntoView({ behavior: 'smooth', block: 'center' });
  }
  el('player').addEventListener('error', () => { el('playback-error').textContent = 'The browser could not play this clip. Download the MP4 to try another player, or sign in again if your session expired.'; });
  async function exports() {
    clearTimeout(pollTimer);
    const data = await request('/api/nvr/exports');
    el('jobs').replaceChildren();
    for (const job of data.jobs.filter(j => j.state !== 'Ready').slice(0, 20)) {
      const row = document.createElement('div'); row.className = 'job';
      const details = document.createElement('div'); details.className = 'job-details';
      const label = document.createElement('span'); label.className = 'job-description';
      label.textContent = `Channel ${job.recording.channel} · ${displayTime(job.recording.begin)} · ${job.state}: ${job.message}`;
      const progress = document.createElement('progress'); progress.max = 100;
      const amount = document.createElement('span'); amount.className = 'job-progress-text';
      const percentage = Math.max(0, Math.min(100, Number(job.retrievalPercent) || 0));
      const retrieved = bytes(Number(job.retrievedBytes) || 0);
      if (job.state === 'Converting') {
        progress.setAttribute('aria-label', `Preparing MP4 for channel ${job.recording.channel}`);
        amount.textContent = `Retrieval complete · ${retrieved} received · Preparing MP4…`;
      } else {
        progress.value = percentage;
        progress.setAttribute('aria-label', `Retrieval progress for channel ${job.recording.channel}`);
        amount.textContent = job.state === 'Queued' ? 'Waiting to retrieve video' : `Retrieval: approximately ${percentage}% · ${retrieved} received`;
        progress.setAttribute('aria-valuetext', amount.textContent);
      }
      details.append(label, progress, amount); row.append(details);
      if (['Queued', 'Retrieving', 'Converting'].includes(job.state)) row.append(button('Cancel transfer', async () => { await request(`/api/nvr/exports/${job.id}/cancel`, 'POST'); await exports(); }));
      if (['Failed', 'Cancelled'].includes(job.state)) row.append(button('Delete message', async () => { await request(`/api/nvr/exports/${job.id}/message`, 'DELETE'); await exports(); }));
      el('jobs').append(row);
    }
    el('saved').replaceChildren();
    for (const saved of data.saved) {
      const row = document.createElement('div'); row.className = 'clip'; const label = document.createElement('span');
      label.textContent = `Channel ${saved.recording.channel} · ${displayTime(saved.recording.begin)} to ${displayTime(saved.recording.end)} · ${bytes(saved.sizeBytes)}`;
      const download = document.createElement('a'); download.href = `/api/nvr/exports/${saved.id}/video?download=true`; download.textContent = 'Download MP4';
      row.append(label, button('Play', () => play(saved)), download, button('Delete server copy', async () => {
        if (!confirm('Delete this saved clip from the server? The NVR recording will remain.')) return;
        await request(`/api/nvr/exports/${saved.id}`, 'DELETE'); await exports();
      })); el('saved').append(row);
    }
    if (!data.saved.length) el('saved').textContent = 'No saved clips yet.';
    if (!document.hidden && data.jobs.some(j => ['Queued', 'Retrieving', 'Converting'].includes(j.state))) pollTimer = setTimeout(() => exports().catch(error => notice(error.message)), 1500);
  }
  handle('refresh-exports', 'click', exports);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) clearTimeout(pollTimer);
    else exports().catch(error => notice(error.message));
  });
  (async () => {
    await exports();
    try {
      const settings = await request('/api/nvr/settings');
      el('host').value = settings.host; el('port').value = settings.port; el('username').value = settings.username; el('password').placeholder = settings.hasPassword ? 'Saved password (blank keeps it)' : 'Enter recorder password';
      el('connection-panel').open = !settings.host;
      if (settings.notice) notice(settings.notice);
    } catch (error) { notice(error.message); }
  })().catch(error => notice(error.message));
})();
