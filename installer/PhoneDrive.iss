; Build: ISCC /DPayloadDir=<publish-directory> /DOutputDir=<release-directory> PhoneDrive.iss
#ifndef PayloadDir
  #define PayloadDir "..\dist\Windows"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef BuildVersion
  #define BuildVersion "0.2.5-performance-preview"
#endif
[Setup]
AppId={{5A4C6D26-9165-4B0D-BF1C-DB51F1F59B01}
AppName=PhoneDrive
AppVersion={#BuildVersion}
AppPublisher=PhoneDrive contributors
DefaultDirName={localappdata}\Programs\PhoneDrive
DefaultGroupName=PhoneDrive
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=PhoneDrive-{#BuildVersion}-Setup-x64
SetupIconFile=..\assets\brand\phonedrive.ico
UninstallDisplayIcon={app}\PhoneDrive.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=PhoneDrive

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PhoneDrive"; Filename: "{app}\PhoneDrive.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,PhoneDrive}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PhoneDrive"; Filename: "{app}\PhoneDrive.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PhoneDrive.exe"; Description: "{cm:LaunchProgram,PhoneDrive}"; Flags: nowait postinstall skipifsilent

; Pairing/settings live outside {app}; uninstall deliberately preserves those files.
