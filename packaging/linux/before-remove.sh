#!/bin/sh
set -eu
# fpm passes upgrade/1 on upgrade and remove/0 on removal, depending on package manager.
case "${1:-}" in upgrade|1) exit 0 ;; esac
if [ -d /run/systemd/system ]; then
    systemctl disable --now security-camera-viewer.service || true
fi
# Settings, recordings, certificates and the service account are deliberately retained.
