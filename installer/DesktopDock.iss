#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef AppPayload
  #error AppPayload is required
#endif
#ifndef SetupHelper
  #error SetupHelper is required
#endif

#ifdef Validation
  #define ProductName "Desktop Dock P6 Validation"
  #define ProductKey "DesktopDock.Windows.P6.Validation"
#else
  #define ProductName "Desktop Dock"
  #define ProductKey "DesktopDock.Windows"
#endif

[Setup]
AppId={#ProductKey}
AppName={#ProductName}
AppVersion={#ReleaseVersion}
AppPublisher=Desktop Dock
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\versions\{#ReleaseVersion}\DesktopDock.exe
VersionInfoVersion={#ReleaseVersion}
VersionInfoDescription={#ProductName} Setup
SetupLogging=yes
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopTask=Create a desktop shortcut
chinesesimp.DesktopTask=创建桌面快捷方式
english.StartupTask=Run Desktop Dock when signing in to Windows
chinesesimp.StartupTask=登录 Windows 时运行 Desktop Dock
english.LaunchApp=Open Desktop Dock
chinesesimp.LaunchApp=打开 Desktop Dock
english.CloseDock=Exit Desktop Dock from its notification-area menu before continuing.
chinesesimp.CloseDock=请先从通知区域退出 Desktop Dock，再继续。
english.BadDestination=Select a new or empty program folder. Existing installations must be updated in their own folder. Program and profile folders must be separate.
chinesesimp.BadDestination=请选择新的或空的程序目录。已有安装需在原安装目录升级。程序目录和配置目录必须分开。
english.BadVersion=Cannot replace version %1 with %2. Keep the installed version and use its recovery instructions.
chinesesimp.BadVersion=无法用 %2 替换已安装的 %1。请保留当前安装并查看恢复说明。
english.PreflightFailed=Setup stopped before replacing any program files. %1
chinesesimp.PreflightFailed=安装在更改程序文件前停止。%1
english.KeepData=Remove Desktop Dock configuration as well? Choose No to keep it. Project targets, original desktop files and release backups are preserved.
chinesesimp.KeepData=同时删除 Desktop Dock 配置吗？选择「否」会保留配置。项目目标、原始桌面文件和发布备份会保留。
english.CleanupFailed=The requested cleanup stopped. %1 Review the profile or desktop recovery instructions before retrying.
chinesesimp.CleanupFailed=清理已停止。%1 请按说明核对配置或恢复桌面图标后重试。
english.StartupFailed=Desktop Dock was installed. Windows sign-in registration could not be updated. Review the setting in Desktop Dock.
chinesesimp.StartupFailed=Desktop Dock 已安装。登录启动项更新失败，请在 Desktop Dock 设置中检查状态。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartupTask}"; Flags: unchecked

[Files]
Source: "{#SetupHelper}"; DestDir: "{app}\versions\{#ReleaseVersion}"; Flags: ignoreversion; AfterInstall: CheckCopyCheckpoint
Source: "{#AppPayload}\*"; DestDir: "{app}\versions\{#ReleaseVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\versions\{#ReleaseVersion}\DesktopDock.exe"; Parameters: "{code:LaunchArguments}"; WorkingDir: "{app}\versions\{#ReleaseVersion}"
Name: "{group}\Desktop Dock Settings"; Filename: "{app}\versions\{#ReleaseVersion}\DesktopDock.exe"; Parameters: "{code:SettingsArguments}"; WorkingDir: "{app}\versions\{#ReleaseVersion}"

[Run]
Filename: "{app}\versions\{#ReleaseVersion}\DesktopDock.exe"; Parameters: "{code:LaunchArguments}"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  AppUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductKey}_is1';
  ShortcutRecordKey = 'Software\{#ProductKey}\Installer';
  HelperFileName = 'DesktopDock.SetupHelper.exe';
  RunValueName = 'DesktopDock.Windows';
#ifdef Validation
  RunKey = 'Software\DesktopDock.Windows.P6.Validation\StartupProbe';
#else
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
#endif

var
  RemoveProfile: Boolean;
  PreflightPrepared: Boolean;
  PreserveExistingStartup: Boolean;

function ProfileDirectory: String;
begin
#ifdef Validation
  Result := ExpandConstant('{param:PROFILE|}');
  if (Result = '') or (CompareText(ExpandFileName(Result), ExpandConstant('{localappdata}\DesktopDock\config')) = 0) then
    RaiseException('Validation requires an isolated /PROFILE path.');
#else
  Result := ExpandConstant('{localappdata}\DesktopDock\config');
#endif
end;

function BackupDirectory: String;
begin
#ifdef Validation
  Result := ExpandConstant('{param:BACKUPS|}');
  if Result = '' then
    RaiseException('Validation requires an isolated /BACKUPS path.');
#else
  Result := ExpandConstant('{localappdata}\DesktopDock\release-backups');
#endif
end;

function LaunchArguments(Param: String): String;
begin
#ifdef Validation
  Result := '--config-dir ' + AddQuotes(ProfileDirectory);
#else
  Result := '';
#endif
end;

function SettingsArguments(Param: String): String;
begin
  Result := Trim(LaunchArguments('') + ' --settings');
end;

function AppRoot: String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
end;

function VersionExecutable(const Root, Version: String): String;
begin
  Result := AddBackslash(Root) + 'versions\' + Version + '\DesktopDock.exe';
end;

function InstalledExecutable: String;
begin
  Result := VersionExecutable(AppRoot, '{#ReleaseVersion}');
end;

function RegisteredStartup: String;
var
  Saved, Prefix, Suffix, Version: String;
  Packed: Int64;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, RunKey, RunValueName, Saved) then
    exit;
  Prefix := '"' + AddBackslash(AppRoot) + 'versions\';
  Suffix := '\DesktopDock.exe" --startup';
  if (CompareText(Copy(Saved, 1, Length(Prefix)), Prefix) <> 0) or
    (CompareText(Copy(Saved, Length(Saved) - Length(Suffix) + 1, Length(Suffix)), Suffix) <> 0) then
    exit;
  Version := Copy(Saved, Length(Prefix) + 1, Length(Saved) - Length(Prefix) - Length(Suffix));
  if (Pos('\', Version) = 0) and (Pos('/', Version) = 0) and StrToVersion(Version, Packed) and
    (CompareText(Saved, '"' + VersionExecutable(AppRoot, Version) + '" --startup') = 0) then
    Result := VersionExecutable(AppRoot, Version);
end;

function ExecuteHelper(const Executable, Arguments: String; var Detail: String): Boolean;
var
  ExitCode: Integer;
  Report: String;
  ReportBytes: AnsiString;
begin
  Report := ExpandConstant('{tmp}\desktop-dock-release-report.txt');
  DeleteFile(Report);
  Result := Exec(Executable, Arguments + ' ' + AddQuotes(Report), ExtractFileDir(Executable),
    SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
  if LoadStringFromFile(Report, ReportBytes) then
    Detail := UTF8Decode(ReportBytes)
  else
    Detail := 'The configuration helper returned ' + IntToStr(ExitCode) + '.';
  DeleteFile(Report);
  Log(Detail);
end;

function EmptyDirectory(const Directory: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Directory) + '*', Entry) then
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        Result := False;
        exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

function DirectoryOverlap(const First, Second: String): Boolean;
var
  A, B: String;
begin
  A := Uppercase(AddBackslash(ExpandFileName(First)));
  B := Uppercase(AddBackslash(ExpandFileName(Second)));
  Result := (Copy(A, 1, Length(B)) = B) or (Copy(B, 1, Length(A)) = A);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion, OldLocation, Detail: String;
  OldPacked, NewPacked: Int64;
begin
  Result := '';
  if PreflightPrepared then
    exit;
  if DirectoryOverlap(AppRoot, ProfileDirectory) or DirectoryOverlap(AppRoot, BackupDirectory) then
  begin
    Result := CustomMessage('BadDestination');
    exit;
  end;

  OldVersion := '0.0.0';
  if RegQueryStringValue(HKCU, AppUninstallKey, 'InstallLocation', OldLocation) then
  begin
    if CompareText(RemoveBackslashUnlessRoot(OldLocation), AppRoot) <> 0 then
    begin
      Result := CustomMessage('BadDestination');
      exit;
    end;
    if not RegQueryStringValue(HKCU, AppUninstallKey, 'DisplayVersion', OldVersion) or
      not StrToVersion(OldVersion, OldPacked) or not StrToVersion('{#ReleaseVersion}', NewPacked) or
      (ComparePackedVersion(OldPacked, NewPacked) > 0) then
    begin
      Result := FmtMessage(CustomMessage('BadVersion'), [OldVersion, '{#ReleaseVersion}']);
      exit;
    end;
  end
  else if not EmptyDirectory(AppRoot) then
  begin
    Result := CustomMessage('BadDestination');
    exit;
  end;

  ExtractTemporaryFile(HelperFileName);
  if not ExecuteHelper(ExpandConstant('{tmp}\') + HelperFileName,
    '--prepare-upgrade ' + AddQuotes(ProfileDirectory) + ' ' + AddQuotes(BackupDirectory) +
    ' ' + AddQuotes(OldVersion) + ' "{#ReleaseVersion}"', Detail) then
  begin
    Result := FmtMessage(CustomMessage('PreflightFailed'), [Detail]) + #13#10 + CustomMessage('CloseDock');
    exit;
  end;
  PreserveExistingStartup := RegisteredStartup <> '';
  PreflightPrepared := True;
end;

#ifdef Validation
function CheckpointProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
#endif

procedure CheckCopyCheckpoint;
#ifdef Validation
var
  Marker: String;
#endif
begin
#ifdef Validation
  Marker := ExpandConstant('{param:INTERRUPT_MARKER|}');
  if Marker <> '' then
  begin
    Log('P6 validation worker paused after first file write.');
    if SaveStringToFile(Marker, AnsiString(IntToStr(CheckpointProcessId)), False) then
      Sleep(15000);
  end;
#endif
end;

function DesktopShortcut: String;
begin
#ifdef Validation
  Result := ExpandConstant('{param:SHORTCUTDIR|}');
  if (Result = '') then
    RaiseException('Validation requires an isolated /SHORTCUTDIR path.');
  Result := AddBackslash(Result) + '{#ProductName}.lnk';
#else
  Result := ExpandConstant('{autodesktop}\{#ProductName}.lnk');
#endif
end;

function OwnsDesktopShortcut: Boolean;
var
  Digest: String;
begin
  Result := FileExists(DesktopShortcut) and
    RegQueryStringValue(HKCU, ShortcutRecordKey, 'DesktopShortcutSHA256', Digest) and
    (CompareText(Digest, GetSHA256OfFile(DesktopShortcut)) = 0);
end;

procedure InstallDesktopShortcut;
var
  Created: String;
begin
  if not (WizardIsTaskSelected('desktopicon') or OwnsDesktopShortcut) then
    exit;
  if FileExists(DesktopShortcut) and not OwnsDesktopShortcut then
  begin
    Log('Existing desktop shortcut is not owned by Desktop Dock and was preserved.');
    exit;
  end;
  Created := CreateShellLink(DesktopShortcut, '{#ProductName}', InstalledExecutable, LaunchArguments(''),
    ExtractFileDir(InstalledExecutable), '', 0, SW_SHOWNORMAL);
  if CompareText(Created, DesktopShortcut) = 0 then
    RegWriteStringValue(HKCU, ShortcutRecordKey, 'DesktopShortcutSHA256', GetSHA256OfFile(Created));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;
  InstallDesktopShortcut;
  if not (WizardIsTaskSelected('startup') or PreserveExistingStartup) then
    exit;
#ifdef Validation
  RegWriteStringValue(HKCU, RunKey, RunValueName, '"' + InstalledExecutable + '" --startup');
#else
  if not Exec(InstalledExecutable, '--register-startup', ExtractFileDir(InstalledExecutable),
    SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
  begin
    Log('Desktop Dock login startup registration failed.');
    if not WizardSilent then
      SuppressibleMsgBox(CustomMessage('StartupFailed'), mbInformation, MB_OK, IDOK);
  end;
#endif
end;

function InitializeUninstall: Boolean;
var
  Detail, Helper: String;
begin
  Helper := ExtractFileDir(InstalledExecutable) + '\' + HelperFileName;
  Result := ExecuteHelper(Helper, '--ensure-profile-closed ' + AddQuotes(ProfileDirectory), Detail);
  if not Result then
  begin
    SuppressibleMsgBox(CustomMessage('CloseDock') + #13#10 + Detail, mbError, MB_OK, IDOK);
    exit;
  end;

  if UninstallSilent then
    RemoveProfile := ExpandConstant('{param:REMOVEDATA|0}') = '1'
  else
    RemoveProfile := SuppressibleMsgBox(CustomMessage('KeepData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Detail, Helper, StartupTarget: String;
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  if RemoveProfile then
  begin
    Helper := ExtractFileDir(InstalledExecutable) + '\' + HelperFileName;
    if not ExecuteHelper(Helper, '--remove-profile-data ' + AddQuotes(ProfileDirectory), Detail) then
    begin
      Detail := FmtMessage(CustomMessage('CleanupFailed'), [Detail]);
      Log(Detail);
      SuppressibleMsgBox(Detail, mbError, MB_OK, IDOK);
      Abort;
    end;
  end;

  StartupTarget := RegisteredStartup;
  if StartupTarget <> '' then
  begin
#ifdef Validation
    RegDeleteValue(HKCU, RunKey, RunValueName);
    RegDeleteKeyIfEmpty(HKCU, RunKey);
#else
    if not Exec(StartupTarget, '--uninstall-cleanup', ExtractFileDir(StartupTarget),
      SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
      RaiseException(FmtMessage(CustomMessage('CleanupFailed'), ['Windows login startup']));
#endif
  end;
  if OwnsDesktopShortcut then
    DeleteFile(DesktopShortcut);
  RegDeleteValue(HKCU, ShortcutRecordKey, 'DesktopShortcutSHA256');
  RegDeleteKeyIfEmpty(HKCU, ShortcutRecordKey);
end;
