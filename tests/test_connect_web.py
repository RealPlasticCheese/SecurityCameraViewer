"""Run connection-script checks without opening a user browser.

Usage: python tests/test_connect_web.py --bash "C:/Program Files/Git/bin/bash.exe"
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[1]
BASH = shutil.which("bash")


class LinuxScriptTests(unittest.TestCase):
    def setUp(self):
        if not BASH:
            self.skipTest("Bash is unavailable")
        self.temp = tempfile.TemporaryDirectory(prefix="connect viewer ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        shutil.copyfile(REPO / "connect-web.sh", self.root / "connect-web.sh")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.capture = self.root / "arguments"
        self.environment = os.environ.copy()
        self.environment["SCV_SCRIPT_CAPTURE"] = self.capture.as_posix()
        self.environment["SCV_SCRIPT_EXIT"] = "0"
        self.environment["SCV_SCRIPT_BIN"] = self.bin.as_posix()
        self.executable(self.bin / "dotnet", 'printf "%s\\n" "$@" > "$SCV_SCRIPT_CAPTURE"\nexit "$SCV_SCRIPT_EXIT"\n')

    @staticmethod
    def executable(path, body):
        path.write_text("#!/usr/bin/env bash\n" + body, encoding="utf-8", newline="\n")
        path.chmod(0o755)

    def publish(self, packaged=False, native=False):
        output = self.root if packaged else self.root / "artifacts/web"
        output.mkdir(parents=True, exist_ok=True)
        (output / "SecurityCameraViewer.Web.dll").write_text("fixture")
        if native:
            self.executable(output / "SecurityCameraViewer.Web", 'printf "%s\\n" "$@" > "$SCV_SCRIPT_CAPTURE"\nexit "$SCV_SCRIPT_EXIT"\n')

    def run_script(self, *arguments):
        return subprocess.run([BASH, "-c", 'test_bin="$SCV_SCRIPT_BIN"; if command -v cygpath >/dev/null; then test_bin=$(cygpath -u "$test_bin"); fi; export PATH="$test_bin:/usr/bin:/bin:$PATH"; bash "$@"',
                               "script-test", (self.root / "connect-web.sh").as_posix(), *arguments],
                              cwd=REPO, env=self.environment, capture_output=True, text=True, timeout=20)

    def test_missing_address_reports_usage(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 1)
        self.assertIn("Usage:", result.stderr)
        self.assertFalse(self.capture.exists())

    def test_framework_publish_preserves_arguments(self):
        self.publish()
        address = "https://server.example/viewer/?name=Front%20door&mode=live"
        result = self.run_script(address, "--browser-path", "/browser tools/chromium")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.capture.read_text().splitlines(),
                         ["artifacts/web/SecurityCameraViewer.Web.dll", "--connect", address, "--browser-path", "/browser tools/chromium"])

    def test_packaged_script_uses_sibling_runtime(self):
        self.publish(packaged=True)
        result = self.run_script("http://server:5080/")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.capture.read_text().splitlines(), ["./SecurityCameraViewer.Web.dll", "--connect", "http://server:5080/"])

    def test_self_contained_publish_does_not_require_dotnet(self):
        self.publish(native=True)
        (self.bin / "dotnet").unlink()
        result = self.run_script("https://server.example/", "--browser", "chromium")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.capture.read_text().splitlines(), ["--connect", "https://server.example/", "--browser", "chromium"])

    def test_viewer_failure_is_propagated(self):
        self.publish()
        self.environment["SCV_SCRIPT_EXIT"] = "7"
        self.assertEqual(self.run_script("http://server:5080/").returncode, 7)

    def test_first_build_then_connect_without_ffmpeg(self):
        (self.root / "build-web.sh").write_text(
            'mkdir -p artifacts/web\nprintf fixture > artifacts/web/SecurityCameraViewer.Web.dll\n', newline="\n")
        result = self.run_script("http://server:5080/")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--connect", self.capture.read_text())

    def test_build_failure_stops_before_launch(self):
        (self.root / "build-web.sh").write_text("exit 9\n", newline="\n")
        self.assertEqual(self.run_script("http://server:5080/").returncode, 9)
        self.assertFalse(self.capture.exists())


@unittest.skipUnless(os.name == "nt", "Windows command scripts need cmd.exe")
class WindowsScriptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="connect viewer ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.capture = self.root / "capture.json"
        self.environment = os.environ.copy()
        self.environment["SCV_BROWSER_TEST_CAPTURE"] = str(self.capture)
        self.browser = REPO / "tests/SecurityCameraViewer.Web.Tests/bin/Release/net10.0/SecurityCameraViewer.Web.Tests.exe"
        self.assertTrue(self.browser.exists(), "Build the Release browser test project first")
        self.assertTrue((REPO / "artifacts/web/SecurityCameraViewer.Web.dll").exists(), "Publish the web server first")

    def run_script(self, script, *arguments):
        # All inputs are fixed test fixtures. Explicit quotes preserve URLs with ampersands.
        command = '"' + " ".join('"' + str(argument) + '"' for argument in [script, *arguments]) + '"'
        return subprocess.run("cmd.exe /d /s /c " + command, cwd=self.root,
                              env=self.environment, capture_output=True, text=True, timeout=20)

    def wait_capture(self):
        import time
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if self.capture.exists():
                try:
                    return json.loads(self.capture.read_text())
                except json.JSONDecodeError:
                    pass
            time.sleep(0.05)
        self.fail("Capture browser did not receive the address")

    def test_root_script_connects_without_starting_server(self):
        address = "https://server.example:9443/viewer/?name=Front%20door&mode=live"
        settings = self.root / "unused-settings.json"
        result = self.run_script(REPO / "connect-web.cmd", address, "--browser-path", self.browser, "--settings", settings)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.wait_capture()["Argument"], "--app=" + address)
        self.assertFalse(settings.exists())

    def test_packaged_script_finds_sibling_executable(self):
        result = self.run_script(REPO / "artifacts/web/connect-web.cmd", "http://server:5080/", "--browser-path", self.browser)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.wait_capture()["Argument"], "--app=http://server:5080/")

    def test_missing_address_reports_usage(self):
        result = self.run_script(REPO / "connect-web.cmd")
        self.assertEqual(result.returncode, 1)
        self.assertIn("Usage:", result.stdout)
        self.assertFalse(self.capture.exists())

    def test_invalid_address_reports_failure(self):
        result = self.run_script(REPO / "connect-web.cmd", "file:///tmp/site", "--browser-path", self.browser)
        self.assertEqual(result.returncode, 1)
        self.assertIn("HTTP or HTTPS", result.stderr)
        self.assertFalse(self.capture.exists())

    def test_missing_browser_reports_failure(self):
        result = self.run_script(REPO / "connect-web.cmd", "http://server:5080/", "--browser-path", self.root / "missing.exe")
        self.assertEqual(result.returncode, 1)
        self.assertFalse(self.capture.exists())

    def test_first_build_then_connect(self):
        shutil.copyfile(REPO / "connect-web.cmd", self.root / "connect-web.cmd")
        build = self.root / "build-web.ps1"
        build.write_text(
            "$ErrorActionPreference = 'Stop'\n"
            "New-Item -ItemType Directory -Path artifacts/web -Force | Out-Null\n"
            f"Get-ChildItem -LiteralPath '{REPO / 'artifacts/web'}' -File | Where-Object Extension -in '.dll', '.json' | Copy-Item -Destination artifacts/web\n"
        )
        result = self.run_script(self.root / "connect-web.cmd", "http://server:5080/", "--browser-path", self.browser)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.root / "artifacts/web/SecurityCameraViewer.Web.dll").exists())
        self.assertEqual(self.wait_capture()["Argument"], "--app=http://server:5080/")

    def test_build_failure_stops_before_launch(self):
        shutil.copyfile(REPO / "connect-web.cmd", self.root / "connect-web.cmd")
        (self.root / "build-web.ps1").write_text("exit 9\n")
        result = self.run_script(self.root / "connect-web.cmd", "http://server:5080/")
        self.assertEqual(result.returncode, 1)
        self.assertFalse(self.capture.exists())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bash")
    options, remaining = parser.parse_known_args()
    if options.bash:
        BASH = options.bash
    unittest.main(argv=[__file__, *remaining])
