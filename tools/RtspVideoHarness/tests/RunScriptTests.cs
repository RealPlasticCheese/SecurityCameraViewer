using System.Diagnostics;
using System.Text.Json;

namespace RtspVideoHarness.Tests;

internal static class RunScriptTests
{
    public static async Task RunAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools", "RtspVideoHarness", "run.sh")))
        {
            directory = directory.Parent;
        }
        var tool = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find harness scripts."), "tools", "RtspVideoHarness");
        var shells = new List<(string Executable, bool PowerShell)>();
        if (OperatingSystem.IsWindows())
        {
            shells.Add((Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), true));
            var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            if (File.Exists(gitBash))
            {
                shells.Add((gitBash, false));
            }
        }
        else
        {
            shells.Add(("bash", false));
        }

        const string url = "https://www.youtube.com/watch?v=fixture&list=one";
        TestCase[] cases =
        [
            new("An old system extractor cannot suppress the default local installation", [url], Install: true),
            new("Explicit YouTube input installs the local extractor", ["--youtube", url, "--port", "8555"], Install: true),
            new("URL detection accepts uppercase scheme and host", [url.ToUpperInvariant()], Install: true),
            new("An existing local extractor is retained", [url], Local: true),
            new("An explicit extractor path skips automatic installation", [url, "--yt-dlp", "custom yt-dlp with spaces"]),
            new("Folder input needs no extractor", ["sample videos", "--blank-ms", "1000"]),
            new("Omitted input keeps the caller directory", []),
            new("Help with a URL needs no extractor", [url, "--help"]),
            new("A literal folder named --youtube needs no extractor", ["--", "--youtube"]),
            new("A URL after the option terminator still installs the extractor", ["--port", "8555", "--", url], Install: true),
            new("First run builds and installs without changing the caller directory", [url], Install: true, Build: true),
            new("Installer failure stops before playback", [url], Install: true, InstallerFails: true),
            new("Build failure stops before installation or playback", [url], Build: true, BuildFails: true),
            new("Playback exit status is propagated", [url], Local: true, ExitCode: 9)
        ];
        foreach (var shell in shells)
        {
            foreach (var test in cases)
            {
                await RunCaseAsync(tool, shell.Executable, shell.PowerShell, test);
            }
        }
    }

    private static async Task RunCaseAsync(string tool, string executable, bool powerShell, TestCase test)
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.Combine(baseDirectory, "run-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var caller = Path.Combine(root, "caller folder");
            var artifacts = Path.Combine(root, "artifacts");
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(caller);
            Directory.CreateDirectory(artifacts);
            Directory.CreateDirectory(bin);
            var suffix = powerShell ? ".ps1" : ".sh";
            File.Copy(Path.Combine(tool, "run" + suffix), Path.Combine(root, "run" + suffix));
            if (!test.Build)
            {
                File.WriteAllText(Path.Combine(artifacts, "RtspVideoHarness.dll"), "fixture");
            }
            Executable(Path.Combine(artifacts, powerShell ? "mediamtx.exe" : "mediamtx"), "#!/usr/bin/env bash\nexit 0\n");
            if (test.Local)
            {
                Executable(Path.Combine(artifacts, powerShell ? "yt-dlp.exe" : "yt-dlp"), "#!/usr/bin/env bash\nexit 0\n");
            }
            var log = Path.Combine(root, "arguments.txt");
            var cwd = Path.Combine(root, "cwd.txt");
            var installed = Path.Combine(root, "installed.txt");
            var built = Path.Combine(root, "built.txt");
            var launcher = Path.Combine(root, "launcher.ps1");
            if (powerShell)
            {
                File.WriteAllText(launcher, """
                    $ErrorActionPreference = 'Stop'
                    function global:yt-dlp { '2024.04.09' }
                    function global:dotnet {
                        ConvertTo-Json -InputObject @($args) -Compress | Set-Content -LiteralPath $env:HARNESS_SCRIPT_TEST_LOG -Encoding UTF8
                        (Get-Location).Path | Set-Content -LiteralPath $env:HARNESS_SCRIPT_TEST_CWD -Encoding UTF8
                        $global:LASTEXITCODE = [int]$env:HARNESS_SCRIPT_TEST_EXIT
                    }
                    & (Join-Path $PSScriptRoot 'run.ps1') @args
                    exit $LASTEXITCODE
                    """);
                File.WriteAllText(Path.Combine(root, "install-ytdlp.ps1"), """
                    'called' | Set-Content -LiteralPath $env:HARNESS_SCRIPT_TEST_INSTALLED
                    if ($env:HARNESS_SCRIPT_TEST_INSTALL_FAIL -eq '1') { throw 'Fixture installer failed.' }
                    New-Item -ItemType File -Path (Join-Path $PSScriptRoot 'artifacts/yt-dlp.exe') -Force | Out-Null
                    """);
                File.WriteAllText(Path.Combine(root, "build.ps1"), """
                    param([switch]$SkipMediaMtx)
                    'called' | Set-Content -LiteralPath $env:HARNESS_SCRIPT_TEST_BUILT
                    if ($env:HARNESS_SCRIPT_TEST_BUILD_FAIL -eq '1') { throw 'Fixture build failed.' }
                    New-Item -ItemType File -Path (Join-Path $PSScriptRoot 'artifacts/RtspVideoHarness.dll') -Force | Out-Null
                    """);
            }
            else
            {
                Executable(Path.Combine(bin, "yt-dlp"), "#!/usr/bin/env bash\necho 2024.04.09\n");
                Executable(Path.Combine(bin, "dotnet"), """
                    #!/usr/bin/env bash
                    printf '%s\n' "$@" > "$HARNESS_SCRIPT_TEST_LOG"
                    """ + (OperatingSystem.IsWindows() ? "\npwd -W" : "\npwd") + " > \"$HARNESS_SCRIPT_TEST_CWD\"\nexit \"$HARNESS_SCRIPT_TEST_EXIT\"\n");
                File.WriteAllText(Path.Combine(root, "install-ytdlp.sh"), """
                    #!/usr/bin/env bash
                    echo called > "$HARNESS_SCRIPT_TEST_INSTALLED"
                    if [[ $HARNESS_SCRIPT_TEST_INSTALL_FAIL == 1 ]]; then echo 'Fixture installer failed.' >&2; exit 7; fi
                    tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
                    touch "$tool_dir/artifacts/yt-dlp"
                    chmod +x "$tool_dir/artifacts/yt-dlp"
                    """ + "\n");
                File.WriteAllText(Path.Combine(root, "build.sh"), """
                    #!/usr/bin/env bash
                    echo called > "$HARNESS_SCRIPT_TEST_BUILT"
                    if [[ $HARNESS_SCRIPT_TEST_BUILD_FAIL == 1 ]]; then echo 'Fixture build failed.' >&2; exit 8; fi
                    tool_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
                    touch "$tool_dir/artifacts/RtspVideoHarness.dll"
                    """ + "\n");
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = caller
            };
            if (powerShell)
            {
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher })
                {
                    start.ArgumentList.Add(argument);
                }
            }
            else
            {
                start.ArgumentList.Add(Path.Combine(root, "run.sh").Replace('\\', '/'));
                var bashBin = OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(bin[0]) + bin[2..].Replace('\\', '/') : bin;
                start.Environment["PATH"] = bashBin + ":" + (OperatingSystem.IsWindows() ? "/usr/bin:/bin" : start.Environment["PATH"]);
            }
            foreach (var argument in test.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
            string EnvironmentPath(string value) => powerShell ? value : value.Replace('\\', '/');
            start.Environment["HARNESS_SCRIPT_TEST_LOG"] = EnvironmentPath(log);
            start.Environment["HARNESS_SCRIPT_TEST_CWD"] = EnvironmentPath(cwd);
            start.Environment["HARNESS_SCRIPT_TEST_INSTALLED"] = EnvironmentPath(installed);
            start.Environment["HARNESS_SCRIPT_TEST_BUILT"] = EnvironmentPath(built);
            start.Environment["HARNESS_SCRIPT_TEST_EXIT"] = test.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["HARNESS_SCRIPT_TEST_INSTALL_FAIL"] = test.InstallerFails ? "1" : "0";
            start.Environment["HARNESS_SCRIPT_TEST_BUILD_FAIL"] = test.BuildFails ? "1" : "0";
            using var process = Process.Start(start) ?? throw new IOException("Cannot start run-script regression fixture.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            var diagnostics = await output + await error;
            var failure = test.InstallerFails || test.BuildFails;
            var correctExit = failure ? process.ExitCode != 0 : process.ExitCode == test.ExitCode;
            var forwarded = failure ? !File.Exists(log) : File.Exists(log) &&
                (powerShell ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(log))! : File.ReadAllLines(log)).Skip(1).SequenceEqual(test.Arguments);
            var sameDirectory = failure || (File.Exists(cwd) && Path.GetFullPath(File.ReadAllText(cwd).Trim()) == Path.GetFullPath(caller));
            var passed = correctExit && forwarded && sameDirectory && File.Exists(installed) == test.Install && File.Exists(built) == test.Build;
            Check.That(passed, $"{(powerShell ? "PowerShell" : "Bash")}: {test.Name}" +
                (passed ? "" : $". Exit={process.ExitCode}, forwarded={forwarded}, directory={sameDirectory}, installed={File.Exists(installed)}, built={File.Exists(built)}. {diagnostics.Trim()}"));
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (!target.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException("Run-script fixture cleanup escaped its temporary test directory.");
            }
            Directory.Delete(target, recursive: true);
        }
    }

    private static void Executable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private sealed record TestCase(string Name, string[] Arguments, bool Install = false, bool Local = false,
        bool Build = false, bool InstallerFails = false, bool BuildFails = false, int ExitCode = 0);
}
