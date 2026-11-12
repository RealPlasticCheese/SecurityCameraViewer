"""Test capture wrappers with fake tools; never attach to a real process.

Run: python tests/test_profiling_scripts.py --bash "C:/Program Files/Git/bin/bash.exe"
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[1]
BASH = shutil.which("bash")
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")


class CaptureScriptTests(unittest.TestCase):
    def setUp(self):
        fixture_root = REPO / "artifacts/profiling-script-tests"
        fixture_root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="profiling scripts ", dir=fixture_root)
        self.root = Path(self.temp.name)
        self.assertTrue(self.root.resolve().is_relative_to(fixture_root.resolve()))
        self.addCleanup(self.temp.cleanup)
        self.environment = os.environ.copy()
        self.environment["TEST_CAPTURE_ARGS"] = str(self.root / "arguments")
        self.ps_tool = self.root / "fake tool.ps1"
        self.ps_tool.write_text("""
if ($args[0] -eq '--version') {
    if ($env:TEST_VERSION_FAIL) { exit 7 }
    Write-Output '10.0.750501'
    exit 0
}
$args | Set-Content -LiteralPath $env:TEST_CAPTURE_ARGS
$outputIndex = [Array]::IndexOf($args, '--output')
Set-Content -LiteralPath $args[$outputIndex + 1] -Value 'fixture'
if ($env:TEST_CAPTURE_FAIL) { exit 9 }
exit 0
""", encoding="utf-8")
        self.sh_tool = self.root / "fake tool.sh"
        self.sh_tool.write_text("""#!/usr/bin/env bash
if [[ $1 == --version ]]; then
    [[ -z ${TEST_VERSION_FAIL:-} ]] || exit 7
    echo '10.0.750501'
    exit 0
fi
printf '%s\\n' "$@" > "$TEST_CAPTURE_ARGS"
while (($#)); do
    if [[ $1 == --output ]]; then echo fixture > "$2"; break; fi
    shift
done
[[ -z ${TEST_CAPTURE_FAIL:-} ]] || exit 9
""", encoding="utf-8", newline="\n")
        self.sh_tool.chmod(0o755)

    def invoke(self, shell, mode="trace", duration="60", process_id="1234", extra=(), output=None):
        output = output or self.root / "output space $literal"
        if shell == "powershell":
            if not POWERSHELL:
                self.skipTest("PowerShell unavailable")
            command = [POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(REPO / "profile-web.ps1"),
                       "-ProcessId", process_id, "-Mode", mode, "-DurationSeconds", duration,
                       "-OutputDirectory", str(output), "-ToolPath", str(self.ps_tool), *extra]
        else:
            if not BASH:
                self.skipTest("Bash unavailable")
            command = [BASH, str(REPO / "profile-web.sh"), "--process-id", process_id, "--mode", mode,
                       "--duration-seconds", duration, "--output-directory", str(output), "--tool-path", str(self.sh_tool), *extra]
        result = subprocess.run(command, cwd=self.root, env=self.environment, capture_output=True, text=True, timeout=20)
        recorded = self.root / "arguments"
        arguments = recorded.read_text(encoding="utf-8-sig").splitlines() if recorded.exists() else []
        return result, arguments, output

    def test_modes_duration_and_quoting(self):
        for shell in ("powershell", "bash"):
            for mode in ("trace", "allocations", "metrics"):
                with self.subTest(shell=shell, mode=mode):
                    output = self.root / f"{shell} {mode} $literal"
                    result, arguments, _ = self.invoke(shell, mode=mode, duration="90061", output=output)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual(arguments[arguments.index("--duration") + 1], "01:01:01:01")
                    self.assertEqual(arguments[arguments.index("--process-id") + 1], "1234")
                    extension = "csv" if mode == "metrics" else "nettrace"
                    self.assertTrue((output / f"{mode}.{extension}").exists())
                    metadata = (output / f"capture-{mode}.txt").read_text(encoding="utf-8-sig")
                    self.assertIn("toolVersion=10.0.750501", metadata)
                    self.assertIn("exitCode=0", metadata)
                    if mode == "metrics":
                        self.assertIn("System.Runtime,SecurityCameraViewer.Processing", arguments)
                        self.assertNotIn("--profile", arguments)
                    else:
                        profile = "gc-verbose" if mode == "allocations" else "dotnet-common,dotnet-sampled-thread-time"
                        self.assertIn(profile, arguments)

    def test_resource_budgets(self):
        for shell in ("powershell", "bash"):
            with self.subTest(shell=shell):
                extra = ("-MaxHistograms", "120") if shell == "powershell" else ("--max-histograms", "120")
                result, arguments, _ = self.invoke(shell, mode="metrics", extra=extra, output=self.root / f"metrics {shell}")
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(arguments[arguments.index("--maxHistograms") + 1], "120")
                extra = ("-TraceBufferMegabytes", "32") if shell == "powershell" else ("--trace-buffer-megabytes", "32")
                result, arguments, _ = self.invoke(shell, extra=extra, output=self.root / f"trace {shell}")
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(arguments[arguments.index("--buffersize") + 1], "32")

    def test_invalid_options(self):
        for shell in ("powershell", "bash"):
            for options in ({"mode": "unknown"}, {"duration": "0"}, {"duration": "-1"}, {"process_id": "0"}, {"process_id": "bad"}):
                with self.subTest(shell=shell, options=options):
                    result, _, output = self.invoke(shell, **options)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(output.exists())

    def test_missing_tool_and_version_failure(self):
        for shell in ("powershell", "bash"):
            with self.subTest(shell=shell):
                tool = self.ps_tool if shell == "powershell" else self.sh_tool
                saved = tool.read_bytes()
                tool.unlink()
                result, _, output = self.invoke(shell)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(output.exists())
                tool.write_bytes(saved)
                tool.chmod(0o755)
                self.environment["TEST_VERSION_FAIL"] = "1"
                result, _, output = self.invoke(shell)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(output.exists())
                del self.environment["TEST_VERSION_FAIL"]

    def test_failure_propagation_and_overwrite_protection(self):
        for shell in ("powershell", "bash"):
            with self.subTest(shell=shell):
                self.environment["TEST_CAPTURE_FAIL"] = "1"
                result, _, output = self.invoke(shell, output=self.root / shell)
                self.assertEqual(result.returncode, 9, result.stdout + result.stderr)
                metadata = output / "capture-trace.txt"
                before = metadata.read_bytes()
                self.assertIn(b"exitCode=9", before)
                del self.environment["TEST_CAPTURE_FAIL"]
                result, _, _ = self.invoke(shell, output=output)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(metadata.read_bytes(), before)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bash", default=BASH)
    options, remaining = parser.parse_known_args()
    BASH = options.bash
    unittest.main(argv=[__file__, *remaining])
