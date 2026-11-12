#!/usr/bin/env bash
set -euo pipefail
publish=$(realpath "$1")
version=$2
output=$(realpath "$3")
root=$(cd -- "$(dirname -- "$0")/.." && pwd)
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Use a numeric three-part version.' >&2; exit 1; }
test -f "$publish/SecurityCameraViewer.Web"
tar -czf "$output/SecurityCameraViewer-$version-linux-x64.tar.gz" -C "$publish" .
# Packaging tools run in an isolated container; never install them on the camera host.
docker build -t scv-packaging -f "$root/packaging/linux/Dockerfile" "$root/packaging/linux"
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp \
    -v "$publish:/publish:ro" -v "$root/packaging/linux:/packaging:ro" -v "$output:/output" \
    scv-packaging bash /packaging/build.sh "$version"
