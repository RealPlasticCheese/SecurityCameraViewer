param([switch]$SkipMediaMtx, [string]$MediaMtxVersion = '1.21.1')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $PSScriptRoot 'artifacts'
& dotnet publish (Join-Path $PSScriptRoot 'src/RtspVideoHarness.csproj') -c Release -o $output "-p:RestoreConfigFile=$repository/NuGet.Config" --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'RTSP harness build failed.' }
if (-not $SkipMediaMtx -and -not (Test-Path -LiteralPath (Join-Path $output 'mediamtx.exe'))) {
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'amd64' }
    & (Join-Path $repository 'install-mediamtx.ps1') -Version $MediaMtxVersion -Architecture $architecture -Destination $output
}
Write-Host "Built in $output. Run: $PSScriptRoot/run.cmd <video-folder-or-YouTube-URL>"
