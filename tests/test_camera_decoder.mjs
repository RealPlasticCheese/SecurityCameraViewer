// Real codec and pixel tests for the exact decoder shipped to browsers.
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import createDecoder from '../src/SecurityCameraViewer.Web/wwwroot/vendor/camera-decoder/decoder.mjs';
const require = createRequire(import.meta.url);
const { CameraMp4 } = require('../src/SecurityCameraViewer.Web/wwwroot/camera-mp4.js');
const root = 'artifacts/browser-decoder/fixtures'; mkdirSync(root, { recursive: true });
const wasmBinary = readFileSync('src/SecurityCameraViewer.Web/wwwroot/vendor/camera-decoder/decoder.wasm');
const module = await createDecoder({ wasmBinary });
function ffmpeg(args) {
  const result = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...args], { encoding: null, maxBuffer: 64 * 1024 * 1024 });
  assert.equal(result.status, 0, result.stderr?.toString()); return result.stdout;
}
const results = [];
for (const codec of ['baseline', 'main', 'high', 'hevc', 'hevc10']) {
  const hevc = codec.startsWith('hevc'), path = `${root}/${codec}.mp4`;
  const options = hevc ? ['-c:v', 'libx265', '-x265-params', 'keyint=24:bframes=2:log-level=error', '-pix_fmt', codec === 'hevc10' ? 'yuv420p10le' : 'yuv420p'] :
    ['-c:v', 'libx264', '-profile:v', codec, '-pix_fmt', 'yuv420p', '-g', '24', '-bf', codec === 'baseline' ? '0' : '2'];
  ffmpeg(['-f', 'lavfi', '-i', 'testsrc2=size=160x96:rate=12', '-t', '4', ...options,
    '-an', '-movflags', '+empty_moov+frag_keyframe+default_base_moof+negative_cts_offsets', '-frag_duration', '1000000', path]);
  const bytes = readFileSync(path), info = CameraMp4.initialize(bytes), boxes = CameraMp4.boxes(bytes), packets = [];
  for (let index = 0; index < boxes.length; index++) if (boxes[index].type === 'moof') {
    const end = boxes[index + 1].end; packets.push(...CameraMp4.packets(bytes.subarray(boxes[index].start, end), info));
  }
  const expected = ffmpeg(['-i', path, '-an', '-pix_fmt', 'rgba', '-f', 'rawvideo', 'pipe:1']);
  const handle = module._camera_create(hevc ? 1 : 0); assert.ok(handle);
  const frames = [], timestamps = []; let milliseconds = 0;
  function drain() {
    while (true) {
      const started = performance.now(), result = module._camera_next(handle); milliseconds += performance.now() - started;
      assert.ok(result >= 0, `Receive frame failed: ${result}`); if (!result) break;
      const width = module._camera_width(handle), height = module._camera_height(handle), ptr = module._camera_pixels(handle);
      assert.equal(width, 160); assert.equal(height, 96);
      frames.push(Buffer.from(module.HEAPU8.subarray(ptr, ptr + width * height * 4))); timestamps.push(module._camera_pts(handle));
    }
  }
  try {
    for (const packet of packets) {
      const ptr = module._malloc(packet.data.length); module.HEAPU8.set(packet.data, ptr);
      const started = performance.now();
      assert.equal(module._camera_push(handle, ptr, packet.data.length, packet.pts), 0);
      milliseconds += performance.now() - started; module._free(ptr); drain();
    }
    assert.equal(module._camera_push(handle, 0, 0, 0), 0); drain();
    const actual = Buffer.concat(frames); assert.equal(frames.length, 48, `${codec} frame count`); assert.equal(actual.length, expected.length);
    for (let index = 1; index < timestamps.length; index++) assert.ok(timestamps[index] >= timestamps[index - 1], 'B-frame presentation order');
    let difference = 0; for (let i = 0; i < actual.length; i++) difference += Math.abs(actual[i] - expected[i]);
    const meanPixelError = difference / actual.length; assert.ok(meanPixelError < 1, `${codec}: unexpected pixel error ${meanPixelError}`);
    results.push({ codec, frames: frames.length, meanPixelError, meanDecodeMilliseconds: milliseconds / frames.length });
    console.log(`PASS: ${codec}, ${frames.length} frames in presentation order, mean pixel error ${meanPixelError.toFixed(4)}`);
  } finally { module._camera_destroy(handle); }
}
assert.ok(module._camera_push(0, 0, 1, 0) < 0); assert.ok(module._camera_next(0) < 0); module._camera_destroy(0);
writeFileSync(`${root}/decoder-results.json`, JSON.stringify(results, null, 2));
