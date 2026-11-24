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
; 主程序从通配里排除，单独一条放在**最后**（见下一条）。原因在 [Code] 里详述：
; exe 一旦早于 dll 落地，连接器守护就会在中途拉起新进程、锁住 clrjit.dll。
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "\{#MyAppExeName}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ⚠️ 这一条必须留在 [Files] 的最后面。Inno 按节内顺序复制，
;    放最后 = dll 全部换完、安装快结束了才让 exe 出现，守护进程没有中途插手的窗口。
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

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
; 安装期为绕开「文件被映射」而临时改名的旧主程序（详见 [Code] 顶部注释）
Type: files; Name: "{app}\{#MyAppExeName}.stale-*"

[Code]
const
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  LegacyDisplayName = '服务器运维助手';

var
  LegacyDir: String;
  LegacyCmd: String;
  LegacyFound: Boolean;
  LegacyMigrate: Boolean;
  StashedSrc: String;   { 被改名藏起来的旧主程序原路径，装完/取消时用来兜底还原 }
  StashedExe: String;   { 改名后的实际路径（...\ServerOpsHelper.exe.stale-<时间戳>） }

  { ─────────────────── 运行中的实例：先把 exe 藏起来，再杀 ───────────────────

  为什么需要这一整套：

  智能体客户端的连接器（`dsh-mcp-connector` 的 `stdio-startup-guard.js`）会用
  `ServerOpsHelper.exe --mcp-stdio` 拉起一个**无窗口后台进程**，并且带自动重启守护：
  杀掉一个，它立刻再拉一个（2026-11-18 实测 22:48:57 / 22:49:19 各起一个；
  手动结束后不到 1 秒又出现新 PID）。
  进程活着时会把 clrjit.dll / coreclr.dll 这些运行时映像映射住，
  安装程序覆盖它们时 DeleteFile 返回 ERROR_ACCESS_DENIED(5)，
  界面上就是「尝试替换已存在的文件时失败：DeleteFile 失败；错误代码 5。拒绝访问。」
  —— 看着像权限问题，其实是文件被映射（属主就是当前用户，能删，只是「正被使用」）。
  `CloseApplications=yes` 对这种**无窗口 + 被守护重启**的组合天生无效：
  Restart Manager 只认得注册过窗口的进程。

  光 taskkill 不管用：杀了立刻被拉起，新进程正好落在「正在复制文件」的窗口里。
  实测过两条路（2026-11-18，本机 Win11 / NTFS）：

    · 整个安装目录改名 —— **不行**。进程活着时 RenameFile 目录会失败，
      报 WinError 5（cwd 不在里面）或 32（cwd 在里面）；
    · 单个文件改名 —— **可以**。运行中的 exe、被映射的 clrjit.dll 都能改名，
      而且改名后原路径能立刻写入新文件。

  所以顺序是：
    1. 把安装目录里的主程序改名成 .stale-<时间戳>
       -> 守护重启时那个固定路径上已经没有 exe，CreateProcess 失败，拉不起来
    2. taskkill /F 同步杀掉现存实例（此时杀完不会被补充，因为没 exe 可拉）
    3. 轮询 tasklist 直到进程真的没了（最多 5 秒）
    4. Inno 开始复制；主程序 exe 在 [Files] 里被安排在最后一条，
       等所有 dll 都换完才落地 —— 守护即使这时候起来了也没有冲突
  装完之后（ssPostInstall）再补一刀并删掉 .stale-* 残留；
  安装被取消时（DeinitializeSetup）把藏起来的旧 exe 还原，别让用户的应用打不开。 }

function CountAppInstances(): Integer;
var
  TmpFile: String;
  Lines: TArrayOfString;
  Code, i: Integer;
  Line: String;
begin
  Result := 0;
  TmpFile := ExpandConstant('{tmp}\running-instances.txt');
  DeleteFile(TmpFile);

  Exec(ExpandConstant('{cmd}'),
       '/c tasklist /FI "IMAGENAME eq {#MyAppExeName}" /NH > "' + TmpFile + '"',
       '', SW_HIDE, ewWaitUntilTerminated, Code);

  if not LoadStringsFromFile(TmpFile, Lines) then
    Exit;

  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[i]);
    // 没有匹配时 tasklist 会输出一句提示（中英文各一种），不能当成"有进程"
    if (Line = '') or (Pos('没有运行的任务', Line) > 0) or (Pos('No tasks are running', Line) > 0) then
      Continue;
    Result := Result + 1;
  end;
end;

procedure StopAppInstances();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/c taskkill /F /IM {#MyAppExeName} >nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ 轮询到实例真的退出为止。taskkill /F 虽然同步返回，但映像引用要过一小会儿
  才彻底释放，紧接着覆盖 dll 仍可能撞「拒绝访问 5」。 }
function WaitForInstancesGone(MaxMs: Integer): Boolean;
begin
  Result := False;
  while MaxMs > 0 do
  begin
    if CountAppInstances() = 0 then
    begin
      Result := True;
      Exit;
    end;
    Sleep(150);
    MaxMs := MaxMs - 150;
  end;
  Result := CountAppInstances() = 0;
end;

{ 把主程序改名藏起来 —— 让守护进程的「自动重启」找不到目标。
  返回 False 只说明改名没成功（极少数情况：杀软盯着 exe），流程仍然继续，
  后面还有 taskkill 兜底，只是会退回「杀了可能被重新拉起」的老问题。 }
function StashAppExe(): Boolean;
var
  Src, Dst: String;
begin
  Result := True;
  Src := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(Src) then
    Exit;                       { 全新安装，没有可藏的 }

  Dst := Src + '.stale-' + GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
  Result := RenameFile(Src, Dst);
  if Result then
  begin
    StashedSrc := Src;
    StashedExe := Dst;
    Log('主程序已改名藏起（防止连接器守护重新拉起）：' + Dst);
  end
  else
    Log('主程序改名失败，只能直接终止进程兜底：' + Src);
end;

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
  Running: Integer;
begin
  Result := '';
  NeedsRestart := False;

  { 先处理运行中的实例：必须在卸旧 MSI / 复制文件之前，否则后面两步都会撞文件占用 }
  Running := CountAppInstances();
  if Running > 0 then
  begin
    { 注意：这一行不能折行成以「[」开头的续行 —— ISPP 会把行首的 [ 当成节标记而报
      「Invalid section tag」。所以数组参数写在同一个物理行里。 }
    Log(Format('检测到 %d 个运行中的实例（含无窗口的 --mcp-stdio 后台进程）', [Running]));

    { 顺序不能反：先藏 exe，再杀进程。
      反过来的话，taskkill 返回到「开始复制文件」之间还有几百毫秒，
      连接器的守护正好在这段时间把新实例拉起来，又去映射 clrjit.dll。 }
    StashAppExe();
    StopAppInstances();

    if WaitForInstancesGone(5000) then
      Log('实例已全部退出')
    else
      Log('警告：5 秒内仍有实例没退出，复制阶段可能报「拒绝访问 5」');
  end;

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

{ 文件全部落地之后收尾：
   1. 安装期间连接器可能又把 exe 拉起来了（主程序是 [Files] 的最后一条，落地很晚），
      它会把 .stale-* 继续映射着 —— 先再杀一刀，让残留文件能删得掉；
   2. 清掉 .stale-* 残留。删不掉也不阻断安装：只记日志，下次安装或卸载时再清。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, Stale: String;
  FindRec: TFindRec;
begin
  if CurStep <> ssPostInstall then
    Exit;

  if CountAppInstances() > 0 then
  begin
    Log('安装完成后仍有实例在跑，先结束再清理残留');
    StopAppInstances();
    WaitForInstancesGone(2000);
  end;

  AppDir := ExpandConstant('{app}');
  if FindFirst(AppDir + '\{#MyAppExeName}.stale-*', FindRec) then
  begin
    try
      repeat
        Stale := AppDir + '\' + FindRec.Name;
        if DeleteFile(Stale) then
          Log('已清理安装期残留：' + Stale)
        else
          Log('残留暂时删不掉（卸载时还会再清一次）：' + Stale);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  { 装完了，藏起来的旧 exe 不再需要还原 }
  StashedSrc := '';
  StashedExe := '';
end;

{ 安装被取消 / 中途失败时的兜底：把藏起来的旧主程序还原回原位。
  藏 exe 是为了让守护进程拉不起来，但如果安装没做完就退出，
  原路径上一直没有 exe，用户双击图标就是「找不到文件」—— 这不能接受。
  判据很简单：安装目录里没有 exe、而 .stale-* 还在，就还原。
  （正常装完时新 exe 已经在位，这里什么都不做。） }
procedure DeinitializeSetup();
begin
  if StashedSrc = '' then
    Exit;
  if FileExists(StashedSrc) then
    Exit;
  if not FileExists(StashedExe) then
    Exit;

  if RenameFile(StashedExe, StashedSrc) then
    Log('安装未完成，已把原主程序还原：' + StashedSrc)
  else
    Log('原主程序还原失败，请手动把 ' + StashedExe + ' 改回 ' + StashedSrc);
end;
