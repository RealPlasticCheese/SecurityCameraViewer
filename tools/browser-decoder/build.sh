#!/usr/bin/env bash
set -euo pipefail
# Emscripten 3.1.64; FFmpeg 8.0.1, without GPL or nonfree components.
# Usage: bash tools/browser-decoder/build.sh /absolute/path/to/ffmpeg-8.0.1
repo=$(cd "$(dirname "$0")/../.." && pwd)
source=$(cd "$1" && pwd)
output="$repo/src/SecurityCameraViewer.Web/wwwroot/vendor/camera-decoder"
mkdir -p "$output"
cd "$source"
if [[ "${2:-}" != "--link-only" ]]; then
emconfigure bash ./configure --cc=emcc --host-cc=emcc --cxx=em++ --ar=emar --ranlib=emranlib --nm=emnm \
  --enable-cross-compile --target-os=none --arch=wasm32 --cpu=generic \
  --disable-x86asm --disable-inline-asm --disable-asm --disable-runtime-cpudetect \
  --disable-programs --disable-doc --disable-debug --disable-autodetect \
  --disable-network --disable-pthreads --disable-everything \
  --enable-decoder=h264,hevc --enable-swscale --disable-avdevice --disable-avformat \
  --disable-avfilter --disable-swresample --extra-cflags='-O3' --extra-ldflags='-O3'
make -j4
fi
emcc -O3 "$repo/tools/browser-decoder/decoder.c" -I. -I./ \
  libavcodec/libavcodec.a libswscale/libswscale.a libavutil/libavutil.a \
  -sMODULARIZE=1 -sEXPORT_ES6=1 -sENVIRONMENT=web,worker,node -sALLOW_MEMORY_GROWTH=1 \
  -sINITIAL_MEMORY=67108864 -sSTACK_SIZE=5242880 -sFILESYSTEM=0 \
  -sEXPORTED_FUNCTIONS='["_malloc","_free","_camera_create","_camera_destroy","_camera_push","_camera_next","_camera_width","_camera_height","_camera_pixels","_camera_pts"]' \
  -o "$output/decoder.mjs"
# Keep generated loader whitespace stable for repository validation.
sed -i 's/[[:blank:]]*$//' "$output/decoder.mjs"
