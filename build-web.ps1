param([switch]$SelfContained, [string]$Runtime)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $publishArgs = @('publish', 'src/SecurityCameraViewer.Web/SecurityCameraViewer.Web.csproj', '-c', 'Release', '-o', 'artifacts/web', "-p:RestoreConfigFile=$PSScriptRoot/NuGet.Config", '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant())
    if ($Runtime) { $publishArgs += @('-r', $Runtime) }
    elseif ($SelfContained) { throw 'Specify -Runtime (for example win-x64 or linux-x64) with -SelfContained.' }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Web server publish failed.' }
    Write-Host "Web server: $PSScriptRoot/artifacts/web"
    Write-Host 'Run launch-web.cmd, or: dotnet artifacts/web/SecurityCameraViewer.Web.dll'
} finally { Pop-Location }
