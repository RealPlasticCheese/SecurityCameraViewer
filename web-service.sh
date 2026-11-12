#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
service_name=security-camera-viewer.service
unit_path="/etc/systemd/system/$service_name"
managed_marker='# Managed by SecurityCameraViewer web-service.sh'

usage() {
  cat <<'EOF'
Usage: bash web-service.sh COMMAND [OPTIONS]

Commands:
  install     Install/update the service, enable startup, and start/restart it
  uninstall   Stop, disable, and remove the service; keep all app/data files
  start       Start the installed service
  stop        Stop it until started again (startup remains enabled)
  restart     Restart it after an application update
  enable      Enable startup and start the service
  disable     Disable startup and stop the service
  status      Show systemd status (nonzero when inactive or missing)
  logs        Follow the last 100 journal entries; Ctrl+C exits
  show        Show the installed unit and any systemd overrides

Install options (repeat these when reinstalling with custom settings):
  --user USER           Run as this non-root user (default: caller/SUDO_USER)
  --port PORT           HTTP port (prompted on interactive install; default: existing/5080)
  --settings PATH       Settings JSON (default: user's local app data path)
  --dotnet PATH         dotnet executable (default: find on PATH)
  --ffmpeg PATH         FFmpeg executable (default: find on PATH)
  -h, --help            Show this help

Run from your normal Linux account; sudo is requested for system changes.
Build first with: bash build-web.sh
The service runs artifacts/web in this checkout. Keep that folder in place
and accessible at boot. Install does not change your firewall or passwords.
EOF
}

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

as_root() {
  if (( EUID == 0 )); then "$@"; else sudo -- "$@"; fi
}

as_user() {
  if [[ $(id -un) == "$service_user" ]]; then
    env HOME="$service_home" "$@"
  else
    as_root sudo -u "$service_user" -- env HOME="$service_home" "$@"
  fi
}

require_systemd() {
  [[ $(uname -s) == Linux ]] || fail 'This script requires Linux with systemd (such as Linux Mint).'
  command -v systemctl >/dev/null || fail 'systemctl was not found.'
  [[ -d /run/systemd/system ]] || fail 'systemd must be running as the system service manager.'
}

check_managed_unit() {
  [[ ! -L "$unit_path" ]] || fail "Refusing to replace/remove a symlink: $unit_path"
  if [[ -e "$unit_path" ]] && ! grep -Fxq -- "$managed_marker" "$unit_path"; then
    fail "$unit_path was not created by this script. Back up and remove that unit manually before installing."
  fi
}

# systemd expands % specifiers even in quoted values. ExecStart uses the ':'
# prefix to disable $ expansion, including for paths in user-local installs.
unit_quote() {
  local value=$1
  value=${value//\\/\\\\}
  value=${value//\"/\\\"}
  value=${value//%/%%}
  printf '"%s"' "$value"
}

check_path() {
  [[ $1 == /* ]] || fail "Expected an absolute path: $1"
  [[ $1 != *[[:cntrl:]]* ]] || fail 'Paths must not contain control characters.'
}

find_executable() {
  local resolved
  resolved=$(command -v -- "$1") || fail "Executable not found: $1"
  resolved=$(readlink -f -- "$resolved") || fail "Cannot resolve executable: $1"
  check_path "$resolved"
  [[ -f "$resolved" && -x "$resolved" ]] || fail "Not an executable file: $resolved"
  printf '%s\n' "$resolved"
}

write_unit() {
  local argument
  cat <<EOF
$managed_marker
[Unit]
Description=Security Camera Viewer web server
Wants=network-online.target
After=network-online.target

[Service]
Type=exec
User=$service_user
EOF
  # WorkingDirectory takes a single literal path; spaces need no quotes.
  printf 'WorkingDirectory=%s\n' "${repo_dir//%/%%}"
  printf 'Environment=%s\n' "$(unit_quote "HOME=$service_home")"
  printf 'Environment=ASPNETCORE_ENVIRONMENT=Production\n'
  printf 'ExecStart=:'
  for argument in "${server_command[@]}"; do
    unit_quote "$argument"
    printf ' '
  done
  cat <<'EOF'

Restart=on-failure
RestartSec=5
TimeoutStopSec=30
UMask=0077
NoNewPrivileges=true
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
}

install_service() (
  check_managed_unit
  local account uid data_dir publish_dir runtime_list temp_dir
  account=$(getent passwd "$service_user") || fail "Unknown Linux user: $service_user"
  IFS=: read -r service_user _ uid _ _ service_home _ <<< "$account"
  [[ "$uid" != 0 ]] || fail 'Choose a non-root service account with --user USER.'
  [[ $service_user =~ ^[a-zA-Z_][a-zA-Z0-9_.-]*\$?$ ]] || fail 'Unsupported service account name.'
  check_path "$repo_dir"
  [[ $repo_dir != *\\* && $repo_dir != *[[:space:]] ]] || fail 'The project path cannot contain backslashes or end with whitespace.'
  check_path "$service_home"
  publish_dir="$repo_dir/artifacts/web"
  [[ -f "$publish_dir/SecurityCameraViewer.Web.dll" && -f "$publish_dir/wwwroot/index.html" &&
     -f "$publish_dir/SecurityCameraViewer.Web.runtimeconfig.json" ]] ||
    fail 'Published web server missing or incomplete. Run bash build-web.sh first.'
  as_user test -r "$publish_dir/SecurityCameraViewer.Web.dll" || fail 'Service user cannot read the published server.'
  as_user test -r "$publish_dir/wwwroot/index.html" || fail 'Service user cannot read the published website.'

  ffmpeg_path=$(find_executable "${ffmpeg_path:-ffmpeg}")
  as_user "$ffmpeg_path" -version >/dev/null || fail 'FFmpeg cannot run as the service user.'
  # Use the current publish's runtime declaration, not native files that may
  # remain in the output folder after changing publish modes.
  if grep -q '"includedFrameworks"[[:space:]]*:' "$publish_dir/SecurityCameraViewer.Web.runtimeconfig.json"; then
    [[ -f "$publish_dir/libhostpolicy.so" && -x "$publish_dir/SecurityCameraViewer.Web" ]] ||
      fail 'Self-contained Linux publish is incomplete. Publish for linux-x64 or linux-arm64 and keep the entire output folder.'
    server_command=("$publish_dir/SecurityCameraViewer.Web")
    as_user test -x "${server_command[0]}" || fail 'Service user cannot execute the self-contained server.'
  else
    dotnet_path=$(find_executable "${dotnet_path:-dotnet}")
    runtime_list=$(as_user "$dotnet_path" --list-runtimes) || fail 'dotnet cannot run as the service user.'
    grep -q '^Microsoft.AspNetCore.App 10\.' <<< "$runtime_list" &&
      grep -q '^Microsoft.NETCore.App 10\.' <<< "$runtime_list" ||
      fail 'The .NET 10 ASP.NET Core Runtime is required. Alternatively publish a self-contained Linux build.'
    server_command=("$dotnet_path" "$publish_dir/SecurityCameraViewer.Web.dll")
  fi

  data_dir="$service_home/.local/share"
  if [[ $(id -un) == "$service_user" && -n ${XDG_DATA_HOME:-} ]]; then data_dir=$XDG_DATA_HOME; fi
  settings_path=${settings_path:-$data_dir/SecurityCameraViewer.Web/settings.json}
  settings_path=$(realpath -m -- "$settings_path")
  check_path "$settings_path"
  as_user mkdir -p -- "$(dirname -- "$settings_path")"
  as_user test -w "$(dirname -- "$settings_path")" || fail 'Service user cannot write the settings directory.'
  if [[ -e "$settings_path" ]]; then
    as_user test -r "$settings_path" || fail 'Service user cannot read the settings file.'
  fi
  server_command+=(--urls "http://$listen_address:$port" --settings "$settings_path" --ffmpeg "$ffmpeg_path")

  temp_dir=$(mktemp -d)
  trap 'rm -f -- "$temp_dir/$service_name"; rmdir -- "$temp_dir"' EXIT
  write_unit > "$temp_dir/$service_name"
  if command -v systemd-analyze >/dev/null; then systemd-analyze verify "$temp_dir/$service_name"; fi
  as_root install -o root -g root -m 644 -- "$temp_dir/$service_name" "$unit_path"
  as_root systemctl daemon-reload
  as_root systemctl enable "$service_name"
  as_root systemctl reset-failed "$service_name" 2>/dev/null || true
  # restart also starts an inactive service and applies changes on reinstall.
  as_root systemctl restart "$service_name"
  sleep 2
  if ! systemctl is-active --quiet "$service_name"; then
    as_root journalctl -u "$service_name" -n 30 --no-pager >&2 || true
    fail 'The service did not stay running. Check the log above (and whether port is already in use).'
  fi
  printf 'Service installed, enabled at boot, and running as %s.\n' "$service_user"
  printf 'HTTP listener: http://%s:%s/\n' "$listen_address" "$port"
  if [[ $listen_address == 127.0.0.1 ]]; then
    printf 'Use the Caddy HTTPS address for LAN access.\n'
  else
    printf 'LAN: http://<this-computer-LAN-IP>:%s/\n' "$port"
  fi
  printf 'Settings: %s\nCamera passwords are saved through the browser.\n' "$settings_path"
  printf 'Use hostname -I to find LAN addresses; use "bash web-service.sh logs" for logs.\n'
)

uninstall_service() {
  check_managed_unit
  if [[ ! -e "$unit_path" ]]; then
    printf 'Service is not installed by this script. Nothing to remove.\n'
    return
  fi
  as_root systemctl disable --now "$service_name"
  as_root rm -- "$unit_path"
  as_root systemctl daemon-reload
  as_root systemctl reset-failed "$service_name" 2>/dev/null || true
  printf 'Service uninstalled. Published files, settings, password files, and any custom systemd drop-ins were kept.\n'
}

main() {
  local action=${1:---help}
  [[ $# == 0 ]] || shift
  case "$action" in
    -h|--help|help) usage; return ;;
    install|uninstall|start|stop|restart|enable|disable|status|logs|show) ;;
    *) usage >&2; fail "Unknown command: $action" ;;
  esac
  service_user=${SUDO_USER:-$(id -un)}
  port=5080 settings_path= dotnet_path= ffmpeg_path= listen_address=0.0.0.0
  local port_given=false listener_pattern='"--urls" "http://(127\.0\.0\.1|0\.0\.0\.0):([0-9]+)"' installed_unit
  if [[ $action == install && -f $unit_path ]] && grep -Fxq -- "$managed_marker" "$unit_path"; then
    installed_unit=$(cat "$unit_path")
    if [[ $installed_unit =~ $listener_pattern ]]; then
      listen_address=${BASH_REMATCH[1]} port=${BASH_REMATCH[2]}
    fi
  fi
  while (( $# )); do
    [[ $1 != -h && $1 != --help ]] || { usage; return; }
    [[ "$action" == install ]] || fail 'Options are only supported with install.'
    case "$1" in
      --user|--port|--settings|--dotnet|--ffmpeg)
        [[ $# -ge 2 && -n $2 && $2 != --* ]] || fail "Missing value for $1"
        case "$1" in
          --user) service_user=$2 ;;
          --port) port=$2; port_given=true ;;
          --settings) settings_path=$2 ;;
          --dotnet) dotnet_path=$2 ;;
          --ffmpeg) ffmpeg_path=$2 ;;
        esac
        shift 2 ;;
      *) fail "Unknown option: $1" ;;
    esac
  done
  if [[ $action == install && $port_given == false && -t 0 ]]; then
    local answer
    read -r -p "Camera app HTTP port [$port]: " answer || fail 'Could not read port; pass --port PORT.'
    port=${answer:-$port}
  fi
  [[ $port =~ ^[0-9]{1,5}$ ]] || fail 'Port must be an integer from 1024 to 65535.'
  port=$((10#$port))
  (( port >= 1024 && port <= 65535 )) || fail 'Port must be from 1024 to 65535 for this non-root service.'
  require_systemd
  case "$action" in
    install) install_service ;;
    uninstall) uninstall_service ;;
    start|stop|restart) as_root systemctl "$action" "$service_name" ;;
    enable|disable) as_root systemctl "$action" --now "$service_name" ;;
    status) systemctl status "$service_name" --no-pager --full ;;
    logs) as_root journalctl -u "$service_name" -n 100 -f ;;
    show) systemctl cat "$service_name" ;;
  esac
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then main "$@"; fi
