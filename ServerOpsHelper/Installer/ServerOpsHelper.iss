; ============================================================================
;  服务器运维助手 —— Inno Setup 6 安装脚本
;
;  为什么从 WiX/MSI 换成 Inno（2026-10-17 老板决定）：
;    · MSI 的事务回滚机制会在目标盘根目录建 E:\Config.Msi 存 .rbf 备份，
;      该目录 ACL 只有 SYSTEM+管理员，非提权安装/修复必然报「错误 5 / 1926」，
;      而且每次安装都会被重置，授权治不了本；
;    · Inno 编译出来就是一个普通 exe：解文件、写注册表、生成 uninstall.exe，
;      完全不碰 Windows Installer —— Config.Msi / 1603 / 1926 这一整类故障从根上消失。
;    · 顺带获得国内用户熟悉的多页向导：欢迎 → 安装范围 → 安装位置（直接可见）
;      → 附加任务 → 准备安装 → 安装中 → 完成。
;
;  版本号由 build_inno.py 从 ServerOpsHelper.csproj 读出来，用 /D 传进来，
;  不在脚本里硬编码，避免和实际编译出来的 exe 版本漂移。
; ============================================================================

#define MyAppName "服务器运维助手"
#define MyAppExeName "ServerOpsHelper.exe"
#define MyAppPublisher "ServerOpsHelper"
#define MyAppId "{{B7C3E2A1-9F4D-4C8E-B1A6-3D5E7F20C9B4}"

#ifndef MyAppVersion
  #define MyAppVersion "1.9.3"
#endif

[Setup]
; AppId 一旦发布就**不能改**：升级要靠它认出「这是同一个产品」并原地覆盖。
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

; 默认装到「当前用户」目录，不弹 UAC；用户也可以在第一页选择「为所有用户安装」
; （那时才会弹一次 UAC，并装到 Program Files）。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\ServerOpsHelper
UsePreviousAppDir=yes
DirExistsWarning=auto

DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no

; 【必须是 no，不能留默认值】
; 默认值是 auto —— 那意味着「机器上已有本产品的安装记录时，自动隐藏选择目录页」，
; 于是老板重装/升级时看不到安装位置，观感就是「这个安装包不让我选目录」。
; 2026-10-17 实际发生：v1.9.3 装过之后再双击安装包，向导里就少了这一页。
DisableDirPage=no

; 现代样式（带左侧大图）—— 就是国内用户最常见的那种安装向导外观
WizardStyle=modern
WizardSizePercent=100

LicenseFile=
InfoBeforeFile=
InfoAfterFile=

OutputDir=..\..\release\v{#MyAppVersion}
OutputBaseFilename=ServerOpsHelper-Setup-v{#MyAppVersion}
SetupIconFile=..\Assets\appicon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

; 安装时如果程序正在运行，交给 Restart Manager 关掉它（比让用户自己去关省事）
CloseApplications=yes
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
; 覆盖几条最常看到的文案，让它更像国内软件的习惯说法
WelcomeLabel1=欢迎安装 [name]
WelcomeLabel2=此向导将引导您完成 [name/ver] 的安装。%n%n建议在继续之前关闭其他正在运行的程序，安装完成后可以从开始菜单或桌面启动。
SelectDirDesc=程序要安装到哪里？
SelectDirLabel3=安装程序会把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=要继续请点「下一步」；如果想换一个文件夹，请点「浏览」。
SelectTasksDesc=还要做哪些事？
SelectTasksLabel2=请选择安装 [name] 时要一并完成的附加任务，然后点「下一步」。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; recursesubdirs + createallsubdirs：**必须保留子目录层级**。
; TerminalAssets\ 里是终端用的 xterm.js，runtimes\ 和 zh-Hans\ 也都在子目录里，
; 一旦被拍平到根目录，安装版的终端就打不开（2026-10-01 在 MSI 那边踩过）。
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理「程序目录里运行时生成的东西」。
; ⚠️ 绝对不能碰 {localappdata}\ServerOpsHelper —— 那里是链接、密码箱、访问历史、
;    登录账号，卸载程序必须一个字都不动（数据与程序分离是硬约定）。
Type: filesandordirs; Name: "{app}\{#MyAppExeName}.WebView2"
Type: filesandordirs; Name: "{app}\logs"

[Code]
const
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  LegacyDisplayName = '服务器运维助手';

var
  LegacyDir: String;
  LegacyCmd: String;
  LegacyFound: Boolean;
  LegacyMigrate: Boolean;

{ 把 '"C:\a b\x.exe" /uninstall /quiet' 拆成 exe 与参数两段 }
procedure SplitCommand(const Cmd: String; var ExeName, Params: String);
var
  P: Integer;
  Rest: String;
begin
  ExeName := '';
  Params := '';
  if Cmd = '' then
    Exit;

  if Copy(Cmd, 1, 1) = '"' then
  begin
    Rest := Copy(Cmd, 2, Length(Cmd) - 1);
    P := Pos('"', Rest);
    if P = 0 then
    begin
      ExeName := Rest;
      Exit;
    end;
    ExeName := Copy(Rest, 1, P - 1);
    Params := Trim(Copy(Rest, P + 1, Length(Rest) - P));
  end
  else
  begin
    P := Pos(' ', Cmd);
    if P = 0 then
      ExeName := Cmd
    else
    begin
      ExeName := Copy(Cmd, 1, P - 1);
      Params := Trim(Copy(Cmd, P + 1, Length(Cmd) - P));
    end;
  end;
end;

{ 在某个注册表根下扫 Uninstall 子键，找旧的 MSI/Burn 安装项 }
function ScanRoot(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  i: Integer;
  Key, Display, Loc, Cmd: String;
begin
  Result := False;
  if not RegGetSubkeyNames(RootKey, UninstallRoot, Names) then
    Exit;

  for i := 0 to GetArrayLength(Names) - 1 do
  begin
    { Inno 自己写的项形如 GUID_is1，跳过 —— 那不叫「旧版」 }
    if Pos('_is1', Names[i]) > 0 then
      Continue;

    Key := UninstallRoot + '\' + Names[i];
    if not RegQueryStringValue(RootKey, Key, 'DisplayName', Display) then
      Continue;
    if Display <> LegacyDisplayName then
      Continue;

    Loc := '';
    Cmd := '';
    RegQueryStringValue(RootKey, Key, 'InstallLocation', Loc);
    if not RegQueryStringValue(RootKey, Key, 'QuietUninstallString', Cmd) then
      RegQueryStringValue(RootKey, Key, 'UninstallString', Cmd);

    LegacyDir := Loc;
    LegacyCmd := Cmd;
    Result := True;
    Exit;
  end;
end;

procedure FindLegacy;
begin
  LegacyFound := False;
  LegacyDir := '';
  LegacyCmd := '';
  if ScanRoot(HKCU) then LegacyFound := True;
  if (not LegacyFound) and ScanRoot(HKLM64) then LegacyFound := True;
  if (not LegacyFound) and ScanRoot(HKLM32) then LegacyFound := True;
end;

{ 旧版装在哪个目录，新版就默认装回哪儿；没有旧版就用默认的用户目录 }
function GetPreviousDir(Default: String): String;
begin
  if LegacyDir <> '' then
    Result := LegacyDir
  else
    Result := Default;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  FindLegacy;
  LegacyMigrate := LegacyFound;

  if LegacyFound and (not WizardSilent) and (ExpandConstant('{param:SKIPMIGRATE|0}') <> '1') then
  begin
    if MsgBox('检测到旧版（MSI 安装方式）装在：' + #13#10 + LegacyDir + #13#10 + #13#10 +
              '新版本会沿用这个位置继续安装。' + #13#10 +
              '建议先卸载旧版，否则「设置 → 应用」里会留下两条记录。' + #13#10 + #13#10 +
              '现在卸载旧版吗？（卸载不会动您的数据：链接、密码、历史都保留）',
              mbConfirmation, MB_YESNO) = IDNO then
      LegacyMigrate := False;
  end;

  { 静默安装默认不迁移，避免自动化测试时误删真实安装；需要时显式传 MIGRATE=1 }
  if WizardSilent and (ExpandConstant('{param:MIGRATE|0}') <> '1') then
    LegacyMigrate := False;
end;

{ 在复制文件之前把旧 MSI 产品卸掉：
  同一目录先删后装，避免旧文件残留；卸载失败（例如 Config.Msi 权限问题）
  不让安装中断 —— 只是留下一条旧记录，功能不受影响。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExeName, Params: String;
  Code: Integer;
begin
  Result := '';
  NeedsRestart := False;

  if (not LegacyMigrate) or (LegacyCmd = '') then
    Exit;

  SplitCommand(LegacyCmd, ExeName, Params);
  if not FileExists(ExeName) then
    Exit;

  if not Exec(ExeName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('旧版卸载程序启动失败：' + ExeName)
  else if Code <> 0 then
    Log(Format('旧版卸载返回 %d，继续安装（旧记录可能残留）', [Code]))
  else
    Log('旧版已卸载：' + ExeName);
end;
