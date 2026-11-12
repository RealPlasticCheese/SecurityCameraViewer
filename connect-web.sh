#!/usr/bin/env bash
set -euo pipefail
if [[ $# -eq 0 ]]; then
  echo 'Usage: bash connect-web.sh http://server:5080/ [--browser chrome|chromium|edge|brave|default]' >&2
  exit 1
fi
cd "$(dirname "$0")"
viewer=artifacts/web
if [[ -f SecurityCameraViewer.Web.dll ]]; then viewer=.; fi
if [[ ! -f "$viewer/SecurityCameraViewer.Web.dll" ]]; then bash build-web.sh; fi
if [[ -x "$viewer/SecurityCameraViewer.Web" ]]; then
  exec "$viewer/SecurityCameraViewer.Web" --connect "$@"
fi
exec dotnet "$viewer/SecurityCameraViewer.Web.dll" --connect "$@"
