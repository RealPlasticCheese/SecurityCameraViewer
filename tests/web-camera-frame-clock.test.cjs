const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const CameraFrameClock = runInNewContext(readFileSync('src/SecurityCameraViewer.Web/wwwroot/camera-frame-clock.js', 'utf8') + '\nCameraFrameClock');
const start = Date.parse('2026-10-10T00:00:00Z');
const packet = (startSeconds, endSeconds, delay = 0, streamId = 'first') => ({ streamId, startSeconds, endSeconds,
  producedAt: new Date(start + endSeconds * 1000 + delay).toISOString() });

test('media presentation timestamps map to server frame time independently of browser clock and transport delay', () => {
  const clock = new CameraFrameClock();
  assert.equal(clock.time(0), null);
  assert.equal(clock.update(packet(0, 1)), true);
  assert.equal(clock.time(.2), start + 200);
  assert.equal(clock.time(1), start + 1000);
  assert.equal(clock.time(NaN), null);
  assert.equal(clock.time(Infinity), null);
});

test('delayed production cannot push detections ahead and subsequent fragments recover lower pipeline latency', () => {
  const clock = new CameraFrameClock();
  clock.update(packet(0, 1, 200));
  assert.equal(clock.time(.2), start + 400);
  clock.update(packet(1, 2, 900));
  assert.equal(clock.time(.2), start + 400);
  clock.update(packet(2, 3, 0));
  assert.equal(clock.time(2.2), start + 2200);
  assert.equal(clock.time(1.2), start + 1200);
  assert.equal(clock.time(.2), start + 400); // Later pacing does not rewrite this earlier calibration.
});

test('reconnect epochs and explicit resets do not reuse an old MP4 origin', () => {
  const clock = new CameraFrameClock(); clock.update(packet(10, 11));
  clock.update({ ...packet(0, 1, 5000, 'second') });
  assert.equal(clock.time(.2), start + 5200);
  clock.reset(); assert.equal(clock.time(.2), null);
});

test('invalid or missing metadata never invents a playback timestamp', () => {
  const clock = new CameraFrameClock();
  for (const value of [undefined, {}, { ...packet(0, 1), producedAt: 'invalid' },
    { ...packet(0, 1), startSeconds: NaN }, packet(1, 1), packet(2, 1),
    { ...packet(0, 1), endSeconds: Infinity }, { ...packet(0, 1), streamId: '' }]) {
    assert.equal(clock.update(value), false); assert.equal(clock.time(0), null);
  }
});

test('a paced source cannot accumulate enough clock drift to hide retained detections', () => {
  for (const rate of [.95, 1.05, 1.2]) {
    const clock = new CameraFrameClock();
    for (let second = 0; second < 3600; second++) {
      clock.update(packet(second, second + 1, (second + 1) * (rate - 1) * 1000));
      const frame = second + .5;
      assert.ok(Math.abs(clock.time(frame) - (start + frame * rate * 1000)) <= Math.abs(rate - 1) * 1500 + 1);
    }
    assert.ok(clock.samples.length < 15, 'Timing metadata follows the live media retention window.');
  }
});

test('a source pause recovers without a new stream initialization and does not retime old buffered pictures', () => {
  const clock = new CameraFrameClock(); clock.update(packet(0, 1));
  const oldFrame = clock.time(.5);
  clock.update(packet(1, 2, 40000)); clock.update(packet(2, 3, 40000));
  assert.equal(clock.time(2.5), start + 42500);
  assert.equal(clock.time(.5), oldFrame);
  clock.update(packet(3, 4, 40000)); assert.equal(clock.time(3.5), start + 43500);
});

test('isolated delayed flushing and burst catch-up preserve the neighboring buffered frames', () => {
  const clock = new CameraFrameClock(); clock.update(packet(0, 1));
  clock.update(packet(1, 2, 900)); assert.equal(clock.time(1.5), start + 1500);
  clock.update(packet(2, 3)); assert.equal(clock.time(1.5), start + 1500);
  clock.update(packet(3, 4, 2000)); clock.update(packet(4, 5, 1000)); clock.update(packet(5, 6));
  assert.equal(clock.time(3.5), start + 3500); assert.equal(clock.time(4.5), start + 4500);
});

test('timestamp discontinuities and backwards server clocks discard obsolete calibration', () => {
  const clock = new CameraFrameClock(); clock.update(packet(10, 11));
  clock.update(packet(0, 1, 20000)); assert.equal(clock.time(.5), start + 20500);
  clock.update(packet(1, 2, -10000)); assert.equal(clock.time(1.5), start - 8500);
  assert.equal(clock.samples.length, 1);
  clock.update(packet(0, 3, 5000)); assert.equal(clock.samples.length, 1);
  assert.equal(clock.time(.5), start + 5500);
});

test('a change in source pace cannot shift an older buffered picture forward', () => {
  const clock = new CameraFrameClock();
  for (let second = 0; second < 6; second++) clock.update(packet(second, second + 1, (second + 1) * 200));
  clock.update(packet(6, 7, 1200)); clock.update(packet(7, 8, 1200));
  const buffered = clock.time(7.5);
  clock.update(packet(8, 9, 1200));
  assert.equal(clock.time(7.5), buffered); assert.equal(clock.time(8.5), start + 9700);
});

test('retention follows the media window, duplicates are harmless and malformed updates preserve valid timing', () => {
  const clock = new CameraFrameClock(2);
  for (let second = 0; second < 20; second++) clock.update(packet(second, second + 1));
  assert.equal(clock.time(0), null); assert.equal(clock.time(18.5), start + 18500);
  const retained = clock.samples.length;
  clock.update(packet(19, 20)); assert.equal(clock.samples.length, retained);
  assert.equal(clock.update({ ...packet(20, 21), producedAt: 'bad' }), false);
  assert.equal(clock.time(19.5), start + 19500);
  for (const budget of [0, -1, NaN, Infinity]) assert.throws(() => new CameraFrameClock(budget), /retention/);
});
