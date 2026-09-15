#define MyAppName "BetterJoy"
#define MyServiceName "BetterJoy2"
; Pre-v7.3.0 installs registered the service under this name - MigrateLegacyService stops and
; deletes it during install/upgrade so it doesn't linger orphaned once MyServiceName below takes
; over, and UninstallRun best-effort cleans it up too in case that migration never got to run.
#define MyLegacyServiceName "BetterJoy"
#define MyAppVersion "v7.3.0"
#define MyAppPublisher "BetterJoy Contributors"
#define MyAppURL "https://github.com/Geofferey/BetterJoy"
#define MyAppExeName "BetterJoy2.exe"
#define MyBuildDir "..\BetterJoyForCemu\bin\x64\Release"
#define MyViGEmBusInstaller "ViGEmBus_1.22.0_x64_x86_arm64.exe"
#define MyHidHideInstaller "HidHide_1.5.230_x64.exe"
#define MyFakerInputInstaller "FakerInput_Setup_0.1.1_x64.msi"
#define MyUsbipInstaller "USBip-0.9.7.7-x64.exe"
#define MySteamMicInf "SteamStreamingMicrophone.inf"

[Setup]
; Same GUID as the project's ProjectGuid, so upgrades are detected correctly across releases.
AppId={{1BF709E9-C133-41DF-933A-C9FF3F664C7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=BetterJoy2-{#MyAppVersion}-setup
SetupIconFile=..\BetterJoyForCemu\Icons\betterjoyforcemu_icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "vigembus"; Description: "Install the ViGEmBus driver (required for XInput/DS4 output)"; GroupDescription: "Drivers:"; Flags: checkedonce
Name: "hidhide"; Description: "Install the HidHide driver (hides controllers from other programs, e.g. Steam)"; GroupDescription: "Drivers:"; Flags: unchecked
Name: "fakerinput"; Description: "Install FakerInput virtual keyboard/mouse (supports elevated apps, secure screens, and custom shortcuts)"; GroupDescription: "Drivers:"; Flags: unchecked
Name: "dualsensemic"; Description: "Install the Bluetooth microphone backend (VIIPER + signed usbip-win2 driver)"; GroupDescription: "Drivers:"; Flags: checkedonce
Name: "steammic"; Description: "Install the Steam Streaming Microphone driver (fallback Bluetooth microphone backend, used if VIIPER is off/unavailable)"; GroupDescription: "Drivers:"; Flags: checkedonce

[Files]
; Everything from the Release build, except runtime-generated state that shouldn't ship pre-populated
; with whatever the machine that built it happened to have connected, and *.xml - every one of
; these is a NuGet package's Visual Studio IntelliSense documentation file (Concentus.xml,
; NAudio.xml, System.Memory.xml, ...), meaningful only to a developer referencing the DLL from
; their own project. An installed end-user app never reads them - pure dead weight in every build.
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Excludes: "settings,3rdPartyControllers,! Install the drivers in the Drivers folder,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\Drivers\{#MyViGEmBusInstaller}"; Parameters: "/quiet /norestart"; StatusMsg: "Installing ViGEmBus driver..."; Tasks: vigembus; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort: if the service was never installed these just fail quietly, which is fine -
; Inno doesn't treat a non-zero exit code here as an uninstall failure. The MyLegacyServiceName
; pair is defensive only - MigrateLegacyService already removes it during install/upgrade, this
; just covers a machine where that never got to run.
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopBetterJoyService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteBetterJoyService"
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyLegacyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopLegacyBetterJoyService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyLegacyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteLegacyBetterJoyService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// HidHide is installed via Exec here (rather than a declarative [Run] line) so its exit code can
// be inspected: a WiX Burn bootstrapper returns 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) when a
// reboot is needed, and NeedsRestart() below surfaces that as Inno's own native restart prompt
// instead of it being silently lost.
var
  HidHideExitCode: Integer;
  FakerInputExitCode: Integer;
  UsbipExitCode: Integer;
  SteamMicRebootRequired: Boolean;

// Real service-status polling via the SCM API - sc.exe stop only requests the stop and returns
// as soon as the SCM acknowledges the request, not once the service has actually finished
// shutting down (BetterJoyService.OnStop does real cleanup: unhiding HidHide devices,
// disconnecting ViGEm targets, stopping timers - not instant). Waiting merely for the sc.exe
// process to exit isn't the same as waiting for the file locks it's holding to be released.
const
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;

type
  SERVICE_STATUS = record
    dwServiceType: LongWord;
    dwCurrentState: LongWord;
    dwControlsAccepted: LongWord;
    dwWin32ExitCode: LongWord;
    dwServiceSpecificExitCode: LongWord;
    dwCheckPoint: LongWord;
    dwWaitHint: LongWord;
  end;

function OpenSCManagerW(lpMachineName, lpDatabaseName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: LongWord; lpServiceName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: LongWord; var lpServiceStatus: SERVICE_STATUS): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: LongWord): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';

// Returns the service's current SERVICE_* state, or 0 if it isn't installed / can't be queried
// (never installed, access denied, etc.) - 0 isn't a real SERVICE_* value, so callers can treat
// it as "unknown/absent" without confusing it for a real state.
function GetServiceState(ServiceName: string): LongWord;
var
  SCManager, Service: LongWord;
  Status: SERVICE_STATUS;
begin
  Result := 0;
  SCManager := OpenSCManagerW('', '', SC_MANAGER_CONNECT);
  if SCManager = 0 then
    exit;
  try
    Service := OpenServiceW(SCManager, ServiceName, SERVICE_QUERY_STATUS);
    if Service = 0 then
      exit;
    try
      if QueryServiceStatus(Service, Status) then
        Result := Status.dwCurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(SCManager);
  end;
end;

// FakerInput is an MSI rather than a bootstrapper. It stays an explicit installer task because
// virtual input is optional and installing a system driver should always be a conscious choice.
procedure InstallFakerInput;
var
  ResultCode: Integer;
  Params: String;
begin
  if WizardIsTaskSelected('fakerinput') then begin
    Params := '/i "' + ExpandConstant('{app}\Drivers\{#MyFakerInputInstaller}') + '" /qn /norestart';
    if Exec(ExpandConstant('{sys}\msiexec.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      FakerInputExitCode := ResultCode
    else
      FakerInputExitCode := -1;
  end;
end;

procedure InstallHidHide;
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('hidhide') then begin
    if Exec(ExpandConstant('{app}\Drivers\{#MyHidHideInstaller}'), '/exenoui /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      HidHideExitCode := ResultCode
    else
      HidHideExitCode := -1;
  end;
end;

// VIIPER itself is a bundled user-mode sidecar started on demand by BetterJoy. Only usbip-win2
// needs installation: it supplies the signed virtual USB host controller that exposes VIIPER's
// DualSense audio-only device as an ordinary Windows recording endpoint.
procedure InstallDualSenseMicrophoneBackend;
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('dualsensemic') then begin
    if Exec(ExpandConstant('{app}\Drivers\{#MyUsbipInstaller}'),
        '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010', '', SW_HIDE,
        ewWaitUntilTerminated, ResultCode) then
      UsbipExitCode := ResultCode
    else
      UsbipExitCode := -1;
  end;
end;

// Steam Streaming Microphone (SteamMicrophoneEndpoint.cs's fallback) is a root-enumerated
// (non-PnP) MEDIA-class device: unlike an ordinary PnP driver, staging the INF into the driver
// store isn't enough - the devnode itself has to be created before a driver can bind to it.
// pnputil's /add-driver ... /install only updates a device that already exists (confirmed via
// Microsoft's own guidance recommending it specifically to AVOID creating a root-enumerated
// device); devcon's "install" command is the documented way to create one, but devcon.exe isn't
// redistributable outside the WDK. The actual SetupAPI sequence devcon uses internally now lives
// in BetterJoy2.exe itself (SteamMicrophoneInstaller.cs, run via the "-installsteammic"
// flag below) rather than here in Pascal Script: Setup.exe is always a 32-bit (WOW64) process
// regardless of ArchitecturesInstallIn64BitMode (confirmed by inspecting its actual PE header),
// so calling SetupAPI/newdev.dll directly from here would hit the WOW64-redirected 32-bit copies
// of those DLLs - which don't reliably install a native x64 kernel driver, and did in fact fail
// silently in practice when this used to be implemented as raw P/Invoke here. BetterJoy2.exe
// is a native x64 build, so running the real work there avoids the problem entirely.
procedure InstallSteamStreamingMicrophone;
var
  ResultCode: Integer;
  Params: String;
begin
  if not WizardIsTaskSelected('steammic') then
    exit;

  Params := '-installsteammic "' + ExpandConstant('{app}\Drivers\{#MySteamMicInf}') + '"';
  if Exec(ExpandConstant('{app}\{#MyAppExeName}'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    SteamMicRebootRequired := ResultCode = 3010
  else
    SteamMicRebootRequired := False;
end;

// sc.exe's binPath value has to be one single argument containing the (space-containing,
// quoted) exe path followed by " -service" - the outer quotes let the command-line parser
// treat the whole thing as one token for sc.exe, the escaped inner quotes are what sc.exe
// itself then records as the actual service binary path.
// MainForm has no local-ownership fallback anymore (see clever-wiggling-rocket.md) - the GUI is
// non-functional without the service running, so unlike the optional driver tasks above this
// always runs, no checkbox to skip it. `sc create` on an already-existing service (a plain
// upgrade) just fails harmlessly; the calls after it still bring the service to the desired
// state regardless. The failure action registers automatic recovery (see BetterJoyService.OnStop
// - a crash otherwise leaves the GUI permanently unable to reconnect until someone notices and
// restarts it by hand): 3 restart attempts a second apart.
//
// resetperiod is deliberately short rather than the day it used to be. Waking from sleep now
// stops the service with a non-zero exit code on purpose so the SCM restarts it as a fresh
// process (see BetterJoyService.OnPowerEvent), so these actions are ordinary traffic, not just
// crashes - and a day-long window would let a handful of sleeps accumulate and exhaust them. A
// minute with no further failure clears the count, which still catches a genuine crash loop. The
// failureflag call after it is what makes those actions apply to a deliberate stop at all.
procedure InstallService;
var
  ResultCode: Integer;
  Params: String;
begin
  Params := 'create {#MyServiceName} binPath= "\"' + ExpandConstant('{app}\{#MyAppExeName}') + '\" -service" start= auto DisplayName= "{#MyServiceName}"';
  Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'description {#MyServiceName} "Third-party game controller service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure {#MyServiceName} reset= 60 actions= restart/1000/restart/1000/restart/1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Without this the actions above only ever fire on an actual crash. Waking from sleep stops the
  // service deliberately with a non-zero exit code, which the SCM classes as a non-crash failure
  // and, by default, does nothing about - the service just stayed stopped.
  Exec(ExpandConstant('{sys}\sc.exe'), 'failureflag {#MyServiceName} 1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// One-time cleanup for anyone upgrading from a pre-v7.3.0 install, where the service was
// registered under MyLegacyServiceName instead of MyServiceName: sc create never renames an
// existing service, so without this the old entry would just sit there permanently, orphaned
// and still pointing at the same binary/service class as the new one - two SCM registrations
// racing to control the same physical controllers. Stops it first (delete on a running service
// only marks it for deletion once stopped, not immediately), then deletes it; both are no-ops
// (nonzero exit, ignored) on a machine that never had it.
procedure MigrateLegacyService;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if GetServiceState('{#MyLegacyServiceName}') = 0 then
    exit; // never installed under the old name - nothing to migrate

  if GetServiceState('{#MyLegacyServiceName}') = SERVICE_RUNNING then begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyLegacyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Attempts := 0;
    while (GetServiceState('{#MyLegacyServiceName}') <> SERVICE_STOPPED) and (Attempts < 50) do begin
      Sleep(200);
      Attempts := Attempts + 1;
    end;
  end;

  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#MyLegacyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Before files are copied: an existing installed service still running keeps
// BetterJoy2.exe/its DLLs locked, which fails the file-copy step outright rather than a
// clean upgrade. InstallService always restarts the service afterward regardless of how this
// left it, so there's nothing to remember here beyond "was it running" for the exit-early check.
procedure StopExistingService;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if GetServiceState('{#MyServiceName}') <> SERVICE_RUNNING then
    exit;

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Poll for the service to actually reach SERVICE_STOPPED, not just for the sc.exe command to
  // return - up to 10 seconds, then give up and let the file copy fail loudly if it must rather
  // than hang the installer indefinitely on a service that's stuck shutting down.
  Attempts := 0;
  while (GetServiceState('{#MyServiceName}') <> SERVICE_STOPPED) and (Attempts < 50) do begin
    Sleep(200);
    Attempts := Attempts + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    MigrateLegacyService;
    StopExistingService;
  end;
  if CurStep = ssPostInstall then begin
    InstallFakerInput;
    InstallHidHide;
    InstallDualSenseMicrophoneBackend;
    InstallSteamStreamingMicrophone;
    InstallService;
  end;
end;

function NeedsRestart(): Boolean;
begin
  Result := (HidHideExitCode = 3010) or
    (FakerInputExitCode = 3010) or (FakerInputExitCode = 1641) or
    (UsbipExitCode = 3010) or (UsbipExitCode = 1641) or
    SteamMicRebootRequired;
end;
