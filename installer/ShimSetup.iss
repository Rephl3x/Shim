#define ShimPayload GetEnv("SHIM_SETUP_PAYLOAD")
#define ShimProjectRoot GetEnv("SHIM_PROJECT_ROOT")
#define ShimSetupOutput GetEnv("SHIM_SETUP_OUTPUT")

#if ShimPayload == ""
  #error Set SHIM_SETUP_PAYLOAD to the compiled Shim package directory.
#endif
#if ShimProjectRoot == ""
  #error Set SHIM_PROJECT_ROOT to the Shim source root.
#endif
#if ShimSetupOutput == ""
  #error Set SHIM_SETUP_OUTPUT to the setup output directory.
#endif

[Setup]
AppId={{3394DE67-9291-418E-B369-B2F43CC1D112}
AppName=Shim
AppVersion=0.8.0
AppVerName=Shim 0.8.0
AppPublisher=AMS Labs
AppPublisherURL=https://github.com/Rephl3x/Shim
AppSupportURL=https://github.com/Rephl3x/Shim/issues
DefaultDirName={autopf}\Shim
UsePreviousAppDir=yes
DefaultGroupName=Shim
UninstallDisplayName=Shim 0.8.0
UninstallDisplayIcon={app}\Shim.exe
OutputDir={#ShimSetupOutput}
OutputBaseFilename=Shim-Setup-0.8.0
SetupIconFile={#ShimProjectRoot}\assets\shim-app-icon.ico
VersionInfoVersion=0.8.0.0
VersionInfoProductVersion=0.8.0
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
PrivilegesRequired=admin
CloseApplications=yes
RestartIfNeededByRun=yes
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#ShimPayload}\Shim.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ShimPayload}\Shim.Updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ShimPayload}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ShimPayload}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ShimPayload}\dependencies.lock.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ShimPayload}\drivers\*"; DestDir: "{app}\drivers"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ShimPayload}\runtime\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ShimPayload}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Shim"; Filename: "{app}\Shim.exe"
Name: "{autodesktop}\Shim"; Filename: "{app}\Shim.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\drivers\vJoySetup_v2.2.2.0_Win10_Win11.exe"; Parameters: "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-"; StatusMsg: "Installing the signed vJoy virtual controller..."; Flags: waituntilterminated; Check: ShouldInstallVJoy
Filename: "{app}\drivers\HidHide_1.5.230_x64.exe"; Parameters: "/exenoui /qn /norestart"; StatusMsg: "Installing the signed HidHide device filter..."; Flags: waituntilterminated; Check: ShouldInstallHidHide
Filename: "{app}\Shim.exe"; Description: "Launch Shim and configure your rig"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
function DriverServiceExists(const ServiceName: String): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

function ShouldInstallVJoy: Boolean;
begin
  Result := not DriverServiceExists('vjoy');
end;

function ShouldInstallHidHide: Boolean;
begin
  Result := not DriverServiceExists('HidHide');
end;

var
  ConfigPage: TWizardPage;
  KeepSettings, FreshSettings: TNewRadioButton;
  BackupSettings: TNewCheckBox;
  InstallInfo: TNewStaticText;
  ExistingConfigDirectory, ExistingInstallVersion, ExistingInstallDirectory: String;
  BackupConfigDirectory: String;

function PreviousInstallKey: String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{3394DE67-9291-418E-B369-B2F43CC1D112}_is1';
end;

procedure FindPreviousInstall;
var
  KeyName: String;
begin
  KeyName := PreviousInstallKey;
  ExistingInstallVersion := '';
  ExistingInstallDirectory := '';
  if not RegQueryStringValue(HKLM64, KeyName, 'DisplayVersion', ExistingInstallVersion) then
    if not RegQueryStringValue(HKLM, KeyName, 'DisplayVersion', ExistingInstallVersion) then
      RegQueryStringValue(HKCU, KeyName, 'DisplayVersion', ExistingInstallVersion);
  if not RegQueryStringValue(HKLM64, KeyName, 'InstallLocation', ExistingInstallDirectory) then
    if not RegQueryStringValue(HKLM, KeyName, 'InstallLocation', ExistingInstallDirectory) then
      RegQueryStringValue(HKCU, KeyName, 'InstallLocation', ExistingInstallDirectory);
end;

function HasManagedConfigs: Boolean;
begin
  Result := FileExists(ExistingConfigDirectory + '\profile.json') or
    FileExists(ExistingConfigDirectory + '\options.json') or
    FileExists(ExistingConfigDirectory + '\appearance.json') or
    FileExists(ExistingConfigDirectory + '\setup-draft.json');
end;

function CopyConfigFile(const FileName: String): Boolean;
begin
  Result := True;
  if FileExists(ExistingConfigDirectory + '\' + FileName) then
    Result := CopyFile(ExistingConfigDirectory + '\' + FileName,
      BackupConfigDirectory + '\' + FileName, False);
end;

function MakeConfigBackup: Boolean;
var
  FindRec: TFindRec;
  SourceFile, BackupFile: String;
begin
  Result := True;
  if not HasManagedConfigs then exit;
  BackupConfigDirectory := ExistingConfigDirectory + '\config-backups\installer-' +
    GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
  if not ForceDirectories(BackupConfigDirectory) then
  begin
    Result := False;
    exit;
  end;
  if not CopyConfigFile('profile.json') or not CopyConfigFile('options.json') or
     not CopyConfigFile('appearance.json') or not CopyConfigFile('setup-draft.json') then
  begin
    Result := False;
    exit;
  end;
  if FindFirst(ExistingConfigDirectory + '\profile-before-setup-*.json', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        begin
          SourceFile := ExistingConfigDirectory + '\' + FindRec.Name;
          BackupFile := BackupConfigDirectory + '\' + FindRec.Name;
          if not CopyFile(SourceFile, BackupFile, False) then Result := False;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function RemoveManagedConfigs: Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FileExists(ExistingConfigDirectory + '\profile.json') and not DeleteFile(ExistingConfigDirectory + '\profile.json') then Result := False;
  if FileExists(ExistingConfigDirectory + '\options.json') and not DeleteFile(ExistingConfigDirectory + '\options.json') then Result := False;
  if FileExists(ExistingConfigDirectory + '\appearance.json') and not DeleteFile(ExistingConfigDirectory + '\appearance.json') then Result := False;
  if FileExists(ExistingConfigDirectory + '\setup-draft.json') and not DeleteFile(ExistingConfigDirectory + '\setup-draft.json') then Result := False;
  if FindFirst(ExistingConfigDirectory + '\profile-before-setup-*.json', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           not DeleteFile(ExistingConfigDirectory + '\' + FindRec.Name) then Result := False;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure SettingsChoiceChanged(Sender: TObject);
begin
  BackupSettings.Enabled := not FreshSettings.Checked;
  if FreshSettings.Checked then BackupSettings.Checked := True;
end;

procedure InitializeWizard;
begin
  FindPreviousInstall;
  ExistingConfigDirectory := ExpandConstant('{localappdata}') + '\FHRigBridge';
  ConfigPage := CreateCustomPage(wpSelectDir, 'Previous versions and settings', 'Choose how Shim handles your existing installation and configuration.');
  InstallInfo := TNewStaticText.Create(ConfigPage);
  InstallInfo.Parent := ConfigPage.Surface;
  InstallInfo.Left := ScaleX(0);
  InstallInfo.Top := ScaleY(0);
  InstallInfo.Width := ConfigPage.SurfaceWidth;
  InstallInfo.Height := ScaleY(54);
  InstallInfo.AutoSize := False;
  InstallInfo.WordWrap := True;
  if (ExistingInstallVersion <> '') and (ExistingInstallDirectory <> '') then
    InstallInfo.Caption := 'Shim ' + ExistingInstallVersion + ' was found at ' + ExistingInstallDirectory + '. Setup will upgrade that installation in place and replace its application files.'
  else if ExistingInstallVersion <> '' then
    InstallInfo.Caption := 'Shim ' + ExistingInstallVersion + ' was found. Setup will upgrade that installation in place and replace its application files.'
  else if HasManagedConfigs then
    InstallInfo.Caption := 'Existing Shim settings were found in ' + ExistingConfigDirectory + '. No registered Shim installation was found.'
  else
    InstallInfo.Caption := 'No previous Shim installation or saved configuration was detected.';

  KeepSettings := TNewRadioButton.Create(ConfigPage);
  KeepSettings.Parent := ConfigPage.Surface;
  KeepSettings.Left := ScaleX(0);
  KeepSettings.Top := ScaleY(66);
  KeepSettings.Width := ConfigPage.SurfaceWidth;
  KeepSettings.Height := ScaleY(42);
  KeepSettings.Caption := 'Keep settings and apply supported upgrades';
  KeepSettings.Checked := True;
  KeepSettings.OnClick := @SettingsChoiceChanged;

  FreshSettings := TNewRadioButton.Create(ConfigPage);
  FreshSettings.Parent := ConfigPage.Surface;
  FreshSettings.Left := ScaleX(0);
  FreshSettings.Top := ScaleY(120);
  FreshSettings.Width := ConfigPage.SurfaceWidth;
  FreshSettings.Height := ScaleY(42);
  FreshSettings.Caption := 'Start fresh with a backup of current settings';
  FreshSettings.OnClick := @SettingsChoiceChanged;

  BackupSettings := TNewCheckBox.Create(ConfigPage);
  BackupSettings.Parent := ConfigPage.Surface;
  BackupSettings.Left := ScaleX(22);
  BackupSettings.Top := ScaleY(174);
  BackupSettings.Width := ConfigPage.SurfaceWidth - ScaleX(22);
  BackupSettings.Height := ScaleY(36);
  BackupSettings.Caption := 'Create a timestamped backup of my current configuration before upgrading';
  BackupSettings.Checked := True;

  InstallInfo := TNewStaticText.Create(ConfigPage);
  InstallInfo.Parent := ConfigPage.Surface;
  InstallInfo.Left := ScaleX(0);
  InstallInfo.Top := ScaleY(222);
  InstallInfo.Width := ConfigPage.SurfaceWidth;
  InstallInfo.Height := ScaleY(58);
  InstallInfo.AutoSize := False;
  InstallInfo.WordWrap := True;
  InstallInfo.Caption := 'This release adds a config-version marker but preserves all existing rig, launch and theme values. When Shim includes an explicit migration, it backs up each file before changing it. Device-visibility recovery data is always preserved.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if HasManagedConfigs and (BackupSettings.Checked or FreshSettings.Checked) then
    begin
      if not MakeConfigBackup then
        RaiseException('Shim could not back up your existing configuration. Setup is stopping before it changes your settings.');
      Log('Existing Shim settings backed up to ' + BackupConfigDirectory);
    end;
    if FreshSettings.Checked and HasManagedConfigs then
    begin
      if not RemoveManagedConfigs then
        RaiseException('Shim could not move all settings out of the active configuration folder. Your backup is at ' + BackupConfigDirectory);
      Log('Fresh configuration selected. Previous settings remain in ' + BackupConfigDirectory);
    end;
  end;
end;
