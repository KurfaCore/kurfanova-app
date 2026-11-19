#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist-win"
#endif

[Setup]
AppId={{8E3A4F21-7C9B-4D82-A1F6-3E92D5B74C10}
AppName=Kurfamüzik
AppVersion={#MyAppVersion}
AppVerName=Kurfamüzik {#MyAppVersion}
AppPublisher=KurfaCore
AppPublisherURL=https://kurfanova.kurfa.workers.dev
AppSupportURL=https://github.com/KurfaCore/kurfanova-app/issues
AppUpdatesURL=https://github.com/KurfaCore/kurfanova-app/releases/latest
DefaultDirName={localappdata}\Programs\Kurfamuzik
DefaultGroupName=Kurfamüzik
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Kurfamuzik-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\Kurfanova-Muzik-Windows.exe
UninstallDisplayName=Kurfamüzik
CloseApplications=yes
RestartApplications=yes
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=KurfaCore
VersionInfoDescription=Kurfamüzik Windows Masaüstü Kurulum Paketi
VersionInfoProductName=Kurfamüzik
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Kurfamüzik"; Filename: "{app}\Kurfanova-Muzik-Windows.exe"; IconFilename: "{app}\Kurfanova-Muzik-Windows.exe"
Name: "{group}\{cm:UninstallProgram,Kurfamüzik}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Kurfamüzik"; Filename: "{app}\Kurfanova-Muzik-Windows.exe"; IconFilename: "{app}\Kurfanova-Muzik-Windows.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kurfanova-Muzik-Windows.exe"; Description: "{cm:LaunchProgram,Kurfamüzik}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Kurfanova-Muzik-Windows.exe"; Parameters: "--post-update-pid 0"; Flags: nowait skipifnotsilent; Check: ShouldRelaunchSilent

[Code]
function ShouldRelaunchSilent: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;
