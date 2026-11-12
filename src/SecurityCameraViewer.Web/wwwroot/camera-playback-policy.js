'use strict';

class PlaybackPolicy {
  static labels = { native: 'Native video', browser: 'Browser software decoding', server: 'Server JPEG previews' };
  static choose(preference, capabilities, failed = new Set()) {
    if (!['automatic', 'native', 'browser', 'server'].includes(preference)) throw new Error('Unknown playback method.');
    const automatic = preference === 'automatic';
    const available = method => method === 'server' || !!capabilities[method] && !failed.has(method);
    const method = automatic ? ['native', 'browser', 'server'].find(available) : available(preference) ? preference : null;
    if (!method) return { method: null, message: `${this.labels[preference]} unavailable in this browser for this stream. ${capabilities.reason || ''} Choose Automatic or Server JPEG previews.` };
    const warning = method === 'browser' ? 'Warning: uses extra client CPU.' : method === 'server' ? 'Warning: uses extra server CPU.' : 'Camera video is passed through without server re-encoding.';
    const reason = automatic && method !== 'native' ? ` ${capabilities.reason || 'Native playback failed or is unavailable.'}` : '';
    return { method, message: `${automatic ? 'Automatic → ' : ''}${this.labels[method]}. ${warning}${reason}` };
  }
}

// Describes the codecs in the stream delivered to the browser.
class CameraMediaCodecs {
  static describe(mimeType) {
    const result = { video: null, audio: null };
    if (typeof mimeType !== 'string') return result;
    const parameter = /(?:^|;)\s*codecs\s*=\s*(?:"([^"]*)"|'([^']*)'|([^;\s]+))\s*(?:;|$)/i.exec(mimeType);
    if (!parameter) return result;
    for (const value of (parameter[1] ?? parameter[2] ?? parameter[3]).split(',')) {
      const codec = value.trim().toLowerCase();
      if (/^avc[13](?:\.|$)/.test(codec)) result.video = 'H.264';
      else if (/^(?:hvc1|hev1)(?:\.|$)/.test(codec)) result.video = 'H.265';
      else if (/^mp4a\.40\.(?:2|5|29)$/.test(codec)) result.audio = 'AAC';
    }
    return result;
  }
}

class PlaybackStatistics {
  constructor(now = () => performance.now()) { this.now = now; this.reset(); }
  reset() { this.started = this.now(); this.frames = this.dropped = this.decodeTime = 0; }
  frame(milliseconds = 0) {
    if (!Number.isFinite(milliseconds) || milliseconds < 0) throw new Error('Invalid decode duration.');
    this.frames++; this.decodeTime += milliseconds;
  }
  drop(count = 1) {
    if (!Number.isInteger(count) || count < 0) throw new Error('Invalid dropped frame count.');
    this.dropped += count;
  }
  snapshot() {
    const seconds = (this.now() - this.started) / 1000;
    return { frames: this.frames, dropped: this.dropped, framesPerSecond: seconds > 0 ? this.frames / seconds : 0,
      meanDecodeMilliseconds: this.frames ? this.decodeTime / this.frames : 0 };
  }
}
if (typeof module !== 'undefined') module.exports = { PlaybackPolicy, CameraMediaCodecs, PlaybackStatistics };
