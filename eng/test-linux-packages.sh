#!/usr/bin/env bash
set -euo pipefail
packages=$(realpath "$1")
root=$(cd -- "$(dirname -- "$0")/.." && pwd)
for image in ubuntu:24.04 fedora:43; do
    docker run --rm -v "$packages:/packages:ro" -v "$root/packaging/linux:/tests:ro" -v "$root/tests/check_packaged_notices.py:/check-packaged-notices.py:ro" "$image" bash /tests/smoke-test.sh
done
