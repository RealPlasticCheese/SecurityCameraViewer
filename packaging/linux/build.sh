#!/usr/bin/env bash
set -euo pipefail
version=$1
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT
mkdir -p "$stage/usr/lib/security-camera-viewer" "$stage/usr/lib/systemd/system" "$stage/usr/bin" "$stage/usr/share/doc/security-camera-viewer"
cp -a /publish/. "$stage/usr/lib/security-camera-viewer/"
chmod 755 "$stage/usr/lib/security-camera-viewer/SecurityCameraViewer.Web" "$stage/usr/lib/security-camera-viewer/mediamtx" "$stage/usr/lib/security-camera-viewer/setup.py"
cp /packaging/security-camera-viewer.service "$stage/usr/lib/systemd/system/"
printf '#!/bin/sh\nexec python3 /usr/lib/security-camera-viewer/setup.py "$@"\n' > "$stage/usr/bin/security-camera-viewer-setup"
chmod 755 "$stage/usr/bin/security-camera-viewer-setup"
cp /publish/INSTALL.md "$stage/usr/share/doc/security-camera-viewer/"
for format in deb rpm; do
    dependencies=()
    if [[ $format == deb ]]; then
        for dependency in python3 ffmpeg libc6 libgcc-s1 libstdc++6 libicu-dev "libssl3 | libssl3t64" zlib1g ca-certificates passwd; do dependencies+=(-d "$dependency"); done
    else
        for dependency in python3 /usr/bin/ffmpeg glibc libgcc libstdc++ libicu openssl-libs zlib ca-certificates shadow-utils; do dependencies+=(-d "$dependency"); done
    fi
    fpm -s dir -t "$format" -n security-camera-viewer -v "$version" --architecture x86_64 \
        --maintainer 'Frank Tzanabetis' --description 'Security Camera Viewer web server' \
        --url 'https://plasticcheese.com' --license 'MIT (see THIRD-PARTY-NOTICES.md)' \
        --after-install /packaging/after-install.sh --before-remove /packaging/before-remove.sh --after-remove /packaging/after-remove.sh \
        "${dependencies[@]}" --package /output/ -C "$stage" .
done
