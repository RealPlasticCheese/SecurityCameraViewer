$ErrorActionPreference = 'Stop'
choco install ffmpeg --yes --no-progress
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg installation failed.' }
. "$PSScriptRoot/../packaging/windows/Setup-Server.ps1"
$binary = Resolve-FfmpegExecutable (Get-Command ffmpeg.exe -ErrorAction Stop).Source
# FFmpeg lifetime tests need the real process, not Chocolatey's child-process launcher.
Write-Host "##vso[task.prependpath]$(Split-Path $binary)"
