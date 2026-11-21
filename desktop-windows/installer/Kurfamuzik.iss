#ifndef MyAppVersion
  #define MyAppVersion "1.2.9"
#endif
#define MyAppGuid "{8E3A4F21-7C9B-4D82-A1F6-3E92D5B74C10}"
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
UsePreviousAppDir=yes
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=no
DisableFinishedPage=no
DisableProgramGroupPage=yes
ShowLanguageDialog=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Kurfamuzik-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\Kurfanova-Muzik-Windows.exe
UninstallDisplayName=Kurfamüzik
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
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

[CustomMessages]
turkish.StartMenuTask=Başlat menüsü kısayolu oluştur
english.StartMenuTask=Create a Start Menu shortcut
turkish.RunOnStartupTask=Windows açılışında başlat
english.RunOnStartupTask=Run automatically when Windows starts
turkish.AdditionalOptions=Başlangıç Seçenekleri:
english.AdditionalOptions=Startup Options:
turkish.DeleteUserDataPrompt=Favoriler, çalma listeleri ve ayarlar (user-data.json) da silinsin mi?%n%n'Hayır' seçerseniz gelecekte yeniden kurduğunuzda verileriniz korunur.
english.DeleteUserDataPrompt=Do you also want to delete your favorites, playlists, and settings (user-data.json)?%n%nIf you select 'No', your data will be preserved for future installations.
turkish.AlreadyInstalledPrompt=Kurfamüzik (v%1) bilgisayarınızda zaten kurulu.%n%nMevcut program dosyaları temizlenip v%2 sürümü üzerine kurulacaktır. Favorileriniz, çalma listeleriniz ve ayarlarınız korunur.%n%nDevam etmek istiyor musunuz?
english.AlreadyInstalledPrompt=Kurfamüzik (v%1) is already installed on your computer.%n%nExisting program files will be replaced cleanly with v%2. Your favorites, playlists, and settings will be preserved.%n%nDo you want to continue?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "{cm:StartMenuTask}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "{cm:RunOnStartupTask}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

[InstallDelete]
; Clean stale program binaries/runtimes before copying fresh files while preserving unins*, Data/, user-data.json and config.json
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\*.deps.json"
Type: files; Name: "{app}\*.runtimeconfig.json"
Type: files; Name: "{app}\Kurfanova*.exe"
Type: files; Name: "{app}\createdump.exe"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Kurfamüzik"; Filename: "{app}\Kurfanova-Muzik-Windows.exe"; IconFilename: "{app}\Kurfanova-Muzik-Windows.exe"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,Kurfamüzik}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\Kurfamüzik"; Filename: "{app}\Kurfanova-Muzik-Windows.exe"; IconFilename: "{app}\Kurfanova-Muzik-Windows.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Kurfamüzik"; ValueData: """{app}\Kurfanova-Muzik-Windows.exe"""; Flags: uninsdeletevalue; Tasks: startupicon
Root: HKCU; Subkey: "Software\KurfaCore\KurfanovaMuzik"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\KurfaCore\KurfanovaMuzik"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Data"
Type: filesandordirs; Name: "{app}\Updates"
Type: filesandordirs; Name: "{app}\Logs"
Type: files; Name: "{app}\kurfanova.ico"
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\*.tmp"
Type: files; Name: "{app}\*.old"
Type: files; Name: "{app}\*.bak"
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\Kurfanova-Muzik-Windows.exe"; Description: "{cm:LaunchProgram,Kurfamüzik}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Kurfanova-Muzik-Windows.exe"; Parameters: "--post-update-pid 0"; Flags: nowait skipifnotsilent; Check: ShouldRelaunchSilent

[Code]
function DetectExistingInstallVersion(var OutVersion: String): Boolean;
var
  UninstallKey: String;
  LegacyKey: String;
  BrandKey: String;
  ExistingExe1: String;
  ExistingExe2: String;
begin
  Result := False;
  OutVersion := '';
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppGuid}_is1';
  LegacyKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik';
  BrandKey := 'Software\KurfaCore\KurfanovaMuzik';

  if RegKeyExists(HKCU, UninstallKey) then
  begin
    Result := True;
    RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', OutVersion);
  end
  else if RegKeyExists(HKCU, LegacyKey) then
  begin
    Result := True;
    RegQueryStringValue(HKCU, LegacyKey, 'DisplayVersion', OutVersion);
  end;

  if (OutVersion = '') and RegKeyExists(HKCU, BrandKey) then
  begin
    if RegQueryStringValue(HKCU, BrandKey, 'Version', OutVersion) then
      Result := True;
  end;

  if not Result then
  begin
    ExistingExe1 := ExpandConstant('{localappdata}\Programs\Kurfamuzik\Kurfanova-Muzik-Windows.exe');
    ExistingExe2 := ExpandConstant('{localappdata}\KurfanovaMuzik\Kurfanova-Muzik-Windows.exe');
    if FileExists(ExistingExe1) or FileExists(ExistingExe2) then
      Result := True;
  end;

  if OutVersion = '' then
    OutVersion := '1.x';
end;

function InitializeSetup(): Boolean;
var
  InstalledVer: String;
  PromptMsg: String;
begin
  Result := True;
  if WizardSilent then
    Exit;

  if DetectExistingInstallVersion(InstalledVer) then
  begin
    PromptMsg := FmtMessage(CustomMessage('AlreadyInstalledPrompt'), [InstalledVer, '{#MyAppVersion}']);
    if MsgBox(PromptMsg, mbConfirmation, MB_YESNO or MB_DEFBUTTON1) <> IDYES then
      Result := False;
  end;
end;

function ShouldRelaunchSilent: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

procedure KillRunningAppProcesses;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /T /IM Kurfanova-Muzik-Windows.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /T /IM KurfanovaMuzik.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(650);
end;

procedure CleanupLegacyShortcuts(IncludeCurrentBrand: Boolean);
begin
  DeleteFile(ExpandConstant('{userdesktop}\Kurfanova Müzik.lnk'));
  DeleteFile(ExpandConstant('{userdesktop}\KurfanovaMuzik.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\Kurfanova Müzik.lnk'));
  DeleteFile(ExpandConstant('{commondesktop}\KurfanovaMuzik.lnk'));
  DeleteFile(ExpandConstant('{userdesktop}\Kurfanova-Muzik-Windows.exe'));
  DeleteFile(ExpandConstant('{userdesktop}\KurfanovaMuzik.exe'));

  if DirExists(ExpandConstant('{userprograms}\Kurfanova Müzik')) then
    DelTree(ExpandConstant('{userprograms}\Kurfanova Müzik'), True, True, True);
  if DirExists(ExpandConstant('{userprograms}\KurfanovaMuzik')) then
    DelTree(ExpandConstant('{userprograms}\KurfanovaMuzik'), True, True, True);

  if IncludeCurrentBrand then
  begin
    DeleteFile(ExpandConstant('{userdesktop}\Kurfamüzik.lnk'));
    DeleteFile(ExpandConstant('{commondesktop}\Kurfamüzik.lnk'));
    if DirExists(ExpandConstant('{userprograms}\Kurfamüzik')) then
      DelTree(ExpandConstant('{userprograms}\Kurfamüzik'), True, True, True);
  end;
end;

procedure CleanupLegacyStartupValues(IncludeCurrentBrand: Boolean);
begin
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'KurfanovaMuzik');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Kurfamuzik');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Kurfanova Müzik');
  if IncludeCurrentBrand then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Kurfamüzik');
end;

procedure MigrateAndCleanLegacyInstall;
var
  LegacyDir: String;
  LegacyProgramsDir: String;
  AppDir: String;
begin
  AppDir := ExpandConstant('{app}');
  LegacyDir := ExpandConstant('{localappdata}\KurfanovaMuzik');
  LegacyProgramsDir := ExpandConstant('{localappdata}\Programs\KurfanovaMuzik');

  // Remove old standalone Control Panel uninstall entry so no duplicate/broken entry remains
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik');

  if DirExists(LegacyDir) and (CompareText(LegacyDir, AppDir) <> 0) then
  begin
    // Preserve user-data.json and config.json into new Inno install directory
    if FileExists(LegacyDir + '\user-data.json') and (not FileExists(AppDir + '\user-data.json')) then
      FileCopy(LegacyDir + '\user-data.json', AppDir + '\user-data.json', True);
    if FileExists(LegacyDir + '\config.json') and (not FileExists(AppDir + '\config.json')) then
      FileCopy(LegacyDir + '\config.json', AppDir + '\config.json', True);

    DeleteFile(LegacyDir + '\Kurfanova-Muzik-Windows.exe');
    DeleteFile(LegacyDir + '\KurfanovaMuzik.exe');
    DeleteFile(LegacyDir + '\kurfanova.ico');

    if DirExists(LegacyDir + '\WebView2') then
      DelTree(LegacyDir + '\WebView2', True, True, True);
    if DirExists(LegacyDir + '\Data') then
      DelTree(LegacyDir + '\Data', True, True, True);
    if DirExists(LegacyDir + '\Updates') then
      DelTree(LegacyDir + '\Updates', True, True, True);
    if DirExists(LegacyDir + '\updates') then
      DelTree(LegacyDir + '\updates', True, True, True);
    if DirExists(LegacyDir + '\Logs') then
      DelTree(LegacyDir + '\Logs', True, True, True);

    RemoveDir(LegacyDir);
  end;

  if DirExists(LegacyProgramsDir) and (CompareText(LegacyProgramsDir, AppDir) <> 0) then
  begin
    DeleteFile(LegacyProgramsDir + '\Kurfanova-Muzik-Windows.exe');
    DeleteFile(LegacyProgramsDir + '\KurfanovaMuzik.exe');
    RemoveDir(LegacyProgramsDir);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningAppProcesses;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    CleanupLegacyShortcuts(False);
    CleanupLegacyStartupValues(False);
    MigrateAndCleanLegacyInstall;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
  LegacyDir: String;
  PurgeData: Boolean;
begin
  AppDir := ExpandConstant('{app}');
  LegacyDir := ExpandConstant('{localappdata}\KurfanovaMuzik');

  if CurUninstallStep = usUninstall then
  begin
    KillRunningAppProcesses;
    CleanupLegacyShortcuts(True);
    CleanupLegacyStartupValues(True);
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\KurfaCore\KurfanovaMuzik');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(AppDir + '\Data') then
      DelTree(AppDir + '\Data', True, True, True);
    if DirExists(AppDir + '\Updates') then
      DelTree(AppDir + '\Updates', True, True, True);
    if DirExists(AppDir + '\Logs') then
      DelTree(AppDir + '\Logs', True, True, True);

    if DirExists(LegacyDir) then
    begin
      DeleteFile(LegacyDir + '\Kurfanova-Muzik-Windows.exe');
      DeleteFile(LegacyDir + '\KurfanovaMuzik.exe');
      DeleteFile(LegacyDir + '\kurfanova.ico');
      if DirExists(LegacyDir + '\WebView2') then
        DelTree(LegacyDir + '\WebView2', True, True, True);
      if DirExists(LegacyDir + '\Data') then
        DelTree(LegacyDir + '\Data', True, True, True);
      if DirExists(LegacyDir + '\Updates') then
        DelTree(LegacyDir + '\Updates', True, True, True);
      if DirExists(LegacyDir + '\updates') then
        DelTree(LegacyDir + '\updates', True, True, True);
      if DirExists(LegacyDir + '\Logs') then
        DelTree(LegacyDir + '\Logs', True, True, True);
    end;

    PurgeData := False;
    if not UninstallSilent then
    begin
      PurgeData := MsgBox(CustomMessage('DeleteUserDataPrompt'), mbConfirmation, MB_YESNO) = IDYES;
    end;

    if PurgeData then
    begin
      if DirExists(AppDir) then
        DelTree(AppDir, True, True, True);
      if DirExists(LegacyDir) then
        DelTree(LegacyDir, True, True, True);
    end
    else
    begin
      if DirExists(AppDir) then
        RemoveDir(AppDir);
      if DirExists(LegacyDir) then
        RemoveDir(LegacyDir);
    end;
  end;
end;
