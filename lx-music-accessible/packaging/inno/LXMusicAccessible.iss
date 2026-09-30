; Inno Setup 6 package definition for LX Music Accessible.
; Build from repository root with build_release.ps1; the script supplies AppSource,
; AppVersion and OutputDir so release paths and versions cannot drift.

#define MyAppName "落雪音乐无障碍版"
#define MyAppPublisher "LX Music Accessible Team"
#define MyAppExeName "落雪音乐无障碍版V3.0.exe"

#ifndef AppSource
#define AppSource "..\artifacts\release-staging\app-framework-dependent"
#endif

#ifndef AppVersion
#define AppVersion "3.0.0"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{7F3D9B50-2D64-4E28-A257-7BF9D9D23A93}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DefaultDirName={autopf}\LXMusicAccessible
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=LXMusicAccessible-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\LXMusicAccessible.Setup.exe"
Type: files; Name: "{app}\落雪音乐无障碍版V2.0.exe"
Type: files; Name: "{app}\.lxmusic-install-owner.json"
Type: files; Name: "{app}\.lxmusic-install-manifest.json"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
MissingNet48=落雪音乐无障碍版需要 .NET Framework 4.8 或更高版本。请先安装 .NET Framework 4.8 Runtime，然后重新运行安装程序。

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM,
    'Software\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox(CustomMessage('MissingNet48'), mbCriticalError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Remove the legacy custom-installer uninstall entry so Programs and Features
    // does not show two uninstallers after upgrading an older installation.
    RegDeleteKeyIncludingSubkeys(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\LxMusicAccessible');
  end;
end;
