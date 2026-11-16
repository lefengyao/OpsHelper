"""打单文件 EXE 安装程序（Burn 引导程序，内部包着 MSI）。

用法（先跑过 build_installer.py，保证 MSI 存在）：
    cd ServerOpsHelper/Installer
    python build_bundle.py

产物：<工作区>/release/v<版本>/ServerOpsHelper-Setup-v<版本>.exe
"""
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
WORKSPACE = os.path.dirname(PROJECT)

# MSI 与 EXE 的落点：<工作区>/release/v<版本>/（与 build_installer.py、release_all.py 同一套布局）
# 版本号在 main() 里读出来后才拼得准，所以这里不预设常量。
BUNDLE_WXS = os.path.join(HERE, "Bundle.wxs")
ICON_PATH = os.path.join(PROJECT, "Assets", "appicon.ico")
# 引导程序界面字符串表（简体中文）。WixStdBA 自带资源里没有 2052 主界面，
# 中文界面完全靠这份文件覆盖官方 HyperlinkTheme.wxl 的字符串 ID。
LOC_FILE = os.path.join(HERE, "zh-CN.wxl")

WIX = os.environ.get(
    "WIX_EXE",
    os.path.join(os.path.expanduser("~"), ".workbuddy", "tools", "wix5", "wix.exe"),
)

# WiX 扩展版本，与 wix.exe 5.0.2 配套，**必须显式写死在 -ext 参数里**。
# 原因见 build_installer.py 里同一常量的注释：不写版本号时，
# 全局扩展目录里一旦混进 WiX 6 的包（会被判为 damaged），
# wix 会只挑最新那个去试、失败就报「扩展找不到」，连可用的 5.0.2 都不看。
WIX_EXT_VERSION = "5.0.2"


def read_version():
    root = ET.parse(BUNDLE_WXS).getroot()
    ns = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
    return root.find("w:Bundle", ns).get("Version")


def main():
    if not os.path.exists(WIX):
        print(f"找不到 wix.exe：{WIX}\n可用环境变量 WIX_EXE 指定路径。")
        return 2

    if not os.path.exists(ICON_PATH):
        print(f"找不到应用图标：{ICON_PATH}\n请先执行 python Assets/make_icon.py 生成。")
        return 2

    if not os.path.exists(LOC_FILE):
        print(f"找不到中文语言文件：{LOC_FILE}\n它是引导程序界面中文的前提，请勿删除。")
        return 2

    version = read_version()
    release_dir = os.path.join(WORKSPACE, "release", f"v{version}")
    msi_path = os.path.join(release_dir, f"ServerOpsHelper-Setup-v{version}.msi")

    if not os.path.exists(msi_path):
        print(f"找不到 MSI：{msi_path}\n请先跑 build_installer.py（release_all.py 会自动按顺序做）。")
        return 2

    exe_path = os.path.join(release_dir, f"ServerOpsHelper-Setup-v{version}.exe")
    pdb_path = os.path.join(tempfile.gettempdir(), f"ServerOpsHelper-bundle-{version}.wixpdb")

    # ⚠️ WiX 输出的移动**不带覆盖**：目标 EXE 已存在时报
    # WIX7012 "Unable to move file"（2026-09-30 重打同版本时踩到）。
    # 所以打之前先把旧 EXE 删掉。若它正被占用（用户开着安装程序等），
    # 这里会抛出真实冲突 —— 报出来比让 wix 半路失败、状态不明要好。
    if os.path.exists(exe_path):
        os.remove(exe_path)

    cmd = [
        WIX, "build",
        os.path.basename(BUNDLE_WXS),
        "-arch", "x64",
        # WiX 5 里 Burn 的引导程序扩展叫 BootstrapperApplications（v3/v4 时代叫 Bal）。
        # **必须显式带版本号**（见 WIX_EXT_VERSION 的说明）。
        "-ext", f"WixToolset.BootstrapperApplications.wixext/{WIX_EXT_VERSION}",
        "-culture", "zh-CN",
        "-d", f"IconFile={ICON_PATH}",
        "-d", f"MsiFile={msi_path}",
        # LocalizationFile 是相对 wxs 的路径，所以传文件名即可；
        # 但 wix build 的 cwd 必须是 wxs 所在目录（下面 cwd=HERE 保证这点）。
        "-d", f"LocFile={os.path.basename(LOC_FILE)}",
        "-pdb", pdb_path,
        "-o", exe_path,
    ]

    print("执行：", " ".join(cmd))
    proc = subprocess.run(
        cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=HERE
    )
    if proc.stdout:
        print(proc.stdout)
    if proc.stderr:
        print(proc.stderr)

    if proc.returncode != 0 or not os.path.exists(exe_path):
        print("打包失败")
        if os.path.exists(pdb_path):
            keep = os.path.join(HERE, "ServerOpsHelper-Bundle.wixpdb")
            # %TEMP% 常在 C: 而项目在 D: —— os.replace 跨盘会抛
            # WinError 17「无法将文件移到不同的磁盘驱动器」（2026-09-30 踩到），
            # 必须用 shutil.move（跨盘自动 copy+delete，与 build_installer.py 一致）
            shutil.move(pdb_path, keep)
            print(f"符号库已保留用于排查：{keep}")
        return 1

    if os.path.exists(pdb_path):
        os.remove(pdb_path)

    print(f"EXE 安装程序已生成：{exe_path}（{os.path.getsize(exe_path) / 1024 / 1024:.1f} MB）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
