"""真机迁移：卸载 v1.9.3 → 完整装卸回归 → 安装 v1.9.4 到 E:\\AI\\ServerOpsHelper。

铁律：全程不碰 %LOCALAPPDATA%\\ServerOpsHelper（数据目录），只在开始/结束各数一次文件数做对比。
所有安装/卸载都走 subprocess（bash 会把参数里的反斜杠拧成正斜杠，MSI/Inno 都不认）。
"""
import json
import os
import subprocess
import sys

INSTALL_DIR = r"E:\AI\ServerOpsHelper"
DATA_DIR = os.path.join(os.environ["LOCALAPPDATA"], "ServerOpsHelper")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETUP = os.path.join(ROOT, "release", "v1.9.4", "ServerOpsHelper-Setup-v1.9.4.exe")
PUBLISH = os.path.join(ROOT, "ServerOpsHelper", "publish", "win-x64")
VERIFY = os.path.join(ROOT, "Probe", "verify_inno_payload.py")


def count_files(d):
    return sum(len(f) for _r, _s, f in os.walk(d)) if os.path.isdir(d) else 0


def arp_entries():
    import winreg
    base = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
    out = []
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, base)
    except OSError:
        return out
    i = 0
    while True:
        try:
            name = winreg.EnumKey(key, i)
        except OSError:
            break
        i += 1
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, base + "\\" + name) as sub:
                try:
                    dn = winreg.QueryValueEx(sub, "DisplayName")[0]
                except OSError:
                    continue
                if "服务器运维助手" not in dn:
                    continue
                try:
                    ver = winreg.QueryValueEx(sub, "DisplayVersion")[0]
                except OSError:
                    ver = "?"
                out.append((name, dn, ver))
        except OSError:
            pass
    return out


def run(args, label):
    print(f"\n>>> {label}")
    print("    " + " ".join(args))
    p = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(f"    exit={p.returncode}")
    return p


def main():
    data_before = count_files(DATA_DIR)
    print(f"数据目录基线：{data_before} 个文件")
    print(f"迁移前 ARP：{arp_entries()}")
    print(f"迁移前程序目录：{count_files(INSTALL_DIR)} 个文件")

    # ① 卸载当前的 1.9.3
    unins = os.path.join(INSTALL_DIR, "unins000.exe")
    if os.path.isfile(unins):
        run([unins, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], "卸载 v1.9.3")
    else:
        print("[跳过] 没找到 unins000.exe")

    print(f"卸载后 ARP：{arp_entries()}")
    left = count_files(INSTALL_DIR)
    print(f"卸载后程序目录：{left} 个文件（WebView2 缓存会留下，正常）")

    # ② 完整装卸回归（此时本机已无安装记录，会真装真卸到临时目录）
    p = run([sys.executable, VERIFY, SETUP, PUBLISH], "安装包载荷与装/卸回归")
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        if line.strip().startswith(("PASS", "FAIL", "=====", "结果")):
            print("    " + line.strip()[:160])
    if p.returncode != 0:
        print("\n!!! 装卸回归失败，停止迁移，先把问题查清楚")
        return 1

    # ③ 装 1.9.4 到原位置
    run([SETUP, "/VERYSILENT", "/SP-", "/SUPPRESSMSGBOXES", "/NORESTART",
         f"/DIR={INSTALL_DIR}", "/MERGETASKS=desktopicon"], "安装 v1.9.4")

    data_after = count_files(DATA_DIR)
    print("\n================ 结果 ================")
    print(f"ARP：{arp_entries()}")
    print(f"程序目录：{count_files(INSTALL_DIR)} 个文件")
    print(f"数据目录：{data_after} 个文件（基线 {data_before}）")
    print("数据目录未减少 ✓" if data_after >= data_before else "!!! 数据文件变少了")

    with open(os.path.join(ROOT, ".tools", "migrate_result.json"), "w", encoding="utf-8") as fh:
        json.dump({"arp": arp_entries(), "data_before": data_before, "data_after": data_after},
                  fh, ensure_ascii=False, indent=2)
    return 0 if data_after >= data_before else 1


if __name__ == "__main__":
    sys.exit(main())
