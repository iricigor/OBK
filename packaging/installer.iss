#ifndef AppVersion
  #error AppVersion must be supplied by the build script.
#endif

[Setup]
AppId=OBK-369698EA-C752-47FB-A855-C323D53150A9{param:VerificationId|}
AppName=OBK
AppVersion={#AppVersion}
AppPublisher=OBK contributors
AppPublisherURL=https://github.com/iricigor/OBK
DefaultDirName={localappdata}\Programs\OBK
DefaultGroupName=OBK
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=OBK-{#AppVersion}-windows-x64-setup
#ifdef FastCompression
Compression=lzma2/fast
#else
Compression=lzma2
#endif
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\OBK.App\Assets\OBK.ico
UninstallDisplayIcon={app}\OBK.exe
SetupLogging=yes
UsePreviousLanguage=no
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\dist\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\app"

[Icons]
Name: "{group}\OBK"; Filename: "{app}\OBK.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall OBK"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\OBK.exe"; Description: "Launch OBK"; Flags: nowait postinstall skipifsilent
; Automatic updates pass /Relaunch=1 so the updated app starts again after a silent install.
Filename: "{app}\OBK.exe"; Flags: nowait; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:Relaunch|0}') = '1';
end;
