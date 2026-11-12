"""Test HTTPS setup without network access, credentials, root, or host services.

python tests/test_web_https.py --bash "C:/Program Files/Git/bin/bash.exe"
"""
import argparse
import json
import http.client
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "web-https.sh"
BASH = shutil.which("bash")
HARNESS = r'''
source "$1"
fixture=$(cd "$2" && pwd)
config_dir="$fixture/config"
state_dir="$fixture/state"
certificate_dir="$fixture/tls"
caddy_file="$fixture/Caddyfile"
site_file="$fixture/viewer.caddy"
listener_file="$fixture/listener.caddy"
legacy_site_file="$fixture/legacy.caddy"
installed_script="$fixture/lib/web-https.sh"
unit_dir="$fixture/units"
domain=viewer.example.com email=admin@example.com port=5080 https_port=443
record() { printf '%s\n' "$*" >> "$fixture/commands"; }
lego() { record "lego $*"; return "${LEGO_STATUS:-0}"; }
runuser() { record "validate $*"; return "${VALIDATE_STATUS:-0}"; }
systemctl() {
  record "systemctl $*"
  if [[ $1 == show ]]; then printf '%s' "${OVERRIDES:-}"; fi
  if [[ $1 == restart ]]; then return "${START_STATUS:-0}"; fi
  if [[ $1 == reload && ${RELOAD_STATUS:-0} != 0 ]]; then return "$RELOAD_STATUS"; fi
}
ss() { if [[ ${OCCUPIED_PORT:-} == "${*: -1}" ]]; then printf 'LISTEN fixture\n'; fi; }
curl() {
  record "curl $*"
  if [[ $* == *'%{redirect_url}'* ]]; then
    if [[ ${*: -1} == "http://$domain:$https_port/"* && -n ${SAME_PORT_REDIRECT_RESULT:-} ]]; then
      printf '%s' "$SAME_PORT_REDIRECT_RESULT"
      return 0
    fi
    printf '%s' "${REDIRECT_RESULT:-308 https://$domain:$https_port/manifest.webmanifest?https-check=1}"
    return "${REDIRECT_STATUS:-0}"
  fi
  if [[ ${*: -1} == https:* ]]; then return "${HTTPS_STATUS:-0}"; fi
  return "${HTTP_STATUS:-0}"
}
sleep() { :; }
open_firewall() { record "firewall $https_port"; }
install() {
  local mode=0755 directory=no
  while [[ $1 == -* ]]; do
    case "$1" in
      -d) directory=yes; shift ;;
      -o|-g) shift 2 ;;
      -m) mode=$2; shift 2 ;;
      *) return 99 ;;
    esac
  done
  if [[ $directory == yes ]]; then mkdir -p -- "$@"; else cp -- "$1" "$2"; chmod "$mode" "$2"; fi
}
eval "$3"
'''


class HttpsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="https-script-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for folder in ("config", "state/certificates", "tls", "units"):
            (self.root / folder).mkdir(parents=True)
        self.original = "# existing site\nhttp://localhost:8080 { respond hello }\n"
        (self.root / "Caddyfile").write_text(self.original)
        self.crt = self.root / "state/certificates/viewer.example.com.crt"
        self.key = self.crt.with_suffix(".key")
        self.crt.write_text("new certificate")
        self.key.write_text("new key")

    def invoke(self, code, ok=True, **env):
        result = subprocess.run(
            [BASH, "-c", HARNESS, "test", SCRIPT.as_posix(), self.root.as_posix(), code],
            env={**os.environ, **env}, text=True, capture_output=True, timeout=30)
        if ok:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def log(self):
        path = self.root / "commands"
        return path.read_text() if path.exists() else ""

    def test_valid_hostname_and_port_boundaries(self):
        for port in ("1", "5080", "65535", "05080"):
            with self.subTest(port=port):
                self.invoke(f"port={port}; validate_settings")

    def test_rejects_invalid_hostnames_and_injection(self):
        for domain in ("192.168.1.50", "localhost", "https://camera.example.com", "camera.example.com:443",
                       "a..com", "-a.example.com", "a-.example.com", "a.example.com.",
                       "a.example.com{", "A.example.com", "a" * 64 + ".com"):
            with self.subTest(domain=domain):
                self.invoke(f"domain='{domain}'; validate_settings", ok=False)

    def test_rejects_bad_ports_and_email(self):
        for port in ("0", "65536", "-1", "abc", "5080x"):
            self.invoke(f"port='{port}'; validate_settings", ok=False)
        self.invoke("email=missing; validate_settings", ok=False)

    def test_rejects_invalid_https_ports_and_listener_collisions(self):
        for value in ("0", "65536", "-1", "hello", "80", "5080"):
            self.invoke(f"https_port='{value}'; validate_settings", ok=False)
        self.invoke("port=443; validate_settings", ok=False)
        self.invoke("port=80; validate_settings", ok=False)

    def test_prompts_accept_input_defaults_and_reject_eof(self):
        self.invoke("prompt_port https_port HTTPS 443 <<< '5080'; [[ $https_port == 5080 ]]")
        self.invoke("prompt_port https_port HTTPS 443 <<< ''; [[ $https_port == 443 ]]")
        self.invoke("prompt_port https_port HTTPS 443 </dev/null", ok=False)

    def migration_fixture(self):
        service = self.root / "units/security-camera-viewer.service"
        service.write_text('# Managed by SecurityCameraViewer web-service.sh\n'
                           'ExecStart=:"/usr/bin/dotnet" "/app/viewer.dll" "--urls" "http://0.0.0.0:5080" '
                           '"--settings" "/private/settings.json" "--ffmpeg" "/usr/bin/ffmpeg"\n')
        legacy = self.root / "legacy.caddy"
        legacy.write_text("https://192.168.1.50 {\n    tls internal\n    reverse_proxy 127.0.0.1:5080\n}\n")
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; write_settings > "$config_dir/settings"; '
                    'write_site > "$site_file"; printf "import %s\\n" "$legacy_site_file" >> "$caddy_file"')
        return service, legacy

    def test_migration_preserves_arguments_and_updates_both_proxies_and_renewal(self):
        service, legacy = self.migration_fixture()
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; port=5081; https_port=5080; migrate_ports 5080')
        content = service.read_text()
        self.assertIn('"--urls" "http://127.0.0.1:5081"', content)
        self.assertIn('"--settings" "/private/settings.json" "--ffmpeg" "/usr/bin/ffmpeg"', content)
        self.assertIn("reverse_proxy 127.0.0.1:5081", legacy.read_text())
        site = (self.root / "viewer.caddy").read_text()
        self.assertIn("https://viewer.example.com:5080 {", site)
        self.assertNotIn("https://viewer.example.com,", site)
        self.assertNotIn(":443", site)
        self.assertNotIn("legacy.caddy", (self.root / "Caddyfile").read_text())
        self.assertIn("reverse_proxy 127.0.0.1:5081", site)
        settings = (self.root / "config/settings").read_text()
        self.assertIn("https_port=5080", settings)
        self.assertIn("port=5081", settings)
        self.assertIn("--resolve viewer.example.com:5080:127.0.0.1", self.log())
        self.assertNotIn("lego ", self.log())
        self.assertEqual((self.root / "lib/web-https.sh").read_bytes(), SCRIPT.read_bytes())
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; port=5081; https_port=5080; migrate_ports 5081',
                    OCCUPIED_PORT="sport = :5080")

    def test_migration_failures_restore_service_proxies_and_saved_ports(self):
        service, legacy = self.migration_fixture()
        paths = [service, legacy, self.root / "viewer.caddy", self.root / "config/settings", self.root / "Caddyfile"]
        original = {path: path.read_bytes() for path in paths}
        for env in ({"VALIDATE_STATUS": "1"}, {"START_STATUS": "1"}, {"HTTP_STATUS": "1"},
                    {"HTTPS_STATUS": "1"}, {"RELOAD_STATUS": "1"}, {"REDIRECT_STATUS": "7"}):
            with self.subTest(env=env):
                self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; port=5081; https_port=5080; migrate_ports 5080',
                            ok=False, **env)
                for path, data in original.items():
                    self.assertEqual(path.read_bytes(), data)

    def test_migration_rejects_occupied_ports_overrides_and_privileged_backend(self):
        service, _ = self.migration_fixture()
        original = service.read_bytes()
        for env in ({"OCCUPIED_PORT": "sport = :5081"}, {"OVERRIDES": "/etc/systemd/system/custom.conf"}):
            self.invoke('port=5081; https_port=5080; migrate_ports 5080', ok=False, **env)
            self.assertEqual(service.read_bytes(), original)
        self.invoke('port=81; https_port=5080; migrate_ports 5080', ok=False)
        self.invoke('port=5081; https_port=5082; migrate_ports 5080', ok=False, OCCUPIED_PORT="sport = :5082")

    def test_migration_refuses_unmanaged_service(self):
        service, _ = self.migration_fixture()
        service.write_text("# custom service\n")
        self.invoke('port=5081; https_port=5080; migrate_ports 5080', ok=False)
        self.assertEqual(service.read_text(), "# custom service\n")

    def test_old_settings_remain_compatible_with_renewal(self):
        self.invoke('unset https_port; validate_settings; [[ $https_port == 443 ]]; write_site')

    def test_http_redirect_uses_selected_https_port_and_original_uri(self):
        for port in (443, 5080, 8443):
            with self.subTest(port=port):
                site = self.invoke(f'https_port={port}; write_site').stdout
                self.assertIn("http://viewer.example.com {", site)
                self.assertIn(f"redir https://viewer.example.com:{port}{{uri}} 308", site)
                self.assertNotIn("http://viewer.example.com:5080", site)

    def test_http_redirect_check_requires_status_port_path_and_query(self):
        self.invoke('https_port=5080; verify_http_redirect')
        self.assertIn("--resolve viewer.example.com:80:127.0.0.1", self.log())
        self.assertIn("--resolve viewer.example.com:5080:127.0.0.1", self.log())
        for result in ("200 ", "308 https://viewer.example.com/manifest.webmanifest?https-check=1",
                       "308 https://viewer.example.com:5080/", "302 https://foreign.example/"):
            self.invoke('https_port=5080; verify_http_redirect', ok=False, REDIRECT_RESULT=result)
        self.invoke('https_port=5080; verify_http_redirect', ok=False, REDIRECT_STATUS="7")
        self.invoke('https_port=5080; verify_http_redirect', ok=False, SAME_PORT_REDIRECT_RESULT="400 ")

    def test_default_https_port_redirect_can_be_verified(self):
        self.invoke('https_port=443; verify_http_redirect')
        self.assertIn("http://viewer.example.com:443/manifest.webmanifest?https-check=1", self.log())

    def test_listener_preserves_global_options_and_is_repeatable(self):
        for original in (self.original, "# custom options\n{ # existing global\n    admin off\n}\n" + self.original):
            (self.root / "Caddyfile").write_text(original)
            self.invoke('https_port=5080; configure_listener; configure_listener')
            content = (self.root / "Caddyfile").read_text()
            self.assertEqual(content.count("import "), 1)
            self.assertIn(self.original, content)
            if "admin off" in original:
                self.assertIn("admin off", content)
            options = (self.root / "listener.caddy").read_text()
            self.assertIn("servers :5080", options)
            self.assertLess(options.index("http_redirect"), options.index("\n        tls"))

    @unittest.skipUnless(shutil.which("caddy"), "Caddy adapter integration runs where Caddy is installed")
    def test_real_caddy_adapter_preserves_redirect_status_and_uri(self):
        config = self.root / "redirect.caddy"
        config.write_text("{\n" + self.invoke('https_port=5080; write_listener_options').stdout + "}\n" +
                          self.invoke('https_port=5080; write_site').stdout)
        result = subprocess.run(["caddy", "adapt", "--config", str(config), "--adapter", "caddyfile"],
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)

        def responses(value):
            if isinstance(value, dict):
                if value.get("handler") == "static_response":
                    yield value
                for child in value.values():
                    yield from responses(child)
            elif isinstance(value, list):
                for child in value:
                    yield from responses(child)

        adapted = json.loads(result.stdout)
        servers = adapted["apps"]["http"]["servers"].values()
        tls_server = next(server for server in servers if ":5080" in server["listen"])
        self.assertEqual(tls_server["listener_wrappers"], [{"wrapper": "http_redirect"}, {"wrapper": "tls"}])
        redirects = [value for value in responses(adapted) if "Location" in value.get("headers", {})]
        self.assertEqual(len(redirects), 1)
        self.assertEqual(redirects[0]["status_code"], 308)
        self.assertEqual(redirects[0]["headers"]["Location"],
                         ["https://viewer.example.com:5080{http.request.uri}"])

    @unittest.skipUnless(shutil.which("caddy"), "Live same-port test requires Caddy")
    def test_real_caddy_redirects_plain_http_and_serves_trusted_tls_on_same_port(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        options = self.invoke(f'https_port={port}; write_listener_options').stdout
        # Caddy matches server options against the complete listener address.
        # Keep this fixture loopback-only while retaining the generated wrappers.
        options = options.replace(f"servers :{port}", f"servers 127.0.0.1:{port}")
        config = self.root / "live.caddy"
        config.write_text("{\n admin off\n skip_install_trust\n auto_https disable_redirects\n" + options +
                          "}\nhttps://127.0.0.1:" + str(port) + " {\n bind 127.0.0.1\n tls internal\n respond healthy\n}\n")
        environment = {**os.environ, "XDG_DATA_HOME": str(self.root / "caddy-data"),
                       "XDG_CONFIG_HOME": str(self.root / "caddy-config")}
        with (self.root / "caddy.log").open("w") as log:
            server = subprocess.Popen(["caddy", "run", "--config", str(config), "--adapter", "caddyfile"],
                                      env=environment, stdout=log, stderr=log)
            try:
                deadline = time.monotonic() + 15
                while True:
                    try:
                        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=1)
                        connection.request("GET", "/path?camera=1")
                        response = connection.getresponse()
                        self.assertEqual(response.status, 308)
                        self.assertEqual(response.getheader("Location"), f"https://127.0.0.1:{port}/path?camera=1")
                        connection.close()
                        break
                    except OSError:
                        if time.monotonic() > deadline or server.poll() is not None:
                            self.fail((self.root / "caddy.log").read_text())
                        time.sleep(0.1)
                root = self.root / "caddy-data/caddy/pki/authorities/local/root.crt"
                context = ssl.create_default_context(cafile=str(root))
                connection = http.client.HTTPSConnection("127.0.0.1", port, context=context, timeout=5)
                connection.request("GET", "/path?camera=1")
                response = connection.getresponse()
                self.assertEqual(response.status, 200)
                self.assertEqual(response.read(), b"healthy")
                connection.close()
            finally:
                server.terminate()
                try:
                    server.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait(timeout=5)

    def test_settings_round_trip_shell_metacharacters(self):
        self.invoke(r'''NAMECHEAP_API_USER='user name'; NAMECHEAP_API_KEY='secret $value `echo nope` "quote"'
expected=$NAMECHEAP_API_KEY
write_settings > "$config_dir/settings"
unset NAMECHEAP_API_KEY
source "$config_dir/settings"
[[ $NAMECHEAP_API_KEY == "$expected" ]]''')
        self.assertNotIn("secret", self.log())

    def test_refuses_unmanaged_files(self):
        self.invoke('check_managed "$caddy_file"', ok=False)
        self.invoke('printf "%s\n" "$marker" > "$site_file"; check_managed "$site_file"')

    @unittest.skipIf(os.name == "nt", "Windows symlinks require extra privileges")
    def test_refuses_symlinks(self):
        (self.root / "viewer.caddy").symlink_to(self.root / "Caddyfile")
        self.invoke('check_managed "$site_file"', ok=False)

    def test_first_issue_uses_dns_and_lets_encrypt(self):
        self.crt.unlink()
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; obtain_certificate')
        log = self.log()
        self.assertIn("--dns namecheap", log)
        self.assertIn("https://acme-v02.api.letsencrypt.org/directory", log)
        self.assertIn("--domains viewer.example.com run", log)
        self.assertNotIn("secret", log)

    def test_existing_certificate_renews(self):
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; obtain_certificate')
        self.assertIn("renew --days 30", self.log())

    def test_api_failure_does_not_touch_serving_configuration(self):
        self.invoke('NAMECHEAP_API_USER=user NAMECHEAP_API_KEY=secret; obtain_certificate', ok=False, LEGO_STATUS="1")
        self.assertEqual((self.root / "Caddyfile").read_text(), self.original)
        self.assertFalse((self.root / "viewer.caddy").exists())

    def test_activation_preserves_other_sites_and_is_repeatable(self):
        self.invoke('activate_certificate; activate_certificate')
        config = (self.root / "Caddyfile").read_text()
        self.assertIn(self.original, config)
        self.assertEqual(config.count("import "), 2)
        site = (self.root / "viewer.caddy").read_text()
        self.assertIn("https://viewer.example.com", site)
        self.assertIn("reverse_proxy 127.0.0.1:5080", site)
        self.assertNotIn("tls internal", site)
        self.assertEqual((self.root / "tls/viewer.key").read_text(), "new key")
        self.assertIn("validate -u caddy -- caddy validate", self.log())

    @unittest.skipIf(os.name == "nt", "POSIX permissions are verified on Linux")
    def test_caddy_certificate_permissions(self):
        self.invoke('activate_certificate')
        for filename in ("viewer.crt", "viewer.key"):
            self.assertEqual((self.root / "tls" / filename).stat().st_mode & 0o777, 0o640)

    def test_validation_failure_restores_existing_managed_site(self):
        previous = "# previous hostname and port\n"
        (self.root / "viewer.caddy").write_text(previous)
        self.invoke('activate_certificate', ok=False, VALIDATE_STATUS="1")
        self.assertEqual((self.root / "viewer.caddy").read_text(), previous)

    def test_validation_failure_restores_previous_files(self):
        (self.root / "tls/viewer.key").write_text("old key")
        (self.root / "tls/viewer.crt").write_text("old cert")
        self.invoke('activate_certificate', ok=False, VALIDATE_STATUS="1")
        self.assertEqual((self.root / "Caddyfile").read_text(), self.original)
        self.assertEqual((self.root / "tls/viewer.key").read_text(), "old key")
        self.assertEqual((self.root / "tls/viewer.crt").read_text(), "old cert")
        self.assertFalse((self.root / "viewer.caddy").exists())

    def test_reload_failure_restores_config(self):
        self.invoke('activate_certificate', ok=False, RELOAD_STATUS="1")
        self.assertEqual((self.root / "Caddyfile").read_text(), self.original)
        self.assertFalse((self.root / "tls/viewer.key").exists())

    def test_missing_key_restores_config(self):
        self.key.unlink()
        self.invoke('activate_certificate', ok=False)
        self.assertEqual((self.root / "Caddyfile").read_text(), self.original)
        self.assertFalse((self.root / "tls/viewer.crt").exists())

    def test_installs_persistent_daily_timer_and_independent_script(self):
        self.invoke('install_timer')
        timer = (self.root / "units/security-camera-viewer-https.timer").read_text()
        service = (self.root / "units/security-camera-viewer-https.service").read_text()
        self.assertIn("Persistent=true", timer)
        self.assertIn("OnCalendar=daily", timer)
        self.assertIn("web-https.sh renew", service)
        self.assertEqual((self.root / "lib/web-https.sh").read_bytes(), SCRIPT.read_bytes())
        self.assertIn("enable --now security-camera-viewer-https.timer", self.log())

    def test_help_and_bad_arguments_need_no_root_or_services(self):
        for args, expected in ((["--help"], 0), (["wat"], 1), (["install"], 1), (["renew", "extra"], 1)):
            result = subprocess.run([BASH, str(SCRIPT), *args], capture_output=True, text=True)
            self.assertEqual(result.returncode, expected, result.stderr)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bash", default=BASH)
    args, remaining = parser.parse_known_args()
    BASH = args.bash
    unittest.main(argv=[__file__, *remaining])
