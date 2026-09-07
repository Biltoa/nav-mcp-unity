; NAV MCP — Windows installer (Inno Setup 6)
;
; Per-user by design: PrivilegesRequired=lowest means no administrator prompt, which matters for
; the audience this app exists for. It installs to %LOCALAPPDATA%\Programs\NAV MCP, appears in
; Add/Remove Programs, and upgrades in place.
;
; Build it after scripts/publish.ps1 has produced dist/:
;   iscc scripts\installer.iss
;   iscc /DAppVersion=1.1.0 scripts\installer.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName        "NAV MCP"
#define AppExeName     "NAV MCP.exe"
#define AppPublisher   "NAV MCP"
#define AppId          "{{8E5F2C41-9A3D-4B77-BE21-5C0D6A9F7E12}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; No admin prompt, and no chance of installing somewhere the user cannot write.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist-installer
OutputBaseFilename=NAV-MCP-Setup-{#AppVersion}
SetupIconFile=..\src\Umcp.Gui\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app carries its own .NET, so there is nothing to check for and nothing to install first.
AppReadmeFile={app}\INSTALL.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup";     Description: "Start {#AppName} when I &sign in"; GroupDescription: "Startup:"

[Files]
; Everything publish.ps1 produced, including com.umcp.agent — the Unity package has to ship with
; the app, because linking a project points Unity's manifest at this folder.
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; The same value the app's own "Start when I sign in" checkbox writes, so the two agree and either
; one can turn it off. Quoted, because the default path contains a space.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; Tasks: startup; \
    Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the server before removing the files it is running from. The daemon outlives its window by
; design, so closing the app is not enough, and an uninstaller that leaves a running umcpd holding
; the folder open fails halfway through with a file-in-use error.
Filename: "{cmd}"; Parameters: "/c taskkill /IM ""{#AppExeName}"" /F & taskkill /IM umcpd.exe /F"; \
    Flags: runhidden; RunOnceId: "StopNavMcp"

[Code]
// State the app keeps outside its install folder: the token, the logs, the audit trail, the
// linked-project list. Removing it is offered, never assumed — somebody reinstalling a version
// should not lose which projects they had linked.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StateDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    StateDir := ExpandConstant('{localappdata}\UnityMCP');
    if DirExists(StateDir) then
      if MsgBox('Also remove NAV MCP''s settings, logs and linked-project list?' + #13#10 +
                'Your Unity projects are never touched.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(StateDir, True, True, True);
  end;
end;
