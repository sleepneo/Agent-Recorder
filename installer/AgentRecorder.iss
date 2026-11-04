; Compiled only by scripts/build-installer.ps1 from an already validated portable ZIP.
#ifndef PayloadDir
  #error PayloadDir must be supplied by build-installer.ps1
#endif
#define ProductKey "Software\AgentRecorder\Installation"
#define StableAppId "{49E81E5C-57DF-4F8E-93CA-23DE6F10F755}"

[Setup]
AppId={{49E81E5C-57DF-4F8E-93CA-23DE6F10F755}
AppName=Agent Recorder
AppVersion=0.1.19
AppPublisher=Agent Recorder
DefaultDirName={localappdata}\Programs\AgentRecorder
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=Agent Recorder
DisableProgramGroupPage=yes
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
RestartIfNeededByRun=no
UninstallRestartComputer=no
SignedUninstaller=no
Uninstallable=yes
UninstallDisplayIcon={app}\AgentRecorder.App\AgentRecorder.App.exe
OutputDir={#InstallerOutput}
OutputBaseFilename=AgentRecorder-v0.1.19-win-x64-setup-unsigned
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupMutex=Local\AgentRecorder.Setup.v1

[Files]
#include PayloadFiles

[Icons]
Name: "{userprograms}\Agent Recorder\Agent Recorder"; Filename: "{app}\AgentRecorder.App\AgentRecorder.App.exe"; WorkingDir: "{app}"
Name: "{userprograms}\Agent Recorder\Agent instructions"; Filename: "{app}\AGENT-INSTRUCTIONS.zh-CN.md"; WorkingDir: "{app}"

[Registry]
; The uninstall log runs in reverse: remove the five owned values before testing for emptiness.
Root: HKCU; Subkey: "{#ProductKey}"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "Product"; ValueData: "AgentRecorder.PerUser.v1"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "CliPath"; ValueData: "{app}\AgentRecorder.Cli\AgentRecorder.Cli.exe"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "AgentInstructions"; ValueData: "{app}\AGENT-INSTRUCTIONS.zh-CN.md"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "DataDirectory"; ValueData: "{localappdata}\AgentRecorder"; Flags: uninsdeletevalue

[Code]
var
  ServiceLease, MaintenanceLease: THandle;

function NativeCreateMutex(Attributes: Integer; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function NativeGetLastError: Cardinal;
  external 'GetLastError@kernel32.dll stdcall';
function NativeCloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NativeAttributes(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function OrdinaryPath(Path: String): Boolean;
var Attributes, Error: Cardinal; Parent: String;
begin
  Result := False;
  while Path <> '' do begin
    Attributes := NativeAttributes(Path);
    Error := NativeGetLastError;
    if (Attributes = $FFFFFFFF) and (Error <> 2) and (Error <> 3) then Exit;
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then Break;
    Path := Parent;
  end;
  Result := True;
end;

#include GuardHashes

function FixedRoot: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\AgentRecorder');
end;

procedure ReleaseLeases;
begin
  if ServiceLease <> 0 then NativeCloseHandle(ServiceLease);
  if MaintenanceLease <> 0 then NativeCloseHandle(MaintenanceLease);
  ServiceLease := 0;
  MaintenanceLease := 0;
end;

function AllowedArguments: Boolean;
var I, Separator: Integer; P: String;
begin
  Result := False;
  for I := 1 to ParamCount do begin
    P := Uppercase(ParamStr(I));
    if Copy(P, 1, 1) = '-' then P := '/' + Copy(P, 2, Length(P));
    Separator := Pos('=', P);
    if Separator > 0 then P := Copy(P, 1, Separator - 1);
    // Reject force/close/restart overrides before Inno can perform mutations.
    if (P = '/CLOSEAPPLICATIONS') or (P = '/FORCECLOSEAPPLICATIONS') or
       (P = '/RESTARTAPPLICATIONS') or (P = '/ALLUSERS') or
       (P = '/DIR') or (P = '/LOADINF') then Exit;
  end;
  Result := True;
end;

function OwnedValue(Name, Expected: String): Boolean;
var V: String;
begin
  Result := RegQueryStringValue(HKCU, '{#ProductKey}', Name, V) and (CompareText(V, Expected) = 0);
end;

function OwnedRegistration: Boolean;
begin
  Result := OwnedValue('Product', 'AgentRecorder.PerUser.v1') and
    OwnedValue('InstallLocation', FixedRoot) and
    OwnedValue('CliPath', FixedRoot + '\AgentRecorder.Cli\AgentRecorder.Cli.exe') and
    OwnedValue('AgentInstructions', FixedRoot + '\AGENT-INSTRUCTIONS.zh-CN.md') and
    OwnedValue('DataDirectory', ExpandConstant('{localappdata}\AgentRecorder'));
end;

function OwnedShortcut(Name, Target: String; Required: Boolean): Boolean;
var Shell, Link: Variant; Path: String;
begin
  Path := ExpandConstant('{userprograms}\Agent Recorder\') + Name + '.lnk';
  Result := not Required;
  if not FileExists(Path) then Exit;
  Shell := CreateOleObject('WScript.Shell');
  Link := Shell.CreateShortcut(Path);
  Result := (CompareText(Link.TargetPath, Target) = 0) and (Link.Arguments = '');
end;

function CheckOwnership(FirstInstall: Boolean): Boolean;
var Location: String;
begin
  Result := False;
  if CompareText(ExpandConstant('{app}'), FixedRoot) <> 0 then Exit;
  if not OrdinaryPath(FixedRoot) or not OrdinaryPath(ExpandConstant('{userprograms}\Agent Recorder')) then Exit;
  // First install delegates the absent/empty registration proof to the bundled CLI under both leases.
  if not FirstInstall and not OwnedRegistration then Exit;
  if not OwnedShortcut('Agent Recorder', FixedRoot + '\AgentRecorder.App\AgentRecorder.App.exe', False) then Exit;
  if not OwnedShortcut('Agent instructions', FixedRoot + '\AGENT-INSTRUCTIONS.zh-CN.md', False) then Exit;
  if not FirstInstall then begin
    if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#StableAppId}_is1', 'InstallLocation', Location) then Exit;
    if CompareText(RemoveBackslashUnlessRoot(Location), FixedRoot) <> 0 then Exit;
  end;
  Result := True;
end;

function SafeInstallationReason(const Output: TExecOutput): String;
var I: Integer; Reason: String;
begin
  Result := 'INSTALL_STATE_UNKNOWN';
  if Output.Error or (GetArrayLength(Output.StdErr) <> 1) then Exit;
  Reason := Output.StdErr[0];
  if (Pos('INSTALL_', Reason) <> 1) or (Length(Reason) > 80) then Exit;
  for I := 1 to Length(Reason) do
    if not (((Reason[I] >= 'A') and (Reason[I] <= 'Z')) or (Reason[I] = '_')) then Exit;
  Result := Reason;
end;

function AcquireAndCheck(CliDirectory, Operation: String): Boolean;
var Code: Integer; Output: TExecOutput; Name: String; Err: Cardinal;
begin
  Result := False;
  if not OrdinaryPath(CliDirectory) then Exit;
  if not VerifyGuard(CliDirectory) then Exit;
  if not ExecAndCaptureOutput(CliDirectory + '\AgentRecorder.Cli.exe', 'installation-mutex-name', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then Exit;
  if (Code <> 0) or Output.Error or (GetArrayLength(Output.StdOut) <> 1) then Exit;
  Name := Output.StdOut[0];
  if Pos('Global\AgentRecorder.PerUser.Maintenance.v1.S-1-', Name) <> 1 then Exit;
  if MaintenanceLease = 0 then begin
    MaintenanceLease := NativeCreateMutex(0, False, Name);
    Err := NativeGetLastError;
    if (MaintenanceLease = 0) or (Err = 183) then begin Log('INSTALL_LEASE_MISSING'); ReleaseLeases; Exit; end;
    ServiceLease := NativeCreateMutex(0, False, 'Local\AgentRecorder.Service.Instance');
    Err := NativeGetLastError;
    if (ServiceLease = 0) or (Err = 183) then begin Log('INSTALL_LEASE_MISSING'); ReleaseLeases; Exit; end;
  end;
  Result := ExecAndCaptureOutput(CliDirectory + '\AgentRecorder.Cli.exe',
    'installation-check --operation ' + Operation + ' --program-root "' + FixedRoot + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
  Result := Result and (Code = 0) and not Output.Error;
  if not Result then begin
    Log('Installation safety check refused: ' + SafeInstallationReason(Output));
    ReleaseLeases;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := AllowedArguments;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Operation: String;
begin
  Result := 'Installation blocked: active or unknown instance/path/integration. Agent: use the original CLI shutdown --json with the matching data directory; never force close. Review interactive-launch/autostart ownership if still blocked.';
  try
    Operation := 'install';
    if FileExists(FixedRoot + '\agent-recorder.install.json') then Operation := 'upgrade';
    if not AllowedArguments or not CheckOwnership(Operation = 'install') then Exit;
    ExtractTemporaryFiles('{tmp}\guard\*');
    if AcquireAndCheck(ExpandConstant('{tmp}\guard'), Operation) then Result := '';
  except
    Log('INSTALL_STATE_UNKNOWN');
    ReleaseLeases;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    if AllowedArguments and CheckOwnership(False) then
      Result := AcquireAndCheck(FixedRoot + '\AgentRecorder.Cli', 'uninstall');
  except
    Log('INSTALL_STATE_UNKNOWN');
    ReleaseLeases;
  end;
  if not Result then
    SuppressibleMsgBox('Uninstall blocked: active or unknown instance/path/integration. Ask the agent to use the matching original CLI safe shutdown and review integrations. No files were removed.', mbError, MB_OK, IDOK);
end;

procedure DeinitializeSetup;
begin
  ReleaseLeases;
end;

procedure DeinitializeUninstall;
begin
  ReleaseLeases;
end;
