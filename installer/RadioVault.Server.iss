#define MyAppVersion GetEnv("RV_VERSION")
#define ServerPublishDir GetEnv("RV_SERVER_PUBLISH")
#define InstallerOutputDir GetEnv("RV_INSTALLER_OUTPUT")

[Setup]
AppId={{1A86F7D8-89E7-47C3-B6D6-2A4D1AB8F342}
AppName=Radio Vault Server
AppVersion={#MyAppVersion}
AppVerName=Radio Vault Server {#MyAppVersion}
AppPublisher=Radio Vault
DefaultDirName={localappdata}\Programs\Radio Vault Server
DefaultGroupName=Radio Vault
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=RadioVault.Server-{#MyAppVersion}-Setup
SetupIconFile=..\TheRadioVault.Server\Assets\RadioVault.Server.ico
UninstallDisplayIcon={app}\RadioVault.Server.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AppMutex=Local\RadioVault.Server.Instance.v1
VersionInfoVersion=0.35.0.9
VersionInfoCompany=Radio Vault
VersionInfoDescription=Radio Vault Server installer
VersionInfoProductName=Radio Vault Server
VersionInfoProductVersion=0.35.0.9
ChangesEnvironment=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Radio Vault Server automatically when I sign in"; GroupDescription: "Background server:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#ServerPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Radio Vault Server Settings"; Filename: "{app}\RadioVault.Server.exe"
Name: "{group}\Uninstall Radio Vault Server"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Radio Vault Server"; Filename: "{app}\RadioVault.Server.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RadioVaultServer"; ValueData: """{app}\RadioVault.Server.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\RadioVault.Server.exe"; Description: "Open Radio Vault Server settings"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The authoritative database, settings, certificates, transcripts and models
; live outside {app}; upgrades and uninstall never target those data folders.
Type: filesandordirs; Name: "{app}"

[Code]
#include "PreventDowngrade.iss"

function InitializeSetup(): Boolean;
begin
  Result := PreventRadioVaultDowngrade('{1A86F7D8-89E7-47C3-B6D6-2A4D1AB8F342}_is1', '{#MyAppVersion}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
    WizardForm.SelectDirLabel.Caption :=
      'Radio Vault Server will be installed for this Windows account. Your Radio Vault database, archive settings, transcripts and models are stored separately and are not replaced by installation or updates.';
end;
