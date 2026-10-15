; AgentZero Lite — Inno Setup script for the WPF REFERENCE build (Project/AgentZeroWpf).
; The official build is the Avalonia host (installer/AgentZeroLite.iss); WPF stays the
; Windows-first lab where features are tried before they are converted. This script exists
; so a reference build can still be handed out: .github/workflows/release-wpf.yml runs it
; on demand and attaches the result to an existing release.
;
; Its own AppId and folder, so it installs beside the official build instead of over it.
; The two GUIs share the database and settings but not a running session: they hold the
; same single-instance mutex, so only one runs at a time.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{5B7C2A91-3E64-4F0B-9D27-AZLITEWPF001}
AppName=AgentZero Lite (WPF reference)
AppVersion={#AppVersion}
AppPublisher=psmon
AppPublisherURL=https://github.com/psmon/AgentZeroLite
AppSupportURL=https://github.com/psmon/AgentZeroLite/issues
AppUpdatesURL=https://github.com/psmon/AgentZeroLite/releases
DefaultDirName={autopf}\AgentZeroLite-WPF
DefaultGroupName=AgentZero Lite (WPF reference)
OutputDir=..\output
OutputBaseFilename=AgentZeroLite-WPF-v{#AppVersion}-Setup
SetupIconFile=..\Project\AgentZeroWpf\agentzero.ico
UninstallDisplayIcon={app}\AgentZeroLite.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
AppMutex=Local\AgentZeroLite.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean";  MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\publish-wpf\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AgentZero Lite (WPF reference)"; Filename: "{app}\AgentZeroLite.exe"
Name: "{group}\Uninstall AgentZero Lite (WPF reference)"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\AgentZeroLite.exe"; Description: "Launch AgentZero Lite (WPF reference)"; Flags: nowait postinstall skipifsilent
