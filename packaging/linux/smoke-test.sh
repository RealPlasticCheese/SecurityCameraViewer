#!/usr/bin/env bash
set -euo pipefail
source /etc/os-release
if [[ $ID == ubuntu ]]; then
    apt-get update -qq
    apt-get install -y --no-install-recommends /packages/*.deb curl
    dpkg-deb -f /packages/*.deb Maintainer | grep -F 'Frank Tzanabetis'
    test "$(dpkg-deb -f /packages/*.deb Homepage)" = https://plasticcheese.com
    package=(dpkg -i /packages/*.deb)
    remove=(apt-get remove -y security-camera-viewer)
else
    # Fedora's ffmpeg-free provides /usr/bin/ffmpeg for package lifecycle tests.
    dnf install -y --setopt=install_weak_deps=False /packages/*.rpm ffmpeg-free curl util-linux
    rpm -q --qf '%{PACKAGER}\n' security-camera-viewer | grep -F 'Frank Tzanabetis'
    test "$(rpm -q --qf '%{URL}' security-camera-viewer)" = https://plasticcheese.com
    package=(rpm -Uvh --replacepkgs /packages/*.rpm)
    remove=(dnf remove -y security-camera-viewer)
fi
test -f /etc/security-camera-viewer/host.json
test -x /usr/lib/security-camera-viewer/mediamtx
python3 /check-packaged-notices.py /usr/lib/security-camera-viewer
printf 'keep-settings' > /var/lib/security-camera-viewer/preservation-fixture
"${package[@]}"
test "$(cat /var/lib/security-camera-viewer/preservation-fixture)" == keep-settings
cd /tmp
runuser -u security-camera-viewer -- /usr/lib/security-camera-viewer/SecurityCameraViewer.Web \
    --host-config /etc/security-camera-viewer/host.json --urls http://127.0.0.1:5089 > /tmp/scv.log 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
ready=false
for attempt in {1..60}; do
    if curl --fail --silent http://127.0.0.1:5089/manifest.webmanifest > /tmp/manifest; then ready=true; break; fi
    kill -0 "$pid" || { cat /tmp/scv.log; exit 1; }
    sleep 1
done
"$ready" || { cat /tmp/scv.log; exit 1; }
grep -q 'name' /tmp/manifest
kill "$pid"
wait "$pid" || true
trap - EXIT
"${remove[@]}"
test -f /etc/security-camera-viewer/host.json
test "$(cat /var/lib/security-camera-viewer/preservation-fixture)" == keep-settings
