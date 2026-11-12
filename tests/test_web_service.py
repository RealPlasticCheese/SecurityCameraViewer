"""Exercise the installer with mocked system commands; never touch host services.

Run: python tests/test_web_service.py
On Windows: python tests/test_web_service.py --bash "C:/Program Files/Git/bin/bash.exe"
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "web-service.sh"
BASH = shutil.which("bash")
HARNESS = r'''
export PATH="/usr/bin:/bin:$PATH"
source "$1"
fixture=$(CDPATH= cd -- "$2" && pwd)
shift 2
repo_dir="$fixture/app space %i \${HOME}"
unit_path="$fixture/system/$service_name"
export PATH="$fixture/bin:$PATH"
unset SUDO_USER XDG_DATA_HOME
require_systemd() { :; }
id() { printf 'viewer\n'; }
getent() {
  [[ $1 == passwd && $2 == viewer ]] || return 1
  printf 'viewer:x:%s:1000:Viewer:%s/home:/bin/bash\n' "${TEST_UID:-1000}" "$fixture"
}
record() { printf '%s\n' "$*" >> "$fixture/commands"; }
as_user() { env HOME="$service_home" "$@"; }
as_root() {
  record "root $*"
  case "$1" in
    systemctl|journalctl) "$@" ;;
    install)
      [[ $# == 10 && ${10} == "$unit_path" ]] || return 98
      command cp -- "$9" "${10}"
      ;;
    rm)
      [[ $# == 3 && $2 == -- && $3 == "$unit_path" ]] || return 98
      command rm -- "$unit_path"
      ;;
    *) printf 'Unexpected privileged command: %s\n' "$*" >&2; return 98 ;;
  esac
}
systemctl() {
  record "systemctl $*"
  case "$1" in
    is-active) [[ ${TEST_ACTIVE:-yes} == yes ]] ;;
    status) return "${TEST_STATUS:-0}" ;;
    cat) cat "$unit_path" ;;
    *) return 0 ;;
  esac
}
journalctl() { record "journalctl $*"; printf 'Mock service journal\n'; }
systemd-analyze() { record "verify $*"; }
sleep() { :; }
main "$@"
'''


class WebServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="web-service-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "app space %i ${HOME}"
        self.publish = self.repo / "artifacts/web"
        (self.publish / "wwwroot").mkdir(parents=True)
        (self.publish / "SecurityCameraViewer.Web.dll").write_text("fixture")
        (self.publish / "SecurityCameraViewer.Web.runtimeconfig.json").write_text(
            '{"runtimeOptions":{"frameworks":[{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
        (self.publish / "wwwroot/index.html").write_text("fixture")
        (self.root / "system").mkdir()
        (self.root / "home").mkdir()
        (self.root / "bin").mkdir()
        self.unit = self.root / "system/security-camera-viewer.service"
        self.commands = self.root / "commands"
        self.executable("ffmpeg", 'printf "FFmpeg fixture\\n"\n')
        self.executable("dotnet", 'printf "Microsoft.AspNetCore.App %s.0.0 [/fixture]\\nMicrosoft.NETCore.App %s.0.0 [/fixture]\\n" "${TEST_RUNTIME:-10}" "${TEST_RUNTIME:-10}"\n')

    def executable(self, name, body):
        path = self.root / "bin" / name
        path.write_text("#!/usr/bin/env bash\n" + body, newline="\n")
        path.chmod(0o755)
        return path

    def invoke(self, *args, ok=True, **environment):
        result = subprocess.run(
            [BASH, "-c", HARNESS, "test", SCRIPT.as_posix(), self.root.as_posix(), *args],
            cwd=self.root,
            env={**os.environ, **environment},
            text=True, capture_output=True, timeout=30,
        )
        if ok:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def log(self):
        return self.commands.read_text() if self.commands.exists() else ""

    def test_reinstall_preserves_loopback_listener_and_port(self):
        self.invoke("install")
        self.unit.write_text(self.unit.read_text().replace("http://0.0.0.0:5080", "http://127.0.0.1:5081"))
        result = self.invoke("install")
        self.assertIn('"--urls" "http://127.0.0.1:5081"', self.unit.read_text())
        self.assertIn("Caddy HTTPS address", result.stdout)

    def test_explicit_port_retains_existing_loopback_binding(self):
        self.invoke("install")
        self.unit.write_text(self.unit.read_text().replace("http://0.0.0.0:5080", "http://127.0.0.1:5081"))
        self.invoke("install", "--port", "6000")
        self.assertIn('"--urls" "http://127.0.0.1:6000"', self.unit.read_text())

    @unittest.skipIf(os.name == "nt", "Interactive terminal verified on Linux")
    def test_interactive_install_prompts_for_port(self):
        import pty
        master, slave = pty.openpty()
        try:
            process = subprocess.Popen(
                [BASH, "-c", HARNESS, "test", SCRIPT.as_posix(), self.root.as_posix(), "install"],
                stdin=slave, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                env=os.environ.copy(), cwd=self.root)
            os.write(master, b"6001\n")
            stdout, stderr = process.communicate(timeout=30)
            self.assertEqual(process.returncode, 0, stdout + stderr)
            self.assertIn('"--urls" "http://0.0.0.0:6001"', self.unit.read_text())
            self.assertIn("Camera app HTTP port [5080]", stderr)
        finally:
            os.close(master)
            os.close(slave)

    def test_install_enables_startup_and_binds_lan_with_literal_paths(self):
        result = self.invoke("install")
        unit = self.unit.read_text()
        self.assertIn("User=viewer\n", unit)
        self.assertIn('"http://0.0.0.0:5080"', unit)
        self.assertIn("app space %%i ${HOME}", unit)
        self.assertIn('ExecStart=:"', unit)
        self.assertIn("WantedBy=multi-user.target", unit)
        self.assertIn("Restart=on-failure", unit)
        self.assertIn("UMask=0077", unit)
        self.assertIn("Camera passwords are saved through the browser", result.stdout)
        self.assertNotIn("--password-file", unit)
        self.assertIn("systemctl enable security-camera-viewer.service", self.log())
        self.assertIn("systemctl restart security-camera-viewer.service", self.log())
        self.assertIn("verify verify", self.log())

    def test_reinstall_updates_options_preserving_data(self):
        self.invoke("install")
        settings = self.root / "custom data/settings.json"
        settings.parent.mkdir()
        settings.write_text('{"Host":"camera"}')
        password = self.root / "secret.txt"
        password.write_text("private-test-password")
        result = self.invoke("install", "--port", "08080", "--settings", "custom data/settings.json")
        unit = self.unit.read_text()
        self.assertIn('"http://0.0.0.0:8080"', unit)
        self.assertIn("custom data/settings.json", unit)
        self.assertNotIn("--password-file", unit)
        self.assertEqual(settings.read_text(), '{"Host":"camera"}')
        self.assertEqual(password.read_text(), "private-test-password")
        self.assertNotIn("private-test-password", unit + self.log() + result.stdout + result.stderr)

    def test_password_files_are_not_discovered(self):
        (self.repo / "password.txt").write_text("repo-secret")
        (self.publish / "password.txt").write_text("published-secret")
        result = self.invoke("install")
        self.assertNotIn("password.txt", result.stdout)
        self.assertNotIn("--password-file", self.unit.read_text())

    def test_obsolete_password_option_rejected_before_unit_change(self):
        result = self.invoke("install", "--password-file", "secret.txt", ok=False)
        self.assertIn("Unknown option", result.stderr)
        self.assertFalse(self.unit.exists())
        self.assertNotIn("root ", self.log())

    def test_missing_publish_rejected(self):
        (self.publish / "wwwroot/index.html").unlink()
        result = self.invoke("install", ok=False)
        self.assertIn("build-web.sh", result.stderr)
        self.assertFalse(self.unit.exists())

    def test_runtime_version_checked(self):
        result = self.invoke("install", ok=False, TEST_RUNTIME="9")
        self.assertIn(".NET 10 ASP.NET Core Runtime", result.stderr)
        self.assertFalse(self.unit.exists())

    def test_self_contained_publish_does_not_need_dotnet(self):
        (self.publish / "libhostpolicy.so").write_text("fixture")
        (self.publish / "SecurityCameraViewer.Web.runtimeconfig.json").write_text(
            '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
        executable = self.publish / "SecurityCameraViewer.Web"
        executable.write_text("#!/usr/bin/env bash\nexit 0\n", newline="\n")
        executable.chmod(0o755)
        self.invoke("install", "--dotnet", "/does/not/exist")
        command = next(line for line in self.unit.read_text().splitlines() if line.startswith("ExecStart="))
        self.assertIn('artifacts/web/SecurityCameraViewer.Web"', command)
        self.assertNotIn(".dll", command)

    def test_stale_native_files_do_not_change_framework_dependent_launch(self):
        (self.publish / "libhostpolicy.so").write_text("old self-contained runtime")
        executable = self.publish / "SecurityCameraViewer.Web"
        executable.write_text("#!/usr/bin/env bash\nexit 0\n", newline="\n")
        executable.chmod(0o755)
        self.invoke("install")
        command = next(line for line in self.unit.read_text().splitlines() if line.startswith("ExecStart="))
        self.assertIn("SecurityCameraViewer.Web.dll", command)

    def test_incomplete_self_contained_publish_rejected(self):
        (self.publish / "SecurityCameraViewer.Web.runtimeconfig.json").write_text(
            '{"runtimeOptions":{"includedFrameworks":[]}}')
        result = self.invoke("install", ok=False)
        self.assertIn("Self-contained Linux publish is incomplete", result.stderr)
        self.assertFalse(self.unit.exists())

    def test_root_service_account_rejected(self):
        result = self.invoke("install", ok=False, TEST_UID="0")
        self.assertIn("non-root service account", result.stderr)
        self.assertFalse(self.unit.exists())

    def test_unmanaged_unit_is_never_overwritten_or_removed(self):
        self.unit.write_text("# Manually installed service\n")
        for action in ("install", "uninstall"):
            with self.subTest(action=action):
                result = self.invoke(action, ok=False)
                self.assertIn("not created by this script", result.stderr)
                self.assertEqual(self.unit.read_text(), "# Manually installed service\n")
                self.assertNotIn("root ", self.log())

    def test_uninstall_is_repeatable_and_preserves_files(self):
        password = self.repo / "password.txt"
        password.write_text("secret")
        self.invoke("install")
        settings = self.root / "home/.local/share/SecurityCameraViewer.Web/settings.json"
        settings.write_text('{"ChannelCount":4}')
        self.invoke("uninstall")
        self.assertFalse(self.unit.exists())
        self.assertTrue((self.publish / "SecurityCameraViewer.Web.dll").exists())
        self.assertEqual(password.read_text(), "secret")
        self.assertEqual(settings.read_text(), '{"ChannelCount":4}')
        self.assertIn("systemctl disable --now security-camera-viewer.service", self.log())
        result = self.invoke("uninstall")
        self.assertIn("Nothing to remove", result.stdout)

    def test_management_commands(self):
        for action, expected in (
            ("start", "systemctl start security-camera-viewer.service"),
            ("stop", "systemctl stop security-camera-viewer.service"),
            ("restart", "systemctl restart security-camera-viewer.service"),
            ("enable", "systemctl enable --now security-camera-viewer.service"),
            ("disable", "systemctl disable --now security-camera-viewer.service"),
            ("status", "systemctl status security-camera-viewer.service --no-pager --full"),
            ("logs", "journalctl -u security-camera-viewer.service -n 100 -f"),
        ):
            with self.subTest(action=action):
                self.invoke(action)
                self.assertIn(expected, self.log())

    def test_show_installed_configuration(self):
        self.invoke("install")
        result = self.invoke("show")
        self.assertEqual(result.stdout, self.unit.read_text())

    def test_inactive_status_keeps_systemctl_exit_code(self):
        result = self.invoke("status", ok=False, TEST_STATUS="3")
        self.assertEqual(result.returncode, 3)

    def test_start_failure_is_reported_with_logs(self):
        result = self.invoke("install", ok=False, TEST_ACTIVE="no")
        self.assertIn("did not stay running", result.stderr)
        self.assertIn("Mock service journal", result.stderr)
        self.assertTrue(self.unit.exists())

    def test_bad_arguments_cannot_change_service(self):
        for arguments in (("unknown",), ("install", "--unknown"), ("install", "--port"),
                          ("install", "--port", "0"), ("install", "--port", "80"),
                          ("install", "--port", "65536"), ("install", "--port", "1e4"),
                          ("install", "--port", "8080\nUser=root"),
                          ("uninstall", "--port", "5080")):
            with self.subTest(arguments=arguments):
                self.invoke(*arguments, ok=False)
                self.assertFalse(self.unit.exists())
                self.assertEqual(self.log(), "")

    def test_help_without_system_changes(self):
        for arguments in (("--help",), ("install", "--help")):
            result = subprocess.run(
                [BASH, "-c", 'export PATH="/usr/bin:/bin:$PATH"; exec bash "$@"',
                 "test", SCRIPT.as_posix(), *arguments],
                text=True, capture_output=True, timeout=10,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("Usage:", result.stdout)
        self.assertEqual(self.log(), "")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bash", default=BASH)
    options, remaining = parser.parse_known_args()
    BASH = options.bash
    if not BASH:
        parser.error("Bash not found; pass --bash /path/to/bash")
    unittest.main(argv=[__file__, *remaining])
