param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Commit = 'local',
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$publish = Join-Path $root "artifacts/publish/$Runtime"
$packages = Join-Path $root 'artifacts/packages'
function Run {
    param([string]$File, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}
New-Item -ItemType Directory -Path $publish, $packages -Force | Out-Null
if ((Get-ChildItem -LiteralPath $publish -Force | Measure-Object).Count) { throw 'Publish directory must be empty. Use a clean build workspace.' }
Run dotnet @('publish', (Join-Path $root 'src/SecurityCameraViewer.Web'), '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-o', $publish, "-p:Version=$Version", "-p:SourceRevisionId=$Commit", "-p:RestoreConfigFile=$root/NuGet.Config")
Run python @((Join-Path $root 'tests/test_host_configuration.py'), (Join-Path $publish 'SecurityCameraViewer.Web.dll'))
& (Join-Path $root 'install-mediamtx.ps1') -Destination $publish
Copy-Item -Path (Join-Path $root 'packaging/windows/*.ps1'), (Join-Path $root 'packaging/windows/*.cmd') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'docs/installation.md') -Destination (Join-Path $publish 'INSTALL.md')
Copy-Item -LiteralPath (Join-Path $root 'docs/https-namecheap.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'packaging/licenses/InnoSetup-LICENSE.txt') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $publish
Run python @((Join-Path $root 'eng/package-notices.py'), $publish, (Join-Path $root 'src/SecurityCameraViewer.Web/obj/project.assets.json'))
Invoke-WebRequest 'https://raw.githubusercontent.com/bluenviron/mediamtx/v1.21.1/LICENSE' -OutFile (Join-Path $publish 'MediaMTX-LICENSE.txt')
Run python @((Join-Path $root 'tests/check_packaged_notices.py'), $publish)
@{ version = $Version; commit = $Commit; runtime = $Runtime; mediamtx = '1.21.1' } | ConvertTo-Json | Set-Content (Join-Path $publish 'release.json') -Encoding utf8
Compress-Archive -Path "$publish/*" -DestinationPath (Join-Path $packages "SecurityCameraViewer-$Version-win-x64.zip")
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 or pass -InnoCompiler.' }
Run $InnoCompiler @("/DPackageVersion=$Version", "/DPublishDirectory=$publish", "/DPackageDirectory=$packages", (Join-Path $root 'packaging/windows/server.iss'))
Get-ChildItem -LiteralPath $packages -File | Where-Object Extension -ne '.sha256' | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($_.FullName + '.sha256', "$hash  $($_.Name)`n")
}
