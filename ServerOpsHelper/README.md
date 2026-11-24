# ServerOpsHelper（服务器运维助手）

基于 **WPF + ModernWpfUI + WebView2** 的 Windows 桌面运维工具，用于集中管理服务器、执行远程命令、控制本地服务，并支持通过内置浏览器免密登录管理后台。

## 功能特性

| 功能 | 说明 |
|------|------|
| 🔐 本地登录鉴权 | 启动时登录；账号密码 PBKDF2 哈希存储，默认账号 `admin / admin123`；设置页可改密 |
| 🖥️ 服务器管理 | 增删改查、分组标签、本地 JSON 持久化（`%LOCALAPPDATA%\ServerOpsHelper`） |
| 🔒 密码加密存储 | 服务器密码、常用链接密码、密码箱密码统一使用 Windows DPAPI（当前用户范围）加密后落盘 |
| 💻 远程命令执行 | 基于 SSH.NET 在 Linux 服务器上执行命令并回显输出（支持 Windows 服务重启） |
| ⌨️ SSH 交互终端 | 服务器详情页即**真终端**：SSH.NET ShellStream（PTY）+ WebView2 里的 xterm.js。持久会话（不再一条命令一连）、ANSI 颜色、vim/htop、Tab 补全、上下键历史、Ctrl+C、窗口尺寸同步（stty）；资源全部本地内嵌，离线可用 |
| 📁 SFTP 文件管理 | 服务器页下半部分是文件面板：目录浏览（名称 / 大小 / 类型 / 修改时间 / 权限）、上传、下载、重命名、新建文件夹、删除（**删除文件夹是递归的，必须二次确认**）。走**独立的 SFTP 连接**，所以传大文件时终端照常能用；终端与文件区之间可拖拽调整高度 |
| 📊 实时监控 | 服务器页左侧栏：主机名 / 系统 / 运行时长、CPU 使用率（带进度条）与 1·5·15 分钟负载、内存与根分区占用、网络上下行速率。每 3 秒刷新，也是独立连接。数据取自 `/proc` 与 `df`（**不依赖 top / free / vmstat 是否安装**，最小化系统也能用）；CPU 使用率与网络速率靠两次采样算差值，所以首帧显示「—」 |
| 🔄 Windows 服务控制 | 输入服务名即可重启本地服务（含二次确认） |
| 🔗 常用链接 | 保存经常打开的网页后台；按「置顶 → 打开次数」排序，支持内置浏览器打开 / 系统浏览器打开 / 一键复制登录信息 |
| 🛡️ 宝塔专用链接 | 按「服务器 IP + 端口 + 安全入口」一键生成面板地址；打开时自动填充账号密码，也可整段复制登录信息 |
| 🗂️ 访问记录 | 本次打开应用后访问过的页面 + 全部历史（最多 300 个地址）；可搜索、单条删除、清空，也能一键转存为常用链接 |
| 🔑 密码箱 | 左侧列表 + 右侧详情的双栏记录本；支持分类、搜索、密码显隐、一键复制、随机强密码生成、密码强度提示 |
| 🕵️ 登录信息自动捕获 | 内置浏览器里手动登录一次后，自动询问是否把账号密码收进密码箱（同一站点不会反复追问） |
| 🤖 智能体接入（MCP） | 把已保存的服务器暴露成 **MCP 服务**，本机智能体（Claude Desktop / CodeBuddy / Cline 等）可直接查询与操作多台机器。支持 **stdio**（`--mcp-stdio`，推荐）与**本地 HTTP**（127.0.0.1 + Bearer 令牌）两条传输；10 个工具（列机器 / 查详情 / 采指标 / 列目录 / 读文件（分页） / 执行命令 / 批量执行 / 写文件（覆盖或追加） / 本机↔远端文件与目录传输）。密码永不出现在返回里；变更类工具默认关闭；每次调用写审计日志 |
| 🧩 扩展包（加功能不改代码） | 在 `extensions\` 放一个 JSON 就能加命令：填模板 + 参数，命令出现在服务器详情页顶部的下拉框，同时自动变成 MCP 工具给智能体用。`${参数}` 自动做 shell 引用，`writeAccess=false` 的只读命令智能体不用授权就能调；单个扩展写坏了只影响自己，不影响启动 |
| 🚇 FRP 穿透 | 内网穿透管理：frpc/frps 一键启停、转发规则增删改、服务器与面板配置（含 TOML 文本模式）、多套配置档案、实时日志（按级别着色）、frp 版本检测与一键更新。移植自 FrpManager，配置与它**完全共用**（`%APPDATA%\FrpManager`） |
| 🌐 内置浏览器（常驻） | 顶部**标签页**：打开过的页面各占一格，点一下切换、× 关闭；标签变多自动变窄（92~220 DIP）、再多横向滚动；切到其它功能页只折叠不销毁，网页与标签都不丢 |
| 🧭 侧边栏「最近访问」 | 左侧导航栏最下方列出最近打开过的网页，点一下直接切换过去 |
| 🌙 深色 / 浅色主题 | 设置页切换并持久化，重启后自动恢复 |

## 技术栈

- .NET 9 / WPF（`net9.0-windows`）
- [ModernWpfUI](https://github.com/Kinnara/ModernWpf) 0.9.6（Fluent 风格控件）
- [WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)（Microsoft.Web.WebView2 1.0.4078.44）
- [SSH.NET](https://github.com/sshnet/SSH.NET) 2025.1.0（远程命令执行）
- System.ServiceProcess.ServiceController（Windows 服务控制）
- Windows DPAPI（`System.Security.Cryptography.ProtectedData`，凭据加密）

## 项目结构

```
ServerOpsHelper/
├── App.xaml(.cs)               # 应用入口：恢复主题与开关 → 登录 → 主界面
├── MainWindow.xaml(.cs)        # 主窗口：侧边栏导航 + 常驻内置浏览器 + 「最近访问」列表
├── Models/
│   ├── ServerInfo.cs           # 服务器数据模型
│   ├── LinkItem.cs             # 常用链接（常用网页 / 宝塔面板）数据模型 + 地址拼接
│   ├── VaultItem.cs            # 密码箱条目 + 密码强度评估
│   ├── VisitRecord.cs          # 访问记录（含相对时间文案）
│   ├── UrlHelper.cs            # 站点归属判断（scheme://host:port）
│   ├── AppSettings.cs          # 应用设置模型（主题、捕获开关、记录开关）
│   └── ExtensionPack.cs        # 扩展包清单模型（扩展 / 命令 / 参数）
├── ViewModels/                 # INPC：服务器 / 常用链接 / 密码箱 / 访问记录 / 浏览器标签 + 下拉条目模型
├── Views/
│   ├── LoginWindow.xaml(.cs)   # 登录窗口
│   ├── ServerListPage.xaml(.cs)# 服务器列表页（搜索、增删改、连接）
│   ├── ServerEditDialog.xaml(.cs) # 服务器编辑对话框
│   ├── ServerDetailPage.xaml(.cs) # 服务器详情 + SSH 命令执行
│   ├── BookmarksPage.xaml(.cs) # 常用链接页（网页 + 宝塔）
│   ├── LinkEditDialog.xaml(.cs)# 链接编辑对话框（含宝塔字段与地址生成）
│   ├── HistoryPage.xaml(.cs)   # 访问记录页（本次访问条带 + 历史列表）
│   ├── VaultPage.xaml(.cs)     # 密码箱页（列表 + 详情）
│   ├── VaultEditDialog.xaml(.cs)  # 密码记录编辑对话框
│   ├── CredentialCaptureWindow.xaml(.cs) # 捕获登录信息后的保存确认窗口（独立窗口，见下）
│   ├── TextPromptWindow.xaml(.cs) # 通用单行文本输入窗口（当前无调用方，保留给后续功能）
│   ├── ExtensionRunDialog.xaml(.cs)  # 扩展命令参数输入（控件按参数定义动态生成）
│   ├── ExtensionResultWindow.xaml(.cs) # 扩展命令执行结果（只读文本框 + 复制全部）
│   ├── FrpPage.xaml(.cs)      # FRP 穿透（概览 / 规则 / 服务器配置 / 日志 四个页签）
│   ├── WebConsolePage.xaml(.cs)   # 内置浏览器（UserControl，常驻实例）
│   ├── SettingsPage.xaml(.cs)     # 系统设置（主题、默认地址、捕获/记录开关、改密）
│   └── Dialogs.cs              # 统一的确认 / 提示对话框封装
├── Installer/                  # 打包脚本：release_all.py（一键出全套）+ Package.wxs
│                               #   + 生成的 Files.wxs + build_installer.py（只打 MSI）
├── Assets/                     # 应用图标：appicon.ico（多尺寸）+ make_icon.py（生成脚本）
│                               #   + appicon-256.png + preview.png（多尺寸对照图）
├── 使用说明.txt                # 面向使用者的说明，随 build / publish 一起输出
├── ../../Probe/                # 验证探针（工作区里的兄弟目录）：XAML 加载 + 界面行为断言
├── Controls/
│   └── SecretBox.xaml(.cs)     # 密码输入控件（遮挡 / 明文一键切换）
├── Services/
│   ├── ConfigService.cs        # JSON 配置读写（servers / links / vault / history / settings）
│   ├── CredentialService.cs    # DPAPI 加解密（幂等，带 DPAPI: 前缀）
│   ├── PasswordGenerator.cs    # 基于 RNGCryptoServiceProvider 的强密码生成
│   ├── ClipboardService.cs     # 剪贴板封装（重试 + 不抛异常）
│   ├── AuthService.cs          # 账号认证（PBKDF2 哈希）
│   ├── VisitHistoryService.cs  # 访问记录：本次会话（内存）+ 历史（落盘）
│   ├── ShellNavigation.cs      # 外壳导航入口，供各页复用常驻的内置浏览器
│   ├── RemoteExecutionService.cs # SSH 远程命令执行
│   ├── ExtensionService.cs     # 扩展包加载、校验、参数转义与执行（清单式插件）
│   └── ServiceControlService.cs  # Windows 服务控制
├── Frp/                        # 内网穿透（移植自 FrpManager，去掉 HTTP 层）
│   ├── FrpModels.cs            # 代理规则 / frpc / frps 配置模型
│   ├── FrpToml.cs              # TOML 解析与序列化（语法损坏时按行降级，避免一保存就清空）
│   ├── FrpEnvironment.cs       # 多套配置档案 + frp 目录发现（%APPDATA%\FrpManager）
│   ├── FrpProcess.cs           # frpc / frps 子进程管理与日志转接
│   ├── FrpState.cs             # 运行状态 + 环形日志队列
│   ├── FrpInstaller.cs         # 版本检测与官方包下载安装
│   └── FrpService.cs           # 门面：懒初始化 + 退出收尾
├── Mcp/                        # 智能体接入（MCP 服务端），见下方专节
│   ├── McpServerCore.cs        # JSON-RPC 协议核心：initialize / tools list + call / 版本协商
│   ├── McpToolCatalog.cs       # 10 个工具的 JSON Schema 与读写属性
│   ├── McpToolExecutor.cs      # 工具实现：SSH 执行、SFTP 读写、指标采集、服务器解析
│   ├── McpStdioHost.cs         # stdio 传输（--mcp-stdio，无界面模式）
│   ├── McpHttpHost.cs          # 本地 HTTP 传输（TcpListener，127.0.0.1 + Bearer）
│   ├── McpRuntime.cs           # 启停门面 + 客户端配置生成
│   ├── McpTokenStore.cs        # 访问令牌（DPAPI 加密落盘）
│   └── McpAuditLog.cs          # 调用审计日志（logs/mcp-audit-*.log）
└── Converters/                 # 类型 → 图标转换器
```

## 快速开始

```bash
# 需要 .NET 9 SDK
dotnet restore
dotnet build
dotnet run
```

首次登录使用默认账号 `admin / admin123`（该账号仅在首次登录时自动创建）。

## 智能体接入（MCP）

把「已保存的服务器」暴露成 MCP 服务，让本机的 AI 智能体直接管理多台机器。
**没有引入任何 MCP SDK**：协议本身就是 JSON-RPC 2.0 的六个方法，手写比拖一个依赖更可控
（也不用为了一个可选的接入功能给主程序加一堆包）。

### 两条传输

| 传输 | 启动方式 | 适用场景 |
|------|----------|----------|
| **stdio**（推荐） | 客户端以 `ServerOpsHelper.exe --mcp-stdio` 拉起子进程，一行一条 JSON-RPC | 桌面型客户端（Claude Desktop / CodeBuddy / Cline）。不需要端口和令牌，子进程继承当前用户权限，DPAPI 凭据可直接解 |
| **本地 HTTP** | 设置页开关，`TcpListener` 绑 `127.0.0.1:<端口>`，`POST /mcp` | 客户端不支持拉子进程；或想复用正在运行的那一份程序 |

`--mcp-stdio` 走 `App.OnStartup` 的**无界面分支**：不登录、不建窗口、不弹任何 MessageBox
（弹窗会挂住客户端），stdin 关闭即退出。stdout 是协议通道，诊断信息一律走 stderr。

HTTP 侧没选 `HttpListener`：它对 URL 前缀有 ACL 要求，非管理员在某些机器上直接"拒绝访问"。
手写几百行 HTTP（含 `Content-Length` 与 `chunked`）反而行为更可预测，还顺带支持了
`Accept: text/event-stream` 时按 SSE 帧返回。

### 工具

内置 10 个；用户扩展里的命令会以 `ext_<扩展>_<命令>` 追加在后面（见「扩展包」一节），
数量随装了哪些扩展变化。

| 工具 | 变更类 | 说明 |
|------|:------:|------|
| `list_servers` | | 列出已保存服务器，支持 `tag` / 关键字过滤 |
| `get_server` | | 单台详情（不含密码） |
| `get_metrics` | | 实时指标（复用 `RemoteMonitorService` 的采集脚本与解析，连采两次算 CPU / 网络速率） |
| `list_files` | | SFTP 列目录 |
| `read_file` | | 读远程文本，按字节 `offset` 分页（默认 64 KiB/页，上限 1 MiB），返回 `nextOffset` 续读；页边界不会切断 UTF-8 汉字 |
| `exec_command` | ✓ | SSH 执行命令，双层超时（`CommandTimeout` + 外层 `WhenAny` 硬兜底） |
| `exec_command_on_servers` | ✓ | 多台并发执行同一条命令，按 `servers` 或 `tag` 选目标，带并发上限 |
| `write_file` | ✓ | SFTP 写远程文本，`mode=overwrite`（默认）或 `append`（分块拼装大文件） |
| `upload_file` | ✓ | 本机文件/目录 → 远端，SFTP 流式传输：内容不进 JSON，无 8 MiB 体积上限，支持二进制、递归整目录、自动建远端目录 |
| `download_file` | ✓ | 远端文件/目录 → 本机，SFTP 流式下载，支持递归整目录；目录软链跳过以免成环 |

传输类工具（`upload_file` / `download_file`）直接在磁盘与 SFTP 之间流式拷贝，不经过 JSON 编解码，
所以不再有"按块切分再从别处拼回来"的问题；单条失败只记进 `failures`，不会中断整批。
代价是它们会读写本机磁盘，因此和写远程文件一样归入变更类，需要显式授权。

服务器寻址统一为"GUID → 名称精确 → 主机精确 → 名称唯一包含"，重名或歧义时明确报错让模型改用 id。

### 安全设计

1. **密码永不外泄**：返回体是**显式字段拼接**的 JSON，绝不序列化 `ServerInfo`
   （否则以后加个敏感字段就会自动泄漏）。探针里用假密码 `PROBE-SECRET-9f3a` 断言所有输出与审计日志都不含它。
2. **写操作默认关闭**：`AppSettings.McpAllowWriteTools` 默认 `false`，变更类工具直接返回
   "去设置里打开"的提示。只读能力不需要授权。
3. **HTTP 只绑 127.0.0.1**，请求必须带 256 位随机 Bearer 令牌（DPAPI 加密存 `mcp-token.txt`），
   定长比较；不给"改成 0.0.0.0"的开关。
4. **全量审计**：每次工具调用记 `logs/mcp-audit-YYYYMMDD.log`（时间 / 传输 / 工具 / 目标 / 参数摘要 / 耗时）。

### 验证

- `Probe/Program.cs` 的 `RunMcpChecks` / `RunMcpHttpChecks`：协议协商、工具清单、权限闸门、
  密码不外泄、令牌加密落盘、HTTP 401/404/405/413/SSE、端口释放、设置页回填、分页边界（共 80 项）。
- `Probe/mcp_stdio_smoke.py`：**真起进程**喂 JSON-RPC（`python mcp_stdio_smoke.py <exe>` 可测发布版），27 项。
- `Probe/Program.cs` 的 `RunExtensionChecks`：扩展加载 / MCP 合并 / 只读与变更标记 / **参数转义的注入用例** /
  错误清单只影响自己 / 服务器页下拉框出现与隐藏 / 界面执行链路（19 项）。
- `Probe/mcp_extension_smoke.py`：**真起进程**造一个扩展，验证 `tools/list` 合并、只读命令绕过闸门、
  变更命令被拒、int 越界在连 SSH 之前就被拦、审计记到扩展工具（16 项）。

## 扩展包（插件式管理）

扩展 = 给程序加命令的插件，不用改代码、不用重新打包。装好后：

- **界面上**：服务器详情页顶部出现「扩展动作」下拉框，选中 → 填参数（如有）→ 执行，
  结果在新窗口里只读展示，可一键复制；
- **智能体那边**：同一条命令自动变成 MCP 工具，名字是 `ext_<扩展id>_<命令id>`。

### 「设置 → 扩展」能做什么

| 操作 | 说明 |
|------|------|
| 浏览推荐扩展 | 内置三套常用动作（Nginx 维护 / 系统巡检 / 日志排查），点「安装」即装 |
| 安装扩展包 | 选 `.zip`（内含 `extension.json`）或单个 `.json` 清单 |
| 停用 / 启用 | 只切开关、不删文件；停用后命令从界面与智能体清单里同时消失 |
| 卸载 | 删掉该扩展的目录/文件（二次确认） |
| 打开文件夹 | 定位到它的 `extension.json`，方便自己改 |

停用状态记在 `extensions/.state.json`，**不改**你写的清单文件。

### 扩展包怎么打包

```
部署与巡检.zip
└── extension.json          ← 也可以是 部署与巡检/extension.json（一层子目录）
```

zip 里有多套扩展会被拒绝（一次装一个）；带 `../` 的条目会被拦下（防 zip slip）。

### 落盘形式

```
extensions/
  deploy/extension.json     ← 目录形式（可以带脚本、说明等附件）
  tail.json                 ← 单文件形式
  _草稿.json                ← _ 或 . 开头会被跳过
  .state.json               ← 停用名单（程序写，别手改）
```

### 最小清单

```json
{
  "id": "deploy",
  "name": "部署与巡检",
  "version": "1.0.0",
  "description": "我自己的常用动作",
  "commands": [
    {
      "id": "tail_log",
      "name": "看日志",
      "description": "看指定日志的最后若干行",
      "command": "tail -n ${lines} ${path}",
      "writeAccess": false,
      "timeoutSeconds": 30,
      "parameters": [
        { "name": "lines", "type": "int", "default": "100", "min": 1, "max": 5000, "description": "行数" },
        { "name": "path", "type": "path", "default": "/var/log/nginx/error.log", "description": "日志文件" }
      ]
    },
    {
      "id": "reload_nginx",
      "name": "重载 Nginx",
      "description": "先测配置，通过后平滑重载",
      "command": "sudo nginx -t && sudo systemctl reload nginx",
      "writeAccess": true
    }
  ]
}
```

| 字段 | 说明 |
|------|------|
| `id`（扩展/命令） | 小写字母/数字/`_`/`-`，≤ 32 字符；拼出来的工具名 `ext_<扩展>_<命令>` 不能超过 64 |
| `enabled` | 扩展级开关，`false` 时命令既不出现也不暴露给智能体 |
| `command` | shell 模板，在 Linux 服务器上执行 |
| `writeAccess` | `true` = 变更类，智能体调用需要用户打开「允许智能体执行变更操作」；`false` = 只读，直接可调 |
| `exposeToAgent` | 默认 `true`；只给自己点的命令设 `false` |
| `timeoutSeconds` | 默认 60，会夹到 5~600 |
| `parameters[].type` | `string` / `int` / `bool` / `path`；`int` 支持 `min`/`max`；**没有 `default` 就是必填** |

### 三条安全约定（改代码前先读）

1. **`${参数}` 替换时自动做 shell 引用**：包单引号，内部 `'` 转义成 `'\''`，并拒绝换行与控制字符。
   少了这一步，一个带 `; rm -rf /` 的参数就能绕开写权限闸门在服务器上执行任意命令。
   **模板里不要再自己加引号**（否则会变成 `"'值'"`，多一对引号）。
2. **不加载任何外来代码**：没有 DLL、没有 `Assembly.Load`，扩展只是拼命令走既有 SSH 通道 ——
   写权限闸门、审计日志、双层超时全部自动继承，不需要另建一套安全模型。
3. **坏扩展只影响自己**：解析失败会记进「设置 → 扩展」的红色说明，程序照常启动、其余扩展照常可用。

## FRP 穿透（移植自 FrpManager）

左侧导航「FRP 穿透」是一个完整的 frp 管理界面。原项目 `frpmanager-web`
（ASP.NET Core Minimal API + Vue 3，独立 exe + 内置 WebView2）的**业务逻辑被搬成 WPF 原生页**：

- **没有 HTTP 层、没有独立进程**：UI 直接调同一套 C# 类（TOML / 进程 / 档案），
  少一层服务、少一个端口，也不会出现"FrpManager 在跑、界面在另一个进程"的错位。
- **配置完全共用**：读写 `%APPDATA%\FrpManager` 的同一批 profiles 与 `state.json` ——
  以前在 FrpManager 里配好的服务器、规则、frp 目录，切过来直接能用，两个程序可以交替使用。

### 四个页签

| 页签 | 内容 |
|------|------|
| 概览 | 运行状态（模式 / PID / 运行时长 / 配置文件）、待启动配置摘要（目标、规则数）、FRP 环境（目录校验 + 自动定位）、frp 版本检测与一键更新 |
| 转发规则 | 表格（名称 / 类型 / 本地服务 / 远端映射）+ 右侧编辑面板；支持 tcp / udp / http / https / tcpmux，端口型填远程端口、域名型填域名（逗号分隔） |
| 服务器配置 | frpc 表单（地址 / 端口 / Token / 传输协议）或 frps 表单（监听 + 面板账号），可切「文本模式」直接编辑原始 TOML |
| 运行日志 | 实时日志，按级别着色（系统蓝 / 普通灰 / 错误红），可清空、可关自动滚动 |

传输协议支持 TCP / KCP / QUIC / WebSocket / WSS 五种。

### 行为约定（改动前先看）

1. **保存策略**：规则改动**立即写盘**；服务器表单点「保存配置」才写；点「启动」前会先替用户把表单落一次盘 ——
   否则"改了没保存"会起成旧配置，很难自己发现。
2. **退出会停进程**：关闭 OpsHelper 时，正在跑的 frpc/frps 会被停掉。不停的话它会变成没人管的孤儿进程，
   下次打开界面也看不到、更停不掉（原 FrpManager 是常驻托盘，语义不同）。
3. **TOML 容错**：解析失败时降级按行提取关键字段（`serverAddr` / `serverPort` / token 等），
   避免"文本有语法错 → 解析成空配置 → 一保存把用户内容清空"。文本模式保存前也会先解析一遍。
4. **不写坏别人的档案**：档案的新建 / 重命名 / 删除都用 `FrpEnvironment` 的原实现（含"至少保留一个"、
   删除当前档案后自动切到第一个可用档案）。
5. **frp 二进制不随包分发**（frpc 16.7M + frps 20.5M，占安装包一半体积）：
   第一次用点「安装 / 切到稳定版」自动下载到 `%APPDATA%\FrpManager\frp\windows-amd64`。
   开发机或预置环境想免下载，把两个 exe 放到
   `%LOCALAPPDATA%\ServerOpsHelper\components\frp\win-x64\` —— 自动定位会认这个位置
   （候选顺序：程序目录旁的 `frp\` → 组件目录 → 安装器的下载目录 → PATH）。

> frp 路径记在 `%APPDATA%\FrpManager\state.json`；换机器/换构建导致路径失效时，
> 下次打开会自动重新定位，不需要手动改配置。

## 打包与发布

### 应用图标

设计是「蓝色圆角底 + 终端提示符 `>` + 青色光标」，正对本应用的旗舰功能（内置 Web 控制台），
16×16 下依然认得出。几何用归一化坐标描述，每个尺寸都按目标尺寸**原生绘制**再降采样，
不是从一张大图缩下去的 —— 小尺寸的清晰度就是这么来的。

```bash
python Assets/make_icon.py     # 重新生成 appicon.ico / appicon-256.png / preview.png
```

接入点共三处，改图标后只要重跑生成脚本再重新出包即可：
- `csproj` 的 `<ApplicationIcon>`：嵌入 EXE 的 Win32 资源（资源管理器 / 任务栏用）；
- `csproj` 的 `<Resource Include>` + 各窗口的 `Icon="/Assets/appicon.ico"`：窗口标题栏；
- `Installer/Package.wxs` 的 `<Icon Id="AppIcon">` + `ARPPRODUCTICON` + 快捷方式 `Icon`：
  「设置 → 应用」的卸载条目与开始菜单/桌面快捷方式。

### 一键出全套（推荐）

```bash
cd ServerOpsHelper/Installer
python release_all.py
```

按顺序做完：重新发布 → 打单文件安装程序（Inno）→ **装/卸回归**（临时目录，失败即阻断）
→ 打便携包 zip → 刷新已解压的便携版文件夹 → 清理中间产物。
版本号唯一来源是 `ServerOpsHelper.csproj` 的 `<Version>`，安装包与便携包都从这里取，
不会出现「脚本里写的版本」和「实际编出来的版本」不一致。

#### 产物落点：按版本号分目录

所有交付物放在 `<工作区>/release/v<版本>/`，**一个版本一个目录**：

```
release/
  v1.5.3/
    ServerOpsHelper-Setup-v1.5.3.exe      单文件安装程序（唯一的安装包交付物）
    ServerOpsHelper-便携版-v1.5.3.zip     便携压缩包
    ServerOpsHelper-便携版-v1.5.3/        同上解压版（可直接双击运行）
```

> **没有中间 MSI、也没有 ICE 校验**（2026-10-17 换成 Inno 之后）：安装包本身就是一个 exe，
> 出包只有「publish → 编译 → 装/卸回归」三步。WiX/MSI 那套脚本与 `.wxs` 仍留在 `Installer/`，
> 但**不再参与交付**，别照着它们改（见下方「安装包」一节）。
> 早先所有产物是平铺在一个「发布」目录里的，于是**出新版必须先删旧版** ——
> 否则同名文件混在一起分不清是哪一版，历史交付物也留不下来。
> 改成按版本号分目录后，出新版不再动旧版，要回溯哪一版都能找到。

清理规则：
- **只删中间产物**（可再生，留着纯占地方）：`bin/Release`、`obj/Release`、`publish/`、
  `Installer/` 下的符号库残留；
- **不删任何历史版本** —— 旧版本的 `release/v<版本>/` 原样保留；
- 若发现工作区根目录还留着旧布局的 `ServerOpsHelper-便携版-v*` 文件夹，
  会先**迁入** `release/v<版本>/`，再清掉根目录那一份（避免两处占空间、还容易改错地方）。

> ⚠️ 发新版本**必须同时升 `Version`**（两处）：`ServerOpsHelper.csproj` 的 `<Version>`
> （安装包名、安装记录里的版本、便携包目录名全从这里取）与 `使用说明.txt` 首行。
> `Installer/ServerOpsHelper.iss` 里的 `1.9.3` 只是 `#ifndef` 兜底值，正常打包用不到，**不用改**；
> WiX 时代的 `Package.wxs` / `Bundle.wxs` 已不参与出包，也不要去改它们。
> Inno 按 `AppId` 认产品、按 `AppVersion` 记版本，同版本重装是覆盖安装，不会出问题。

依赖两个工具（都不在仓库里，路径可用环境变量覆盖）：
`DOTBUILD_PY`（dotnet 包装器，沙箱里靠它注入 Windows 环境变量）与
`ISCC_EXE`（Inno Setup 6 编译器，默认取 `.tools/InnoSetup6/ISCC.exe`，该目录已 gitignore）。

### 便携版（免安装）

```bash
# 自包含（免装 .NET）的 win-x64 便携版，产物在 publish/win-x64
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishReadyToRun=false -p:DebugType=none -o publish/win-x64
```

产物约 135 MB（压缩后约 59 MB），双击 `ServerOpsHelper.exe` 即可运行，无需安装 .NET。
`csproj` 里的 `<SatelliteResourceLanguages>zh-Hans;en</SatelliteResourceLanguages>` 会把 70 多个语言的
附属资源裁掉，只保留中文与英文。`使用说明.txt` 已声明为工程项，会跟着 build / publish 一起出去。

### 安装包（单文件 EXE，Inno Setup 6）

2026-10-17 起用 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 出安装包，**不再有 MSI**。
原因写在 `Installer/ServerOpsHelper.iss` 头部：MSI 的回滚备份目录 `<盘>:\Config.Msi` 的 ACL 只有
SYSTEM + 管理员，非提权的 perUser 安装在收尾时必然「错误 5 / 1926」（实测装 C:/D: 都正常、装 E:\AI 必挂），
而 Inno 编出来就是一个普通 exe —— 解文件、写注册表、生成 `uninstall.exe`，完全不碰 Windows Installer，
`Config.Msi / 1603 / 1926` 这一整类故障从根上消失。

```bash
cd ServerOpsHelper/Installer
python build_inno.py      # 需要先有 publish/win-x64（release_all.py 会先做）
```

- 产物：`release/v<版本>/ServerOpsHelper-Setup-v<版本>.exe`（约 47 MB，lzma2/max 压缩整个 publish 目录）。
- 版本号由 `build_inno.py` 从 `csproj` 读出来，用 `/DMyAppVersion=` 传给 `.iss`，
  避免「脚本里硬编码的版本」和「实际编出来的 exe」漂移。
- 向导**全简体中文**（`compiler:Languages\ChineseSimplified.isl`），流程：
  欢迎 → 安装范围 → **安装位置** → 附加任务 → 准备安装 → 安装中 → 完成。
  安装位置直接显示在页面上，不再是藏在「选项」背后的输入框。

#### 改安装脚本前必须知道的（每条都是踩出来的）

| 约束 | 原因 |
|------|------|
| `AppId` 一旦发布**不能改** | 升级靠它认出「同一个产品」并原地覆盖；改了会变成并存两套、卸载也各管各的 |
| `DisableDirPage=no` **必须显式写** | 默认值 `auto` 会在「检测到已安装」时隐藏选择目录页，观感就是「这包不让我选目录」（v1.9.3 真实发生） |
| `[Files]` 必须保留 `recursesubdirs createallsubdirs` | `TerminalAssets\`（xterm.js）、`runtimes\`、`zh-Hans\` 一旦被拍平到根目录，**安装版的 SSH 终端直接打不开**（MSI 时代踩过） |
| `[UninstallDelete]` 只能删 `{app}` 下的运行产物 | **绝对不能碰 `{localappdata}\ServerOpsHelper`** —— 链接、密码箱、访问历史、登录账号都在那儿，卸载一个字都不能动 |
| `UsePreviousAppDir=yes` 要保留 | 覆盖升级时自动沿用上次装的目录，用户不用每次重选 |
| `CloseApplications=yes` 要保留 | 程序正在跑时由 Restart Manager 关掉它（有窗口的那种），比让用户自己去关省事 |
| **主程序那条 `[Files]` 必须排在最后** | Inno 按节内顺序复制。exe 早于 dll 落地的话，智能体连接器的重启守护会在中途把 `--mcp-stdio` 拉起来、映射住 `clrjit.dll`，后面的 dll 覆盖全撞「拒绝访问 5」。见下方「后台进程抢文件」 |
| **别改成「整个安装目录改名」** | 实测（2026-11-18，Win11/NTFS）进程活着时 `RenameFile` 目录会失败（WinError 5 或 32）；只有**单个文件**能改名。正确顺序是「先改名藏起 exe → 再 taskkill」 |

#### 后台进程抢文件：为什么安装脚本要「先藏 exe 再杀」

智能体客户端（`dsh-mcp-connector` 的 `stdio-startup-guard.js`）会用
`ServerOpsHelper.exe --mcp-stdio` 常驻一个**无窗口**进程，并且带自动重启：杀掉一个，
一秒内又拉起一个。它活着时把 `clrjit.dll` / `coreclr.dll` 映射住，安装程序 `DeleteFile`
覆盖时返回 `ERROR_ACCESS_DENIED(5)`，界面上那句「尝试替换已存在的文件时失败：DeleteFile
失败；错误代码 5。拒绝访问」就是它 —— **不是权限问题**，属主就是当前用户。

`CloseApplications=yes` 管不到这类进程（Restart Manager 只认有窗口的）。所以 `[Code]` 里：

1. `StashAppExe()` 把主程序改名成 `.stale-<时间戳>` —— 进程活着也能改名，改完原路径就空了，
   守护的 `CreateProcess` 直接失败，拉不起来；
2. 再 `taskkill /F`，并轮询 `tasklist` 确认真的退干净（最多 5 秒）；
3. Inno 开始复制，主程序由最后那条 `[Files]` 落地，dll 早已换完，没有冲突窗口；
4. 装完清 `.stale-*`；**安装被取消则 `DeinitializeSetup` 把旧 exe 还原** —— 否则用户双击图标
   会提示找不到文件（这条有专门的用例：`.tools/verify_cancel_restore.py`）。

回归脚本：`.tools/verify_upgrade_with_guard.py` 会起一个和 dsh 一样的重启守护（每 150ms
拉一次），在它不断拉起的条件下跑覆盖升级，断言装完退出码 0、载荷 270/270、主程序是新版、
没有 `.stale-*` 残留。

- 默认 `PrivilegesRequired=lowest` → 装到 `%LOCALAPPDATA%\Programs\ServerOpsHelper`，**不弹 UAC**；
  用管理员账号运行时第一页会多一个「为所有用户安装」（装到 Program Files，需要一次授权）。
- **支持直接覆盖安装（升级）**，不用先卸载旧版：Inno 按 `AppId` 认产品，同目录原地替换。
- **旧 MSI/Burn 版迁移**：`[Code]` 里会扫 HKCU/HKLM 的 `Uninstall` 键找旧记录，问一句要不要先卸掉，
  然后沿用旧目录安装（静默安装默认不迁移，要迁移需显式传 `MIGRATE=1`）。
  卸载旧 MSI 失败不会中断安装，最多留一条旧记录。
- 安装与卸载都**不碰数据目录** —— 数据与程序分离是这个项目的硬约定。

> WiX/MSI 那一套（`Package.wxs`、`Bundle.wxs`、`Files.wxs`、`build_installer.py`、`build_bundle.py`）
> 作为历史保留，**不再参与出包**。里面的坑（ProductCode 必须按版本确定性生成、ICE 校验当门槛、
> WixStdBA 中文化、`InstallFolder` 不能用 `Type="string"`）都还写在文件注释里，需要回溯时再看。

### 出包与回归（一条命令）

```bash
cd ServerOpsHelper/Installer
python release_all.py          # 重新发布 → 打安装程序 → 装/卸回归 → 打便携 zip → 清中间产物
```

任何一步失败都会**返回非零并停下**，不会产出半成品：Inno 编译失败会打印完整输出；
装/卸回归（`Probe/verify_inno_payload.py`）在临时目录里真装真卸，比对「安装出来的相对路径集合」
与「publish 目录的相对路径集合」—— 不是只比文件总数（MSI 时代两边都是 269 个、路径却错了 17 个），
同时断言数据目录**一个文件都没动**。本机已安装同产品时该脚本会自动跳过（否则会覆盖真实安装记录）。

验证脚本都在 `Probe/`（用隔离环境
`~/.workbuddy/binaries/python/envs/default/Scripts/python.exe` 跑）：

| 脚本 | 作用 |
| --- | --- |
| `verify_inno_payload.py <setup.exe> [发布目录]` | **出包流程内置的门槛**：临时目录真装真卸 + 路径集合比对 + 数据目录零改动 |
| `shot_inno_ui.py [安装包]` | 截 Inno 向导的图，确认「中文界面 + 安装路径可见」，产物在 `Probe/shots/inno_*.png` |
| `e2e_matrix.py` / `shot_zh_ui.py` / `verify_msi_payload.py` / `verify_upgrade.py` | WiX/MSI 时代的回归脚本，保留作历史，**不再用于出包** |
| `mcp_stdio_smoke.py [exe]` | **真起进程**跑一遍 MCP stdio 会话（协议 / 工具 / 权限 / 密码不外泄 / 审计），25 项；可指向发布版 exe |
| `Probe/`（C# 项目，`Probe.exe`） | **界面/行为探针，当前 239 项全绿**。它才是这个项目的安全网：XAML 的坑（StaticResource 缺失、图标名非法、pack URI 解析不到）全是编译期全绿、运行期才炸 |

探针里与本轮修复直接相关的几组检查（都写在 `Probe/Program.cs`）：

- `RunToolsDropDownReopenChecks` —— **五个菜单项逐个"开 ☰ → 点菜单 → 再开 ☰"**，
  断言不抛异常且窗口真能弹出；外加"窗口被硬 `Close()` 后能自动重建"。
  原有的 `RunToolsDropDownChecks` 只测了"开 → 关"、**从没点过菜单项**，
  所以那个必崩路径一直没被发现。
- `RunCredentialMergeChecks` —— 12 项覆盖"链接缺哪项用密码箱补哪项"的合并规则。
- `RunVaultEditRoundTripChecks` —— 5 项验证密码箱编辑对话框的密码往返不丢
  （含中文、空密码、引号/反斜杠/换行等特殊字符）。
- `RunRealVaultReadOnlyDiagnosis` —— 只读诊断真实密码箱**只报成败不打印明文**，
  用来确认 DPAPI 密文能否被当前 Windows 账户解开。

**首次打开白屏专项**（`Probe.exe --web-first-open [--pending-path]`）：

- 照 `MainWindow` 的真实时序起一个窗口：页面先以 `Collapsed` 放进去（= 启动后停在服务器页），
  再「切到 Web 控制台 + 立刻打开网址」，然后每秒钟记录一次控件可见性、尺寸、Core 状态、页面内容。
- 判定必须**同时**满足「页面有内容」和「控件 Visible 且尺寸 > 0」。只看内容会漏掉真正的白屏 ——
  页面其实早加载好了，只是控件一直是 `Collapsed / 0×0`。
- `--pending-path` 走的是慢初始化那条路（环境没就绪就先要求打开），
  修复前它 12 秒后仍是 `vis=Collapsed,w=0,h=0`，修复后 1 秒内可见。
- 这类"用户什么都看不到"的问题，状态栏提示会随着后续操作被覆盖，
  所以真实失败原因还会追加到数据目录的 `logs/webview2.log`（环境创建 / 单标签初始化 / 进程崩溃）。

> **做界面/行为改动后，务必"注入验证"一次**：把修复撤掉重跑，确认对应检查真的报 FAIL。
> 2026-09-25 就这么做过：撤掉两处修复后 12 项 FAIL、恢复后 76 项全 OK ——
> 这才能证明测试不是永远通过的摆设。

关于 `shot_zh_ui.py`，有几个"看不见界面"才踩得到的坑，都写在脚本文件头了，摘要：

1. **界面不在父进程**——Burn 的 UI 由独立子进程 `wixstdba.exe` 绘制。
   按父进程 pid 枚举窗口只能拿到一个 0x0 的 `WixBurnMessageWindow`，必须先查子进程 pid。
2. **窗口先建好再画**——刚创建时 `GetWindowRect` 返回 `(0,0,0,0)`，要等宽高都 > 200 才能截图。
3. **ctypes 句柄参数必须声明 `argtypes`**——默认按 32 位 int 处理，hwnd 一大就
   `OverflowError: int too long to convert`；但 `GetWindowRect` 的 rect 参数要写成
   `POINTER(RECT)`，写成 `POINTER(c_void_p)` 会让 `byref(RECT())` 直接被拒。
4. **UIA 必须包在 `auto.UIAutomationInitializerInThread()` 里**——子线程没有 STA，
   直接调用报「尚未调用 CoInitialize」。
5. **点击只能用 UIA 的 `InvokePattern`**——实测 `SetCursorPos` + `SendInput`
   光标确实移到了按钮中心（`GetCursorPos` 验证过），但 wixstdba 的按钮
   不响应合成鼠标事件；只有 UIA Invoke 能真正触发。

> 这五个坑与「WixStdBA 中文化」「不能选安装目录的排查顺序」已抽成用户级技能
> **`wixstdba-ui-verification-windows`**（`~/.workbuddy/skills/`）。
> 下次要验证安装程序界面，直接照那份技能走，不用再从零试错。

> 内置浏览器依赖 **Microsoft Edge WebView2 Runtime**：Win11 与较新的 Win10 已随系统自带，
> 缺失时到 [微软官网](https://developer.microsoft.com/microsoft-edge/webview2/) 装 Evergreen 版即可。

## 使用要点

### 常用链接

- 「新增网页」录入后台地址；「新增宝塔」录入服务器 IP、端口（默认 8888）、安全入口与账号密码。
- 宝塔类型点「由「IP / 端口 / 安全入口」生成地址」即可拼出 `https://IP:端口/安全入口`，也可直接粘贴完整地址。
- 列表按「置顶 → 打开次数 → 最近打开」排序，越常用的越靠前；每次打开都会累加次数。
- 行内「打开」= 应用内置浏览器（复用登录态）；「浏览器」= 系统默认浏览器；「复制」= 整段登录信息。

### 宝塔专用链接

从「常用链接」打开宝塔类型记录时，内置浏览器会在页面加载完成后自动尝试填充账号密码（覆盖 `#username` / `#password` 等常见选择器）。
未命中时可用「复制登录信息」手动粘贴，也可点工具栏的「填充账号密码」手动触发。

### 内置浏览器不丢页面

- 内置浏览器是**主窗口里的常驻实例**：切到别的功能页只是把它折叠起来，实例与 WebView2 都不销毁，
  切回来网页还在原处，不用重新输地址。
- 从「常用链接」「访问记录」「侧边栏最近访问」点开时，都是复用这个常驻实例，而不是新建页面。
- 首次切到该页时才真正创建 WebView2，启动时不会白占内存。

### 浏览器式标签页

- 顶部一排标签，**打开过的页面各占一格**：点标签切换、点标签上的 × 关闭；
  同一地址重复打开会**复用**已有标签，不会开出重复的。
- 标签宽度按数量自动分配：`可用宽度 / 标签数`，夹在 **92 ~ 220 DIP** 之间
  （`BrowserTab.ComputeWidth`，纯函数、探针里有断言）；到下限后再多的靠横向滚动。
- 关掉当前标签会自动切到邻近标签；全部关光了就等你在地址栏或「打开…」里选。
- 地址栏回车 = 在**当前标签**里跳转（和浏览器一致）；从「常用链接」「访问记录」「侧边栏最近访问」
  「打开…」列表打开则**新开一个标签**。页内点击链接跳转时，标签的标题与地址会跟着更新。
- **工具栏只留一行**：`地址栏 + 打开 + ☰`。填充账号密码、打开…、收藏当前页、
  缩放/重新适配全部收进 ☰ 弹出的**竖向下拉菜单**（独立顶层窗口，贴着按钮弹出、
  失焦/Esc 自动关，**完全不占网页空间**）。独立窗口而不是 Popup —— Popup 会被 WebView2 盖住。
- **网页视觉辅助**：工具面板里的「填充后高亮输入框」开关（默认开，存设置）。
  开着时自动填充账号密码会在网页里高亮闪烁被填的输入框（注入样式 + 动画，约 2.3 秒后自动摘掉，
  不残留类名）；关掉则完全无提示。
- 「打开…」按钮在工具面板里，展开另一个内嵌面板（不是浮层，所以不会被网页盖住），
  里面按「本次 / 常用 / 历史」混排最近访问过的 18 条。
- 标签只表示**本次运行**打开着的页面，不落盘；完整历史在「访问记录」页。
- **页内路由（SPA）跟着标签走**：像宝塔这种用 `history.pushState` 换路由的后台，
  从首页点进 docker 页时**不会触发 `NavigationCompleted`**（那是整页加载才有的），
  所以额外订阅了 `SourceChanged` / `HistoryChanged` / `DocumentTitleChanged`
  把地址和标题同步到标签上 —— 否则标签里存的还是入口地址，切走再切回来就回到首页。
  同理，再点同一个常用链接会**按链接认标签**（地址可能已经变成深层地址，按地址匹配会对不上）。

### ⚠️ 这一页的对话框为什么是「独立窗口」而不是 ContentDialog

`wv2:WebView2` 基于 **`HwndHost`**：网页是一个**原生子窗口**，按 Windows 规则永远画在窗口内的 WPF 内容之上。
而 ModernWpf 的 `ContentDialog` 是**画在窗口内部的浮层** —— 用它就会得到「对话框跑到网页后面、只剩标题露出来」。
所以这一页的提示一律用独立顶层窗口（`CredentialCaptureWindow` / `TextPromptWindow`），
「收藏当前页」干脆不弹框、直接收藏。

排查这类问题别靠猜：
1. 弹对话框前后枚举本进程顶层窗口（`EnumWindows` + `GetClassName`），列表没变 ⇒ 它是窗口内浮层；
2. 想靠 `Visibility = Hidden/Collapsed` 藏网页是**没用的**（实测原生窗口的可见性与尺寸都不变）；
3. `ShowWindow(hwnd, SW_HIDE)` 会立刻生效，但**几轮布局后就被框架抢回可见**。

将来想彻底根治（连所有浮层一起），正解是把网页控件换成 `WebView2CompositionControl`
（渲染进 WPF 视觉树、没有原生子窗口），但它要求目标框架写成 `net9.0-windows10.0.17763.0`，
否则运行期会缺 `Microsoft.Windows.SDK.NET`。属构建配置级改动，要单独评估与实测。
`shellcheck` 探针里已加了约束断言，防止有人改回 ContentDialog。

### 访问记录

- **本次访问**：只存在内存里，关掉应用即清空（上限 30 条），在「访问记录」页顶部成条展示。
- **历史记录**：落盘到 `history.json`，同一个地址只留一条，累加次数并刷新时间，最多 300 个地址。
- 每条记录都能「打开」（内置浏览器）/「收藏」（转存为常用链接）/「删除」，页面右上角可清空全部历史。
- 可在「系统设置 → 密码箱与访问记录」里关掉记录开关，关闭后不再写入新记录。

### 密码箱智能捕获

- 在内置浏览器里**手动**登录（提交表单 / 回车 / 点「登录」类按钮）时，会自动询问是否把账号密码收进密码箱。
- 三重拦截避免打扰：选过「不再提示此站点」（本次运行内）、同站点同账号已问过、密码箱里已有该站点的记录 → 都不再弹窗。
- 保存的来源地址取自 WebView2 上报的真实页面地址，不采信页面自己填的 URL。
- 没有关联链接时，工具栏「填充账号密码」会按当前站点去密码箱里找匹配记录来填充。
- 可在「系统设置 → 密码箱与访问记录」里整体关掉这个提示。

### 密码箱

- 左侧选中记录，右侧查看 / 复制账号、密码、备注。
- 密码默认遮挡显示，点「显示」切明文；切换记录时会自动重新遮起来。
- 编辑对话框里的「生成」按钮产出 16 位随机强密码，并保证同时包含大小写字母、数字与符号。
- **「填充账号密码」的取值规则（2026-09-25 修正）**：先取当前页面关联的「常用链接」，
  链接里**缺的那一项**（多数情况是密码）自动去密码箱里**同站点**的记录补齐。
  - 为什么要补：常用链接和密码箱是两处独立存储。实际数据里就存在"链接是早先建的、
    只填了账号，后来在页面里手动登录时密码被捕获进了密码箱"这种情况 ——
    原实现是"链接只要有账号就整条只用链接"，于是密码恒为空串，
    填充脚本里 `if (!value) { return 0; }` 直接跳过密码框，用户看到的就是"填充失败"。
  - 合并规则抽成了纯函数 `WebConsolePage.MergeCredential`，由探针单独覆盖（12 项）。
  - 宝塔链接的**自动填充**走同一条规则；触发条件也不再看 `link.HasCredential`
    （那正好把"链接只有账号"这种半成品状态排除掉了）。

## 稳定性设计

### 全局异常兜底（2026-09-25 新增）

`App.OnStartup` 里最先挂三层处理器，因为**在此之前应用没有任何全局兜底** ——
任何从事件处理器（尤其 `async void`）、绑定回调里逃逸的异常都会让进程直接退出，
用户看到的就是"点了个按钮软件就没了"，既没提示也没日志。

| 处理器 | 行为 |
| --- | --- |
| `DispatcherUnhandledException` | UI 线程异常：记日志 + 弹提示 + **`e.Handled = true` 拦下**，应用继续运行（弹窗限 3 次防刷屏） |
| `AppDomain.UnhandledException` | 非 UI 线程致命异常：拦不住进程退出，但留下现场 |
| `TaskScheduler.UnobservedTaskException` | 被丢弃的 Task 异常：记日志 + `SetObserved()`，避免它在 GC 时升级成进程级崩溃 |

崩溃日志写到 `%LOCALAPPDATA%\ServerOpsHelper\logs\crash-YYYYMMDD.log`，
写日志本身用最外层 `try/catch` 包死（它跑在异常处理器里，再炸一次就没有兜底了）。

### 两个值得记住的坑

1. **已关闭的 `Window` 不能再 `Show()`**。
   `ToolsDropDownWindow` 的菜单项原来是 `Close()`（真关闭），而控制台页把窗口缓存在
   `_toolsDropDown` 字段里、只在为 null 时新建 —— 于是下次点 ☰ 会对一个已关闭的窗口调
   `Show()`，抛 `InvalidOperationException: ...after a Window has closed` 并让应用退出
   （2026-09-25 事件日志里抓到的真实崩溃）。
   现已改为：菜单项一律走 `NotifyToolsDropDownClosed()`（内部 `Hide()`）；
   同时订阅 `Closed` 把字段清空做兜底，将来有人再写 `Close()` 也只会退化成"下次重建窗口"。
2. **`WebView2.CoreWebView2` 在浏览器进程崩溃后抛异常，而不是返回 `null`**。
   抛的是 `InvalidOperationException: The WebView control is no longer valid because the
   browser process crashed`，所以全项目 19 处 `CoreWebView2 == null` 判断**一个都拦不住**。
   浏览器进程崩溃在真实使用中会发生（内存紧张、页面自身崩溃、GPU 驱动异常）。
   现已统一走 `TryGetCoreWebView2()`（崩了返回 `null` 并短路后续访问），
   并订阅 `CoreWebView2.ProcessFailed` 把状态说清楚。

### WebView2 数据目录跟随配置目录

`WebView2Data` 建在 `ConfigService.ConfigDirectory` 下，而不是写死
`%LOCALAPPDATA%\ServerOpsHelper`。没设环境变量时路径完全不变（老用户登录态照旧），
但探针 / 自动化用 `SERVEROPSHELPER_CONFIG_DIR` 隔离时不会再去**和用户正在运行的实例
抢同一个 WebView2 数据目录** —— 抢的后果是浏览器进程起不来、构造卡死到超时
（2026-09-25 探针就是这么卡住的）。

## 数据文件

所有配置保存在 `%LOCALAPPDATA%\ServerOpsHelper\`：

| 文件 | 内容 |
|------|------|
| `servers.json` | 服务器列表（密码字段为 DPAPI 密文，带 `DPAPI:` 前缀） |
| `links.json` | 常用链接（宝塔 / 网页，密码字段为 DPAPI 密文） |
| `vault.json` | 密码箱记录（密码字段为 DPAPI 密文） |
| `history.json` | 访问历史（最多 300 个地址） |
| `settings.json` | 应用设置（主题、默认后台地址、登录信息捕获开关、访问记录开关） |
| `users.json` | 账号密码哈希（PBKDF2 + 随机盐） |
| `WebView2Data/` | WebView2 用户数据目录（登录态与缓存） |
| `logs/` | 出错记录 `crash-日期.log`（只在出错时产生，可随时删） |

### 数据目录与安全网

- **安装时就会创建**：安装包会把这个目录建出来（`CreateFolder`），
  装完就能直接看到数据存在哪，不用等第一次运行。
  ⚠️ 但它**刻意没有登记 `RemoveFolder`** —— 卸载时不会按组件规则删它。
  实测（2026-09-29，见 `Probe/verify_datafolder.py`）：
  **目录里有数据时，安装与卸载都逐字节不变**（1116 个文件 + `vault.json` md5 全部匹配）；
  目录为空时，卸载会被 MSI 顺手清掉（`CreateFolder` 的标准反向行为，无害，下次安装还会再建）。
- **可重定向**：设置环境变量 `SERVEROPSHELPER_CONFIG_DIR` 即可把上述目录整体搬到别处，
  数据随程序走（真·便携），自动化测试也靠它隔离，绝不会碰到真实数据。
- **每次覆盖写都会留一份上一版** `xxx.json.bak`：配置是全量覆盖式写入，
  万一写进去的内容不对（程序缺陷、误操作、外部脚本），把 `xxx.json.bak` 改回 `xxx.json` 就能恢复。
- 账号文件 `users.json` 与其它配置**共用同一个目录**，跟着上面那个环境变量一起走。

> 注意：DPAPI 密文绑定当前 Windows 用户，迁移到其他机器或账户后需重新录入密码。
> 配置文件写入采用「先克隆快照再加密」的方式，内存中的对象始终保持明文，不会出现保存后界面上密码变成密文的情况。

## 后续规划

- [ ] 服务器批量导入/导出（CSV）
- [x] 命令模板库（常用运维命令一键执行）→ 已由「扩展包」实现（v1.11.0）
- [ ] 连接状态检测（Ping / 端口探测 / 资源监控）
- [ ] 操作审计日志
- [ ] 双因素认证（2FA）
- [ ] 密码箱主密码 + 自动锁屏
