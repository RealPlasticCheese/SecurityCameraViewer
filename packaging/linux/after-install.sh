#!/bin/sh
set -eu
getent group security-camera-viewer >/dev/null || groupadd --system security-camera-viewer
id security-camera-viewer >/dev/null 2>&1 || useradd --system --gid security-camera-viewer --home-dir /var/lib/security-camera-viewer --shell /usr/sbin/nologin security-camera-viewer
install -d -o security-camera-viewer -g security-camera-viewer -m 0700 /var/lib/security-camera-viewer
if [ -d /run/systemd/system ]; then
    active=false
    enabled=false
    systemctl is-active --quiet security-camera-viewer.service && active=true
    systemctl is-enabled --quiet security-camera-viewer.service && enabled=true
    /usr/bin/security-camera-viewer-setup configure
    # Preserve existing enabled/disabled policy. Fresh installs are explicitly started by setup.
    systemctl daemon-reload
    if "$enabled"; then systemctl reenable --force security-camera-viewer.service; fi
    if "$active"; then systemctl restart security-camera-viewer.service; fi
else
    # Container/image installation: defer systemd operations until the host is booted.
    python3 -c 'import runpy; m=runpy.run_path("/usr/lib/security-camera-viewer/setup.py"); m["configure"]()'
fi
