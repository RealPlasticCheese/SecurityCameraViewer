'use strict';

// A press owns the mic and socket. Late permission/handshake completions cannot revive a released press.
class PushToTalk {
  constructor(changed) {
    this.changed = changed; this.channel = null; this.version = 0; this.message = '';
    window.addEventListener('blur', () => this.stop());
    window.addEventListener('pagehide', () => this.stop());
    document.addEventListener('visibilitychange', () => { if (document.hidden) this.stop(); });
    document.addEventListener('pointerup', event => { if (event.pointerId === this.pointer) this.stop(); });
    document.addEventListener('pointercancel', event => { if (event.pointerId === this.pointer) this.stop(); });
    document.addEventListener('keyup', event => { if (this.key === event.code) this.stop(); });
  }
  static available() {
    return window.isSecureContext && !!navigator.mediaDevices?.getUserMedia && !!window.AudioContext && !!window.AudioWorkletNode;
  }
  bind(button, channel, name) {
    button.onpointerdown = event => {
      if (event.button !== 0 || !event.isPrimary || button.disabled || this.channel !== null) return;
      event.preventDefault(); button.focus(); this.pointer = event.pointerId;
      button.setPointerCapture(event.pointerId); this.start(channel, name);
    };
    button.onlostpointercapture = event => { if (this.pointer === event.pointerId) this.stop(); };
    button.onkeydown = event => {
      if (!['Space', 'Enter'].includes(event.code) || event.repeat || button.disabled || this.channel !== null) return;
      event.preventDefault(); this.key = event.code; this.start(channel, name);
    };
    button.onblur = () => { if (this.channel === channel) this.stop(); };
    button.onclick = event => event.preventDefault();
    button.oncontextmenu = event => event.preventDefault();
  }
  notify(message) { this.message = message; this.changed(this); }
  async start(channel, name) {
    if (!PushToTalk.available()) { this.notify('Talkback requires HTTPS (or localhost), microphone access and AudioWorklet support.'); return; }
    const version = ++this.version;
    this.channel = channel; this.notify('Requesting microphone access…');
    this.limit = setTimeout(() => this.stop('Talkback stops after 30 seconds. Release and hold again.'), 30000);
    try {
      // Resume within the pointer/key gesture before the asynchronous permission request.
      const context = this.context = new AudioContext();
      const resumed = context.resume(); resumed.catch(() => {});
      const stream = await navigator.mediaDevices.getUserMedia({ video: false, audio: {
        channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true
      } });
      if (this.version !== version) { stream.getTracks().forEach(track => track.stop()); return; }
      this.stream = stream;
      for (const track of stream.getTracks()) track.onended = () => {
        if (this.version === version) this.stop('Microphone disconnected.');
      };
      await resumed;
      await context.audioWorklet.addModule('/talkback-worklet.js');
      if (this.version !== version) return;
      this.notify('Connecting camera speaker…');
      const socket = this.socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/cameras/${channel}/talk`);
      await new Promise((resolve, reject) => {
        this.cancelOpening = () => reject(new Error('Released'));
        socket.onmessage = event => {
          try {
            const message = JSON.parse(event.data);
            if (message.type !== 'ready' || message.sampleRate !== 8000 || message.frameSamples !== 160 ||
                message.channels !== 1 || message.encoding !== 'pcm_s16le') throw new Error('Camera rejected talkback');
            resolve();
          } catch { reject(new Error('Camera rejected talkback')); }
        };
        socket.onerror = () => reject(new Error('Talkback connection failed'));
        socket.onclose = () => reject(new Error('Talkback connection ended'));
      });
      this.cancelOpening = null;
      if (this.version !== version || socket !== this.socket || socket.readyState !== WebSocket.OPEN) return;
      socket.onmessage = () => this.stop('Camera talkback ended. Release and try again.');
      socket.onclose = socket.onerror = () => {
        if (this.version === version) this.stop('Camera talkback ended. Release and try again.');
      };
      const node = this.node = new AudioWorkletNode(context, 'scv-talkback', { channelCount: 1, channelCountMode: 'explicit', numberOfInputs: 1, numberOfOutputs: 1 });
      node.port.onmessage = event => {
        if (this.version !== version || socket !== this.socket) return;
        if (!(event.data instanceof ArrayBuffer) || event.data.byteLength !== 320 || socket.bufferedAmount > 2560) {
          this.stop('Microphone delivery fell behind. Release and try again.'); return;
        }
        if (socket.readyState !== WebSocket.OPEN) { this.stop('Camera talkback disconnected.'); return; }
        try { socket.send(event.data); node.port.postMessage('ack'); }
        catch { this.stop('Camera talkback disconnected.'); }
      };
      node.onprocessorerror = () => { if (this.version === version) this.stop('Microphone processing stopped.'); };
      this.input = context.createMediaStreamSource(stream);
      this.silence = context.createGain(); this.silence.gain.value = 0;
      this.input.connect(node); node.connect(this.silence); this.silence.connect(context.destination);
      this.notify(`Talking to ${name} · release to stop.`);
    } catch (error) {
      if (this.version !== version) return;
      this.stop(error.name === 'NotAllowedError' ? 'Microphone access denied. Check browser permissions.' :
        'Talkback could not start. Check microphone access and camera availability.');
    }
  }
  stop(message = '') {
    ++this.version; clearTimeout(this.limit);
    this.channel = null; this.pointer = null; this.key = null;
    this.cancelOpening?.(); this.cancelOpening = null;
    this.stream?.getTracks().forEach(track => { track.onended = null; track.stop(); }); this.stream = null;
    this.input?.disconnect(); this.input = null;
    this.node?.disconnect(); this.node?.port.close(); this.node = null;
    this.silence?.disconnect(); this.silence = null;
    this.context?.close().catch(() => {}); this.context = null;
    const socket = this.socket; this.socket = null;
    if (socket) {
      socket.onmessage = socket.onerror = socket.onclose = null;
      try { socket.close(); } catch {}
    }
    this.notify(message);
  }
}
