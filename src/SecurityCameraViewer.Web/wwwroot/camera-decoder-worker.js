'use strict';
let modulePromise, module, decoder, video, canvas, context, epoch = 0, origin, displayClock;
importScripts('/camera-mp4.js');
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
function reset() {
  ++epoch;
  if (decoder) module._camera_destroy(decoder);
  decoder = video = origin = displayClock = undefined;
}
async function initialize(bytes) {
  reset();
  const version = epoch, description = CameraMp4.initialize(new Uint8Array(bytes));
  modulePromise ||= import('/vendor/camera-decoder/decoder.mjs').then(library => library.default());
  module = await modulePromise;
  if (version !== epoch) return;
  video = description;
  decoder = module._camera_create(video.hevc ? 1 : 0);
  if (!decoder) throw new Error('Insufficient resources to start the browser decoder.');
}
async function fragment(bytes, timing) {
  if (!decoder || !video) throw new Error('Browser decoder needs camera initialization.');
  const version = epoch;
  let decodeMilliseconds = 0;
  for (const packet of CameraMp4.packets(new Uint8Array(bytes), video)) {
    if (version !== epoch) return;
    const ptr = module._malloc(packet.data.length);
    if (!ptr) throw new Error('Insufficient client memory for camera decoding.');
    const started = performance.now();
    let result;
    try { module.HEAPU8.set(packet.data, ptr); result = module._camera_push(decoder, ptr, packet.data.length, packet.pts); }
    finally { module._free(ptr); }
    decodeMilliseconds += performance.now() - started;
    if (result < 0) throw new Error('Browser decoder rejected the camera bitstream.');
    while (true) {
      const started = performance.now(), decoded = module._camera_next(decoder);
      decodeMilliseconds += performance.now() - started;
      if (decoded < 0) throw new Error('Browser decoder could not decode the camera frame.');
      if (!decoded) break;
      const pts = module._camera_pts(decoder) / 1000;
      origin ??= pts; displayClock ??= performance.now();
      const sample = timing?.samples.findLast(item => item.start <= pts / 1000);
      const synchronizedDue = sample ? timing.sentAt + (sample.origin + pts * sample.rate - timing.position) / (timing.rate ?? 1) - (performance.timeOrigin || 0) : null;
      const due = synchronizedDue ?? displayClock + pts - origin, wait = due - performance.now();
      if (wait > 0) await delay(wait);
      if (version !== epoch) return;
      if (performance.now() - due > (sample ? timing.toleranceMilliseconds : 1000)) {
        // A network pause or background tab must not leave every future frame permanently late.
        if (!sample) displayClock += performance.now() - due;
        postMessage({ type: 'drop', count: 1 }); decodeMilliseconds = 0; continue;
      }
      const width = module._camera_width(decoder), height = module._camera_height(decoder);
      if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      const pixels = module._camera_pixels(decoder);
      const image = new ImageData(new Uint8ClampedArray(module.HEAPU8.buffer, pixels, width * height * 4), width, height);
      context.putImageData(image, 0, 0);
      postMessage({ type: 'frame', width, height, decodeMilliseconds, mediaTime: pts / 1000 }); decodeMilliseconds = 0;
    }
  }
}
let queue = Promise.resolve();
self.onmessage = event => {
  const message = event.data;
  if (message.type === 'canvas') { canvas = message.canvas; context = canvas.getContext('2d'); return; }
  if (message.type === 'reset') { reset(); return; }
  const version = epoch;
  queue = queue.then(async () => {
    try {
      if (version !== epoch) { postMessage({ type: 'done', id: message.id }); return; }
      if (message.type === 'initialize') await initialize(message.data);
      else if (message.type === 'fragment') await fragment(message.data, message.timing);
      else throw new Error('Unknown browser decoder command.');
      postMessage({ type: 'done', id: message.id });
    } catch { postMessage({ type: 'error', id: message.id, message: 'Browser software decoding failed. Choose Automatic or Server JPEG previews.' }); }
  });
};
