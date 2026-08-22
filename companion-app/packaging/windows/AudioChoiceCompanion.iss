; AudioChoice Companion - Windows installer
; Build with Inno Setup 6:
;   iscc AudioChoiceCompanion.iss

#define MyAppName "AudioChoice Companion"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "AudioChoice"
#define MyAppExeName "AudioChoiceCompanion.exe"

[Setup]
AppId={{5BDBDD7A-849A-4A81-9B85-47F88B40C00C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\AudioChoice Companion
DefaultGroupName=AudioChoice
UninstallDisplayIcon={app}\AudioChoiceCompanion.ico
OutputDir=..\..\distribution
OutputBaseFilename=AudioChoiceCompanionSetup-Windows-x64-1.0.5
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=AudioChoiceCompanion.ico
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\releases\windows-x64-selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "AudioChoiceCompanion.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AudioChoice Companion"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\AudioChoiceCompanion.ico"
Name: "{autodesktop}\AudioChoice Companion"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\AudioChoiceCompanion.ico"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch AudioChoice Companion"; Flags: nowait postinstall skipifsilent
