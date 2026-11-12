'use strict';

// The camera gateway emits fragmented MP4 with default-base-is-moof and a single video track.
// Keep compressed samples intact; the software decoder receives Annex B packets in decode order.
class CameraMp4 {
  static boxes(bytes, start = 0, end = bytes.length) {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), result = [];
    for (let at = start; at < end;) {
      if (end - at < 8) throw new Error('Truncated MP4 header.');
      let size = view.getUint32(at), header = 8;
      if (size === 1) {
        if (end - at < 16) throw new Error('Truncated extended MP4 header.');
        size = Number(view.getBigUint64(at + 8)); header = 16;
      } else if (size === 0) size = end - at;
      if (!Number.isSafeInteger(size) || size < header || size > end - at) throw new Error('Invalid MP4 box size.');
      result.push({ type: String.fromCharCode(...bytes.subarray(at + 4, at + 8)), start: at, data: at + header, end: at + size });
      at += size;
    }
    return result;
  }
  static child(bytes, owner, type) {
    const box = this.boxes(bytes, owner.data, owner.end).find(box => box.type === type);
    if (!box) throw new Error(`Missing MP4 ${type}.`);
    return box;
  }
  static reader(bytes, box, offset = 0) {
    let at = box.data + offset;
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const read = (size, method) => {
      if (at + size > box.end) throw new Error('Truncated MP4 metadata.');
      const value = view[method](at); at += size; return value;
    };
    return { u8: () => read(1, 'getUint8'), u16: () => read(2, 'getUint16'), u32: () => read(4, 'getUint32'),
      i32: () => read(4, 'getInt32'), u64: () => { const value = Number(read(8, 'getBigUint64')); if (!Number.isSafeInteger(value)) throw new Error('MP4 timestamp exceeds precision.'); return value; },
      skip: count => { if (at + count > box.end) throw new Error('Truncated MP4 metadata.'); at += count; },
      data: count => { if (at + count > box.end) throw new Error('Truncated codec parameters.'); const data = bytes.slice(at, at + count); at += count; return data; } };
  }
  static initialize(bytes) {
    const movie = this.boxes(bytes).find(box => box.type === 'moov');
    if (!movie) throw new Error('Missing MP4 initialization.');
    for (const track of this.boxes(bytes, movie.data, movie.end).filter(box => box.type === 'trak')) {
      const media = this.child(bytes, track, 'mdia'), handler = this.child(bytes, media, 'hdlr');
      const r = this.reader(bytes, handler, 8);
      if (String.fromCharCode(...r.data(4)) !== 'vide') continue;
      const tkhd = this.child(bytes, track, 'tkhd'), mdhd = this.child(bytes, media, 'mdhd');
      const id = this.reader(bytes, tkhd, bytes[tkhd.data] === 1 ? 20 : 12).u32();
      const timeScale = this.reader(bytes, mdhd, bytes[mdhd.data] === 1 ? 20 : 12).u32();
      if (!timeScale || !id) throw new Error('Invalid video time scale or track.');
      const table = this.child(bytes, this.child(bytes, media, 'minf'), 'stbl');
      const descriptions = this.child(bytes, table, 'stsd');
      const sample = this.boxes(bytes, descriptions.data + 8, descriptions.end)[0];
      if (!sample || !['avc1', 'avc3', 'hvc1', 'hev1'].includes(sample.type)) throw new Error('Software decoding supports H.264 and H.265.');
      const hevc = sample.type === 'hvc1' || sample.type === 'hev1';
      const width = this.reader(bytes, sample, 24).u16(), height = this.reader(bytes, sample, 26).u16();
      const configuration = this.child(bytes, { data: sample.data + 78, end: sample.end }, hevc ? 'hvcC' : 'avcC');
      const config = this.reader(bytes, configuration), parameters = [];
      let lengthSize;
      if (hevc) {
        config.skip(21); lengthSize = (config.u8() & 3) + 1;
        const arrays = config.u8();
        for (let array = 0; array < arrays; array++) {
          config.u8(); const count = config.u16();
          for (let nal = 0; nal < count; nal++) parameters.push(config.data(config.u16()));
        }
      } else {
        config.skip(4); lengthSize = (config.u8() & 3) + 1;
        const sps = config.u8() & 31;
        for (let nal = 0; nal < sps; nal++) parameters.push(config.data(config.u16()));
        const pps = config.u8();
        for (let nal = 0; nal < pps; nal++) parameters.push(config.data(config.u16()));
      }
      if (!width || !height || !parameters.length) throw new Error('Missing video dimensions or codec parameters.');
      const defaults = { duration: 0, size: 0, flags: 0 };
      const mvex = this.boxes(bytes, movie.data, movie.end).find(box => box.type === 'mvex');
      for (const box of mvex ? this.boxes(bytes, mvex.data, mvex.end).filter(box => box.type === 'trex') : []) {
        const r = this.reader(bytes, box, 4);
        if (r.u32() !== id) continue;
        r.u32(); defaults.duration = r.u32(); defaults.size = r.u32(); defaults.flags = r.u32();
      }
      return { id, timeScale, lengthSize, parameters, defaults, hevc, width, height };
    }
    throw new Error('Missing video track.');
  }
  static annexB(nals) {
    const output = new Uint8Array(nals.reduce((size, nal) => size + 4 + nal.length, 0));
    let at = 0;
    for (const nal of nals) { output.set([0, 0, 0, 1], at); output.set(nal, at + 4); at += nal.length + 4; }
    return output;
  }
  static packets(bytes, video) {
    const boxes = this.boxes(bytes), moof = boxes.find(box => box.type === 'moof'), mdat = boxes.find(box => box.type === 'mdat');
    if (!moof || !mdat) throw new Error('Missing video fragment.');
    const result = [], view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    for (const traf of this.boxes(bytes, moof.data, moof.end).filter(box => box.type === 'traf')) {
      const header = this.child(bytes, traf, 'tfhd'), r = this.reader(bytes, header);
      const flags = r.u32() & 0xffffff;
      if (r.u32() !== video.id) continue;
      if (flags & 1 || !(flags & 0x20000)) throw new Error('Unsupported MP4 data base.');
      if (flags & 2) r.u32();
      const duration = flags & 8 ? r.u32() : video.defaults.duration;
      const size = flags & 16 ? r.u32() : video.defaults.size;
      const defaultFlags = flags & 32 ? r.u32() : video.defaults.flags;
      const clock = this.child(bytes, traf, 'tfdt'), time = this.reader(bytes, clock);
      const version = time.u32() >>> 24;
      if (version > 1) throw new Error('Unsupported MP4 clock.');
      let dts = version ? time.u64() : time.u32(), offset = mdat.data;
      for (const run of this.boxes(bytes, traf.data, traf.end).filter(box => box.type === 'trun')) {
        const r = this.reader(bytes, run), full = r.u32(), flags = full & 0xffffff, count = r.u32();
        if (flags & 1) offset = moof.start + r.i32();
        const firstFlags = flags & 4 ? r.u32() : defaultFlags;
        if (count > mdat.end - mdat.data) throw new Error('Invalid MP4 sample count.');
        for (let sample = 0; sample < count; sample++) {
          const sampleDuration = flags & 256 ? r.u32() : duration, sampleSize = flags & 512 ? r.u32() : size;
          const sampleFlags = flags & 1024 ? r.u32() : sample === 0 ? firstFlags : defaultFlags;
          const cts = flags & 2048 ? full >>> 24 === 1 ? r.i32() : r.u32() : 0;
          if (!sampleDuration || !sampleSize || offset < mdat.data || offset + sampleSize > mdat.end) throw new Error('Invalid video sample bounds.');
          const keyframe = !(sampleFlags & 0x10000), nals = keyframe ? [...video.parameters] : [];
          const end = offset + sampleSize;
          while (offset < end) {
            if (offset + video.lengthSize > end) throw new Error('Truncated video NAL size.');
            let length = 0;
            for (let byte = 0; byte < video.lengthSize; byte++) length = length * 256 + view.getUint8(offset++);
            if (!length || offset + length > end) throw new Error('Invalid video NAL bounds.');
            nals.push(bytes.subarray(offset, offset + length)); offset += length;
          }
          result.push({ data: this.annexB(nals), keyframe, pts: (dts + cts) * 1000000 / video.timeScale });
          dts += sampleDuration;
        }
      }
    }
    return result;
  }
}
if (typeof module !== 'undefined') module.exports = { CameraMp4 };
