"""Test update scripts with mocked commands; no services, pulls, or builds run.

Run: python tests/test_update.py --bash /path/to/bash --powershell /path/to/pwsh
Windows: use Git Bash and powershell.exe (or pwsh.exe).
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
BASH = shutil.which("bash")
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")
STEPS = ["stop", "pull", "build", "start"]
BASH_HARNESS = r'''
export PATH="/usr/bin:/bin:$PATH"
repo=$(CDPATH= cd -- "$1" && pwd)
bin=$(CDPATH= cd -- "$2" && pwd)
export EXPECTED_DIR="$repo" UPDATE_LOG="$repo/commands" PATH="$bin:$PATH"
exec bash "$repo/update.sh"
'''
POWERSHELL_HARNESS = r'''
param([string]$Repository)
$originalLocation = (Get-Location).Path
$log = Join-Path $Repository 'commands'
function Record-Step([string]$Step)
{
    if ((Get-Location).Path -ne $Repository) { throw 'Incorrect working directory.' }
    Add-Content -LiteralPath $log -Value $Step
    $global:LASTEXITCODE = 0
    if ($Step -eq $env:FAIL_STEP) { $global:LASTEXITCODE = 7 }
    if ($Step -eq $env:THROW_STEP) { throw 'Command unavailable.' }
}
function bash
{
    if ($args[0] -eq 'web-service.sh' -and $args.Count -eq 2)
    {
        Record-Step $args[1]
    }
    elseif ($args[0] -eq 'build-web.sh' -and $args.Count -eq 1)
    {
        Record-Step 'build'
    }
    else { throw 'Unexpected Bash invocation.' }
}
function git
{
    if ($args.Count -ne 1 -or $args[0] -ne 'pull') { throw 'Unexpected Git invocation.' }
    Record-Step 'pull'
}
$result = 0
try { & (Join-Path $Repository 'update.ps1') }
catch { [Console]::Error.WriteLine($_); $result = 1 }
if ((Get-Location).Path -ne $originalLocation) { exit 99 }
exit $result
'''


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="web-update-test-")
        self.addCleanup(self.temp.cleanup)
        self.fixture = Path(self.temp.name)
        self.repo = self.fixture / "repository with spaces"
        self.repo.mkdir()
        self.bin = self.fixture / "bin"
        self.bin.mkdir()
        for name in ("update.sh", "update.ps1"):
            shutil.copyfile(ROOT / name, self.repo / name)
        record = '''
[[ "$PWD" == "$EXPECTED_DIR" ]] || exit 99
printf '%s\\n' "$step" >> "$UPDATE_LOG"
[[ "$step" != "${FAIL_STEP:-}" ]] || exit 7
'''
        self.write_shell(self.repo / "web-service.sh",
                         '[[ $# == 1 && ( $1 == stop || $1 == start ) ]] || exit 98\nstep=$1\n' + record)
        self.write_shell(self.repo / "build-web.sh", '[[ $# == 0 ]] || exit 98\nstep=build\n' + record)
        self.write_shell(self.bin / "git", '[[ $# == 1 && $1 == pull ]] || exit 98\nstep=pull\n' + record)
        self.harness = self.fixture / "harness.ps1"
        self.harness.write_text(POWERSHELL_HARNESS, encoding="utf-8")

    def write_shell(self, path, body):
        path.write_text("#!/usr/bin/env bash\nset -euo pipefail\n" + body, encoding="utf-8", newline="\n")
        path.chmod(0o755)

    def invoke(self, shell, fail="", throw=""):
        log = self.repo / "commands"
        if log.exists():
            log.unlink()
        if shell == "bash":
            command = [BASH, "-c", BASH_HARNESS, "test", self.repo.as_posix(), self.bin.as_posix()]
        else:
            command = [POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                       str(self.harness), str(self.repo)]
        result = subprocess.run(command, cwd=self.fixture,
                                env={**os.environ, "FAIL_STEP": fail, "THROW_STEP": throw},
                                text=True, capture_output=True, timeout=30)
        commands = log.read_text(encoding="utf-8-sig").splitlines() if log.exists() else []
        self.assertNotEqual(result.returncode, 99, result.stdout + result.stderr)
        return result, commands

    def test_bash_runs_requested_sequence_from_repository_with_spaces(self):
        result, commands = self.invoke("bash")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(commands, STEPS)

    def test_bash_stops_at_each_failed_step_and_propagates_exit_code(self):
        for index, step in enumerate(STEPS):
            with self.subTest(step=step):
                result, commands = self.invoke("bash", fail=step)
                self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
                self.assertEqual(commands, STEPS[:index + 1])

    def test_powershell_runs_requested_sequence_and_restores_callers_directory(self):
        result, commands = self.invoke("powershell")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(commands, STEPS)

    def test_powershell_checks_each_exit_code_and_restores_directory_on_failure(self):
        for index, step in enumerate(STEPS):
            with self.subTest(step=step):
                result, commands = self.invoke("powershell", fail=step)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertEqual(commands, STEPS[:index + 1])
                self.assertIn("failed", result.stderr)
                if step in ("pull", "build"):
                    self.assertIn("remains stopped", result.stderr)

    def test_powershell_stops_and_restores_directory_when_command_throws(self):
        for index, step in enumerate(STEPS):
            with self.subTest(step=step):
                result, commands = self.invoke("powershell", throw=step)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertEqual(commands, STEPS[:index + 1])
                self.assertIn("Command unavailable", result.stderr)

    def test_bash_missing_build_script_does_not_restart_service(self):
        (self.repo / "build-web.sh").unlink()
        result, commands = self.invoke("bash")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(commands, ["stop", "pull"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bash", default=BASH)
    parser.add_argument("--powershell", default=POWERSHELL)
    options, remaining = parser.parse_known_args()
    BASH, POWERSHELL = options.bash, options.powershell
    if not BASH or not POWERSHELL:
        parser.error("Bash and PowerShell are required; specify --bash and --powershell")
    unittest.main(argv=[__file__, *remaining])
