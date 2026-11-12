$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../packaging/windows/Setup-Server.ps1"
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS: $Message"
}
$Port = 6010
$BindAddress = '127.0.0.1'
$config = New-HostConfiguration 'C:\Server Data' 'C:\FFmpeg\ffmpeg.exe'
Check ($config.urls -eq 'http://127.0.0.1:6010') 'Host configuration uses selected listener'
Check ($config.settings -eq (Join-Path 'C:\Server Data' 'data/settings.json')) 'Settings remain in writable state outside program files'
$Hostname = 'viewer.example.com'
$HttpsMode = 'Public'
Check ((New-CaddySite) -match 'reverse_proxy 127.0.0.1:6010') 'Public HTTPS uses configured backend'
$HttpsMode = 'Internal'
Check ((New-CaddySite) -match 'tls internal') 'Local HTTPS explicitly uses local authority'
$HttpsMode = 'Dns'
$DnsProvider = 'cloudflare'
Check ((New-CaddySite) -match 'dns cloudflare') 'DNS validation uses requested provider'
foreach ($bad in @('a"', "bad`nhost", 'bad/host', '*.example.com', '-bad.example.com', 'a..example.com')) {
    $Hostname = $bad
    $rejected = $false
    try { New-CaddySite | Out-Null } catch { $rejected = $true }
    Check $rejected 'Invalid HTTPS hostname is rejected'
}
$Hostname = 'viewer.example.com'
foreach ($bad in @('bad provider', "bad`nprovider", '../plugin')) {
    $DnsProvider = $bad
    $rejected = $false
    try { New-CaddySite | Out-Null } catch { $rejected = $true }
    Check $rejected 'Invalid DNS provider is rejected'
}
Write-Host 'Windows setup configuration checks passed.'

function Native-ArgumentFixture {
    $script:capturedArguments = $args
    $global:LASTEXITCODE = 0
}
Invoke-Native 'Native-ArgumentFixture' @('first', 'second with space', '--flag', 'last')
Check ($capturedArguments.Count -eq 4 -and $capturedArguments[1] -eq 'second with space' -and $capturedArguments[3] -eq 'last') 'Native argument arrays preserve all values and spaces'

function Get-Service { return $null }
function New-Service { param($Name, $BinaryPathName, $StartupType, $DisplayName); $script:createdPath = $BinaryPathName }
function Get-CimInstance { return [pscustomobject]@{ Name = 'fixture' } }
function Invoke-CimMethod { param($InputObject, $MethodName, $Arguments); $script:serviceArguments = $Arguments; return [pscustomobject]@{ ReturnValue = $script:serviceResult } }
$script:serviceResult = 0
$binaryPath = '"C:\Program Files\Camera Server\SecurityCameraViewer.Web.exe" --host-config "C:\Server Data\host.json"'
Set-ViewerService $binaryPath
Check ($createdPath -eq $binaryPath -and $serviceArguments.PathName -eq $binaryPath) 'Service APIs preserve quoted executable and configuration paths'
Check ($serviceArguments.StartName -eq 'NT AUTHORITY\NetworkService') 'Installed service uses its restricted account'
$script:serviceResult = 5
$rejected = $false
try { Set-ViewerService $binaryPath } catch { $rejected = $true }
Check $rejected 'Service configuration failures are reported'

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixtureRoot = Join-Path $artifactsRoot ('ffmpeg-resolution-' + [Guid]::NewGuid().ToString('N'))
try {
    $shimDirectory = Join-Path $fixtureRoot 'bin'
    $binaryDirectory = Join-Path $fixtureRoot 'lib/ffmpeg/tools/real/bin'
    New-Item -ItemType Directory -Path $shimDirectory, $binaryDirectory -Force | Out-Null
    $shim = Join-Path $shimDirectory 'ffmpeg.exe'
    $binary = Join-Path $binaryDirectory 'ffmpeg.exe'
    Set-Content -LiteralPath $shim 'fixture launcher'
    Set-Content -LiteralPath $binary 'fixture executable'
    Check ((Resolve-FfmpegExecutable $shim $fixtureRoot) -eq $binary) 'Chocolatey launchers resolve to the actual decoder executable'
    Check ((Resolve-FfmpegExecutable $binary $fixtureRoot) -eq $binary) 'Direct decoder paths remain unchanged'
    $secondDirectory = Join-Path $fixtureRoot 'lib/ffmpeg/tools/other/bin'
    New-Item -ItemType Directory -Path $secondDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $secondDirectory 'ffmpeg.exe') 'other fixture'
    $rejected = $false
    try { Resolve-FfmpegExecutable $shim $fixtureRoot } catch { $rejected = $true }
    Check $rejected 'Ambiguous decoder installations require an explicit executable path'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped artifacts.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
