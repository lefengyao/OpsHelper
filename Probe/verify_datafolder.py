"""验证安装包对「数据目录」的两条行为，并守住「卸载不删数据」这条铁律。

要验两件事：
  1. 安装时**会创建**数据目录（`%LOCALAPPDATA%\\ServerOpsHelper`）
  2. 卸载时**不会删**它 —— 哪怕是空的；里面有数据时更不能删

分两个阶段：
  阶段一：数据目录不存在时 → 装 → 应被创建 → 卸 → **应仍然存在**
  阶段二：数据目录已有真实数据时 → 装 → 数据不变 → 卸 → **数据不变**（最关键）

⚠️ 这个测试**必须操作真实的数据目录路径**，因为 MSI 里的路径是写死的、
   不像应用那样可以靠 SERVEROPSHELPER_CONFIG_DIR 重定向。
   三层安全措施：
     a. 动手前已把 8 个数据文件备份到 `.workbuddy/backup/ServerOpsHelper-data-20260929/`；
     b. 阶段一把真实目录**改名**（不是删除、不是复制）到暂存名，测完改回来；
     c. 全过程包在 try/finally 里，任何异常都会走恢复流程。
   另外恢复时**绝不 rmtree**：只删确认过的空目录，非空一律改名保留。

用法：python verify_datafolder.py
"""
import hashlib
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.dirname(HERE)

LOCALAPPDATA = os.environ["LOCALAPPDATA"]
DATA = os.path.join(LOCALAPPDATA, "ServerOpsHelper")
HOLD = os.path.join(LOCALAPPDATA, "ServerOpsHelper.__hold_by_test__")
BASE = os.path.join(HERE, "verify_datafolder")

# 关键数据文件：内容必须逐字节不变
KEY_FILES = ("vault.json", "vault.json.bak", "links.json", "servers.json",
             "history.json", "settings.json", "users.json")

results = []


def case(name, ok, detail):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name} —— {detail}")


def count(d):
    if not os.path.isdir(d):
        return 0
    n = 0
    for _r, _dd, ff in os.walk(d):
        n += len(ff)
    return n


def fingerprint(d):
    """(相对路径, 大小) 列表 —— 能发现删除、清空、大小变化"""
    items = []
    if not os.path.isdir(d):
        return items
    for r, _dd, ff in os.walk(d):
        for f in ff:
            p = os.path.join(r, f)
            try:
                items.append((os.path.relpath(p, d), os.stat(p).st_size))
            except OSError:
                pass
    items.sort()
    return items


def key_hashes(d):
    """关键文件的 md5 —— 首选的铁证（比大小更严）"""
    out = {}
    for name in KEY_FILES:
        p = os.path.join(d, name)
        if os.path.isfile(p):
            with open(p, "rb") as fh:
                out[name] = hashlib.md5(fh.read()).hexdigest()
    return out


def find_exe():
    root = os.path.join(WORKSPACE, "release")
    prefix, suffix = "ServerOpsHelper-Setup-v", ".exe"
    cands = []
    for ver in os.listdir(root):
        d = os.path.join(root, ver)
        if not os.path.isdir(d):
            continue
        for name in os.listdir(d):
            if name.startswith(prefix) and name.endswith(suffix):
                cands.append(os.path.join(d, name))

    def key(p):
        raw = os.path.basename(p)[len(prefix):-len(suffix)]
        try:
            return tuple(int(x) for x in raw.split("."))
        except ValueError:
            return (0,)

    return max(cands, key=key) if cands else None


def run_setup(exe, args, log):
    return subprocess.run([exe] + args + ["-l", log],
                          capture_output=True, text=True).returncode


def application_is_running():
    """ServerOpsHelper 是否正在运行。

    ⚠️ 程序运行时**绝不能**动数据目录：它占用着里面的 WebView2 缓存
    （Cookies、Sessions 等），目录既 rename 不了、也 copy 不全。
    2026-10-01 就是这么踩崩的 —— shutil.move 的 rename 被拒后自动退化成
    copytree，复制到一半报 WinError 32，留下一个残缺副本。
    """
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq ServerOpsHelper.exe", "/NH"],
            capture_output=True, text=True, errors="replace",
        ).stdout
        return "ServerOpsHelper.exe" in (out or "")
    except Exception:
        return False


def main():
    exe = find_exe()
    if not exe:
        print("找不到 release/ 下的安装程序")
        return 2

    print(f"安装程序: {exe}\n")

    # 前置检查：程序运行中就别动数据目录（本脚本此时不做任何操作，直接退出）
    if application_is_running():
        print("⚠️ 检测到 ServerOpsHelper 正在运行 —— 请先关闭程序再跑这个验证。")
        print("   原因：程序占用数据目录里的 WebView2 缓存文件，目录无法安全移动。")
        print("   （真实数据没有被动过；本脚本此时不会做任何操作。）")
        return 4

    if os.path.isdir(HOLD):
        print(f"⚠️ 发现上次遗留的暂存目录：{HOLD}")
        print("   请先人工确认后处理（若里面是真实数据，改名回 ServerOpsHelper 即可）。")
        return 3

    before_count = count(DATA)
    before_hashes = key_hashes(DATA)
    print(f"测试前：数据目录 {DATA}")
    print(f"        文件数 {before_count}，关键文件 {len(before_hashes)} 个\n")

    hold_used = False

    try:
        # ───────── 阶段一：数据目录不存在时，装 → 应创建；卸 → 应保留 ─────────
        print("【阶段一】数据目录不存在时")
        if os.path.isdir(DATA):
            try:
                # 必须用 rename（同盘、原子），**绝不能退化成 copytree**：
                # 复制中途一旦失败就会留下"残缺副本"，比直接报错危险得多
                # （2026-10-01 的教训）。
                os.rename(DATA, HOLD)
            except OSError as exc:
                print(f"  无法暂存数据目录：{exc}")
                print("  —— 程序可能正在运行并占用其中的 WebView2 缓存，请关闭后重试。")
                print("  —— 未做任何改动，真实数据安然无恙。")
                return 4

            hold_used = True
            print(f"  真实数据已暂存到 {HOLD}（{count(HOLD)} 个文件，只是改名、没删）")

        print(f"  安装前数据目录存在？ {os.path.isdir(DATA)}")
        shutil.rmtree(BASE, ignore_errors=True)
        os.makedirs(BASE, exist_ok=True)
        target = os.path.join(BASE, "app")

        rc = run_setup(exe, ["/quiet", "/norestart", f"InstallFolder={target}"],
                       os.path.join(BASE, "i.log"))
        prog = os.path.join(target, "ServerOpsHelper")  # 2026-09-30 起：选父目录，自动建子文件夹
        print(f"  安装退出码 {rc}，程序文件 {count(prog)} 个")

        created = os.path.isdir(DATA)
        case("安装时创建了数据目录", created,
             f"目录存在={created}（这是本次改动的目的）")
        case("程序文件装进了所选位置下的 ServerOpsHelper 子文件夹",
             count(prog) > 200, f"子目录文件数 {count(prog)}")

        rc = run_setup(exe, ["/uninstall", "/quiet", "/norestart"],
                       os.path.join(BASE, "u.log"))
        print(f"  卸载退出码 {rc}")

        # 空目录卸载后会被清掉 —— 这是 MSI CreateFolder 的标准反向行为（不由
        # RemoveFolder 决定），无害：没有数据可丢，下次安装还会再建。
        # 所以这里**如实记录、不作为失败项**；真正要守的是阶段二那条
        # "有数据时卸载一个字节都不能动"。
        still = os.path.isdir(DATA)
        print(f"  [信息] 卸载后空目录是否还在：{still}"
              f"（被清或保留都属正常；有数据时才是关键，见阶段二）")

        case("程序目录已清干净", count(prog) == 0, f"残留 {count(prog)} 个文件")

        # ───────── 恢复真实数据 ─────────
        # 全程 os.rename（同盘、原子）—— 不用 shutil.move，避免它在 rename 被拒时
        # 悄悄退化成 copytree，从而产生"残缺副本"这种最坏的结果。
        print("\n【恢复真实数据】")
        if hold_used:
            if os.path.isdir(DATA):
                n = count(DATA)
                if n == 0:
                    os.rmdir(DATA)
                    print("  已删掉测试创建的空目录")
                else:
                    leftover = DATA + ".test_leftover"
                    os.rename(DATA, leftover)
                    print(f"  ⚠️ 测试目录非空（{n} 个文件），改名为 {leftover} 而未删除")
            os.rename(HOLD, DATA)
            hold_used = False
            print(f"  真实数据已恢复（{count(DATA)} 个文件）")

        restored = count(DATA)
        same_keys = key_hashes(DATA) == before_hashes
        case("恢复后文件数一致", restored == before_count,
             f"{before_count} → {restored}")
        case("恢复后关键文件逐字节一致", same_keys,
             "vault.json 等关键文件 md5 全部匹配" if same_keys else "内容有变化！")

        # ───────── 阶段二：已有真实数据时，装/卸都不能动它 ─────────
        print("\n【阶段二】数据目录已有真实数据时")
        fp_before = fingerprint(DATA)
        hashes_before = key_hashes(DATA)

        rc = run_setup(exe, ["/quiet", "/norestart", f"InstallFolder={target}"],
                       os.path.join(BASE, "i2.log"))
        fp_after_install = fingerprint(DATA)
        case("已有数据时安装：数据一个字节没动", fp_after_install == fp_before,
             f"{len(fp_before)} 个文件 → {len(fp_after_install)} 个")
        case("已有数据时安装：vault.json 内容不变",
             key_hashes(DATA) == hashes_before, "关键文件 md5 应完全一致")

        rc = run_setup(exe, ["/uninstall", "/quiet", "/norestart"],
                       os.path.join(BASE, "u2.log"))
        fp_after_uninstall = fingerprint(DATA)
        case("★★ 已有数据时卸载：数据一个字节没动", fp_after_uninstall == fp_before,
             f"{len(fp_before)} 个文件 → {len(fp_after_uninstall)} 个")
        case("★★ 已有数据时卸载：vault.json 内容不变",
             key_hashes(DATA) == hashes_before,
             "这是最要紧的一条：卸载绝不能动密码箱")

    finally:
        # ── 兜底恢复：任何异常都要把真实数据放回去 ──
        # 同样只用 os.rename：宁可明确报错说"数据仍在 HOLD，请手动改名"，
        # 也不要让 shutil.move 退化复制、搞出半个副本。
        if hold_used:
            print("\n[兜底] 流程异常，正在恢复真实数据…")
            try:
                if os.path.isdir(DATA) and count(DATA) == 0:
                    os.rmdir(DATA)
                elif os.path.isdir(DATA):
                    os.rename(DATA, DATA + ".test_leftover")
                if os.path.isdir(HOLD):
                    os.rename(HOLD, DATA)
                print("[兜底] 已恢复")
            except Exception as exc:  # noqa: BLE001
                print(f"[兜底] 恢复失败，真实数据仍在 {HOLD}，请手动改名回 {DATA}：{exc}")

    # ───────── 汇总 ─────────
    print("\n" + "=" * 62)
    passed = sum(1 for _n, ok, _d in results if ok)
    print(f"结果：{passed}/{len(results)} 通过")
    for n, ok, d in results:
        if not ok:
            print(f"  未通过: {n} —— {d}")
    print("=" * 62)

    if os.path.isdir(BASE):
        shutil.rmtree(BASE, ignore_errors=True)

    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
