'use strict';

// Archive presentation owns coverage, marker navigation and consecutive-segment playback.
class RecordingArchivePlayer {
  constructor(request) {
    this.request = request;
    this.el = id => document.getElementById(id);
    this.recordings = [];
    this.timelineVersion = 0;
    this.playVersion = 0;
    this.current = null;
    this.el('recording-player').addEventListener('ended', () => this.next());
  }

  clear() {
    this.timelineVersion++;
    this.playVersion++;
    this.current = null;
    this.recordings = [];
    const player = this.el('recording-player');
    player.pause(); player.removeAttribute('src'); player.load();
    this.el('recording-player-panel').hidden = true;
    this.el('recording-markers').replaceChildren();
    this.el('recording-timeline').replaceChildren();
    this.el('recording-timeline-status').textContent = '';
    this.el('recording-timeline-panel').hidden = true;
  }

  async coverage(filter, configurationId) {
    const signature = JSON.stringify([configurationId, filter.channel, filter.date, filter.zones, !!filter.savedReplayOnly]);
    if (signature !== this.filterSignature) { this.clear(); this.filterSignature = signature; }
    const version = ++this.timelineVersion;
    this.el('recording-timeline-panel').hidden = !filter.channel || !filter.date;
    if (!filter.channel || !filter.date) return;
    const since = new Date(`${filter.date}T00:00:00`);
    const before = new Date(since); before.setDate(before.getDate() + 1);
    const query = new URLSearchParams({ channel: filter.channel, since: since.toISOString(), before: before.toISOString() });
    if (filter.savedReplayOnly) query.set('savedReplayOnly', 'true');
    this.el('recording-timeline-status').textContent = 'Loading coverage…';
    try {
      const result = await this.request(`recordings/timeline?${query}`);
      if (version !== this.timelineVersion) return;
      this.recordings = result.recordings;
      const timeline = this.el('recording-timeline'); timeline.replaceChildren();
      for (const recording of this.recordings) {
        const start = new Date(recording.startedAt), end = new Date(recording.endedAt ?? Date.now());
        const segment = document.createElement('button'); segment.type = 'button';
        segment.className = `archive-segment ${recording.status === 'ready' ? 'ready' : 'pending'}`;
        segment.disabled = recording.status !== 'ready';
        segment.style.left = `${Math.max(0, (start - since) / (before - since) * 100)}%`;
        segment.style.width = `${Math.max(0.05, (Math.min(end, before) - Math.max(start, since)) / (before - since) * 100)}%`;
        segment.title = `${start.toLocaleTimeString()} – ${end.toLocaleTimeString()} · ${recording.status}`;
        segment.setAttribute('aria-label', segment.title);
        segment.onclick = () => this.play(recording, Math.max(0, (since - start) / 1000));
        timeline.append(segment);
      }
      this.el('recording-timeline-status').textContent = result.truncated
        ? 'First 2,000 segments shown. Use the recording list to browse remaining footage.'
        : this.recordings.length ? `${this.recordings.length} segments · ${since.toLocaleTimeString()} to ${before.toLocaleTimeString()}` : 'No footage for this camera and date.';
    } catch (error) {
      if (version === this.timelineVersion) this.el('recording-timeline-status').textContent = error.message;
    }
  }

  async play(recording, offset = 0) {
    const version = ++this.playVersion;
    let seekOffset = offset;
    this.current = recording;
    const player = this.el('recording-player');
    this.el('recording-player-panel').hidden = false;
    this.el('recording-player-title').textContent = `${recording.cameraName} · ${new Date(recording.startedAt).toLocaleString()}`;
    this.el('recording-markers').replaceChildren();
    player.onloadedmetadata = () => {
      if (version === this.playVersion) player.currentTime = Math.min(seekOffset, Math.max(0, player.duration - 0.1));
    };
    player.src = `/api/recordings/${recording.id}/video`;
    player.play().catch(() => {});
    try {
      const markers = await this.request(`recordings/${recording.id}/markers`);
      if (version !== this.playVersion) return;
      for (const marker of markers.filter(item => !['continuous', 'stream'].includes(item.source))) {
        const timestamp = new Date(marker.timestamp);
        const button = document.createElement('button'); button.type = 'button';
        button.textContent = `${timestamp.toLocaleTimeString()} · ${marker.zones?.map(zone => zone.name).join(', ') || marker.reason}`;
        button.onclick = () => {
          seekOffset = Math.max(0, (timestamp - new Date(recording.startedAt)) / 1000);
          if (Number.isFinite(player.duration)) player.currentTime = Math.min(seekOffset, Math.max(0, player.duration - 0.1));
          player.play().catch(() => {});
        };
        this.el('recording-markers').append(button);
      }
    } catch (error) {
      if (version === this.playVersion) this.el('recording-markers').textContent = `Activity markers unavailable: ${error.message}`;
    }
  }

  next() {
    if (!this.el('recording-play-next').checked || !this.current || this.current.mode === 'event') return;
    const index = this.recordings.findIndex(item => item.id === this.current.id);
    const next = index >= 0 ? this.recordings[index + 1] : null;
    if (!next || next.status !== 'ready' || next.mode === 'event') return;
    const gap = (new Date(next.startedAt) - new Date(this.current.endedAt)) / 1000;
    if (Math.abs(gap) > 2) {
      this.el('recording-timeline-status').textContent = 'Playback reached a recording gap. Select another segment to continue.';
      return;
    }
    this.play(next);
  }
}
