#define AppVersion "1.2.0"

[Setup]
AppId={{6D3B1C52-8E4A-4B7F-9A21-3F0C5E7D2A11}
AppName=EasyIP
AppVersion={#AppVersion}
AppVerName=EasyIP {#AppVersion}
AppPublisher=EasyIP
DefaultDirName={autopf}\EasyIP
DefaultGroupName=EasyIP
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=EasyIP-Setup-{#AppVersion}
SetupIconFile=..\assets\EasyIP.ico
UninstallDisplayIcon={app}\EasyIP.exe
UninstallDisplayName=EasyIP
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=6.2
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "..\bin\EasyIP.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\EasyIP"; Filename: "{app}\EasyIP.exe"
Name: "{autodesktop}\EasyIP"; Filename: "{app}\EasyIP.exe"; Tasks: desktopicon

[Run]
; shellexec is required: the app asks for administrator rights, which a plain launch can't satisfy.
Filename: "{app}\EasyIP.exe"; Description: "Open EasyIP"; Flags: nowait postinstall skipifsilent shellexec

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  { .NET Framework 4.5 or newer. Built into Windows 8 and later, so this only stops very old systems. }
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
     or (Release < 378389) then
  begin
    MsgBox('EasyIP needs Windows 8 or newer.', mbError, MB_OK);
    Result := False;
  end;
end;
