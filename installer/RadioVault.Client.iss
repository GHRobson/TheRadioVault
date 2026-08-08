#define MyAppVersion GetEnv("RV_VERSION")
#define ClientPublishDir GetEnv("RV_CLIENT_PUBLISH")
#define InstallerOutputDir GetEnv("RV_INSTALLER_OUTPUT")

[Setup]
AppId={{2B392685-73CA-4D3A-BC2A-63B89EB66D23}
AppName=Radio Vault Client
AppVersion={#MyAppVersion}
AppVerName=Radio Vault Client {#MyAppVersion}
AppPublisher=Radio Vault
DefaultDirName={localappdata}\Programs\Radio Vault Client
DefaultGroupName=Radio Vault
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=RadioVault.Client-{#MyAppVersion}-Setup
SetupIconFile=..\TheRadioVault.Desktop.Avalonia\Assets\RadioVault.ico
UninstallDisplayIcon={app}\TheRadioVault.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=0.35.0.9
VersionInfoCompany=Radio Vault
VersionInfoDescription=Radio Vault Client installer
VersionInfoProductName=Radio Vault Client
VersionInfoProductVersion=0.35.0.9
ChangesEnvironment=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce

[Files]
Source: "{#ClientPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Radio Vault Client"; Filename: "{app}\TheRadioVault.exe"
Name: "{group}\Uninstall Radio Vault Client"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Radio Vault Client"; Filename: "{app}\TheRadioVault.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TheRadioVault.exe"; Description: "Open Radio Vault Client"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only installed binaries live under {app}. Pairing, preferences and encrypted
; caches live under the user's application-data folders and are preserved.
Type: filesandordirs; Name: "{app}"

[Code]
#include "PreventDowngrade.iss"

function InitializeSetup(): Boolean;
begin
  Result := PreventRadioVaultDowngrade('{2B392685-73CA-4D3A-BC2A-63B89EB66D23}_is1', '{#MyAppVersion}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
    WizardForm.SelectDirLabel.Caption :=
      'Radio Vault Client will be installed for this Windows account. Saved server pairing, display preferences and the encrypted offline cache are stored separately and are preserved during updates.';
end;
