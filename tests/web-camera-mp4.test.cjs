const assert = require('node:assert/strict');
const { test } = require('node:test');
const { CameraMp4 } = require('../src/SecurityCameraViewer.Web/wwwroot/camera-mp4.js');
const box = (type, ...data) => { const bytes = Buffer.concat(data); const header = Buffer.alloc(8); header.writeUInt32BE(bytes.length + 8); header.write(type, 4); return Buffer.concat([header, bytes]); };
const u32 = (...values) => { const bytes = Buffer.alloc(values.length * 4); values.forEach((value, i) => bytes.writeUInt32BE(value >>> 0, i * 4)); return bytes; };
function init(hevc = false) {
  const sample = Buffer.alloc(78); sample.writeUInt16BE(64, 24); sample.writeUInt16BE(48, 26);
  const configuration = hevc ? Buffer.from([1, 1, ...new Array(19).fill(0), 3, 1, 32, 0, 1, 0, 2, 64, 1]) : Buffer.from([1, 100, 0, 40, 255, 225, 0, 2, 103, 1, 1, 0, 2, 104, 1]);
  return box('moov', box('trak', box('tkhd', u32(0, 0, 0, 1)), box('mdia', box('mdhd', u32(0, 0, 0, 1000)),
    box('hdlr', u32(0, 0), Buffer.from('vide')), box('minf', box('stbl', box('stsd', u32(0, 1), box(hevc ? 'hvc1' : 'avc1', sample, box(hevc ? 'hvcC' : 'avcC', configuration))))))),
    box('mvex', box('trex', u32(0, 1, 1, 40, 6, 0x2000000))));
}
function fragment(cts = 0, version = 0) {
  const payload = Buffer.from([0, 0, 0, 2, 101, 7]);
  let moof = box('moof', box('traf', box('tfhd', u32(0x20000, 1)), box('tfdt', version ? u32(0x1000000, 0, 100) : u32(0, 100)), box('trun', u32(0x1000801, 1, 0, cts))));
  moof.writeUInt32BE(moof.length + 8, moof.length - 8);
  return Buffer.concat([moof, box('mdat', payload)]);
}

test('AVC/HEVC parameters and dimensions are extracted without decoding video', () => {
  for (const hevc of [false, true]) {
    const info = CameraMp4.initialize(init(hevc));
    assert.equal(info.hevc, hevc); assert.equal(info.width, 64); assert.equal(info.height, 48);
    assert.equal(info.id, 1); assert.equal(info.timeScale, 1000); assert.equal(info.lengthSize, 4);
    assert.equal(info.parameters.length, hevc ? 1 : 2);
  }
});
test('compressed samples preserve decoder order, keyframe parameter sets and signed composition time', () => {
  const video = CameraMp4.initialize(init());
  for (const version of [0, 1]) {
    const [packet] = CameraMp4.packets(fragment(-40, version), video);
    assert.equal(packet.pts, 60000); assert.equal(packet.keyframe, true);
    assert.deepEqual([...packet.data], [0,0,0,1,103,1,0,0,0,1,104,1,0,0,0,1,101,7]);
  }
});
test('metadata and sample truncation cannot escape their owning boxes', () => {
  const bytes = init();
  for (let size = 0; size < bytes.length; size++) assert.throws(() => CameraMp4.initialize(bytes.subarray(0, size)));
  const data = fragment(), video = CameraMp4.initialize(bytes);
  for (let size = 0; size < data.length; size++) assert.throws(() => CameraMp4.packets(data.subarray(0, size), video));
  const oversized = Buffer.from(data); oversized.writeUInt32BE(99, oversized.length - 6);
  assert.throws(() => CameraMp4.packets(oversized, video), /NAL/);
  assert.throws(() => CameraMp4.boxes(Buffer.from([0,0,0,1,109,111,111,118])), /extended/);
});
test('empty, oversized and unsupported MP4 samples are rejected', () => {
  const info = CameraMp4.initialize(init()), bytes = fragment();
  const invalid = Buffer.from(bytes); invalid.writeUInt32BE(0xffffffff, invalid.indexOf('trun') + 8);
  assert.throws(() => CameraMp4.packets(invalid, info), /count/);
  const base = Buffer.from(bytes); base.writeUInt32BE(1, base.indexOf('tfhd') + 4);
  assert.throws(() => CameraMp4.packets(base, info), /data base/);
  assert.throws(() => CameraMp4.packets(bytes, { ...info, defaults: { duration: 0, size: 0, flags: 0 } }), /bounds/);
  const codec = Buffer.from(init()); codec.write('vp09', codec.indexOf('avc1'));
  assert.throws(() => CameraMp4.initialize(codec), /H.264/);
});
module.exports = { init, fragment };
