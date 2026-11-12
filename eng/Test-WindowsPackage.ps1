param([Parameter(Mandatory)][string]$Packages)
$ErrorActionPreference = 'Stop'
$installer = @(Get-ChildItem -Path $Packages -Filter '*-setup.exe')
if ($installer.Count -ne 1) { throw 'Expected exactly one setup executable.' }
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer[0].FullName)
if ($versionInfo.CompanyName.Trim() -ne 'Frank Tzanabetis' -or
    $versionInfo.LegalCopyright -notlike '*Frank Tzanabetis*') {
    throw 'Installer publisher or copyright attribution is missing.'
}
function Write-InstallDiagnostics {
    Get-CimInstance -ClassName Win32_Service -Filter "Name='SecurityCameraViewer'" | Format-List Name, State, PathName, StartName
    Get-Content "$env:ProgramData/SecurityCameraViewer/host.json" -ErrorAction SilentlyContinue
    Get-Content installer-test.log -Tail 100 -ErrorAction SilentlyContinue
    Get-Content "$env:TEMP/SecurityCameraViewer-setup.log" -Tail 80 -ErrorAction SilentlyContinue
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'SecurityCameraViewer.Web' } -MaxEvents 10 -ErrorAction SilentlyContinue | Format-List Message
}
function Install-Package {
    $process = Start-Process -FilePath $installer[0].FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=service', '/PORT=5089', '/LOG=installer-test.log' -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { Write-InstallDiagnostics; throw "Installer failed: $($process.ExitCode)" }
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest 'http://127.0.0.1:5089/manifest.webmanifest' -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Seconds 1 }
    }
    Write-InstallDiagnostics
    throw 'Installed service is not healthy.'
}
$failure = Start-Process -FilePath $installer[0].FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=service', '/PORT=5089', '/HTTPSMODE=1', '/HOSTNAME=invalid/hostname', '/LOG=installer-test.log' -Wait -PassThru -WindowStyle Hidden
if ($failure.ExitCode -eq 0) { throw 'Invalid HTTPS configuration must produce a failed installer exit code.' }
Write-Host 'PASS: invalid setup configuration reports failure; retrying with valid settings.'
Install-Package
$uninstallEntry = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{732C250D-B7A0-4D5D-AC81-52C931D29A23}_is1'
if ($uninstallEntry.Publisher -ne 'Frank Tzanabetis' -or $uninstallEntry.URLInfoAbout -ne 'https://plasticcheese.com') {
    throw 'Installed application publisher or website is missing.'
}
python "$PSScriptRoot/../tests/check_packaged_notices.py" "$env:ProgramFiles/SecurityCameraViewer"
if ($LASTEXITCODE -ne 0) { throw 'Installed package notices are incomplete.' }
$data = "$env:ProgramData/SecurityCameraViewer"
$before = Get-Content "$data/host.json" -Raw
if ((($before | ConvertFrom-Json).settings) -ne (Join-Path $data 'data/settings.json')) { throw 'Settings must use the service-writable data directory.' }
$serviceAcl = (Get-Acl "$data/host.json").Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-20' }
if ($serviceAcl | Where-Object { ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -ne 0 }) { throw 'The service must not be able to rewrite administrator host configuration.' }
Set-Content "$data/preservation-fixture" 'keep'
Install-Package
if ((Get-Content "$data/host.json" -Raw) -ne $before) { throw 'Upgrade changed host settings.' }
$uninstaller = "$env:ProgramFiles/SecurityCameraViewer/unins000.exe"
$process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Uninstall failed.' }
if (-not (Test-Path "$data/host.json") -or (Get-Content "$data/preservation-fixture") -ne 'keep') { throw 'Uninstall removed persistent data.' }
if (Get-Service SecurityCameraViewer -ErrorAction SilentlyContinue) { throw 'Uninstall left the service registered.' }
