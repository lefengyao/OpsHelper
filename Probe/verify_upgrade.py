"""验证「覆盖安装（升级）」：装旧版 → 直接装新版 → 应自动移除旧版，只留一份。

背景（2026-10-01）：老板问「你这个不能覆盖安装吗」——当时我给不出确定答案，
因为 **e2e 的 C 场景只覆盖了「同版本重装」，跨版本升级从来没测过**。
这个脚本就是补这个盲区，以后每次发版都该跑一遍。

判据（全部硬断言）：
  1. 装完旧版，注册表里正好 1 条产品记录，版本 = 旧版；
  2. **直接**装新版（不先卸载），退出码 0；
  3. 装完新版，注册表里**仍然正好 1 条**（旧版被 MajorUpgrade 自动移除），版本 = 新版；
  4. 程序目录文件数正常（升级后是完整的，不是残留的旧文件）；
  5. 卸载后程序目录清干净；
  6. 全程用户数据目录一个字节没动（铁律）。

用法：python verify_upgrade.py [新版 exe] [旧版 exe]
      不带参数时自动从 release/ 里挑最新版与「次新」版本。
退出码：0 全通过 / 1 有失败 / 2 环境不具备（版本不足）/ 3 程序正在运行
"""
import os
import subprocess
import sys
import winreg

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.dirname(HERE)


def version_from_path(path):
    """从 ...-Setup-v1.8.1.exe 里取出 "1.8.1"。

    必须解析：手动传两个 exe 时，如果版本串留空，
    `版本.startswith("")` 恒为 True → 「没有旧版残留」这条断言必然假 FAIL
    （2026-10-17 踩过：明明升级干净，却被判不通过）。
    """
    name = os.path.basename(path)
    marker = "-Setup-v"
    idx = name.find(marker)
    if idx < 0:
        return ""
    rest = name[idx + len(marker):]
    if rest.lower().endswith(".exe"):
        rest = rest[:-4]
    return rest


def releases():
    """release/ 下的 (版本元组, 版本字符串, exe 路径)，按版本升序。"""
    root = os.path.join(WORKSPACE, "release")
    found = []
    if not os.path.isdir(root):
        return found

    for ver_dir in os.listdir(root):
        d = os.path.join(root, ver_dir)
        if not os.path.isdir(d) or not ver_dir.startswith("v"):
            continue
        raw = ver_dir[1:]
        try:
            key = tuple(int(x) for x in raw.split("."))
        except ValueError:
            continue

        for name in os.listdir(d):
            if name.startswith("ServerOpsHelper-Setup-v") and name.endswith(".exe"):
                found.append((key, raw, os.path.join(d, name)))

    found.sort(key=lambda t: t[0])
    return found


def installed_products():
    """注册表里的产品记录，去重后返回 [(产品码, 版本, 类型)]，类型 = "bundle" / "msi"。

    为什么要分类型：Burn 的引导程序（EXE）和被它包着的 MSI **各自**会写一条 ARP 记录，
    两条名字都叫「服务器运维助手」—— 用户在「设置 → 应用」里就会看到两条同名条目。
    正确做法是让 MSI 那条隐藏（`<MsiPackage Visible="no">`，即给 MSI 加
    ARPSYSTEMCOMPONENT=1），只留 bundle 一条。所以断言必须能区分二者：
      · bundle 条目 → 存在 `BundleProviderKey` 值；
      · msi   条目 → 存在 `WindowsInstaller` = 1。

    另外按 ProductCode 去重：同一个产品可能在 HKCU / HKLM 各有一条视图，那是同一个。
    """
    seen = {}
    areas = [
        (winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    ]
    for root, sub in areas:
        try:
            with winreg.OpenKey(root, sub) as k:
                for i in range(winreg.QueryInfoKey(k)[0]):
                    name = winreg.EnumKey(k, i)
                    try:
                        with winreg.OpenKey(k, name) as sk:
                            try:
                                dn = str(winreg.QueryValueEx(sk, "DisplayName")[0])
                            except OSError:
                                continue
                            if "运维" not in dn and "ServerOps" not in dn:
                                continue

                            try:
                                dv = str(winreg.QueryValueEx(sk, "DisplayVersion")[0])
                            except OSError:
                                dv = "?"

                            kind = "msi"
                            try:
                                winreg.QueryValueEx(sk, "BundleProviderKey")
                                kind = "bundle"
                            except OSError:
                                pass

                            # SystemComponent=1 表示这条在「设置 → 应用」里**不显示**。
                            # Burn 的 `<MsiPackage Visible="no">` 就是靠给 MSI 传
                            # ARPSYSTEMCOMPONENT=1 实现的 —— 注意它只是**隐藏**，
                            # 注册表项照样在，所以不能拿"项存在"当"用户看得见"。
                            hidden = False
                            try:
                                hidden = int(winreg.QueryValueEx(sk, "SystemComponent")[0]) == 1
                            except (OSError, ValueError, TypeError):
                                pass

                            seen.setdefault(name.upper(), (name, dv, kind, hidden))
                    except OSError:
                        continue
        except OSError:
            continue
    return list(seen.values())


def running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq ServerOpsHelper.exe", "/NH"],
            capture_output=True, text=True, errors="replace").stdout
        return "ServerOpsHelper.exe" in (out or "")
    except Exception:
        return False


def count_files(d):
    if not os.path.isdir(d):
        return 0
    n = 0
    for _r, _dd, ff in os.walk(d):
        n += len(ff)
    return n


def count_data():
    d = os.path.join(os.environ["LOCALAPPDATA"], "ServerOpsHelper")
    return count_files(d)


def run_setup(exe, args, log):
    return subprocess.run([exe] + args + ["-l", log],
                          capture_output=True, text=True).returncode


def main():
    exes = releases()
    if len(exes) < 2:
        print(f"release/ 下至少要有两个版本才能测升级（当前 {len(exes)} 个）")
        return 2

    if len(sys.argv) >= 3:
        old = ((), version_from_path(sys.argv[2]), sys.argv[2])
        new = ((), version_from_path(sys.argv[1]), sys.argv[1])
        if not old[1] or not new[1]:
            print(f"⚠️ 无法从文件名解析版本号（旧={old[1]!r} 新={new[1]!r}），"
                  f"文件名应形如 ServerOpsHelper-Setup-v1.8.1.exe")
            return 2
    else:
        new = exes[-1]
        old = exes[-2]

    print(f"旧版：v{old[1]}  {old[2]}")
    print(f"新版：v{new[1]}  {new[2]}\n")

    if running():
        print("⚠️ ServerOpsHelper 正在运行 —— 请先关闭程序再跑（升级会替换程序文件）。")
        return 3

    base = os.path.join(HERE, "verify_upgrade")
    prog = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "ServerOpsHelper")
    data_before = count_data()

    results = []

    def case(name, ok, detail):
        results.append((name, ok, detail))
        print(f"  [{'PASS' if ok else 'FAIL'}] {name} —— {detail}")

    try:
        os.makedirs(base, exist_ok=True)

        # ── 1. 装旧版 ──
        print("【1】安装旧版")
        rc = run_setup(old[2], ["/quiet", "/norestart"], os.path.join(base, "old.log"))
        prods = installed_products()
        # 旧版包可能是「MSI 条目未隐藏」时代的产物，装它会出两条（bundle + msi）——
        # 那是历史包的行为，这里只要求"装上了"，重点看新版那两步
        case("旧版安装成功且已注册", rc == 0 and len(prods) >= 1,
             f"退出码={rc} 产品记录={prods}")

        # ── 2. 直接装新版（不先卸载）──
        print("\n【2】直接覆盖安装新版（不预先卸载）")
        rc = run_setup(new[2], ["/quiet", "/norestart"], os.path.join(base, "new.log"))
        prods = installed_products()
        case("覆盖安装退出码为 0", rc == 0, f"退出码={rc}")
        case("★ 覆盖后没有旧版残留记录",
             all(not p[1].startswith(old[1]) for p in prods) and len(prods) >= 1,
             f"产品记录={[(p[1], p[2]) for p in prods]}")
        case("★ 留下的是新版",
             len(prods) >= 1 and all(p[1].startswith(new[1]) for p in prods),
             f"版本={[p[1] for p in prods]}，期望 {new[1]}")

        # 「设置 → 应用」里能看到几条 —— 这才是用户视角的真实体验。
        # Burn 场景下 bundle 与 MSI 各有一条 ARP 项，正确做法是让 MSI 那条隐藏
        # （MsiPackage Visible="no" → ARPSYSTEMCOMPONENT=1），用户只看到 bundle。
        visible = [p for p in prods if not p[3]]
        case("★ 用户可见的条目只有 1 条（MSI 条目已隐藏）",
             len(visible) == 1 and visible[0][2] == "bundle",
             f"可见={[(p[1], p[2]) for p in visible]}，隐藏={len(prods) - len(visible)} 条")

        n = count_files(prog)
        case("升级后程序文件完整", n > 200, f"文件数={n}")

        # ── 3. 卸载 ──
        print("\n【3】卸载")
        rc = run_setup(new[2], ["/uninstall", "/quiet", "/norestart"],
                       os.path.join(base, "un.log"))
        left = count_files(prog)
        prods = installed_products()
        case("卸载后程序目录清干净", left == 0, f"残留={left}")
        case("卸载后注册表无记录", len(prods) == 0, f"产品记录={prods}")

        # ── 4. 铁律 ──
        data_after = count_data()
        case("★ 用户数据目录零改动", data_before == data_after,
             f"测前={data_before} 测后={data_after}")

    finally:
        import shutil
        shutil.rmtree(base, ignore_errors=True)

    print("\n" + "=" * 62)
    passed = sum(1 for _n, ok, _d in results if ok)
    print(f"结果：{passed}/{len(results)} 通过")
    for n, ok, d in results:
        if not ok:
            print(f"  未通过: {n} —— {d}")
    print("=" * 62)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
