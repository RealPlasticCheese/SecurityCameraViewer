#!/usr/bin/env bash
set -euo pipefail
packages=$(realpath "$1")
health_url=$2
cache=/var/cache/security-camera-viewer
service=security-camera-viewer.service
shopt -s nullglob
files=("$packages"/*.deb)
[[ ${#files[@]} == 1 ]] || { echo 'Expected exactly one DEB package.' >&2; exit 1; }
package=${files[0]}
(cd "$packages"; sha256sum --check "$(basename "$package").sha256")
[[ $health_url == https://* ]] || { echo 'Deployment health URL must use HTTPS.' >&2; exit 1; }
# Preflight before stopping the viewer. Privilege provisioning is an administrator action.
sudo -n true || { echo 'Deployment needs noninteractive package/service privileges; see docs/installation.md.' >&2; exit 1; }
sudo -n install -d -m 700 "$cache"
old_version=$(dpkg-query -W -f='${Version}' security-camera-viewer 2>/dev/null || true)
was_active=false
systemctl is-active --quiet "$service" && was_active=true
was_enabled=false
systemctl is-enabled --quiet "$service" && was_enabled=true
previous="$cache/previous.deb"
can_rollback=false
if [[ -n $old_version ]] && sudo -n test -f "$cache/current.deb"; then
    cached_version=$(sudo -n dpkg-deb -f "$cache/current.deb" Version)
    [[ $cached_version == "$old_version" ]] || { echo 'Cached rollback package does not match installed version.' >&2; exit 1; }
    sudo -n cp "$cache/current.deb" "$previous"
    can_rollback=true
elif [[ -n $old_version ]]; then
    echo 'No matching rollback package cached; retain the installed build package first.' >&2
    exit 1
else
    # First migration retains the old checkout and unit for recovery.
    if sudo -n test -e /etc/security-camera-viewer/host.json; then
        echo 'An existing host.json needs manual migration review before the first deployment.' >&2
        exit 1
    fi
    sudo -n test -f /etc/systemd/system/security-camera-viewer.service || { echo 'Initial migration requires the existing managed service.' >&2; exit 1; }
    sudo -n cp /etc/systemd/system/security-camera-viewer.service "$cache/legacy.service"
fi
recover() {
    echo 'Deployment failed; restoring the previous service.' >&2
    sudo -n systemctl stop "$service" || true
    if "$can_rollback"; then
        sudo -n dpkg --install "$previous" || { echo 'Rollback package installation failed.' >&2; return 1; }
    else
        sudo -n dpkg --remove security-camera-viewer || true
        sudo -n cp "$cache/legacy.service" /etc/systemd/system/security-camera-viewer.service || return 1
        # These files were created by this migration; retaining them prevents a clean retry.
        sudo -n rm -f /etc/security-camera-viewer/host.json /etc/systemd/system/security-camera-viewer.service.pre-package /etc/systemd/system/security-camera-viewer.service.d/packaged-account.conf
    fi
    sudo -n systemctl daemon-reload
    if "$was_enabled"; then sudo -n systemctl reenable --force "$service"; else sudo -n systemctl disable "$service"; fi
    if "$was_active"; then sudo -n systemctl start "$service"; fi
    # Do not restore settings automatically: they may have been changed while running.
}
# Download/resolve dependencies before stopping cameras.
sudo -n apt-get --download-only install -y --allow-downgrades "$package"
trap 'status=$?; recover || true; exit "$status"' ERR
sudo -n systemctl stop "$service"
sudo -n apt-get install -y --allow-downgrades "$package"
sudo -n systemctl reenable --force "$service"
sudo -n systemctl restart "$service"
ready=false
for attempt in {1..60}; do
    if systemctl is-active --quiet "$service" && curl --fail --silent --show-error --max-time 5 "$health_url" >/dev/null; then ready=true; break; fi
    sleep 1
done
"$ready"
sudo -n cp "$package" "$cache/current.deb"
trap - ERR
echo "Deployment healthy: $health_url"
