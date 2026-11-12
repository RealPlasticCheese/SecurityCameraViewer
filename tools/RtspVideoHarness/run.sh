#!/usr/bin/env bash
set -euo pipefail
tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
skip_default_mediamtx=false
youtube=false
custom_ytdlp=false
help=false
build_args=()
for argument in "$@"; do
    case $argument in
        --) break ;;
        --mediamtx|--help|-h) skip_default_mediamtx=true; build_args=(--skip-mediamtx) ;;
    esac
    case $argument in --youtube) youtube=true ;; --yt-dlp) custom_ytdlp=true ;; --help|-h) help=true ;; esac
done
for argument in "$@"; do
    normalized_argument=${argument,,}
    if [[ $normalized_argument =~ ^https?://([^/]+\.)?(youtube\.com|youtu\.be|youtube-nocookie\.com)(/|$) ]]; then youtube=true; fi
done
if [[ ! -f "$tool_dir/artifacts/RtspVideoHarness.dll" || ( $skip_default_mediamtx == false && ! -x "$tool_dir/artifacts/mediamtx" ) ]]; then
    bash "$tool_dir/build.sh" "${build_args[@]}"
fi
# Preserve the caller's working directory, including when the first run builds the tool.
if [[ $youtube == true && $custom_ytdlp == false && $help == false && ! -x "$tool_dir/artifacts/yt-dlp" ]]; then
    bash "$tool_dir/install-ytdlp.sh"
fi
exec dotnet "$tool_dir/artifacts/RtspVideoHarness.dll" "$@"
