#ifndef PayloadRoot
  #error Supply PayloadRoot as the complete clean v1.1 application directory.
#endif
#ifndef ReleaseOutput
  #error Supply ReleaseOutput outside the payload.
#endif
#ifndef AppVersion
  #error Supply AppVersion from release.json.
#endif
#ifndef AssemblyVersion
  #error Supply AssemblyVersion from release.json.
#endif

[Setup]
AppId={{4D5BA61A-2F0C-4E67-80A3-61737C2E1B11}
AppName=YRT Music v1.1
AppVersion={#AppVersion}
AppVerName=YRT Music v{#AppVersion}
AppPublisher=YRT-GPT
AppPublisherURL=https://github.com/yrt-coder/YRTMusic
AppSupportURL=https://github.com/yrt-coder/YRTMusic/issues
DefaultDirName={localappdata}\Programs\YRTMusic-v1.1
DefaultGroupName=YRT Music v1.1
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
UsePreviousAppDir=yes
UsePreviousLanguage=no
ShowLanguageDialog=yes
DisableWelcomePage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
WizardStyle=modern dynamic
WizardSizePercent=110,110
WizardResizable=yes
UninstallDisplayIcon={app}\YRT-GPT_MusicDownloader.exe
UninstallFilesDir={app}\Uninstall
OutputDir={#ReleaseOutput}
OutputBaseFilename=YRTMusic-Setup-v{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
UninstallLogging=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
LicenseFile=..\LICENSE
VersionInfoVersion={#AssemblyVersion}
VersionInfoCompany=YRT-GPT
VersionInfoDescription=YRT Music v1.1 per-user installer
VersionInfoProductName=YRT Music
VersionInfoProductVersion={#AssemblyVersion}
#ifdef SignedBuild
SignTool=yrtgpt
SignToolRunMinimized=yes
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "Notice.en.txt"
Name: "zhcn"; MessagesFile: "Languages\ChineseSimplified.isl"; InfoBeforeFile: "Notice.zh-CN.txt"
Name: "zhtw"; MessagesFile: "Languages\ChineseTraditional.isl"; InfoBeforeFile: "Notice.zh-TW.txt"

[LangOptions]
en.DialogFontSize=12
zhcn.DialogFontSize=12
zhtw.DialogFontSize=12

[CustomMessages]
en.FrameworkMissing=YRT Music requires Microsoft .NET Framework 4.8 or later. Install it from Microsoft, then run this installer again.
zhcn.FrameworkMissing=YRT Music 需要 Microsoft .NET Framework 4.8 或更高版本。请从微软官方安装后，再运行本安装器。
zhtw.FrameworkMissing=YRT Music 需要 Microsoft .NET Framework 4.8 或更新版本。請從微軟官方安裝後，再執行本安裝程式。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*,chrome-profile\*,*.mp3,*.pfx,*.p12,*.key,*.zip"

[Icons]
Name: "{group}\YRT Music v1.1"; Filename: "{app}\YRT-GPT_MusicDownloader.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\YRT Music v1.1"; Filename: "{app}\YRT-GPT_MusicDownloader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function InitializeSetup: Boolean;
var
  FrameworkRelease: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', FrameworkRelease) and (FrameworkRelease >= 528040);
  if not Result then
    SuppressibleMsgBox(CustomMessage('FrameworkMissing'), mbError, MB_OK, IDOK);
end;
