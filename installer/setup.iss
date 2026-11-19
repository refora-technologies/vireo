; Vireo — Inno Setup installer script
; © 2026 Refora Technologies
;
; Built by build.ps1, which passes the version from src\Vireo\Vireo.csproj (/DAppVersion=…), so the program, the installer
; and the release tag can never disagree.
;
; Installed for the current user only (no administrator prompt), like other browsers' per-user installs: it is what lets
; Vireo update itself quietly in the background. The same AppId upgrades in place, keeping the person's data, which lives
; apart from the program in %LOCALAPPDATA%\Vireo.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change this: it is how Windows and later installers know this is the same program.
AppId={{3839B579-E5B8-4ED1-AA71-D4EDB0FCB2E2}
AppName=Vireo
AppVersion={#AppVersion}
AppVerName=Vireo {#AppVersion}
AppPublisher=Refora Technologies
AppPublisherURL=https://github.com/refora-technologies/vireo
AppSupportURL=https://github.com/refora-technologies/vireo/issues
AppUpdatesURL=https://github.com/refora-technologies/vireo/releases
AppContact=reforatech@gmail.com
AppCopyright=Copyright (C) 2026 Refora Technologies

; For this user only: %LOCALAPPDATA%\Programs\Vireo. No administrator rights, now or for updates.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Vireo
DisableDirPage=yes
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=no
UninstallDisplayName=Vireo
UninstallDisplayIcon={app}\Vireo.exe

OutputDir=..\out\installer
OutputBaseFilename=VireoSetup
SetupIconFile=..\src\Vireo\Assets\vireo.ico
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes

; Dark, like the browser.
WizardStyle=modern dark
WizardImageFile=images\wizard-202x386.png,images\wizard-269x515.png,images\wizard-336x643.png,images\wizard-430x824.png,images\wizard-534x1022.png
WizardSmallImageFile=images\small-58.png,images\small-77.png,images\small-97.png,images\small-124.png,images\small-159.png

VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=Vireo
VersionInfoCompany=Refora Technologies
VersionInfoDescription=Vireo Setup
VersionInfoCopyright=Copyright (C) 2026 Refora Technologies

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 or later: what the WebView2 engine supports.
MinVersion=10.0.17763

; A running Vireo (any profile) is closed through the Restart Manager, which lets it save its tabs first. Only Vireo itself:
; the browser engine's own processes belong to Windows' WebView2 runtime, not to this install.
; Tell Windows the program's icon may have changed, so Explorer, the Start menu and the taskbar stop showing a cached one.
ChangesAssociations=yes

CloseApplications=force
CloseApplicationsFilter=Vireo.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nVireo is a fast, private browser: ads and trackers blocked, no pop-ups, and the videos and files a page plays saved in one click. It is free and open source.%n%nIt installs for you only and needs no administrator rights.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\out\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Files an older version had that this one does not (assets are replaced whole), and what the early copy-install left.
Type: filesandordirs; Name: "{app}\Assets"
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\*.xml"
Type: files; Name: "{app}\*.pdb"

[Icons]
Name: "{autoprograms}\Vireo"; Filename: "{app}\Vireo.exe"; IconFilename: "{app}\Vireo.exe"; Comment: "Browse the web with Vireo"
; The download manager on its own: only the downloader, in the notification area, without the browser.
Name: "{autoprograms}\Vireo Downloads"; Filename: "{app}\Vireo.exe"; Parameters: "--downloads"; IconFilename: "{app}\Vireo.exe"; Comment: "Vireo's download manager, without the browser"
Name: "{autodesktop}\Vireo"; Filename: "{app}\Vireo.exe"; IconFilename: "{app}\Vireo.exe"; Comment: "Browse the web with Vireo"; Tasks: desktopicon

[Run]
; Lists Vireo among the browsers Windows offers (the person still chooses the default in Windows Settings).
Filename: "{app}\Vireo.exe"; Parameters: "--register"; Flags: runhidden waituntilterminated; StatusMsg: "Adding Vireo to Windows' list of browsers..."
; Windows keeps shortcut and taskbar icons in a cache; this refreshes it so a new icon shows at once.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden waituntilterminated skipifdoesntexist
Filename: "{app}\Vireo.exe"; Description: "Open Vireo"; Flags: nowait postinstall skipifsilent
; An update chosen with "Restart to update": Vireo opens again, with the same tabs.
Filename: "{app}\Vireo.exe"; Parameters: "{param:REOPENARGS}"; Flags: nowait; Check: ReopenAfterUpdate

[UninstallRun]
Filename: "{app}\Vireo.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "VireoUnregister"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Assets"

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function ReopenAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:REOPEN|0}') = '1');
end;

{ The Evergreen WebView2 runtime, wherever Microsoft's installer put it (for the machine, or for this user). }
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

{ Windows 11 and up-to-date Windows 10 already have it. Otherwise Microsoft's own small installer fetches it. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Page: TDownloadWizardPage;
  Code: Integer;
begin
  Result := '';
  if WebView2Installed then Exit;
  try
    if not WizardSilent then
    begin
      Page := CreateDownloadPage('Getting the browser engine', 'Vireo uses Microsoft''s WebView2 engine, which this computer does not have yet.', nil);
      Page.Clear;
      Page.Add(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '');
      Page.Show;
      try
        Page.Download;
      finally
        Page.Hide;
      end;
    end
    else
      DownloadTemporaryFile(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) or not WebView2Installed then
      Result := 'Microsoft''s WebView2 engine could not be installed. Connect to the internet and run Setup again, or install it from https://developer.microsoft.com/microsoft-edge/webview2/';
  except
    Result := 'Microsoft''s WebView2 engine could not be downloaded. Connect to the internet and run Setup again.';
  end;
end;

{ The program goes; the person's browsing data stays unless they say otherwise (a reinstall then finds it all again). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\Vireo');
  if UninstallSilent or not DirExists(Data) then Exit;
  if MsgBox('Also delete your Vireo browsing data from this computer?' + #13#10 + #13#10 +
            'This removes your history, bookmarks, settings, sign-ins and profiles. Your downloaded files are not touched.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
