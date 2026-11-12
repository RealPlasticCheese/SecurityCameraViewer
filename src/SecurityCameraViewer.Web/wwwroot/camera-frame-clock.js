'use strict';

// Convert MP4 presentation time to the server's frame clock. Fragment timestamps
// precede transport buffering; neither the browser clock nor request latency is used.
class CameraFrameClock {
  constructor(retainedSeconds = 11) {
    // Live MSE retains eight seconds behind playback and permits three seconds ahead.
    if (!Number.isFinite(retainedSeconds) || retainedSeconds <= 0) throw new RangeError('Timing retention must be positive.');
    this.retainedSeconds = retainedSeconds; this.reset();
  }
  reset() { this.streamId = undefined; this.samples = []; this.intervals = []; this.pruned = false; }
  update(packet) {
    const produced = Date.parse(packet?.producedAt);
    if (!Number.isFinite(produced) || !Number.isFinite(packet?.startSeconds) ||
        !Number.isFinite(packet?.endSeconds) || packet.endSeconds <= packet.startSeconds || !packet.streamId) return false;
    let previous = this.samples.at(-1);
    if (this.streamId !== packet.streamId || previous && (packet.startSeconds < previous.start || packet.endSeconds < previous.end || produced < previous.produced)) {
      this.reset(); previous = undefined;
    }
    this.streamId = packet.streamId;
    if (previous && previous.start === packet.startSeconds && previous.end === packet.endSeconds && previous.produced === produced) return true;
    if (previous && packet.endSeconds > previous.end) {
      this.intervals.push((produced - previous.produced) / ((packet.endSeconds - previous.end) * 1000));
      if (this.intervals.length > 3) this.intervals.shift();
    }
    // Three consistent intervals distinguish source pacing from a delayed flush and
    // its short catch-up interval. Irregular flushing leaves the established rate intact.
    const sorted = [...this.intervals].sort((a, b) => a - b), median = sorted.length === 3 ? sorted[1] : 0;
    const consistent = Number.isFinite(median) && median > 0 && sorted[2] - sorted[0] <= median * .1;
    const rate = consistent ? median : previous?.rate || 1;
    const origin = produced - packet.endSeconds * rate * 1000;
    const calibrated = previous ? Math.min(previous.produced - previous.end * rate * 1000, origin) : origin;
    // Lower flush latency may improve the neighboring fragment, but a pace change
    // must not make any of its buffered pictures select a later detection sample.
    if (previous && previous.start * rate * 1000 + calibrated <= previous.start * previous.rate * 1000 + previous.origin &&
        previous.end * rate * 1000 + calibrated <= previous.end * previous.rate * 1000 + previous.origin) {
      previous.rate = rate; previous.origin = calibrated;
    }
    this.samples.push({ start: packet.startSeconds, end: packet.endSeconds, produced, rate, origin: calibrated });
    const cutoff = packet.startSeconds - this.retainedSeconds;
    while (this.samples.length > 1 && this.samples[1].end < cutoff) { this.samples.shift(); this.pruned = true; }
    return true;
  }
  time(mediaSeconds) {
    if (!Number.isFinite(mediaSeconds) || !this.samples.length || this.pruned && mediaSeconds < this.samples[0].start) return null;
    let low = 0, high = this.samples.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if (this.samples[middle].start <= mediaSeconds) low = middle + 1; else high = middle;
    }
    const sample = this.samples[Math.max(0, low - 1)];
    return sample.origin + mediaSeconds * sample.rate * 1000;
  }

  media(wallMilliseconds) {
    if (!Number.isFinite(wallMilliseconds)) return null;
    const sample = this.samples.findLast(item => wallMilliseconds >= item.origin + item.start * item.rate * 1000 &&
      wallMilliseconds < item.origin + item.end * item.rate * 1000);
    return sample ? { seconds: (wallMilliseconds - sample.origin) / (sample.rate * 1000), rate: sample.rate } : null;
  }
}

// One capture-time cursor for encoded live pictures, including focused sound. The
// initial delay allows the longest incoming fragment to reach each decoder.
class LiveCameraClock {
  static shared = new LiveCameraClock();
  constructor(now = () => performance.now(), { staleMilliseconds = 8000, toleranceMilliseconds = 100, catchUpRate = 1.1 } = {}) {
    if (!(staleMilliseconds > 0) || !(toleranceMilliseconds > 0) || !Number.isFinite(staleMilliseconds + toleranceMilliseconds))
      throw new RangeError('Live synchronization intervals must be positive and finite.');
    if (!Number.isFinite(catchUpRate) || catchUpRate <= 1) throw new RangeError('Live catch-up rate must be finite and greater than one.');
    Object.assign(this, { now, staleMilliseconds, toleranceMilliseconds, catchUpRate });
    this.rate = 1;
    this.members = new Map(); this.active = []; this.anchor = null;
  }
  remove(key) { this.members.delete(key); if (!this.members.size) this.current(); }
  update(key, clock, mediaEnd) {
    const sample = clock.samples.at(-1), edge = clock.time(mediaEnd);
    if (!sample || !Number.isFinite(edge)) { this.remove(key); return null; }
    const previous = this.members.get(key);
    // Keep enough headroom for alternating short/long fragments instead of treating
    // every short fragment as evidence that the viewer has fallen behind.
    this.members.set(key, { edge, delay: Math.max(...clock.samples.map(item => (item.end - item.start) * item.rate * 1000)),
      received: previous?.mediaEnd === mediaEnd && previous.streamId === clock.streamId ? previous.received : this.now(),
      mediaEnd, streamId: clock.streamId });
    return this.current();
  }
  current() {
    const now = this.now(), entries = [...this.members].filter(([, item]) => now - item.received < this.staleMilliseconds);
    if (!entries.length) { this.active = []; this.streams = new Map(); this.anchor = null; this.rate = 1; return null; }
    const changed = entries.length !== this.active.length || entries.some(([key, item], i) =>
      key !== this.active[i] || this.streams?.get(key) !== item.streamId);
    const edge = Math.min(...entries.map(([, item]) => item.edge));
    const delay = Math.max(...entries.map(([, item]) => item.delay));
    // Remember the freshest production time relative to our monotonic timer.
    // Decoder backpressure can delay every incoming fragment while leaving only
    // a small local buffer; measuring backlog only from that buffer hides drift.
    // This needs no agreement between the browser's and server's wall clocks.
    const observed = Math.max(...entries.map(([, item]) => item.edge)) - Math.max(now, this.started ?? now);
    this.liveOffset = changed || this.anchor === null ? observed : Math.max(this.liveOffset, observed);
    const live = now + this.liveOffset - delay;
    // Fragment durations and production jitter can change the desired delay on
    // every arrival. Re-anchoring there replays older pictures, then skips ahead.
    // Underruns hold it, but their lost time must not become permanent latency.
    if (changed || this.anchor === null) { this.anchor = edge - delay; this.rate = 1; }
    else {
      const elapsed = Math.max(0, now - this.started), normal = this.anchor + elapsed;
      // Recover smoothly using elapsed time, stopping extra advancement at the
      // safe live position. Neither packet arrivals nor repeated polls can jump it.
      const recovery = Math.min(elapsed * (this.rate - 1), Math.max(0, live - normal));
      this.anchor = Math.max(this.anchor, Math.min(edge, normal + recovery));
      const backlog = live - this.anchor;
      // One extra fragment of hysteresis absorbs delivery jitter. Once catching
      // up, return to normal only when the accumulated backlog is recovered.
      this.rate = backlog > (this.rate > 1 ? this.toleranceMilliseconds : delay + this.toleranceMilliseconds) ? this.catchUpRate : 1;
    }
    this.started = changed ? now : Math.max(this.started ?? now, now);
    this.edge = edge; this.active = entries.map(([key]) => key);
    this.streams = new Map(entries.map(([key, item]) => [key, item.streamId]));
    return this.anchor;
  }
  synchronize(key, clock, video, play) {
    if (!video.buffered.length) { this.remove(key); return false; }
    const at = this.update(key, clock, video.buffered.end(video.buffered.length - 1));
    if (at === null) return null; // Compatibility with older servers without fragment timing.
    const position = clock.media(at);
    const inside = position && Array.from({ length: video.buffered.length }, (_, i) => i)
      .some(i => position.seconds >= video.buffered.start(i) && position.seconds < video.buffered.end(i));
    if (!inside) { video.pause(); return false; }
    // Browsers impose decoder-specific rate limits. Seeks still correct the shared
    // cursor when continuous playback cannot use the measured source pace.
    try { if (video.playbackRate !== this.rate / position.rate) video.playbackRate = this.rate / position.rate; }
    catch { video.playbackRate = 1; }
    const difference = (position.seconds - video.currentTime) * position.rate * 1000;
    // Hold an ahead camera until the shared cursor catches up. Seeking backwards
    // to repair ordinary live jitter repeats footage and makes camera clocks jump.
    if (difference < -this.toleranceMilliseconds) { video.pause(); return false; }
    if (!video.seeking && difference > this.toleranceMilliseconds) video.currentTime = position.seconds;
    if (video.paused) play();
    return true;
  }
}
