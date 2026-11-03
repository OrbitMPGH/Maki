; The Windows installer people download. Velopack's Setup.exe has no wizard and always installs into
; %LOCALAPPDATA%\Maki.Desktop, so this wraps it: it asks for a folder, then runs Velopack's Setup.exe
; with --installto. Velopack still owns the install, the Apps entry, the uninstaller and updates.
; This installer leaves nothing of its own behind (Uninstallable=no).
;
; Built by build-windows.ps1 -Pack:
;   ISCC /DAppVersion=0.35.0 /DFileVersion=0.35.0.0 /DVelopackSetup=<Maki.Desktop-win-Setup.exe> /O<dir> windows-setup.iss

#ifndef AppVersion
  #error Pass /DAppVersion=X.Y.Z
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef VelopackSetup
  #error Pass /DVelopackSetup=<path to Maki.Desktop-win-Setup.exe>
#endif

[Setup]
AppId=Maki.Desktop.Setup
AppName=Maki
AppVersion={#AppVersion}
AppPublisher=Maki
AppPublisherURL=https://github.com/OrbitMPGH/Maki
VersionInfoVersion={#FileVersion}
; An existing install's folder, otherwise %LOCALAPPDATA%\Programs\Maki. Never %LOCALAPPDATA%\Maki:
; that is the data folder, and uninstalling deletes the install folder and everything in it. The
; last part is also what Inno adds to a folder picked with Browse, so installs get a folder of
; their own.
DefaultDirName={code:DefaultDir}
PrivilegesRequired=lowest
Uninstallable=no
DisableWelcomePage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
UsePreviousAppDir=no
WizardStyle=modern
SetupIconFile=..\..\src\Maki.Desktop\maki.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=Maki-Setup

[Messages]
WelcomeLabel2=This installs Maki, a self-hosted manga library and reader, along with a tray icon that keeps it running in the background.%n%nYour library, settings and database are kept in your AppData\Local\Maki folder, apart from the program. Updating or uninstalling Maki leaves them in place.
FinishedLabel=Maki is installed. Its icon is in the notification area, and it starts with Windows from now on.

[Files]
Source: "{#VelopackSetup}"; DestName: "MakiVelopackSetup.exe"; Flags: dontcopy nocompression

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Maki.Desktop';

function Normalized(Dir: String): String;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(Dir)));
end;

function IsSameOrInside(Dir, Parent: String): Boolean;
begin
  Dir := Normalized(Dir);
  Parent := Normalized(Parent);
  Result := (Dir = Parent) or (Pos(AddBackslash(Parent), Dir) = 1);
end;

function IsMakiInstall(Dir: String): Boolean;
var
  Manifest: AnsiString;
begin
  Result := FileExists(AddBackslash(Dir) + 'Update.exe')
    and LoadStringFromFile(AddBackslash(Dir) + 'current\sq.version', Manifest)
    and (Pos('<id>Maki.Desktop</id>', String(Manifest)) > 0);
end;

function IsEmptyDir(Dir: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function CanWriteTo(Dir: String): Boolean;
var
  Created: Boolean;
  Probe: String;
begin
  Created := not DirExists(Dir);
  Result := ForceDirectories(Dir);
  if not Result then
    Exit;
  Probe := AddBackslash(Dir) + '.maki-setup-probe';
  Result := SaveStringToFile(Probe, '', False);
  DeleteFile(Probe);
  if Created then
    RemoveDir(Dir);
end;

function ExistingInstall(): String;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Result) then
    Result := '';
end;

function Refuse(Message: String): Boolean;
begin
  SuppressibleMsgBox(Message, mbError, MB_OK, IDOK);
  Result := False;
end;

function DefaultDir(Param: String): String;
begin
  Result := ExistingInstall();
  if Result = '' then
    Result := ExpandConstant('{userpf}\Maki');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir, Data, Existing: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  Dir := WizardDirValue();
  Data := ExpandConstant('{localappdata}\Maki');
  Existing := ExistingInstall();

  if IsSameOrInside(Dir, Data) or IsSameOrInside(Data, Dir) then
    Result := Refuse('Maki keeps your library and settings in ' + Data + ', so it cannot be installed there or in a folder that contains it. Pick another folder.')
  else if Length(Normalized(Dir)) <= 3 then
    Result := Refuse('Pick a folder rather than a whole drive. Uninstalling Maki deletes its install folder.')
  else if (Existing <> '') and (Normalized(Existing) <> Normalized(Dir)) then
    Result := Refuse('Maki is already installed in ' + Existing + '. Install into that folder to update it, or uninstall it first from Settings > Apps.')
  else if DirExists(Dir) and not IsEmptyDir(Dir) and not IsMakiInstall(Dir) then
    Result := Refuse(Dir + ' already has files in it. Uninstalling Maki deletes its install folder and everything in it, so pick an empty or new folder.')
  else if not CanWriteTo(Dir) then
    Result := Refuse('You cannot write to ' + Dir + ' without administrator rights. Pick a folder in your user folder or on another drive.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, Params: String;
  Code: Integer;
begin
  Result := '';
  Dir := WizardDirValue();
  ExtractTemporaryFile('MakiVelopackSetup.exe');
  // Silent here means silent there too, which also skips starting Maki at the end.
  Params := '--installto "' + Dir + '"';
  if WizardSilent() then
    Params := '--silent ' + Params;
  if not Exec(ExpandConstant('{tmp}\MakiVelopackSetup.exe'), Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
    Result := 'Maki''s setup could not be started: ' + SysErrorMessage(Code)
  else if Code <> 0 then
    Result := 'Maki''s setup stopped with an error (exit code ' + IntToStr(Code) + ').'
  else if not IsMakiInstall(Dir) then
    Result := 'Setup was cancelled before Maki was installed.';
end;
