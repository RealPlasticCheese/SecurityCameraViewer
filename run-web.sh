#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v ffmpeg >/dev/null; then
  echo 'FFmpeg is required. On Debian/Ubuntu/Mint: sudo apt install ffmpeg' >&2
  exit 1
fi
if [[ ! -f artifacts/web/SecurityCameraViewer.Web.dll ]]; then bash build-web.sh; fi
if [[ -x artifacts/web/SecurityCameraViewer.Web ]]; then
  exec artifacts/web/SecurityCameraViewer.Web "$@"
fi
exec dotnet artifacts/web/SecurityCameraViewer.Web.dll "$@"
