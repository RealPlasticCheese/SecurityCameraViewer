param([switch]$PackageOnly, [string]$ServerAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    function Run {
        param([string]$File, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
    }
    Run $(if ($IsLinux) { 'python3' } else { 'python' }) @('-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_packaging.py', '-v')
    Run $(if ($IsLinux) { 'python3' } else { 'python' }) @('-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_package_notices.py', '-v')
    & "$root/tests/Test-WindowsSetup.ps1"
    if ($IsWindows) {
        Run "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$root/tests/Test-WindowsSetup.ps1")
    }
    if ($PackageOnly) { return }
    if (-not $ServerAssembly) {
        $testPublish = Join-Path $root 'artifacts/test-web'
        Run dotnet @('publish', 'src/SecurityCameraViewer.Web', '-c', 'Release', '--self-contained', 'false', '-o', $testPublish)
        $ServerAssembly = Join-Path $testPublish 'SecurityCameraViewer.Web.dll'
    }
    $ServerAssembly = [IO.Path]::GetFullPath($ServerAssembly)
    foreach ($project in @('SecurityCameraViewer.Tests', 'SecurityCameraViewer.Nvr.Tests', 'SecurityCameraViewer.Recording.Tests', 'SecurityCameraViewer.Web.Tests', 'SecurityCameraViewer.Streaming.Tests')) {
        $arguments = @('run', '--project', "tests/$project", '-c', 'Release')
        if ($project -eq 'SecurityCameraViewer.Web.Tests') { $arguments += @('--', '--settings-only') }
        if ($project -eq 'SecurityCameraViewer.Recording.Tests') { $arguments += @('--', '--unit-only') }
        Run dotnet $arguments
    }
    Run dotnet @('run', '--project', 'tests/SecurityCameraViewer.Web.Tests', '-c', 'Release', '--', '--browser-launch-only', $ServerAssembly)
    Run node (@('--test') + @(Get-ChildItem 'tests/web-*.test.cjs' | ForEach-Object FullName))
} finally { Pop-Location }
