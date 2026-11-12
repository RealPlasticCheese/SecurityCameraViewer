# Camera browser decoder

This separately replaceable WebAssembly module contains the FFmpeg 8.0.1 H.264 and
HEVC software decoders, libavutil and libswscale. No video encoders, demuxers,
networking, GPL or nonfree components are enabled. The wrapper is LGPL-2.1-or-later.
FFmpeg is copyright its contributors. See LICENSE-LGPL.txt and FFmpeg's source
COPYING files for the complete notices.

The JavaScript loader includes Emscripten runtime support under its MIT license;
see LICENSE-EMSCRIPTEN.txt. The compiler version is 3.1.64.

Published servers supply the corresponding source at these relative URLs:

- [FFmpeg 8.0.1 source](ffmpeg-8.0.1.tar.xz)
- [Streaming wrapper source](decoder.c)
- [Build configuration and link command](build.sh)

During development, the wrapper and build script are in `tools/browser-decoder/`.
The unchanged FFmpeg source is also available from
https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz (SHA-256
`05ee0b03119b45c0bdb4df654b96802e909e0a752f72e4fe3794f487229e5a41`).

To rebuild, install Emscripten 3.1.64 from https://github.com/emscripten-core/emsdk,
source its environment, extract the pinned FFmpeg source, and run:

```sh
bash tools/browser-decoder/build.sh /absolute/path/to/ffmpeg-8.0.1
```

The generated `decoder.mjs` and `decoder.wasm` can be replaced independently of
the server. They are loaded only when a camera uses Browser software decoding.
No third-party CDN is contacted by the viewer. Publishing fetches and verifies
the source archive once into the intermediate build directory, then includes it
in the server's static files; it is never downloaded by ordinary playback.
