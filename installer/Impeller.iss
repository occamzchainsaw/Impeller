; Impeller — installer for the engine service and the window.
;
; Built by scripts\pack-installer.ps1, which publishes both halves first and passes their paths in.
; Compiling this by hand needs the same three defines:
;
;   ISCC.exe installer\Impeller.iss /DVersion=0.1.0 /DEngineDir=... /DAppDir=...
;
; The engine is a service running as LocalSystem, so this installer needs administrator. The window
; does not, and is launched afterwards as the user rather than from here.

#ifndef Version
  #error Pass /DVersion=x.y.z
#endif
#ifndef EngineDir
  #error Pass /DEngineDir=<published engine folder>
#endif
#ifndef AppDir
  #error Pass /DAppDir=<published shell folder>
#endif

#define AppName "Impeller"
#define Publisher "Impeller contributors"
#define ServiceName "ImpellerEngine"

[Setup]
; Never change this. It is how an upgrade finds the previous install, and how Windows knows the
; entry in Apps & features belongs to this program.
AppId={{8F3C9A21-6D4E-4E7B-9C2A-1B5F0D7E4A83}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#Publisher}
VersionInfoVersion={#Version}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename={#AppName}-{#Version}-win-x64-Setup
SetupIconFile={#AppDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\App\Impeller.exe
WizardStyle=modern
DisableWelcomePage=no

; The service needs it. Everything else here would happily run as the user.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Both halves carry their own copy of .NET and the Windows App SDK, so the payload is around half a
; gigabyte of mostly-compressible binaries. lzma2/max with solid compression takes it to roughly a
; third of that, and the extra minute it costs to build is paid once by us rather than every time
; by somebody on a slow connection.
Compression=lzma2/max
SolidCompression=yes

; Restart Manager, so a running window is closed politely instead of locking its own files. No
; single-instance mutex is declared on purpose: two shells at once is a supported arrangement, and
; adding a mutex to make this installer's life easier would change how the app behaves.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Everything"
Name: "headless"; Description: "Engine only, for a machine with no one logged in"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "engine"; Description: "Impeller Engine — the Windows service that drives the fans"; \
    Types: full headless custom; Flags: fixed
Name: "app"; Description: "Impeller — the window and its tray icon"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Components: app; Flags: unchecked

; ----------------------------------------------------------------------------------------------
; The most important block in this file.
;
; Copying over the top would leave behind files a previous version shipped and this one does not.
; Deleting the folder wholesale would take the user's configurations with it, because on a writable
; path the engine keeps its state BESIDE its executable rather than in ProgramData.
;
; So: App is cleared entirely, because nothing in it is state. Engine is cleared by extension,
; because everything we ship there is a .dll, .exe or .pdb — while Configurations\, Logs\,
; sensor-identity.json, selected-configuration.json, names.json and plugins.json match none of
; these patterns and survive untouched. appsettings.json is ours and is overwritten anyway.
;
; Anything added to the engine's payload that is not one of those extensions must be added here too.
; ----------------------------------------------------------------------------------------------
[InstallDelete]
Type: filesandordirs; Name: "{app}\App"
Type: filesandordirs; Name: "{app}\Engine\runtimes"
Type: files; Name: "{app}\Engine\*.dll"
Type: files; Name: "{app}\Engine\*.exe"
Type: files; Name: "{app}\Engine\*.pdb"

[Files]
Source: "{#EngineDir}\*"; DestDir: "{app}\Engine"; Components: engine; \
    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppDir}\*"; DestDir: "{app}\App"; Components: app; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\App\Impeller.exe"; Components: app
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\App\Impeller.exe"; Components: app; Tasks: desktopicon

[Run]
; Registering is done by the engine's own verb rather than by an Inno service entry, so the path
; handed to the service control manager is necessarily the path of the thing registering it. There
; is nothing to get wrong on an upgrade or a move.
Filename: "{app}\Engine\Impeller.EngineService.exe"; Parameters: "install"; \
    StatusMsg: "Registering the Impeller engine service..."; Components: engine; \
    Flags: runhidden waituntilterminated
Filename: "{app}\Engine\Impeller.EngineService.exe"; Parameters: "start"; \
    StatusMsg: "Starting the Impeller engine service..."; Components: engine; \
    Flags: runhidden waituntilterminated

; Launched unelevated, because the window runs as the user and an app started from an elevated
; installer inherits that token — which would then write its autostart entry into the wrong hive.
Filename: "{app}\App\Impeller.exe"; Description: "Start Impeller now"; Components: app; \
    Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
Type: dirifempty; Name: "{app}\Engine"
Type: dirifempty; Name: "{app}"

[Code]
const
  ServiceName = '{#ServiceName}';

{ Stops and unregisters the service, whatever registered it.

  Deliberately sc.exe rather than the engine's own uninstall verb. At this point the new binaries
  are not extracted yet and the old ones may be anywhere — including a folder somebody copied by
  hand, which this installer has no record of. Removal only needs the service name; registration is
  the half that needs a correct path, and that still goes through the engine's own verb below. }
procedure RemoveService;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + ServiceName, '', SW_HIDE, ewWaitUntilTerminated, Code);

  { A stop is asynchronous and a delete on a service still shutting down is marked pending until the
    next reboot, which would make the fresh registration fail for a reason nobody could see. }
  Sleep(1500);

  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ' + ServiceName, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Before a single file is replaced. A running service holds its own executable open, and on a
    writable install path it also holds the log it is writing. }
  RemoveService;
  Result := '';
end;

{ Whether the engine will keep its state beside itself or under ProgramData.

  It decides this at startup by trying to write, and the Settings page reports the answer — but
  somebody choosing a folder deserves to know before they commit to one, because it is the
  difference between a portable install they can copy and one whose state lives elsewhere. }
function StateLocationNote: String;
var
  Path: String;
begin
  Path := ExpandConstant('{app}');

  if Pos(LowerCase(ExpandConstant('{commonpf}')), LowerCase(Path)) = 1 then
    Result := 'Your configurations will be kept in ' + ExpandConstant('{commonappdata}') + '\Impeller,'
      + ' because Program Files is not writable.'
  else
    Result := 'Your configurations will be kept in ' + Path + '\Engine, beside the service.'
      + ' The whole folder can be copied or moved.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
    WizardForm.ReadyMemo.Lines.Add(#13#10 + StateLocationNote);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Root: String;
begin
  if CurUninstallStep = usUninstall then
    RemoveService;

  if CurUninstallStep <> usPostUninstall then
    Exit;

  { An unattended uninstall has nobody to ask, and a question nobody can answer is a program that
    hangs. Silence keeps the state, which is the same answer the prompt defaults to. }
  if UninstallSilent then
    Exit;

  Root := ExpandConstant('{app}\Engine');

  { Inno removes what it installed, and the engine's state was written at runtime — so none of it is
    on that list and all of it is still here. Asked rather than assumed, and defaulting to keeping
    it: sensor-identity.json is what makes a curve point at the right fan, and it cannot be
    reconstructed by hand. }
  if not DirExists(Root + '\Configurations') and not FileExists(Root + '\sensor-identity.json') then
    Exit;

  if MsgBox('Delete your Impeller configurations as well?' + #13#10#13#10
      + 'This removes every curve and fan setting in' + #13#10 + Root + #13#10#13#10
      + 'It also removes sensor-identity.json, which records which fan is which on this machine. '
      + 'Keep them if you may reinstall.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Root + '\Configurations', True, True, True);
    DelTree(Root + '\Logs', True, True, True);
    DeleteFile(Root + '\sensor-identity.json');
    DeleteFile(Root + '\selected-configuration.json');
    DeleteFile(Root + '\names.json');
    DeleteFile(Root + '\plugins.json');
  end;
end;
