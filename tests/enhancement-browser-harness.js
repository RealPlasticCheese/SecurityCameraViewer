'use strict';

document.getElementById('run').onclick = () => {
  const result = document.getElementById('result');
  const processor = new CameraEnhancementProcessor(fx, document);
  const source = document.createElement('canvas'); source.width = source.height = 64;
  const context = source.getContext('2d');
  const neutral = { brightness: 0, contrast: 0, gamma: 1, denoise: 0, sharpness: 0, radius: 1.2, saturation: 0, vibrance: 0 };
  const lines = [];
  function check(condition, name) { if (!condition) throw new Error(name); lines.push('PASS: ' + name); }
  function image(pixel) {
    const data = context.createImageData(64, 64);
    for (let y = 0; y < 64; y++) for (let x = 0; x < 64; x++) {
      const color = pixel(x, y), offset = (y * 64 + x) * 4;
      data.data.set([...color, 255], offset);
    }
    context.putImageData(data, 0, 0);
  }
  function render(settings = neutral) {
    processor.render(source, settings);
    const gl = processor.canvas.getContext('experimental-webgl');
    const pixels = new Uint8Array(64 * 64 * 4);
    gl.readPixels(0, 0, 64, 64, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
    return pixels;
  }
  function mean(pixels) {
    let sum = 0; for (let i = 0; i < pixels.length; i += 4) sum += pixels[i]; return sum / (pixels.length / 4);
  }
  function variance(pixels) {
    const average = mean(pixels); let sum = 0;
    for (let i = 0; i < pixels.length; i += 4) sum += (pixels[i] - average) ** 2;
    return sum / (pixels.length / 4);
  }
  try {
    image(() => [128, 128, 128]);
    const identity = render();
    check(Math.abs(mean(identity) - 128) <= 1, 'Neutral filters preserve original pixels');
    check(mean(render({ ...neutral, brightness: 20 })) > mean(identity) + 30, 'Brightness changes actual output pixels');
    check(mean(render({ ...neutral, gamma: 1.5 })) > mean(identity) + 20, 'Gamma lifts captured shadow detail');
    image(() => [180, 60, 30]);
    const gray = render({ ...neutral, saturation: -100 });
    check(Math.abs(gray[0] - gray[1]) <= 1 && Math.abs(gray[1] - gray[2]) <= 1, 'Saturation reaches grayscale without geometry changes');
    let random = 731;
    image(() => { random = (random * 1664525 + 1013904223) >>> 0; const v = 128 + random % 41 - 20; return [v, v, v]; });
    const noisy = render(), denoised = render({ ...neutral, denoise: 100 });
    check(variance(denoised) < variance(noisy) * 0.5, 'Edge-preserving denoise reduces measured grain variance');
    image(x => { const v = x < 30 ? 64 : x === 30 ? 88 : x === 31 ? 112 : x === 32 ? 144 : x === 33 ? 168 : 192; return [v, v, v]; });
    const originalEdge = render(), sharpEdge = render({ ...neutral, sharpness: 100, radius: 2 });
    const offset = (32 * 64 + 33) * 4;
    check(sharpEdge[offset] > originalEdge[offset], 'Unsharp mask strengthens the existing edge');
    result.textContent = lines.join('\n') + '\nAll 6 pixel tests passed.';
  } catch (error) {
    result.textContent = lines.join('\n') + '\nFAIL: ' + error.message;
  } finally {
    processor.dispose();
  }
};
