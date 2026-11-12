#!/usr/bin/env bash
# Managed by SecurityCameraViewer web-https.sh
set -euo pipefail

config_dir=/etc/security-camera-viewer-https
state_dir=/var/lib/security-camera-viewer-https
certificate_dir=/etc/caddy/security-camera-viewer-tls
caddy_file=/etc/caddy/Caddyfile
site_file=/etc/caddy/security-camera-viewer-acme.caddy
listener_file=/etc/caddy/security-camera-viewer-listener.caddy
legacy_site_file=/etc/caddy/security-camera-viewer.caddy
installed_script=/usr/local/lib/security-camera-viewer/web-https.sh
unit_dir=/etc/systemd/system
marker='# Managed by SecurityCameraViewer web-https.sh'

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Usage: sudo bash web-https.sh install DOMAIN EMAIL [BACKEND_PORT [HTTPS_PORT]]
       sudo bash web-https.sh ports [HTTPS_PORT [BACKEND_PORT]]
       sudo bash web-https.sh refresh
       sudo bash web-https.sh renew

Linux Mint/Ubuntu with systemd. Installs Caddy and lego using apt, obtains a
Let's Encrypt certificate through Namecheap DNS, and enables daily renewal.
Prompts for Namecheap API username and key locally. Allowlist your public
outbound IPv4 in Namecheap first. Missing ports are prompted interactively.
The ports command moves an existing managed viewer to a loopback HTTP port
and configures the HTTPS port without asking for API credentials again.
The camera app serves HTTPS only on the selected port. No router forwarding
is needed. Existing Caddy sites and the camera application are preserved.
HTTP on port 80 redirects to the selected HTTPS port, retaining path and query.
Plain HTTP on the HTTPS port also redirects, through Caddy's listener wrapper.
refresh applies the saved settings and updates the renewal script without
requesting credentials or restarting the camera application.
Install agrees to the Let's Encrypt subscriber agreement on your behalf.
Credentials and ACME account keys are stored root-only outside the checkout.
EOF
}

validate_settings() {
  [[ $domain == *.* && ${#domain} -le 253 && $domain != *[!a-z0-9.-]* ]] ||
    fail 'Use a lowercase fully qualified DNS hostname, without a scheme or port.'
  local label
  local -a labels
  IFS=. read -r -a labels <<< "$domain"
  [[ $domain != *. ]] || fail 'Hostname must not end with a dot.'
  for label in "${labels[@]}"; do
    [[ ${#label} -ge 1 && ${#label} -le 63 && $label != -* && $label != *- ]] || fail 'Invalid DNS label.'
  done
  [[ $domain == *[a-z]* ]] || fail 'Use a DNS hostname, not an IP address.'
  [[ $email == ?*@?*.?* && $email != *[[:space:][:cntrl:]]* && $email != -* ]] || fail 'A contact email is required.'
  [[ $port =~ ^[0-9]{1,5}$ ]] || fail 'Backend port must be from 1 to 65535.'
  port=$((10#$port))
  (( port >= 1 && port <= 65535 )) || fail 'Backend port must be from 1 to 65535.'
  https_port=${https_port:-443}
  [[ $https_port =~ ^[0-9]{1,5}$ ]] || fail 'HTTPS port must be from 1 to 65535.'
  https_port=$((10#$https_port))
  (( https_port >= 1 && https_port <= 65535 )) || fail 'HTTPS port must be from 1 to 65535.'
  [[ $https_port != "$port" && $port != 80 && $https_port != 80 ]] ||
    fail 'HTTP backend and HTTPS listeners must use separate ports; Caddy also uses HTTP port 80.'
}

prompt_port() {
  local variable=$1 label=$2 default=$3 answer
  read -r -p "$label [$default]: " answer || fail 'Port input is required; pass port arguments for noninteractive use.'
  printf -v "$variable" '%s' "${answer:-$default}"
}

check_managed() {
  local path=$1
  [[ ! -L $path ]] || fail "Refusing to replace a symlink: $path"
  if [[ -e $path ]] && ! grep -Fxq "$marker" "$path"; then
    fail "Unmanaged file exists: $path. Review and move it before installing."
  fi
}

write_settings() {
  local name
  printf '%s\n' "$marker"
  for name in domain email port https_port NAMECHEAP_API_USER NAMECHEAP_API_KEY; do
    printf '%s=%q\n' "$name" "${!name}"
  done
}

write_site() {
  local addresses="https://$domain:${https_port:-443}"
  cat <<EOF
$marker
$addresses {
    tls $certificate_dir/viewer.crt $certificate_dir/viewer.key
    reverse_proxy 127.0.0.1:$port
}

http://$domain {
    redir https://$domain:${https_port:-443}{uri} 308
}
EOF
}

write_listener_options() {
  cat <<EOF
$marker
servers :${https_port:-443} {
    listener_wrappers {
        http_redirect
        tls
    }
}
EOF
}

configure_listener() {
  check_managed "$listener_file"
  write_listener_options > "$listener_file"
  chmod 0644 "$listener_file"
  if grep -Fxq "    import $listener_file" "$caddy_file"; then return; fi
  local first content
  first=$(awk '!/^[[:space:]]*(#|$)/ { print; exit }' "$caddy_file")
  content=$(cat "$caddy_file")
  if [[ $first =~ ^[[:space:]]*\{([[:space:]]*\#.*)?[[:space:]]*$ ]]; then
    # Extend an existing global block without replacing its other options.
    awk -v listener_import="    import $listener_file" '
      !added && /^[[:space:]]*\{([[:space:]]*#.*)?[[:space:]]*$/ {
        print; print listener_import; added=1; next
      }
      { print }
    ' <<< "$content" > "$caddy_file"
  else
    printf '{\n    import %s\n}\n\n%s\n' "$listener_file" "$content" > "$caddy_file"
  fi
}

verify_https() {
  curl --fail --silent --show-error --max-time 30 --resolve "$domain:$https_port:127.0.0.1" \
    "https://$domain:$https_port/manifest.webmanifest" >/dev/null
  printf 'HTTPS verified: https://%s:%s/\n' "$domain" "$https_port"
}

verify_http_redirect() {
  local redirect expected http_port
  expected="308 https://$domain:$https_port/manifest.webmanifest?https-check=1"
  for http_port in 80 "$https_port"; do
    redirect=$(curl --silent --show-error --max-time 15 --output /dev/null \
      --write-out '%{http_code} %{redirect_url}' --resolve "$domain:$http_port:127.0.0.1" \
      "http://$domain:$http_port/manifest.webmanifest?https-check=1")
    [[ $redirect == "$expected" ]] || fail "HTTP redirect on port $http_port did not preserve the HTTPS port, path, and query."
  done
  printf 'HTTP redirect verified: http://%s/ -> https://%s:%s/\n' "$domain" "$domain" "$https_port"
}

open_firewall() {
  if command -v ufw >/dev/null && ufw status | grep -q '^Status: active'; then
    ufw allow 80/tcp comment 'Security Camera Viewer HTTP redirect'
    ufw allow "$https_port/tcp" comment 'Security Camera Viewer HTTPS'
  fi
}

migrate_ports() (
  local old_port=$1 service_file="$unit_dir/security-camera-viewer.service"
  local content old_argument new_argument backup path name legacy_content
  (( port >= 1024 )) || fail 'The non-root camera service requires a backend port of at least 1024.'
  [[ ! -L $service_file ]] && grep -Fxq '# Managed by SecurityCameraViewer web-service.sh' "$service_file" ||
    fail 'Port migration requires the service installed by web-service.sh.'
  [[ -z $(systemctl show security-camera-viewer.service --property=DropInPaths --value) ]] ||
    fail 'Service overrides exist. Review the listener configuration manually before migrating.'
  # Match the generated service argument exactly, keeping all other startup options.
  content=$(cat "$service_file")
  old_argument="\"--urls\" \"http://0.0.0.0:$old_port\""
  if [[ $content != *"$old_argument"* ]]; then old_argument="\"--urls\" \"http://127.0.0.1:$old_port\""; fi
  [[ $content == *"$old_argument"* ]] || fail 'Service listener does not match the saved backend port.'
  [[ ${content#*"$old_argument"} != *"$old_argument"* ]] || fail 'Multiple service listener arguments found.'
  if [[ $port != "$old_port" ]] && [[ -n $(ss -H -ltn "sport = :$port") ]]; then
    fail "Backend port $port is already in use. Choose another port."
  fi
  if [[ $https_port != "$old_port" && $https_port != 443 ]] && [[ -n $(ss -H -ltn "sport = :$https_port") ]]; then
    # Repeating the current setup is valid when Caddy already owns this listener.
    grep -Fq "https://$domain:$https_port" "$site_file" || fail "HTTPS port $https_port is already in use."
  fi
  if [[ -f $legacy_site_file ]]; then
    [[ $(grep -Ec "^[[:space:]]*reverse_proxy 127[.]0[.]0[.]1:$old_port[[:space:]]*$" "$legacy_site_file") == 1 ]] ||
      fail 'The old IP HTTPS site has a custom backend. Review it before migrating.'
  fi
  local -a paths=("$service_file" "$site_file" "$listener_file" "$legacy_site_file" "$config_dir/settings" "$installed_script" "$caddy_file")
  backup=$(mktemp -d)
  rollback_ports() {
    local status=$?
    if (( status != 0 )); then
      for path in "${paths[@]}"; do
        name=${path##*/}
        if [[ -f $backup/$name ]]; then cp -p -- "$backup/$name" "$path"; else rm -f -- "$path"; fi
      done
      systemctl daemon-reload
      systemctl restart security-camera-viewer.service || true
      systemctl reload caddy || true
    fi
    rm -rf -- "$backup"
    exit "$status"
  }
  for path in "${paths[@]}"; do
    [[ ! -L $path ]] || fail "Refusing a symlink: $path"
    if [[ -e $path ]]; then cp -p -- "$path" "$backup/${path##*/}"; fi
  done
  trap rollback_ports EXIT
  new_argument="\"--urls\" \"http://127.0.0.1:$port\""
  printf '%s\n' "${content/"$old_argument"/"$new_argument"}" > "$service_file"
  if [[ -f $legacy_site_file ]]; then
    legacy_content=$(cat "$legacy_site_file")
    printf '%s\n' "${legacy_content/"reverse_proxy 127.0.0.1:$old_port"/"reverse_proxy 127.0.0.1:$port"}" > "$legacy_site_file"
    # Retain the old IP site on disk, but stop importing its separate 443 listener.
    local caddy_content line
    caddy_content=$(cat "$caddy_file")
    : > "$caddy_file"
    while IFS= read -r line || [[ -n $line ]]; do
      if [[ $line != "import $legacy_site_file" ]]; then printf '%s\n' "$line" >> "$caddy_file"; fi
    done <<< "$caddy_content"
  fi
  systemctl daemon-reload
  systemctl restart security-camera-viewer.service
  # Poll readiness so Caddy is only reconfigured once the new backend responds.
  local attempt
  for attempt in {1..30}; do
    if curl --fail --silent --show-error --max-time 2 "http://127.0.0.1:$port/manifest.webmanifest" >/dev/null 2>&1; then break; fi
    (( attempt < 30 )) || fail 'Viewer failed to start on the new backend port.'
    sleep 1
  done
  activate_certificate
  write_settings > "$config_dir/settings"
  chmod 0600 "$config_dir/settings"
  install_timer
  open_firewall
  verify_https
  verify_http_redirect
)

obtain_certificate() {
  export NAMECHEAP_API_USER NAMECHEAP_API_KEY
  local -a arguments=(--path "$state_dir" --server https://acme-v02.api.letsencrypt.org/directory
    --accept-tos --email "$email" --dns namecheap --domains "$domain")
  if [[ -s $state_dir/certificates/$domain.crt ]]; then
    lego "${arguments[@]}" renew --days 30
  else
    lego "${arguments[@]}" run
  fi
}

activate_certificate() (
  # Restore both configuration and certificates if validation or reload fails.
  local backup path name
  local -a paths=("$caddy_file" "$site_file" "$listener_file" "$certificate_dir/viewer.crt" "$certificate_dir/viewer.key")
  backup=$(mktemp -d)
  rollback() {
    local status=$?
    if (( status != 0 )); then
      for path in "${paths[@]}"; do
        name=${path##*/}
        if [[ -f $backup/$name ]]; then cp -p -- "$backup/$name" "$path"; else rm -f -- "$path"; fi
      done
      systemctl reload caddy 2>/dev/null || true
    fi
    rm -rf -- "$backup"
    exit "$status"
  }
  trap rollback EXIT
  for path in "${paths[@]}"; do
    [[ ! -L $path ]] || fail "Refusing a symlink: $path"
    if [[ -e $path ]]; then cp -p -- "$path" "$backup/${path##*/}"; fi
  done
  install -d -o root -g caddy -m 0750 "$certificate_dir"
  install -o root -g caddy -m 0640 "$state_dir/certificates/$domain.crt" "$certificate_dir/viewer.crt"
  install -o root -g caddy -m 0640 "$state_dir/certificates/$domain.key" "$certificate_dir/viewer.key"
  write_site > "$site_file"
  chmod 0644 "$site_file"
  if ! grep -Fxq "import $site_file" "$caddy_file"; then printf '\nimport %s\n' "$site_file" >> "$caddy_file"; fi
  configure_listener
  # Validate as the service user, including certificate/key readability.
  runuser -u caddy -- caddy validate --config "$caddy_file" --adapter caddyfile
  systemctl enable --now caddy
  systemctl reload caddy
)

install_timer() {
  install -d -m 0755 "${installed_script%/*}"
  if [[ $(readlink -f "${BASH_SOURCE[0]}") != "$installed_script" ]]; then
    install -o root -g root -m 0755 "${BASH_SOURCE[0]}" "$installed_script"
  fi
  cat > "$unit_dir/security-camera-viewer-https.service" <<EOF
$marker
[Unit]
Description=Renew Security Camera Viewer HTTPS certificate
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
ExecStart=/bin/bash $installed_script renew
UMask=0077
EOF
  cat > "$unit_dir/security-camera-viewer-https.timer" <<EOF
$marker
[Unit]
Description=Check Security Camera Viewer certificate daily

[Timer]
OnCalendar=daily
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
EOF
  systemctl daemon-reload
  systemctl enable --now security-camera-viewer-https.timer
}

main() {
  local action=${1:---help}
  case "$action" in
    --help|-h|help) usage; return ;;
    install) [[ $# -ge 3 && $# -le 5 ]] || { usage; fail 'Expected DOMAIN EMAIL and optional BACKEND_PORT HTTPS_PORT.'; } ;;
    ports) [[ $# -le 3 ]] || fail 'ports accepts HTTPS_PORT and BACKEND_PORT.' ;;
    renew|refresh) [[ $# == 1 ]] || fail "$action takes no arguments." ;;
    *) usage; fail "Unknown command: $action" ;;
  esac
  [[ $(uname -s) == Linux && -d /run/systemd/system ]] || fail 'Requires Linux with systemd.'
  (( EUID == 0 )) || fail 'Run with sudo bash web-https.sh ...'
  # Serialize interactive setup and the renewal timer.
  exec 9>/run/lock/security-camera-viewer-https.lock
  flock -n 9 || fail 'Another HTTPS setup or renewal is running.'
  umask 077
  if [[ $action == install ]]; then
    domain=$2 email=$3 port=${4:-} https_port=${5:-}
    [[ -n $port ]] || prompt_port port 'Internal camera app HTTP port' 5080
    [[ -n $https_port ]] || prompt_port https_port 'Browser HTTPS port' 443
    validate_settings
    local path
    for path in "$site_file" "$listener_file" "$config_dir/settings" "$installed_script" \
      "$unit_dir/security-camera-viewer-https.service" "$unit_dir/security-camera-viewer-https.timer"; do
      check_managed "$path"
    done
    read -r -p 'Namecheap API username: ' NAMECHEAP_API_USER
    read -r -s -p 'Namecheap API key (hidden): ' NAMECHEAP_API_KEY
    printf '\n'
    [[ -n $NAMECHEAP_API_USER && -n $NAMECHEAP_API_KEY ]] || fail 'Both credentials are required.'
    apt-get update
    apt-get install -y caddy lego curl ca-certificates
    curl --fail --silent --show-error --max-time 15 "http://127.0.0.1:$port/manifest.webmanifest" >/dev/null ||
      fail 'Viewer is not responding. Start it before configuring HTTPS.'
    install -d -o root -g root -m 0700 "$config_dir" "$state_dir"
    # Issue first: an API or validation failure leaves the serving config intact.
    obtain_certificate
    write_settings > "$config_dir/settings"
    chmod 0600 "$config_dir/settings"
  else
    [[ -f $config_dir/settings ]] || fail 'Run install first.'
    source "$config_dir/settings"
    if [[ $action == ports ]]; then
      local old_port=$port
      https_port=${2:-} port=${3:-}
      [[ -n $https_port ]] || prompt_port https_port 'Browser HTTPS port' 5080
      [[ -n $port ]] || prompt_port port 'Internal camera app HTTP port' 5081
      validate_settings
      check_managed "$site_file"
      check_managed "$installed_script"
      migrate_ports "$old_port"
      return
    fi
    validate_settings
    check_managed "$site_file"
    if [[ $action != refresh ]]; then obtain_certificate; fi
  fi
  activate_certificate
  if [[ $action == install || $action == refresh ]]; then
    install_timer
    open_firewall
  fi
  # Public trust and the actual hostname must both work; never use --insecure.
  verify_https
  verify_http_redirect
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then main "$@"; fi
