$ErrorActionPreference = 'Stop'
$version = '6.7.0'
$temporaryDirectory = $env:RUNNER_TEMP ?? $env:AGENT_TEMPDIRECTORY ?? [IO.Path]::GetTempPath()
$download = Join-Path $temporaryDirectory "innosetup-$version.exe"
Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/is-6_7_0/innosetup-$version.exe" -OutFile $download
if ((Get-AuthenticodeSignature -LiteralPath $download).Status -ne 'Valid') {
    throw 'Official Inno Setup download failed Authenticode verification.'
}
$process = Start-Process -FilePath $download -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/ALLUSERS', '/NOICONS' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed: $($process.ExitCode)." }
