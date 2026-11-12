#ifndef PackageVersion
  #define PackageVersion "0.0.0"
#endif
#ifndef PublishDirectory
  #define PublishDirectory "..\..\artifacts\publish\win-x64"
#endif
#ifndef PackageDirectory
  #define PackageDirectory "..\..\artifacts\packages"
#endif

[Setup]
AppId={{732C250D-B7A0-4D5D-AC81-52C931D29A23}
AppName=Security Camera Viewer
AppPublisher=Frank Tzanabetis
AppPublisherURL=https://plasticcheese.com
AppSupportURL=https://plasticcheese.com
AppCopyright=Copyright (c) 2026 Frank Tzanabetis
DisableWelcomePage=no
AppVersion={#PackageVersion}
DefaultDirName={autopf}\SecurityCameraViewer
DefaultGroupName=Security Camera Viewer
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#PackageDirectory}
OutputBaseFilename=SecurityCameraViewer-{#PackageVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\SecurityCameraViewer.Web.exe
CloseApplications=no
SetupLogging=yes

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "service"; Description: "Run the server as a Windows service at startup"; Flags: checkedonce
Name: "firewall"; Description: "Allow HTTP connections from the local subnet on private networks"; Flags: unchecked
Name: "ffmpeg"; Description: "Install FFmpeg using Windows Package Manager if missing (internet required)"; Flags: checkedonce

[Registry]
Root: HKLM; Subkey: "Software\SecurityCameraViewer"; ValueType: string; ValueName: "Port"; ValueData: "{code:GetPort}"; Flags: uninsdeletekey

[Icons]
Name: "{group}\Open camera viewer"; Filename: "http://localhost:{code:GetPort}/"
Name: "{group}\Start server manually"; Filename: "{app}\Start-Server.cmd"
Name: "{group}\Installation and HTTPS guide"; Filename: "{app}\INSTALL.md"

[Run]
Filename: "http://localhost:{code:GetPort}/"; Description: "Open camera setup in your browser"; Flags: shellexec postinstall skipifsilent; Check: ServiceSelected

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Setup-Server.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveServerService"

[Code]
var
  SetupFailureCode: Integer;
  HostPage: TInputQueryWizardPage;
  HttpsPage: TInputOptionWizardPage;
  DnsPage: TInputQueryWizardPage;

procedure InitializeWizard;
var PreviousPort: String;
begin
  WizardForm.WelcomeLabel2.Caption := 'Created by Frank Tzanabetis'#13#10 +
    'https://plasticcheese.com'#13#10#13#10 + WizardForm.WelcomeLabel2.Caption;
  HostPage := CreateInputQueryPage(wpSelectDir, 'Server settings', 'Choose the HTTP port',
    'Existing host settings are preserved during upgrades. Camera settings are configured in the browser.');
  HostPage.Add('HTTP port (use a separate backend port behind HTTPS):', False);
  PreviousPort := '5080';
  RegQueryStringValue(HKLM, 'Software\SecurityCameraViewer', 'Port', PreviousPort);
  HostPage.Values[0] := ExpandConstant('{param:PORT|' + PreviousPort + '}');
  HttpsPage := CreateInputOptionPage(HostPage.ID, 'HTTPS setup assistance', 'Choose certificate provisioning',
    'Setup can generate a Caddy configuration. Activation, DNS credentials and certificate verification are described in the installed guide.', True, False);
  HttpsPage.Add('Preserve existing HTTPS or configure later');
  HttpsPage.Add('Public domain with automatic ACME certificates');
  HttpsPage.Add('LAN-only domain using DNS validation');
  HttpsPage.Add('Local certificate authority (requires client trust setup)');
  HttpsPage.SelectedValueIndex := StrToIntDef(ExpandConstant('{param:HTTPSMODE|0}'), 0);
  DnsPage := CreateInputQueryPage(HttpsPage.ID, 'HTTPS hostname', 'Enter your domain and DNS provider',
    'For DNS validation, install the corresponding Caddy provider module and set its credentials privately on this host.');
  DnsPage.Add('Hostname:', False);
  DnsPage.Add('Caddy DNS provider module (DNS mode only):', False);
  DnsPage.Values[0] := ExpandConstant('{param:HOSTNAME|}');
  DnsPage.Values[1] := ExpandConstant('{param:DNSPROVIDER|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = DnsPage.ID) and (HttpsPage.SelectedValueIndex = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var P: Integer;
begin
  Result := True;
  if CurPageID = HostPage.ID then begin
    P := StrToIntDef(HostPage.Values[0], 0);
    Result := (P >= 1) and (P <= 65535);
    if not Result then MsgBox('Enter a port from 1 to 65535.', mbError, MB_OK);
  end;
end;

function GetPort(Param: String): String;
begin
  Result := HostPage.Values[0];
end;

function ServiceSelected: Boolean;
begin
  Result := WizardIsTaskSelected('service') and (SetupFailureCode = 0);
end;

function Quote(Value: String): String;
begin
  if (Pos('"', Value) > 0) or (Pos(#13, Value) > 0) or (Pos(#10, Value) > 0) then
    RaiseException('Setup values must not contain quotes or newlines.');
  Result := '"' + Value + '"';
end;

function GetSetupArguments(Param: String): String;
var Mode, HttpsMode: String;
begin
  if ServiceSelected then Mode := 'Service' else Mode := 'Manual';
  case HttpsPage.SelectedValueIndex of
    1: HttpsMode := 'Public';
    2: HttpsMode := 'Dns';
    3: HttpsMode := 'Internal';
  else HttpsMode := 'Preserve'; end;
  Result := '-NoProfile -ExecutionPolicy Bypass -File ' + Quote(ExpandConstant('{app}\Setup-Server.ps1')) +
    ' -InstallDirectory ' + Quote(ExpandConstant('{app}')) + ' -Port ' + Quote(GetPort('')) + ' -Mode ' + Mode +
    ' -HttpsMode ' + HttpsMode;
  if HttpsMode <> 'Preserve' then Result := Result + ' -Hostname ' + Quote(DnsPage.Values[0]);
  if HttpsMode = 'Dns' then Result := Result + ' -DnsProvider ' + Quote(DnsPage.Values[1]);
  if WizardIsTaskSelected('firewall') then Result := Result + ' -OpenFirewall';
  if WizardIsTaskSelected('ffmpeg') then Result := Result + ' -InstallFfmpeg';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  // Stop a prior service before replacing its binaries. No service exists on first install.
  if FileExists(ExpandConstant('{app}\Setup-Server.ps1')) then
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File ' + Quote(ExpandConstant('{app}\Setup-Server.ps1')) + ' -Action Stop',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := 'Could not stop the existing server. Check its service before retrying.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if CurStep = ssPostInstall then
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      GetSetupArguments(''), '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      SetupFailureCode := 1;
      WizardForm.FinishedHeadingLabel.Caption := 'Server setup failed';
      WizardForm.FinishedLabel.Caption := 'Application files were installed, but server configuration failed. Check %TEMP%\SecurityCameraViewer-setup.log, correct the error and rerun setup.';
      Log(WizardForm.FinishedLabel.Caption);
      if not WizardSilent then MsgBox(WizardForm.FinishedLabel.Caption, mbError, MB_OK);
    end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := SetupFailureCode;
end;
