"""验收 Inno Setup 安装包：真的装一遍、真的卸一遍，全程不碰数据目录。

用法：
    python verify_inno_payload.py <安装包.exe> [发布目录]

它做什么（都在临时目录里，不碰老板真实的安装和数据）：
  1. 快照 `%LOCALAPPDATA%\\ServerOpsHelper` 的文件数（这是**数据目录**，全程必须一动不动）；
  2. 静默安装到 `%TEMP%\\opshelper-inno-verify-*`，不建快捷方式（/MERGETASKS="!desktopicon"），
     不迁移旧版（SKIPMIGRATE=1）；
  3. 逐条比对「安装出来的相对路径集合」与「publish 目录的相对路径集合」——
     不是只比文件总数：当年 MSI 两边都是 269 个，路径却错了 17 个（子目录被拍平），
     只数个数根本发现不了；
  4. 静默卸载，确认程序文件清干净、数据目录仍然一个文件都没少。

为什么要装一遍而不是解包检查：Inno 没有官方反编译手段，而真正会出问题的恰恰是
「文件落到哪儿」，只有装一遍才看得见。装到临时目录 + 立刻卸载，风险可控。
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time
import winreg

APP_NAME = "服务器运维助手"
APP_DIR_NAME = "ServerOpsHelper"


def data_dir():
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, APP_DIR_NAME)


def snapshot(root):
    """返回 root 下的相对路径集合（统一小写，Windows 不区分大小写）。"""
    out = set()
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            rel = os.path.relpath(os.path.join(dirpath, f), root)
            out.add(rel.lower())
    return out


def count_files(root):
    return sum(len(files) for _d, _s, files in os.walk(root))


def find_inno_install():
    """查 HKCU 里本产品（Inno 的 {AppId}_is1）当前的安装位置，用于防止误伤真实安装。"""
    base = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, base) as key:
            i = 0
            while True:
                try:
                    name = winreg.EnumKey(key, i)
                except OSError:
                    break
                i += 1
                if not name.lower().endswith("_is1"):
                    continue
                try:
                    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, base + "\\" + name) as sub:
                        display, _ = winreg.QueryValueEx(sub, "DisplayName")
                        # 用 startswith 而不是 == ：DisplayName 可能被写成
                        # 「服务器运维助手 1.9.3」这样的「名字+版本」，精确比对会漏判，
                        # 漏判的后果是**把真实安装的注册项当成不存在、装一遍又卸掉**，
                        # 用户那边「设置→应用」里的条目就凭空消失了（2026-10-17 实际发生）。
                        if not str(display).startswith(APP_NAME):
                            continue
                        try:
                            loc, _ = winreg.QueryValueEx(sub, "InstallLocation")
                        except OSError:
                            loc = ""
                        return loc
                except OSError:
                    continue
    except OSError:
        pass
    return None


def run_setup(exe, target, log_path, extra=()):
    args = [
        exe,
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
        "/MERGETASKS=!desktopicon",
        "/DIR=" + target,
        "/LOG=" + log_path,
    ] + list(extra)
    return subprocess.run(args, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def main():
    if len(sys.argv) < 2:
        print("用法：python verify_inno_payload.py <安装包.exe> [发布目录]")
        return 2

    setup = os.path.abspath(sys.argv[1])
    publish = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "ServerOpsHelper", "publish", "win-x64")

    if not os.path.exists(setup):
        print(f"FAIL 找不到安装包：{setup}")
        return 1
    if not os.path.isdir(publish):
        print(f"FAIL 找不到发布目录：{publish}")
        return 1

    existing = find_inno_install()
    if existing:
        if "--skip-if-installed" in sys.argv:
            print(f"[跳过] 本机已安装（{existing}）。")
            print("       装/卸回归会覆盖真实的「设置→应用」注册项，因此不在已安装的机器上跑。")
            print("       要在本机验证，先卸载程序再重新执行本脚本。")
            return 0
        print(f"FAIL 本产品已经装过（{existing}）。")
        print("     先卸载再跑这个脚本，否则会覆盖真实的「设置→应用」注册项。")
        return 1

    dd = data_dir()
    data_before = count_files(dd) if os.path.isdir(dd) else 0
    print(f"数据目录：{dd}  {data_before} 个文件（全程不得变化）")

    target = tempfile.mkdtemp(prefix="opshelper-inno-verify-")
    log_path = os.path.join(tempfile.gettempdir(), "opshelper-inno-verify.log")

    checks = []

    def check(name, ok, detail=""):
        checks.append((name, ok, detail))
        print(f"{'PASS' if ok else 'FAIL'}  {name}" + (f"  [{detail}]" if detail else ""))
        return ok

    try:
        print(f"安装到：{target}")
        proc = run_setup(setup, target, log_path, ["/SKIPMIGRATE=1"])
        check("静默安装退出码为 0", proc.returncode == 0, f"exit={proc.returncode}")
        if proc.returncode != 0:
            print((proc.stdout or "")[-1000:], (proc.stderr or "")[-1000:])
            return 1

        want = snapshot(publish)
        got = snapshot(target)
        # Inno 会在安装目录里放卸载程序，这两个是它自己加的，不算载荷
        artifacts = {n for n in got if n.lower() in ("unins000.exe", "unins000.dat")}
        payload = got - {n for n in got if n.lower() in ("unins000.exe", "unins000.dat")}

        check("载荷文件数与发布目录一致", len(payload) == len(want),
              f"安装 {len(payload)} / 发布 {len(want)}")
        missing = sorted(want - payload)
        extra = sorted(payload - want)
        check("没有缺文件", not missing, "、".join(missing[:3]))
        check("没有多余文件", not extra, "、".join(extra[:3]))
        check("卸载程序已生成", len(artifacts) >= 1, "、".join(sorted(artifacts)))

        # 子目录层级：这是当年 MSI 拍平事故的验收点
        want_dirs = {os.path.dirname(p) for p in want if os.path.dirname(p)}
        got_dirs = {os.path.dirname(p) for p in payload if os.path.dirname(p)}
        check("子目录层级完全一致", want_dirs == got_dirs,
              f"发布 {len(want_dirs)} 个 / 安装 {len(got_dirs)} 个")
        if want_dirs != got_dirs:
            for d in sorted(want_dirs - got_dirs)[:5]:
                print(f"      缺目录：{d}")

        for key_dir in ("terminalassets", "runtimes", "zh-hans"):
            if any(d.startswith(key_dir) for d in want_dirs):
                check(f"关键子目录 {key_dir} 已安装",
                      os.path.isdir(os.path.join(target, key_dir)))

        exe = os.path.join(target, "ServerOpsHelper.exe")
        check("主程序已落位", os.path.exists(exe))

        # 卸载
        uninst = os.path.join(target, "unins000.exe")
        proc = subprocess.run(
            [uninst, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        check("静默卸载退出码为 0", proc.returncode == 0, f"exit={proc.returncode}")

        deadline = time.time() + 60
        while time.time() < deadline and os.path.isdir(target) and count_files(target) > 0:
            time.sleep(0.5)

        left = count_files(target) if os.path.isdir(target) else 0
        check("卸载后程序目录已清空", left == 0, f"残留 {left} 个文件")

        data_after = count_files(dd) if os.path.isdir(dd) else 0
        check("数据目录未被触碰", data_after == data_before,
              f"{data_before} → {data_after}")

        check("「设置→应用」注册项已清除", find_inno_install() is None)
    finally:
        if os.path.isdir(target):
            shutil.rmtree(target, ignore_errors=True)
        for p in (log_path,):
            if os.path.exists(p):
                try:
                    os.remove(p)
                except OSError:
                    pass

    ok = sum(1 for _n, o, _d in checks if o)
    total = len(checks)
    print(f"\n{'=' * 60}\n结果：{ok}/{total} 通过")
    if ok != total:
        print("失败项：")
        for n, o, d in checks:
            if not o:
                print(f"  · {n}  {d}")
        return 1
    print("安装包载荷与安装/卸载行为全部符合预期。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
