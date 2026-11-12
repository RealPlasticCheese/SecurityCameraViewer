param([string]$Version = 'latest')
$ErrorActionPreference = 'Stop'
if ($Version -ne 'latest' -and $Version -notmatch '^\d{4}\.\d{2}\.\d{2}$') { throw 'Use latest or a yt-dlp release date such as 2026.08.19.' }
$asset = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'yt-dlp_arm64.exe' } else { 'yt-dlp.exe' }
$release = if ($Version -eq 'latest') { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download' } else { "https://github.com/yt-dlp/yt-dlp/releases/download/$Version" }
$output = Join-Path $PSScriptRoot 'artifacts'
$temporary = Join-Path $output ('yt-dlp-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    $binary = Join-Path $temporary $asset
    Invoke-WebRequest -Uri "$release/$asset" -OutFile $binary -UseBasicParsing
    $checksumFile = Join-Path $temporary 'SHA2-256SUMS'
    Invoke-WebRequest -Uri "$release/SHA2-256SUMS" -OutFile $checksumFile -UseBasicParsing
    $checksumLine = Get-Content -LiteralPath $checksumFile | Where-Object { $_ -match "^[a-fA-F0-9]{64}\s+\*?$([Regex]::Escape($asset))\s*$" } | Select-Object -First 1
    if (-not $checksumLine -or (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne ($checksumLine -split '\s+')[0]) { throw 'yt-dlp checksum verification failed.' }
    Copy-Item -LiteralPath $binary -Destination (Join-Path $output 'yt-dlp.exe') -Force
    Write-Host "yt-dlp installed in $output. YouTube also needs Deno or Node.js on PATH."
} finally {
    $checkedTemporary = [IO.Path]::GetFullPath($temporary)
    $checkedOutput = [IO.Path]::GetFullPath($output) + [IO.Path]::DirectorySeparatorChar
    if ($checkedTemporary.StartsWith($checkedOutput, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $checkedTemporary -Recurse -Force }
}
