'use strict';

// Voice-only downsampling to 8 kHz. Bounded messages; no microphone signal reaches an output.
class TalkbackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.width = sampleRate / 8000;
    this.remaining = this.width; this.sum = 0;
    this.frame = new ArrayBuffer(320); this.view = new DataView(this.frame); this.samples = 0; this.pending = 0;
    this.port.onmessage = () => { this.pending = Math.max(0, this.pending - 1); };
  }
  process(inputs, outputs) {
    for (const channels of outputs) for (const output of channels) output.fill(0);
    const input = inputs[0]?.[0];
    if (!input) return true;
    for (const sample of input) {
      let available = 1;
      while (available > 1e-8) {
        const weight = Math.min(available, this.remaining);
        this.sum += sample * weight; this.remaining -= weight; available -= weight;
        if (this.remaining < 1e-8) {
          const value = Math.max(-1, Math.min(1, this.sum / this.width));
          this.view.setInt16(this.samples++ * 2, Math.round(value * (value < 0 ? 32768 : 32767)), true);
          this.sum = 0; this.remaining = this.width;
          if (this.samples === 160) {
            if (this.pending >= 2) { this.port.postMessage({ type: 'overflow' }); return false; }
            this.port.postMessage(this.frame, [this.frame]); this.pending++;
            this.frame = new ArrayBuffer(320); this.view = new DataView(this.frame); this.samples = 0;
          }
        }
      }
    }
    return true;
  }
}
registerProcessor('scv-talkback', TalkbackProcessor);
