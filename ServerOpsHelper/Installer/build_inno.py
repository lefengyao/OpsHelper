"""打单文件 EXE 安装程序（Inno Setup 6）。

用法：
    cd ServerOpsHelper/Installer
    python build_inno.py

产物：<工作区>/release/v<版本>/ServerOpsHelper-Setup-v<版本>.exe

和 WiX 那套的区别（2026-10-17 换的）：
  · 不再有 MSI 这个中间产物 —— Inno 直接把整个 publish 目录压进一个 exe；
  · 不再依赖 wix.exe / ICE 校验，编译期就那一步；
  · 版本号唯一来源是 ServerOpsHelper.csproj 的 <Version>，通过 /D 传给脚本，
    不会出现「脚本里写的版本」和「实际编出来的版本」不一致。

编译器位置默认取工作区内的便携版（.tools/InnoSetup6），可用环境变量 ISCC_EXE 覆盖。
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
WORKSPACE = os.path.dirname(PROJECT)
PUBLISH = os.path.join(PROJECT, "publish", "win-x64")
ISS = os.path.join(HERE, "ServerOpsHelper.iss")
CSPROJ = os.path.join(PROJECT, "ServerOpsHelper.csproj")

ISCC = os.environ.get(
    "ISCC_EXE",
    os.path.join(WORKSPACE, ".tools", "InnoSetup6", "ISCC.exe"),
)


def read_version():
    """从 csproj 读版本号 —— 单一事实来源。"""
    with open(CSPROJ, encoding="utf-8") as fh:
        text = fh.read()
    m = re.search(r"<Version>([\d.]+)</Version>", text)
    if not m:
        raise SystemExit("csproj 里找不到 <Version>，无法确定安装包版本号")
    return m.group(1)


def count_files(root):
    n = 0
    for _dirpath, _dirs, files in os.walk(root):
        n += len(files)
    return n


def main():
    if not os.path.exists(ISCC):
        print(f"找不到 Inno Setup 编译器：{ISCC}")
        print("可用环境变量 ISCC_EXE 指定；或把便携版放到 .tools/InnoSetup6/ISCC.exe")
        return 2

    if not os.path.isdir(PUBLISH):
        print(f"找不到发布目录：{PUBLISH}\n请先 dotnet publish（release_all.py 会自动做）。")
        return 2

    version = read_version()
    release_dir = os.path.join(WORKSPACE, "release", f"v{version}")
    os.makedirs(release_dir, exist_ok=True)

    files = count_files(PUBLISH)
    print(f"[Inno] 版本 {version}　载荷 {files} 个文件")
    print(f"[Inno] 编译器 {ISCC}")

    args = [
        ISCC,
        "/Qp",
        f"/DMyAppVersion={version}",
        f"/O{release_dir}",
        ISS,
    ]
    proc = subprocess.run(
        args, capture_output=True, text=True,
        encoding="utf-8", errors="replace", cwd=HERE,
    )

    out = (proc.stdout or "") + (proc.stderr or "")
    for line in out.splitlines():
        s = line.strip()
        if s and ("error" in s.lower() or "错误" in s or "警告" in s
                  or "Successful" in s or "successful" in s):
            print("   ", s[:200])

    if proc.returncode != 0:
        print(f"打 EXE 安装程序失败（ISCC 退出码 {proc.returncode}）")
        print(out.strip()[-1500:])
        return 1

    exe = os.path.join(release_dir, f"ServerOpsHelper-Setup-v{version}.exe")
    if not os.path.exists(exe):
        print(f"ISCC 报告成功，但没找到产物：{exe}")
        return 1

    print(f"[Inno] EXE 安装程序已生成：{exe}  {os.path.getsize(exe) / 1024 / 1024:.1f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
