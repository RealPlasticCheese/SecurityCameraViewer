param([string]$Version = '1.21.1', [ValidateSet('amd64', 'arm64')][string]$Architecture = 'amd64', [string]$Destination = "$PSScriptRoot/artifacts/web")
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric MediaMTX release version, for example 1.21.1.' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
$asset = "mediamtx_v${Version}_windows_${Architecture}.zip"
$release = "https://github.com/bluenviron/mediamtx/releases/download/v$Version"
$temporary = Join-Path $PSScriptRoot ("artifacts/mediamtx-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    $archive = Join-Path $temporary $asset
    Invoke-WebRequest -Uri "$release/$asset" -OutFile $archive -UseBasicParsing
    $checksumPath = Join-Path $temporary 'checksums.sha256'
    Invoke-WebRequest -Uri "$release/checksums.sha256" -OutFile $checksumPath -UseBasicParsing
    $checksums = Get-Content -LiteralPath $checksumPath -Raw
    $line = ($checksums -split "`n" | Where-Object { $_ -match "^[a-fA-F0-9]{64}\s+\*?$([Regex]::Escape($asset))\s*$" } | Select-Object -First 1)
    if (-not $line) { throw "Release checksums do not contain $asset." }
    $expected = ($line -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'MediaMTX checksum verification failed.' }
    $expanded = Join-Path $temporary 'expanded'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    $binary = Join-Path $expanded 'mediamtx.exe'
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'The release archive contains no mediamtx.exe.' }
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    Copy-Item -LiteralPath $binary -Destination (Join-Path $destinationPath 'mediamtx.exe')
    Write-Host "MediaMTX $Version installed in $destinationPath. The web server manages its configuration and lifecycle."
} finally {
    $checkedTemporary = [IO.Path]::GetFullPath($temporary)
    $checkedArtifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if ($checkedTemporary.StartsWith($checkedArtifacts, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $checkedTemporary -Recurse -Force }
}
