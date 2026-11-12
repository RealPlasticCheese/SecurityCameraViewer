#!/usr/bin/env bash
set -euo pipefail
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
version=${1:-1.21.1}
destination=${2:-"$repo_dir/artifacts/web"}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { printf '%s\n' 'Use a numeric MediaMTX release version.' >&2; exit 1; }
case $(uname -m) in
    x86_64) architecture=amd64 ;;
    aarch64|arm64) architecture=arm64 ;;
    armv7l) architecture=armv7 ;;
    *) printf '%s\n' 'Unsupported architecture; install MediaMTX manually.' >&2; exit 1 ;;
esac
for command in curl sha256sum tar; do command -v "$command" >/dev/null || { printf 'Install %s first.\n' "$command" >&2; exit 1; }; done
asset="mediamtx_v${version}_linux_${architecture}.tar.gz"
release="https://github.com/bluenviron/mediamtx/releases/download/v$version"
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
curl --fail --location --silent --show-error "$release/$asset" -o "$temporary/$asset"
curl --fail --location --silent --show-error "$release/checksums.sha256" -o "$temporary/checksums.sha256"
expected=$(awk -v asset="$asset" '$2 == asset || $2 == "*" asset {print $1}' "$temporary/checksums.sha256")
[[ $expected =~ ^[a-fA-F0-9]{64}$ ]] || { printf '%s\n' 'The release checksum was not found.' >&2; exit 1; }
actual=$(sha256sum "$temporary/$asset")
[[ ${actual%% *} == "$expected" ]] || { printf '%s\n' 'MediaMTX checksum verification failed.' >&2; exit 1; }
tar -xzf "$temporary/$asset" -C "$temporary" mediamtx
mkdir -p -- "$destination"
install -m 755 "$temporary/mediamtx" "$destination/mediamtx"
printf 'MediaMTX %s installed in %s. The web server manages its configuration and lifecycle.\n' "$version" "$destination"
