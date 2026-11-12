const assert = require('node:assert/strict');
const { test } = require('node:test');
const { PlaybackPolicy, CameraMediaCodecs, PlaybackStatistics } = require('../src/SecurityCameraViewer.Web/wwwroot/camera-playback-policy.js');

test('automatic chooses native first, then software, then shared server previews', () => {
  assert.equal(PlaybackPolicy.choose('automatic', { native: true, browser: true }).method, 'native');
  assert.equal(PlaybackPolicy.choose('automatic', { native: false, browser: true }).method, 'browser');
  assert.equal(PlaybackPolicy.choose('automatic', { native: false, browser: false }).method, 'server');
  assert.match(PlaybackPolicy.choose('automatic', { browser: true }).message, /client CPU/);
  assert.match(PlaybackPolicy.choose('automatic', {}).message, /server CPU/);
});

test('explicit methods never silently fallback and report unavailable native/software decoders', () => {
  for (const method of ['native', 'browser']) {
    const unavailable = PlaybackPolicy.choose(method, {});
    assert.equal(unavailable.method, null);
    assert.match(unavailable.message, /unavailable/);
    assert.equal(PlaybackPolicy.choose(method, { native: true, browser: true }).method, method);
  }
  assert.equal(PlaybackPolicy.choose('server', {}).method, 'server');
  assert.throws(() => PlaybackPolicy.choose('bad', {}), /playback method/);
});

test('automatic explains the resolved method and the reason for a fallback', () => {
  const capabilities = { native: false, browser: true, reason: 'H.265 unsupported by this browser' };
  const choice = PlaybackPolicy.choose('automatic', capabilities);
  assert.match(choice.message, /Automatic.*Browser software decoding/);
  assert.match(choice.message, /H.265 unsupported/);
  assert.match(PlaybackPolicy.choose('automatic', { native: true }).message, /Automatic.*Native video/);
  assert.equal(PlaybackPolicy.choose('automatic', { native: true, browser: true }, new Set(['native'])).method, 'browser');
  assert.equal(PlaybackPolicy.choose('automatic', { native: true, browser: true }, new Set(['native', 'browser'])).method, 'server');
});

test('benchmark statistics separate received/displayed/dropped frames and reset by playback mode', () => {
  let time = 0;
  const stats = new PlaybackStatistics(() => time);
  stats.frame(4); stats.frame(6); stats.drop(3); time = 2000;
  assert.deepEqual(stats.snapshot(), { frames: 2, dropped: 3, framesPerSecond: 1, meanDecodeMilliseconds: 5 });
  stats.reset();
  assert.deepEqual(stats.snapshot(), { frames: 0, dropped: 0, framesPerSecond: 0, meanDecodeMilliseconds: 0 });
  assert.throws(() => stats.frame(NaN), /decode/);
  assert.throws(() => stats.drop(-1), /dropped/);
});

test('codec descriptions identify both supported video sample entries and delivered AAC audio', () => {
  for (const [sample, expected] of [['avc1.640028', 'H.264'], ['avc3.42001F', 'H.264'], ['hvc1.1.6.L93.B0', 'H.265'], ['hev1.1.6.L93.B0', 'H.265']]) {
    assert.deepEqual(CameraMediaCodecs.describe(`video/mp4; codecs="${sample}"`), { video: expected, audio: null });
    for (const audio of ['mp4a.40.2', 'mp4a.40.5', 'mp4a.40.29'])
      assert.deepEqual(CameraMediaCodecs.describe(`video/mp4; codecs="${sample},${audio}"`), { video: expected, audio: 'AAC' });
  }
});

test('codec descriptions handle parameter whitespace, case, ordering and quoted or unquoted values', () => {
  for (const mime of [
    'video/mp4; CODECS = " MP4A.40.2, HVC1.1.6.L93.B0 "; other=1',
    "video/mp4; other=1; codecs='avc3.640028, mp4a.40.2'",
    'video/mp4; codecs=avc1.640028,mp4a.40.2'
  ]) {
    const codecs = CameraMediaCodecs.describe(mime);
    assert.ok(['H.264', 'H.265'].includes(codecs.video)); assert.equal(codecs.audio, 'AAC');
  }
});

test('missing, malformed and unrecognized codec metadata never invents a video or audio type', () => {
  for (const mime of [null, undefined, {}, 1, '', 'video/mp4', 'video/mp4; codecs=""', 'video/mp4; codecs="hvc1',
    'video/mp4; codecs="avc10.640028,mp4a.40.200"', 'video/mp4; codecs="unknown"', 'video/mp4; othercodecs="avc1.640028"'])
    assert.deepEqual(CameraMediaCodecs.describe(mime), { video: null, audio: null });
  assert.deepEqual(CameraMediaCodecs.describe('audio/mp4; codecs="mp4a.40.2"'), { video: null, audio: 'AAC' });
});
