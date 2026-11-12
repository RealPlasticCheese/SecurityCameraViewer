#!/usr/bin/env bash
set -euo pipefail
packages=$(realpath "$1")
root=$(cd -- "$(dirname -- "$0")/.." && pwd)
for image in ubuntu:24.04 fedora:43; do
    docker run --rm -v "$packages:/packages:ro" -v "$root/packaging/linux:/tests:ro" "$image" bash /tests/smoke-test.sh
done
