'use strict';

// Test-only: copy this page/script into a demo publish's wwwroot. Never deploy them with the application.
const result = document.getElementById('result'), run = document.getElementById('run');
const NativeSocket = WebSocket, NativeContext = AudioContext;
const nativeGetUserMedia = navigator.mediaDevices.getUserMedia;
let frames = 0, requests = 0, synthetic, oscillator, stream, contexts = [];
const player = new PushToTalk(() => { result.textContent = player.message || 'Released.'; });

function check(condition, message) { if (!condition) throw new Error(message); }
async function delay(ms) { await new Promise(resolve => setTimeout(resolve, ms)); }

run.onclick = async () => {
  run.disabled = true; frames = 0; requests = 0; contexts = [];
  let outcome;
  try {
    check(['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname), 'This harness requires a loopback server.');
    const state = await (await fetch('/api/state')).json();
    check(state.demo === true, 'This harness requires the --demo server and local camera fixture.');
    check(state.settings.host === '127.0.0.1' && state.settings.rtspPort === 18554 && state.settings.sources.length === 0,
      'This harness requires the unmodified loopback demo configuration.');
    const capability = await (await fetch('/api/cameras/1/talkback')).json();
    check(capability.supported, 'Start the loopback fixture with a channel 1 speaker track.');
    synthetic = new NativeContext(); await synthetic.resume();
    const destination = synthetic.createMediaStreamDestination();
    oscillator = synthetic.createOscillator(); oscillator.frequency.value = 440;
    const gain = synthetic.createGain(); gain.gain.value = .12;
    oscillator.connect(gain); gain.connect(destination); oscillator.start();
    stream = destination.stream;
    navigator.mediaDevices.getUserMedia = async () => { requests++; return stream; };
    window.AudioContext = class extends NativeContext { constructor(...args) { super(...args); contexts.push(this); } };
    window.WebSocket = class extends NativeSocket {
      send(bytes) { if (bytes instanceof ArrayBuffer) frames++; super.send(bytes); }
    };
    await player.start(1, 'Synthetic fixture');
    check(player.channel === 1 && player.message.startsWith('Talking'), 'Speaker handshake did not complete.');
    await delay(1500);
    check(player.channel === 1 && frames >= 50, 'Real AudioWorklet did not deliver continuous PCM frames.');
    player.stop(); await delay(150);
    check(requests === 1 && stream.getTracks().every(track => track.readyState === 'ended'), 'Release did not stop synthetic input tracks.');
    check(contexts.every(context => context.state === 'closed'), 'Release did not close capture AudioContexts.');
    check(player.channel === null && !player.socket, 'Release retained the camera speaker connection.');
    const stoppedFrames = frames; await delay(300);
    check(frames === stoppedFrames, 'Audio continued after release.');
    outcome = `PASS: ${frames} real AudioWorklet PCM frames sent through the speaker WebSocket.\nPASS: release stopped all input tracks, contexts and further frames.\nNo real microphone was opened; no local sound was played.`;
  } catch (error) { outcome = `FAIL: ${error.message}`; }
  finally {
    player.stop(player.message); oscillator?.stop(); await synthetic?.close();
    window.AudioContext = NativeContext; window.WebSocket = NativeSocket;
    navigator.mediaDevices.getUserMedia = nativeGetUserMedia;
    run.disabled = false;
    result.textContent = outcome;
  }
};
