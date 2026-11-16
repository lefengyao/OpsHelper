# -*- coding: utf-8 -*-
"""把旧版 MSI/Burn 安装卸掉，为 Inno 版本让路。

只做一件事：按注册表里的 QuietUninstallString 静默卸载，然后核对：
  · 程序目录是否清空（WebView2 缓存残留属正常，卸载程序不删它）
  · 数据目录 %LOCALAPPDATA%\\ServerOpsHelper 是否一个文件都没少

⚠️ 绝不用 bash 启动安装/卸载程序：Git Bash 会把参数里的反斜杠转成正斜杠。
"""
import os
import re
import subprocess
import sys
import winreg

APP_NAME = "服务器运维助手"
INSTALL_DIR = r"E:\AI\ServerOpsHelper"
UNINST_ROOT = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"


def find_quiet_uninstall(root):
    """按 **DisplayName 精确匹配** 找本产品的卸载命令。

    ⚠️ 2026-10-17 踩过的坑：只按「子键名不是 _is1」过滤，会顺手抓到机器上
       别的软件（当时抓到 Trailblazer Deploy）。必须先读 DisplayName 确认是本产品，
       才能动它的卸载程序 —— 卸载是不可逆操作，认错产品就是事故。
    """
    try:
        key = winreg.OpenKey(root, UNINST_ROOT)
    except OSError:
        return None
    i = 0
    while True:
        try:
            sub = winreg.EnumKey(key, i)
        except OSError:
            break
        i += 1
        try:
            with winreg.OpenKey(root, UNINST_ROOT + "\\" + sub) as s:
                display, _ = winreg.QueryValueEx(s, "DisplayName")
                if not str(display).startswith(APP_NAME):
                    continue
                quiet, _ = winreg.QueryValueEx(s, "QuietUninstallString")
                print(f"命中注册项：{sub}  [{display}]")
                return quiet
        except OSError:
            continue
    return None


def split(cmd):
    m = re.match(r'^"([^"]+)"\s*(.*)$', cmd)
    if m:
        return m.group(1), m.group(2)
    return cmd, ""


def count_files(root):
    return sum(len(f) for _d, _s, f in os.walk(root))


def main():
    data_dir = os.path.join(os.environ["LOCALAPPDATA"], "ServerOpsHelper")
    data_before = count_files(data_dir) if os.path.isdir(data_dir) else 0
    print(f"数据目录：{data_dir}  {data_before} 个文件")

    cmd = find_quiet_uninstall(winreg.HKEY_CURRENT_USER)
    if not cmd:
        print("没找到 HKCU 里的旧版卸载项，可能已经卸过了。")
        return 0

    exe, args = split(cmd)
    print(f"卸载程序：{exe}\n参数：{args}")
    if not os.path.exists(exe):
        print(f"FAIL 卸载程序不存在：{exe}")
        return 1

    p = subprocess.run([exe] + args.split(), capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    print(f"退出码：{p.returncode}")
    if p.returncode != 0:
        print("stdout:", (p.stdout or "")[-800:])
        print("stderr:", (p.stderr or "")[-800:])
        return 1

    left = count_files(INSTALL_DIR) if os.path.isdir(INSTALL_DIR) else 0
    data_after = count_files(data_dir) if os.path.isdir(data_dir) else 0
    print(f"程序目录残留：{left} 个文件（WebView2 缓存属正常残留）")
    print(f"数据目录：{data_before} → {data_after}")

    if data_after != data_before:
        print("FAIL 数据目录文件数发生变化！")
        return 1
    print("OK 数据未受影响")
    return 0


if __name__ == "__main__":
    sys.exit(main())
