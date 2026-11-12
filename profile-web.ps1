param(
    [Parameter(Mandatory = $true)][ValidateRange(1, [int]::MaxValue)][int]$ProcessId,
    [ValidateSet('trace', 'allocations', 'metrics')][string]$Mode = 'trace',
    [ValidateRange(1, [int]::MaxValue)][int]$DurationSeconds = 60,
    [string]$OutputDirectory,
    [string]$ToolPath,
    [ValidateRange(1, [int]::MaxValue)][int]$MaxHistograms = 64,
    [ValidateRange(1, [int]::MaxValue)][int]$TraceBufferMegabytes = 256
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot ('artifacts/profiling/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$toolName = if ($Mode -eq 'metrics') { 'dotnet-counters' } else { 'dotnet-trace' }
if (!$ToolPath) {
    $ToolPath = Join-Path $PSScriptRoot "artifacts/profiling-tools/$toolName.exe"
}
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
    throw "Missing $toolName. Install the tools using docs/profiling.md, or specify -ToolPath."
}
$extension = if ($Mode -eq 'metrics') { 'csv' } else { 'nettrace' }
$outputPath = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$Mode.$extension"))
$metadataPath = Join-Path ([IO.Path]::GetDirectoryName($outputPath)) "capture-$Mode.txt"
if ((Test-Path -LiteralPath $outputPath) -or (Test-Path -LiteralPath $metadataPath)) {
    throw 'Capture output already exists. Choose a new output directory.'
}
$toolVersion = (& $ToolPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "$toolName could not report its version." }
$duration = [TimeSpan]::FromSeconds($DurationSeconds)
$durationArgument = '{0:00}:{1:00}:{2:00}:{3:00}' -f $duration.Days, $duration.Hours, $duration.Minutes, $duration.Seconds
$captureArguments = @('collect', '--process-id', "$ProcessId", '--duration', $durationArgument, '--output', $outputPath)
if ($Mode -eq 'metrics') {
    $captureArguments += @('--format', 'csv', '--counters', 'System.Runtime,SecurityCameraViewer.Processing', '--maxHistograms', "$MaxHistograms")
} else {
    $profile = if ($Mode -eq 'allocations') { 'gc-verbose' } else { 'dotnet-common,dotnet-sampled-thread-time' }
    $captureArguments += @('--profile', $profile, '--buffersize', "$TraceBufferMegabytes")
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
@(
    "startedUtc=$([DateTime]::UtcNow.ToString('O'))", "processId=$ProcessId", "mode=$Mode",
    "durationSeconds=$DurationSeconds", "toolVersion=$toolVersion", "os=$([Environment]::OSVersion)",
    "maxHistograms=$MaxHistograms", "traceBufferMegabytes=$TraceBufferMegabytes"
) | Set-Content -LiteralPath $metadataPath -Encoding UTF8
Write-Host "Capturing $Mode for $DurationSeconds seconds into $outputPath"
& $ToolPath @captureArguments
$captureExitCode = $LASTEXITCODE
Add-Content -LiteralPath $metadataPath -Value "exitCode=$captureExitCode"
exit $captureExitCode
