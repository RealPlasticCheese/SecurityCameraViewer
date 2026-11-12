#!/usr/bin/env bash
set -euo pipefail
tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
version=${1:-latest}
[[ $version == latest || $version =~ ^[0-9]{4}\.[0-9]{2}\.[0-9]{2}$ ]] || { echo 'Use latest or a yt-dlp release date.' >&2; exit 1; }
case $(uname -m) in
    x86_64) asset=yt-dlp_linux ;;
    aarch64|arm64) asset=yt-dlp_linux_aarch64 ;;
    *) asset=yt-dlp; command -v python3 >/dev/null || { echo 'Install Python 3.10+ or yt-dlp manually for this architecture.' >&2; exit 1; } ;;
esac
for command in curl sha256sum; do command -v "$command" >/dev/null || { printf 'Install %s first.\n' "$command" >&2; exit 1; }; done
release="https://github.com/yt-dlp/yt-dlp/releases/latest/download"
if [[ $version != latest ]]; then release="https://github.com/yt-dlp/yt-dlp/releases/download/$version"; fi
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
curl --fail --location --silent --show-error "$release/$asset" -o "$temporary/$asset"
curl --fail --location --silent --show-error "$release/SHA2-256SUMS" -o "$temporary/SHA2-256SUMS"
expected=$(awk -v asset="$asset" '$2 == asset || $2 == "*" asset {print $1}' "$temporary/SHA2-256SUMS")
[[ $expected =~ ^[a-fA-F0-9]{64}$ ]] || { echo 'The yt-dlp release checksum was not found.' >&2; exit 1; }
actual=$(sha256sum "$temporary/$asset")
[[ ${actual%% *} == "$expected" ]] || { echo 'yt-dlp checksum verification failed.' >&2; exit 1; }
mkdir -p -- "$tool_dir/artifacts"
install -m 755 "$temporary/$asset" "$tool_dir/artifacts/yt-dlp"
printf 'yt-dlp installed in %s/artifacts. YouTube also needs Deno or Node.js on PATH.\n' "$tool_dir"
