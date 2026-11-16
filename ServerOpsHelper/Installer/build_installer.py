"""按发布产物重新生成 Files.wxs，并调用 wix 打出 MSI 安装包。

用法（先做过 dotnet publish）：
    cd ServerOpsHelper/Installer
    python build_installer.py

产物：<工作区>/release/v<版本>/ServerOpsHelper-Setup-v<版本>.msi
"""
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
WORKSPACE = os.path.dirname(PROJECT)

PUBLISH_DIR = os.path.join(PROJECT, "publish", "win-x64")
FILES_WXS = os.path.join(HERE, "Files.wxs")
PACKAGE_WXS = os.path.join(HERE, "Package.wxs")

# 应用图标：给「设置 → 应用」的卸载条目与快捷方式用（由 Package.wxs 的 $(IconFile) 引用）
ICON_PATH = os.path.join(PROJECT, "Assets", "appicon.ico")

WIX = os.environ.get(
    "WIX_EXE",
    os.path.join(os.path.expanduser("~"), ".workbuddy", "tools", "wix5", "wix.exe"),
)

# WiX 扩展版本，与 wix.exe 5.0.2 配套，**必须显式写死在 -ext 参数里**。
#
# 为什么（2026-09-29 踩的坑）：
#   以前只写 `-ext WixToolset.UI.wixext`，让 wix 自己去全局扩展目录
#   （~/.wix/extensions）挑。平时没问题，但一旦那里混进别的版本 ——
#   本次就是被装进了 WiX 6 的 6.0.2（包结构是 wixext6，wix 5 认不出来，
#   在 extension list 里显示为 "6.0.2 (damaged)"）——
#   wix 会**只挑最新的 6.0.2 去试**，试不通就直接报
#   「error WIX0144: The extension could not be found」，
#   连已经装好的 5.0.2 都不再看一眼（报错里的 Checked paths 只有 6.0.2 那个路径）。
#   结果就是：环境里明明有可用的 5.0.2，构建却整条挂掉。
# 显式带版本号后，构建结果不再受全局扩展目录里有什么的摆布。
WIX_EXT_VERSION = "5.0.2"

# 主程序单独在 Package.wxs 里定义（要挂快捷方式），这里不要重复收录
SKIP = {"ServerOpsHelper.exe"}


def read_version():
    """从 Package.wxs 里读版本号"""
    root = ET.parse(PACKAGE_WXS).getroot()
    ns = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
    package = root.find("w:Package", ns)
    return package.get("Version")


def read_upgrade_code():
    """从 Package.wxs 读 UpgradeCode（升级判定与 ProductCode 派生都以它为准）。"""
    root = ET.parse(PACKAGE_WXS).getroot()
    ns = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
    package = root.find("w:Package", ns)
    code = package.get("UpgradeCode")
    if not code:
        raise RuntimeError("Package.wxs 里找不到 UpgradeCode")
    return code


def make_product_code(version):
    """按版本号**确定性**生成 ProductCode。

    ⚠️ 不能用 WiX 默认的 ProductCode="*"（每次构建随机生成一个新 GUID）：
    同一个版本号重新出包会得到不同的 ProductCode，而 MSI 是拿 ProductCode 认"产品"的
    —— 于是两批同版本的包会被当成**两个不同的产品**，装两次就出现两条卸载记录，
    卸载一条另一条还留个幽灵。

    2026-10-01 实际踩到：1.8.0 出了两次包（改完使用说明后重出过一次），
    机器上就留下两条 v1.8.0 的产品记录（HKCU 一条、HKLM 一条）。

    MSI 的硬要求是「同一个版本必须同一个 ProductCode、不同版本必须不同」——
    用 uuid5(UpgradeCode, "ServerOpsHelper-<版本>") 正好满足：
    同版本永远算出同一个 GUID，版本一变 GUID 就变，同时也配合 MajorUpgrade
    按版本号判断升级。
    """
    namespace = uuid.UUID(read_upgrade_code())
    return "{" + str(uuid.uuid5(namespace, f"ServerOpsHelper-{version}")).upper() + "}"


def collect_files():
    r"""列出要打包的文件（相对路径，正斜杠）。

    ⚠️ 2026-10-01 大修：以前这里是「同名文件只留路径最深的一份」的**全局去重**，
    再加上所有组件都塞在 Directory="INSTALLFOLDER" —— 两个做法叠加，结果是把
    子目录**整个拍平**：
        TerminalAssets\terminal.html        → terminal.html（直奔程序根目录）
        TerminalAssets\{xterm.js,xterm.css,addon-fit.js}
        zh-Hans\*.resources.dll             → 根目录
        runtimes\win-x64\native\WebView2Loader.dll → 根目录

    实测：便携版 269 个文件里有 17 个落错位置。后果是**安装版的终端根本打不开**
    —— 程序按 `程序目录\TerminalAssets\terminal.html` 去找终端资源（见
    Views/ServerDetailPage.xaml.cs），而那里什么都没有；WPF 的中文资源也失效。

    现在的规则：**保留原始相对路径与目录层级**，让安装版与便携版的目录结构完全一致
    （便携版本来就是 publish 的原样拷贝，这是唯一可信的基准）。
    只有「同一个目标目录里出现同名文件」时才去重 —— 那才是会触发 ICE30 的情况。
    """
    cand = []
    for root, _dirs, files in os.walk(PUBLISH_DIR):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, PUBLISH_DIR).replace(os.sep, "/")
            if rel in SKIP:
                continue
            cand.append((rel, full))

    # 只有「同一个目标目录里的同名文件」才会冲突（ICE30）；不同目录同名是合法的，
    # 而且两份都必须装（根目录与 runtimes\win-x64\native\ 各有一份 WebView2Loader.dll）
    seen = set()
    files = []
    for rel, full in sorted(cand):
        parent = rel.rsplit("/", 1)[0] if "/" in rel else ""
        key = (parent.lower(), os.path.basename(rel).lower())
        if key in seen:
            print(f"去重：跳过同目录同名文件 {rel}")
            continue
        seen.add(key)
        files.append((rel, full))
    return files


def safe_id(rel):
    """把相对路径变成合法的 WiX Id。

    注意：WiX 的 Id 只允许 ASCII 的字母/数字/下划线/点，
    直接用 str.isalnum() 会把中文当合法字符（例如 `使用说明.txt`），必须显式限定 ASCII。
    """
    out = []
    for ch in rel:
        if ch.isascii() and (ch.isalnum() or ch in "_."):
            out.append(ch)
        else:
            out.append("_")
    return "".join(out)


def write_files_wxs(files):
    r"""生成 AppFiles 组件组。

    两个必须遵守的约束（2026-09-21 血泪教训，之前全部漏了）：

    1. **perUser 安装的组件 KeyPath 必须是 HKCU 注册表项**（ICE38 / ICE43）。
       我们装到 %LOCALAPPDATA%，属于「用户配置目录」。这类组件的 KeyPath 若写成文件，
       Windows Installer 无法正确判断组件归属，会出现装不上、卸不干净。
       所以每个组件改成「一个 HKCU 标记注册表项 + File（不带 KeyPath）」。
       注册表键名用组件 Id，天然唯一。

    2. **目录层级必须原样保留**（2026-10-01 修）。
       以前所有组件都写在 Directory="INSTALLFOLDER"，子目录被整个拍平，
       TerminalAssets\terminal.html 跑到程序根目录 → 安装版终端打不开。
       现在按文件所在子目录生成 <Directory>（见上面的 emit_dirs），
       并给组件加 Directory="D_<子目录>"。
       同一个**目标目录**里不允许出现同名文件（ICE30）—— 不同目录同名没问题，
       而且必须都装（根目录与 runtimes\win-x64\native\ 各有一份 WebView2Loader.dll）。

    Guid 用 uuid5(相对路径) 生成，确定性且唯一（不能用 Guid="*"，它按文件名算，
    同名文件会撞成同一个 GUID 直接报错）。
    """
    import uuid

    ns = uuid.UUID("6f1c9b0e-3a4d-4f2b-9c77-2e5a8d1b4c60")

    # 收集文件所在的全部子目录（含中间层级），按层级嵌套生成 <Directory>
    subs = []
    for rel, _full in files:
        if "/" not in rel:
            continue
        parts = rel.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            p = "/".join(parts[:i])
            if p not in subs:
                subs.append(p)

    kids = {}
    for p in subs:
        parent = p.rsplit("/", 1)[0] if "/" in p else ""
        kids.setdefault(parent, []).append(p)
    for v in kids.values():
        v.sort()

    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<!-- 本文件由 build_installer.py 自动生成，请不要手工改；重跑脚本即可刷新 -->",
        '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
        "  <Fragment>",
        '    <DirectoryRef Id="INSTALLFOLDER">',
    ]

    def emit_dirs(parent, depth):
        r"""把子目录按层级嵌套写出来（TerminalAssets、runtimes\win-x64\native …）。"""
        for p in kids.get(parent, []):
            pad = "      " + "  " * depth
            name = p.rsplit("/", 1)[-1]
            did = "D_" + safe_id(p)
            if kids.get(p):
                lines.append(f'{pad}<Directory Id="{did}" Name="{name}">')
                emit_dirs(p, depth + 1)
                lines.append(f"{pad}</Directory>")
            else:
                lines.append(f'{pad}<Directory Id="{did}" Name="{name}" />')

    emit_dirs("", 0)

    lines += [
        "    </DirectoryRef>",
        "  </Fragment>",
        "  <Fragment>",
        '    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">',
    ]

    for rel, full in files:
        cid = "C_" + safe_id(rel)
        fid = "F_" + safe_id(rel)
        guid = str(uuid.uuid5(ns, rel)).upper()

        # 注意：这里**不写** KeyPath="yes"，由下面的 HKCU 注册表项当 KeyPath
        attrs = f'Id="{fid}" Source="$(PublishDir){rel}"'

        # 子目录里的文件：给 Component 指定自己的 Directory，覆盖组件组的 INSTALLFOLDER。
        # 这一行就是「不再拍平目录」的关键（2026-10-01 修）。
        parent = rel.rsplit("/", 1)[0] if "/" in rel else ""
        dattr = f' Directory="D_{safe_id(parent)}"' if parent else ""

        lines.append(f'      <Component Id="{cid}"{dattr} Guid="{{{guid}}}">')
        lines.append(f"        <File {attrs} />")
        # 组件归属标记：perUser 组件的 KeyPath 必须在 HKCU（ICE38/ICE43 要求）
        lines.append(
            f'        <RegistryValue Root="HKCU" '
            f'Key="Software\\ServerOpsHelper\\Components" Name="{cid}" '
            f'Type="integer" Value="1" KeyPath="yes" />'
        )
        lines.append("      </Component>")

    # 每个新建的子目录都要登记 RemoveFolder —— ICE64 要求「用户配置目录下的目录
    # 必须在 RemoveFile 表里有登记」，不登记就整包拦截（2026-10-01 实测 5 条 ERROR）。
    # 组件必须放在它自己的目录里（RemoveFolder 放 Directory 里会报 WIX0005），
    # KeyPath 仍用 HKCU 键（ICE38）。从最深的排起：卸载时先删子目录、再删父目录。
    for p in sorted(subs, key=lambda x: (-x.count("/"), x)):
        did = "D_" + safe_id(p)
        cid = "C_Dir_" + safe_id(p)
        guid = str(uuid.uuid5(ns, "dir::" + p)).upper()
        lines.append(f'      <Component Id="{cid}" Directory="{did}" Guid="{{{guid}}}">')
        lines.append(f'        <RemoveFolder Id="Remove_{cid}" On="uninstall" />')
        lines.append(
            f'        <RegistryValue Root="HKCU" '
            f'Key="Software\\ServerOpsHelper\\Components" Name="{cid}" '
            f'Type="integer" Value="1" KeyPath="yes" />'
        )
        lines.append("      </Component>")

    lines += [
        "    </ComponentGroup>",
        "  </Fragment>",
        "</Wix>",
        "",
    ]

    with open(FILES_WXS, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


def main():
    if not os.path.isdir(PUBLISH_DIR):
        print(f"找不到发布目录：{PUBLISH_DIR}\n请先执行 dotnet publish（见项目 README）。")
        return 2

    if not os.path.exists(WIX):
        print(f"找不到 wix.exe：{WIX}\n可用环境变量 WIX_EXE 指定路径。")
        return 2

    if not os.path.exists(ICON_PATH):
        print(f"找不到应用图标：{ICON_PATH}\n请先执行 python Assets/make_icon.py 生成。")
        return 2

    version = read_version()
    files = collect_files()
    write_files_wxs(files)
    print(f"已生成 Files.wxs：{len(files)} 个文件（版本 {version}）")

    # 交付目录按版本号分：<工作区>/release/v<版本>/
    # 与 release_all.py 保持同一套布局，单独跑本脚本时的落点也对得上
    release_dir = os.path.join(WORKSPACE, "release", f"v{version}")
    os.makedirs(release_dir, exist_ok=True)
    msi_path = os.path.join(release_dir, f"ServerOpsHelper-Setup-v{version}.msi")

    # wixpdb 是调试用的符号库：不能混进交付目录，但也不该长期留在源码目录里当垃圾。
    # 做法是打包期间写到临时目录，成功就删掉，失败才把它挪到 Installer/ 供排查。
    pdb_path = os.path.join(tempfile.gettempdir(), f"ServerOpsHelper-{version}.wixpdb")

    cmd = [
        WIX, "build",
        PACKAGE_WXS, FILES_WXS,
        "-arch", "x64",
        # 安装向导（选安装位置）来自 UI 扩展。
        # **必须显式带版本号**（见 WIX_EXT_VERSION 的说明）。
        "-ext", f"WixToolset.UI.wixext/{WIX_EXT_VERSION}",
        # 向导界面用中文（扩展里嵌了各语言的本地化资源）
        "-culture", "zh-CN",
        "-d", f"PublishDir={PUBLISH_DIR}\\",
        "-d", f"IconFile={ICON_PATH}",
        # ProductCode 按版本号确定性生成（同版本重出必得同一 GUID）——
        # 用 WiX 默认的 "*" 会让同版本的两批包变成"两个产品"，详见 make_product_code 的说明
        "-d", f"ProductCode={make_product_code(version)}",
        "-pdb", pdb_path,
        "-o", msi_path,
    ]

    print("执行：", " ".join(cmd))
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.stdout:
        print(proc.stdout)
    if proc.stderr:
        print(proc.stderr)

    if proc.returncode != 0 or not os.path.exists(msi_path):
        print("打包失败")
        if os.path.exists(pdb_path):
            keep = os.path.join(HERE, "ServerOpsHelper.wixpdb")
            shutil.move(pdb_path, keep)
            print(f"符号库已保留用于排查：{keep}")
        return 1

    # 成功：符号库没用了，直接删掉，别在源码目录里留垃圾
    if os.path.exists(pdb_path):
        os.remove(pdb_path)

    print(f"安装包已生成：{msi_path}（{os.path.getsize(msi_path) / 1024 / 1024:.1f} MB）")

    # ------------------------------------------------------------------
    # 【强制】MSI 合规性校验（ICE）
    #
    # 为什么必须做（2026-09-21 事故）：
    # v1.5.0 第一次交付时，"wix build 返回 0" 就被当成了通过，结果打出来的 MSI
    # 带着 271 条 ICE 违规（265 条 ICE38 + ICE30 + ICE64 + ICE43），
    # 用户双击安装直接 0x80070643 失败。
    #
    # 「编译通过」≠「能装」。ICE 校验是 Windows Installer 的官方最佳实践检查，
    # 必须当成交付门槛：ERROR 级一律拦住不放行。
    # ------------------------------------------------------------------
    if not run_ice_validation(msi_path):
        print("MSI 未通过 ICE 校验，已拦截（不交付不合格的安装包）")
        return 1

    return 0


# ICE 校验里我们**接受**的已知告警（ERROR 级，但经确认属于本场景的合理选择）。
# 用精确匹配，避免"因为想忽略所以放宽标准"。
#
# ICE64 · ProgramsFolder：
#   它要求「用户配置目录下的每一层」都在 RemoveFile 表里登记。但我们只创建
#   %LOCALAPPDATA%\Programs\ServerOpsHelper（INSTALLFOLDER），
#   %LOCALAPPDATA%\Programs 是**系统本来就有的公共目录**（很多程序都装这儿）。
#   若给它登记 RemoveFolder，卸载本程序时会尝试删掉整个 Programs 目录，
#   可能连带影响其他程序的文件夹 —— 这是真事故，绝不能做。
#   所以这一条明确豁免，并在注释里留下理由。
#
# ICE64 · DataFolder（2026-09-29 新增）：
#   同一条规则，这次针对数据目录 %LOCALAPPDATA%\ServerOpsHelper。
#   我们是**故意**不登记 RemoveFolder 的：一登记，卸载时 MSI 就会把这个目录
#   连同里面的密码箱（vault.json）、服务器列表、访问历史一起删干净 ——
#   用户的密码就没了。这里的选择是「宁可卸载后留一个空目录，也不能丢一个密码」。
#   注意安装时它照样会被创建：CreateFolder 负责建、不登记就不负责删，两头都占。
#
# ICE64 · INSTALLPARENT（2026-09-30 新增）：
#   「选父目录自动建应用文件夹」的产物：向导里用户填的是**父目录**（如 E:\AI），
#   程序装进 E:\AI\ServerOpsHelper\（真正的安装层 INSTALLFOLDER 已登记 RemoveFolder）。
#   这一层**绝不能**登记 RemoveFolder —— 登记了的话，卸载时 MSI 会去删
#   用户自己选的那个父目录。用户要是选了 E:\ 或 D:\Programs 这类目录，
#   轻则报错、重则连用户自己的其他文件一起没了。所以这条也是明知故犯，
#   而且"不清理"恰恰是唯一正确的行为。
ICE_ERROR_WHITELIST = (
    "ICE64: The directory ProgramsFolder is in the user profile",
    "ICE64: The directory DataFolder is in the user profile",
    "ICE64: The directory INSTALLPARENT is in the user profile",
)


def run_ice_validation(msi_path):
    """对打好的 MSI 跑 ICE 校验，返回 True 表示无 ERROR 级问题。

    `wix msi validate` 的输出形如：
        xxx.msi : error   WIX0204: ICE38: ...
        xxx.msi : warning WIX1076: ICE91: ...
    我们只把 **error** 级当阻断项。
    """
    cmd = [WIX, "msi", "validate", msi_path]
    print("\n" + "=" * 66)
    print("ICE 校验：", " ".join(cmd))
    print("=" * 66)

    proc = subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    out = (proc.stdout or "") + (proc.stderr or "")

    errors, warnings = [], []
    for line in out.splitlines():
        low = line.lower()
        if " error " in low or ": error" in low:
            errors.append(line.strip())
        elif " warning " in low or ": warning" in low:
            warnings.append(line.strip())

    # 归类统计，输出比"刷 271 行"友好得多
    from collections import Counter
    import re as _re

    def summarize(items):
        c = Counter()
        for it in items:
            m = _re.search(r"(ICE\d+)", it)
            c[m.group(1) if m else "其他"] += 1
        return c

    if warnings:
        print(f"警告 {len(warnings)} 条（不影响安装，仅提示）：")
        for k, v in summarize(warnings).most_common():
            print(f"    {k}: {v}")

    if not errors:
        print("ICE 校验通过：0 个 ERROR。")
        return True

    real_errors = [e for e in errors if not any(w in e for w in ICE_ERROR_WHITELIST)]
    waived = len(errors) - len(real_errors)

    if waived:
        print(f"（已豁免 {waived} 条已知可接受项，理由见 build_installer.py 的 ICE_ERROR_WHITELIST）")

    if not real_errors:
        print("ICE 校验通过：0 个未豁免的 ERROR。")
        return True

    print(f"\n发现 ERROR {len(real_errors)} 条（阻断交付）：")
    for k, v in summarize(real_errors).most_common():
        print(f"    {k}: {v}")
    print("\n前 10 条明细：")
    for e in real_errors[:10]:
        print("   ", e)
    return False


if __name__ == "__main__":
    sys.exit(main())
