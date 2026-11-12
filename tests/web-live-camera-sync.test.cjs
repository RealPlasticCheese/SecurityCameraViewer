const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');
const { CameraFrameClock, LiveCameraClock } = runInNewContext(
  readFileSync('src/SecurityCameraViewer.Web/wwwroot/camera-frame-clock.js', 'utf8') + '\n({ CameraFrameClock, LiveCameraClock })');
const origin = Date.parse('2026-10-11T11:00:00Z');
function clock(mediaOrigin = 0, rate = 1, count = 12, wallOrigin = origin) {
  const value = new CameraFrameClock();
  for (let i = 0; i < count; i++) value.update({ streamId: 'first', startSeconds: mediaOrigin + i,
    endSeconds: mediaOrigin + i + 1, producedAt: new Date(wallOrigin + (i + 1) * rate * 1000).toISOString() });
  return value;
}
function video(start, end, currentTime = start) {
  return { currentTime, playbackRate: 1, paused: true, seeking: false,
    buffered: { length: 1, start: () => start, end: () => end }, pause() { this.paused = true; } };
}
const play = value => () => { value.paused = false; };

test('continuous live playback never rewinds or jumps its UTC cursor when real cameras alternate short and long fragments', () => {
  const fixtures = JSON.parse(readFileSync('tests/fixtures/live-sync-variable-fragments.json', 'utf8'));
  let now = 0;
  const shared = new LiveCameraClock(() => now);
  const players = fixtures.map((fixture, index) => ({ fixture, clock: new CameraFrameClock(), key: {},
    video: video(index * 100, index * 100), index: 0, previous: index * 100, received: 0 }));
  let previousUtc = null;
  for (now = 0; now < 15000; now += 20) {
    for (const player of players) {
      const v = player.video;
      if (!v.paused) v.currentTime = Math.min(v.buffered.end(), v.currentTime + .02 * v.playbackRate);
      while (player.fixture.packets[player.index]?.received <= now) {
        const packet = player.fixture.packets[player.index++], mediaOrigin = players.indexOf(player) * 100;
        player.clock.update({ streamId: 'steady', startSeconds: mediaOrigin + packet.start, endSeconds: mediaOrigin + packet.end,
          producedAt: new Date(origin + packet.produced).toISOString() });
        v.buffered.end = () => mediaOrigin + packet.end;
        player.received++;
      }
      if (player.received) shared.synchronize(player.key, player.clock, v, play(v));
    }
    const utc = shared.current();
    if (now > 5000 && previousUtc !== null) {
      assert.ok(utc >= previousUtc, `The shared cursor went backwards at ${now} ms.`);
      assert.ok(utc - previousUtc <= 20.001, `The cursor jumped forward at ${now} ms.`);
      for (const player of players) assert.ok(player.video.currentTime >= player.previous,
        `Camera ${players.indexOf(player)} replayed an older picture at ${now} ms.`);
      for (const player of players) assert.ok(player.video.currentTime - player.previous <=
        .02 * player.video.playbackRate + 2 * shared.toleranceMilliseconds / 1000,
        `Camera ${players.indexOf(player)} skipped footage at ${now} ms.`);
    }
    previousUtc = utc;
    for (const player of players) player.previous = player.video.currentTime;
  }
  assert.ok(players.every(player => player.video.currentTime > player.fixture.packets[0].start + players.indexOf(player) * 100 + 10),
    'All cameras continue to display fresh footage rather than freezing to avoid backwards playback.');
});

test('a camera ahead of the shared live cursor holds its current picture and resumes as the cursor catches up', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), c = clock(), v = video(0, 12, 11.5), key = {};
  shared.synchronize(key, c, v, play(v));
  assert.equal(v.currentTime, 11.5); assert.equal(v.paused, true);
  now = 500; shared.synchronize(key, c, v, play(v));
  assert.equal(v.currentTime, 11.5); assert.equal(v.paused, false);
});

test('buffer underrun and subsequent delivery do not reset the cursor backwards or instantly skip the stalled duration', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), c = clock(), key = {}, v = video(0, 12);
  shared.synchronize(key, c, v, play(v));
  now = 1000; assert.equal(shared.current(), origin + 12000);
  now = 2000; assert.equal(shared.current(), origin + 12000);
  c.update({ streamId: 'first', startSeconds: 12, endSeconds: 13, producedAt: new Date(origin + 13000).toISOString() });
  v.buffered.end = () => 13;
  assert.equal(shared.update(key, c, 13), origin + 12000);
  now += 20; assert.equal(shared.current(), origin + 12020);
});

test('live timing restarts once for a new stream epoch and backwards timer readings cannot replay elapsed time', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), c = clock(), key = {};
  assert.equal(shared.update(key, c, 12), origin + 11000);
  now = 100; assert.equal(shared.current(), origin + 11100);
  now = 50; assert.equal(shared.current(), origin + 11100);
  now = 100; assert.equal(shared.current(), origin + 11100);
  c.reset(); c.update({ streamId: 'new', startSeconds: 0, endSeconds: 1, producedAt: new Date(origin + 30000).toISOString() });
  assert.equal(shared.update(key, c, 1), origin + 29000);
  now = 120; assert.equal(shared.current(), origin + 29020);
  shared.remove(key); assert.equal(shared.active.length, 0); assert.equal(shared.streams.size, 0);
});

test('wall-to-media conversion inverts calibrated time across camera origins and source rates', () => {
  for (const mediaOrigin of [0, 120, 10000]) for (const rate of [.95, 1, 1.2]) {
    const c = clock(mediaOrigin, rate);
    for (const seconds of [mediaOrigin + 5.25, mediaOrigin + 10.75]) {
      const p = c.media(c.time(seconds));
      assert.ok(Math.abs(p.seconds - seconds) < 1e-6); assert.ok(Math.abs(p.rate - rate) < 1e-6);
    }
    for (const at of [NaN, Infinity, origin - 10000, origin + 999999]) assert.equal(c.media(at), null);
  }
});

test('simultaneous live cameras select the same UTC picture despite independent media origins and delivery delays', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), a = clock(100), b = clock(500);
  const av = video(100, 112), bv = video(500, 512), ak = {}, bk = {};
  assert.equal(shared.synchronize(ak, a, av, play(av)), true);
  now = 700;
  assert.equal(shared.synchronize(bk, b, bv, play(bv)), true);
  shared.synchronize(ak, a, av, play(av));
  assert.equal(a.time(av.currentTime), b.time(bv.currentTime));
  assert.equal(av.currentTime, 111); assert.equal(bv.currentTime, 511);
  now += 500;
  shared.synchronize(ak, a, av, play(av)); shared.synchronize(bk, b, bv, play(bv));
  assert.equal(a.time(av.currentTime), origin + 11500);
  assert.equal(a.time(av.currentTime), b.time(bv.currentTime));
});

test('live rate compensation and a shared wall clock prevent accumulation of cross-camera drift', () => {
  for (const rate of [.95, 1.05, 1.2]) {
    let now = 0;
    const shared = new LiveCameraClock(() => now), a = clock(0, 1), b = new CameraFrameClock();
    for (let i = 0; i < 12; i++) b.update({ streamId: 'first', startSeconds: 100 + i / rate,
      endSeconds: 100 + (i + 1) / rate, producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
    const av = video(0, 12), bv = video(100, 100 + 12 / rate), ak = {}, bk = {};
    shared.synchronize(ak, a, av, play(av)); shared.synchronize(bk, b, bv, play(bv));
    shared.synchronize(ak, a, av, play(av));
    assert.ok(Math.abs(a.time(av.currentTime) - b.time(bv.currentTime)) <= shared.toleranceMilliseconds);
    assert.ok(Math.abs(bv.playbackRate - 1 / rate) < 1e-6);
    for (let i = 12; i < 1800; i++) {
      now = (i - 11) * 1000;
      a.update({ streamId: 'first', startSeconds: i, endSeconds: i + 1, producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
      // Both sources cover the same physical instant but have different relative clocks.
      const media = (i + 1) / rate;
      b.update({ streamId: 'first', startSeconds: 100 + media - 1 / rate, endSeconds: 100 + media,
        producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
      av.buffered.end = () => i + 1; bv.buffered.end = () => 100 + media;
      shared.synchronize(ak, a, av, play(av)); shared.synchronize(bk, b, bv, play(bv));
      shared.synchronize(ak, a, av, play(av));
      assert.ok(Math.abs(a.time(av.currentTime) - b.time(bv.currentTime)) <= shared.toleranceMilliseconds);
    }
    assert.ok(a.samples.length < 20 && b.samples.length < 25, 'Long runs retain only timing for the live window.');
  }
});

test('a stopped camera cannot hold healthy live cameras indefinitely or renew its freshness with old buffered data', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now, { staleMilliseconds: 8000 }), a = clock(), b = clock();
  const ak = {}, bk = {}, av = video(0, 12), bv = video(0, 12);
  shared.synchronize(ak, a, av, play(av)); shared.synchronize(bk, b, bv, play(bv));
  now = 8001;
  a.update({ streamId: 'first', startSeconds: 19, endSeconds: 20, producedAt: new Date(origin + 20000).toISOString() });
  av.buffered.end = () => 20;
  shared.synchronize(ak, a, av, play(av));
  assert.equal(shared.current(), origin + 19000);
  shared.synchronize(bk, b, bv, play(bv));
  assert.equal(shared.current(), origin + 19000); assert.equal(bv.paused, true);
  assert.equal(shared.active.length, 1);
  shared.remove(ak); assert.equal(shared.current(), null);
});

test('joining, leaving, source reconnects and missing buffered coverage reset membership without mixing epochs', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), a = clock(), av = video(0, 12), key = {};
  shared.synchronize(key, a, av, play(av)); shared.remove(key); assert.equal(shared.current(), null);
  a.reset();
  av.currentTime = 0; // A new MediaSource resets the actual video element's media position.
  a.update({ streamId: 'new', startSeconds: 0, endSeconds: 1, producedAt: new Date(origin + 30000).toISOString() });
  av.buffered.end = () => 1;
  shared.synchronize(key, a, av, play(av)); assert.equal(shared.current(), origin + 29000);
  assert.equal(av.currentTime, 0);
  now = 100;
  av.buffered = { length: 1, start: () => .8, end: () => 1 };
  assert.equal(shared.synchronize(key, a, av, play(av)), false); assert.equal(av.paused, true);
  av.buffered = { length: 0 };
  assert.equal(shared.synchronize(key, a, av, play(av)), false); assert.equal(shared.members.size, 0);
});

test('live synchronization respects seek-in-progress and supports old-server timing fallback', () => {
  const shared = new LiveCameraClock(() => 0), a = clock(), av = video(0, 12, 10.5);
  av.seeking = true;
  shared.synchronize({}, a, av, play(av)); assert.equal(av.currentTime, 10.5);
  assert.equal(shared.synchronize({}, new CameraFrameClock(), av, play(av)), null);
  for (const options of [{ staleMilliseconds: 0 }, { staleMilliseconds: Infinity }, { toleranceMilliseconds: -1 }, { toleranceMilliseconds: NaN }])
    assert.throws(() => new LiveCameraClock(() => 0, options), /positive and finite/);
});

test('unsupported or clamped decoder rates still follow the shared cursor through corrective seeks', () => {
  for (const rejects of [true, false]) {
    const shared = new LiveCameraClock(() => 0), a = clock(0, 1.2), av = video(0, 12);
    let playbackRate = 1;
    Object.defineProperty(av, 'playbackRate', { get: () => playbackRate, set: value => {
      if (rejects && value !== 1) throw new RangeError('Unsupported decoder pace');
      playbackRate = 1;
    } });
    assert.equal(shared.synchronize({}, a, av, play(av)), true);
    assert.equal(av.currentTime, 11); assert.equal(av.paused, false); assert.equal(av.playbackRate, 1);
  }
});

test('repeated delivery stalls recover live latency automatically without rewinding or jumping the shared cursor', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now);
  const players = [1, 1.2].map((rate, index) => ({ key: {}, clock: new CameraFrameClock(),
    video: video(index * 100, index * 100 + 12), rate, index, delivered: 12 }));
  // Both cameras initially cover the same UTC edge despite different source paces.
  for (const p of players) {
    for (let i = 0; i < 12; i++) p.clock.update({ streamId: 'steady', startSeconds: p.index * 100 + i / p.rate,
      endSeconds: p.index * 100 + (i + 1) / p.rate, producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
    p.video.buffered.end = () => p.index * 100 + p.delivered / p.rate;
    shared.synchronize(p.key, p.clock, p.video, play(p.video));
  }
  let previous = shared.current(), recovered = 0, accelerated = false;
  for (now = 20; now <= 240000; now += 20) {
    for (const p of players) {
      if (!p.video.paused) p.video.currentTime = Math.min(p.video.buffered.end(), p.video.currentTime + .02 * p.video.playbackRate);
      const stalled = p.index === 1 && now % 60000 >= 10000 && now % 60000 < 14000;
      if (!stalled) while (p.delivered < 12 + Math.floor(now / 1000)) {
        const i = p.delivered++;
        p.clock.update({ streamId: 'steady', startSeconds: p.index * 100 + i / p.rate,
          endSeconds: p.index * 100 + (i + 1) / p.rate, producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
      }
      shared.synchronize(p.key, p.clock, p.video, play(p.video));
    }
    const at = shared.current();
    assert.ok(at >= previous, 'Live recovery must never replay older footage.');
    assert.ok(at - previous <= 20 * shared.catchUpRate + .001, 'Recovery advances by elapsed time, never an instantaneous jump.');
    assert.equal(shared.current(), at, 'Polling twice cannot advance the cursor twice.');
    if (shared.rate > 1) accelerated = true;
    if (now % 60000 === 59000) {
      assert.ok(shared.edge - at <= 1500, `A recovered viewer must stay near live after repeated stalls: ${shared.edge - at} ms behind.`);
      assert.equal(shared.rate, 1, 'Normal speed resumes once the backlog is recovered.');
      for (const p of players) assert.ok(Math.abs(p.clock.time(p.video.currentTime) - at) <= shared.toleranceMilliseconds + 1);
      recovered++;
    }
    previous = at;
  }
  assert.ok(accelerated, 'Buffered footage must be caught up without a user pressing 2x.');
  assert.equal(recovered, 4);
});

test('catch-up pace is configurable, resets with membership and epochs, and compensates native source pace', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now, { catchUpRate: 1.25 }), c = clock(0, 1.2), v = video(0, 12), key = {};
  shared.synchronize(key, c, v, play(v));
  now = 5000; shared.current();
  for (let i = 12; i < 18; i++) c.update({ streamId: 'first', startSeconds: i, endSeconds: i + 1,
    producedAt: new Date(origin + (i + 1) * 1200).toISOString() });
  v.buffered.end = () => 18;
  shared.synchronize(key, c, v, play(v));
  assert.equal(shared.rate, 1.25); assert.ok(Math.abs(v.playbackRate - 1.25 / 1.2) < 1e-6);
  const at = shared.current(); now += 100;
  assert.equal(shared.current(), at + 125);
  c.reset(); c.update({ streamId: 'new', startSeconds: 0, endSeconds: 1, producedAt: new Date(origin + 30000).toISOString() });
  shared.update(key, c, 1); assert.equal(shared.rate, 1);
  shared.remove(key); assert.equal(shared.rate, 1);
  for (const catchUpRate of [0, 1, -1, NaN, Infinity])
    assert.throws(() => new LiveCameraClock(() => 0, { catchUpRate }), /catch-up rate/i);
});

test('decoder rate failures and backwards timer readings cannot prevent or double-count catch-up', () => {
  for (const rejects of [false, true]) {
    let now = 0;
    const shared = new LiveCameraClock(() => now), c = clock(), v = video(0, 12), key = {};
    Object.defineProperty(v, 'playbackRate', { get: () => 1, set: value => {
      if (rejects && value !== 1) throw new RangeError('Unsupported decoder pace');
    } });
    shared.synchronize(key, c, v, play(v));
    now = 5000; shared.current();
    for (let i = 12; i < 18; i++) c.update({ streamId: 'first', startSeconds: i, endSeconds: i + 1,
      producedAt: new Date(origin + (i + 1) * 1000).toISOString() });
    v.buffered.end = () => 18;
    shared.synchronize(key, c, v, play(v)); assert.equal(shared.rate, shared.catchUpRate);
    now += 100; shared.synchronize(key, c, v, play(v));
    const at = shared.current(); assert.equal(c.time(v.currentTime), at); assert.equal(v.playbackRate, 1);
    now -= 50; assert.equal(shared.current(), at);
    now += 50; assert.equal(shared.current(), at);
    shared.remove(key); assert.equal(shared.rate, 1);
  }
});

test('decoder backpressure cannot disguise accumulated delay as a small local buffer', () => {
  let now = 0;
  const shared = new LiveCameraClock(() => now), c = clock(), v = video(0, 12), key = {};
  shared.synchronize(key, c, v, play(v));
  now = 5000; shared.current();
  // A consumer acknowledging queued packets slowly may have only one second
  // buffered even though the packet's production time is several seconds old.
  c.update({ streamId: 'first', startSeconds: 12, endSeconds: 13, producedAt: new Date(origin + 13000).toISOString() });
  v.buffered.end = () => 13;
  shared.synchronize(key, c, v, play(v));
  assert.ok(shared.edge - shared.current() <= 1000);
  assert.equal(shared.rate, shared.catchUpRate, 'Freshest known production time plus elapsed time reveals the hidden queue delay.');
  const at = shared.current(); now += 100; shared.synchronize(key, c, v, play(v));
  assert.equal(shared.current(), at + 110); assert.equal(v.playbackRate, shared.catchUpRate);
  assert.equal(c.time(v.currentTime), shared.current());
});
