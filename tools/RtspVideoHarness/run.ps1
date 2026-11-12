$ErrorActionPreference = 'Stop'
$harnessArguments = @($args)
$optionArguments = @()
foreach ($argument in $harnessArguments) {
    if ($argument -eq '--') { break }
    $optionArguments += $argument
}
$assembly = Join-Path $PSScriptRoot 'artifacts/RtspVideoHarness.dll'
$skipDefaultMediaMtx = $optionArguments -contains '--mediamtx' -or $optionArguments -contains '--help' -or $optionArguments -contains '-h'
if (-not (Test-Path -LiteralPath $assembly) -or (-not $skipDefaultMediaMtx -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'artifacts/mediamtx.exe')))) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipMediaMtx:$skipDefaultMediaMtx
}
# Keep the caller's working directory: omitted and relative folders must refer to it.
if (-not ($optionArguments -contains '--help' -or $optionArguments -contains '-h') -and
    ($optionArguments -contains '--youtube' -or ($harnessArguments | Where-Object { $_ -match '^https?://([^/]+\.)?(youtube\.com|youtu\.be|youtube-nocookie\.com)(/|$)' })) -and
    -not ($optionArguments -contains '--yt-dlp') -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'artifacts/yt-dlp.exe'))) {
    & (Join-Path $PSScriptRoot 'install-ytdlp.ps1')
}
& dotnet $assembly @harnessArguments
exit $LASTEXITCODE
