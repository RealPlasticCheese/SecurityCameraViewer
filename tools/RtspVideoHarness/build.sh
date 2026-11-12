#!/usr/bin/env bash
set -euo pipefail
tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_dir=$(CDPATH= cd -- "$tool_dir/../.." && pwd)
skip_mediamtx=false
if [[ ${1:-} == --skip-mediamtx ]]; then skip_mediamtx=true; shift; fi
[[ $# == 0 ]] || { echo 'Usage: build.sh [--skip-mediamtx]' >&2; exit 1; }
dotnet publish "$tool_dir/src/RtspVideoHarness.csproj" -c Release -o "$tool_dir/artifacts" "-p:RestoreConfigFile=$repo_dir/NuGet.Config" --self-contained false
if [[ $skip_mediamtx == false && ! -x "$tool_dir/artifacts/mediamtx" ]]; then
    bash "$repo_dir/install-mediamtx.sh" "${MEDIAMTX_VERSION:-1.21.1}" "$tool_dir/artifacts"
fi
printf 'Built in %s/artifacts. Run: bash %s/run.sh <video-folder-or-YouTube-URL>\n' "$tool_dir" "$tool_dir"
