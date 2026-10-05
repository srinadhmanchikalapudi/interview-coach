; Inno Setup script for Interview Coach. Built by tools\publish.ps1 -Installer (or by the release workflow), which passes:
;   /DAppVersion=1.0.0   the version shown in the installer and in Windows' list of apps
;   /DSourceDir=...      the folder produced by dotnet publish (InterviewCoach.App.exe and Prompts\)
;   /DOutputDir=...      where the setup program is written

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\InterviewCoach-1.0.0-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Interview Coach"
#define AppExe "InterviewCoach.App.exe"
#define RepoUrl "https://github.com/srinadhmanchikalapudi/interview-coach"

[Setup]
; The AppId identifies the program to Windows: it must never change, so that a new version upgrades the old one instead of installing beside it.
AppId={{6F2B8E54-3D1A-4C7B-9A0E-5B7C1D2E4F83}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=srinadhmanchikalapudi
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}

; Installs for the current user only (no administrator prompt) into %LOCALAPPDATA%\Programs; the wizard offers "all users" for those who want it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; A new version replaces the old one, closing the app if it is running.
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE
SetupIconFile=..\src\InterviewCoach.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=InterviewCoach-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The prompts sit next to the exe so they can be tweaked; the program has built-in copies if they are missing.
Source: "{#SourceDir}\Prompts\*"; DestDir: "{app}\Prompts"; Flags: ignoreversion recursesubdirs createallsubdirs

; The licenses travel with the program (the Speech SDK's terms require its license text to be included).
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent
; An update started from inside the program passes /RESTARTAPP=1, so the program comes back by itself when the silent install is done.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RestartRequested

[Code]
function RestartRequested: Boolean;
begin
  Result := ExpandConstant('{param:RESTARTAPP|0}') = '1';
end;

// Uninstalling removes the program only. The user's profiles, saved questions, history, settings and logs are theirs, so they are kept
// unless the user says to delete them (never asked for a silent uninstall).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    DataDir := ExpandConstant('{localappdata}\InterviewCoach');
    if DirExists(DataDir) then
      if MsgBox('Also delete your profiles, saved questions, history, settings and logs?' + #13#10#13#10 +
                'They are stored in ' + DataDir + '. Choose No to keep them for a later install.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
