"""MSI 载荷校验：不安装，直接读安装包数据库，断言它装出来的东西 == 便携版。

做法：`wix msi decompile` 把 MSI 反编译成 wxs（含完整 Directory 树 + File 表），
再重建「每个文件最终落到 INSTALLFOLDER 下的哪个相对路径」，与便携版目录逐条比对。

为什么要有这一层：
  · e2e_matrix / verify_upgrade 要真装真卸，会动到用户机器上已装的版本，不能随手跑；
  · 而"拍平子目录"这类事故（2026-10-01 踩过）恰恰是**编译期全绿、装上才炸**，
    必须在出包前就有一道零风险的关卡。

用法：python verify_msi_payload.py <msi> <便携版目录>
"""
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

WIX = r"C:\Users\15610\.workbuddy\tools\wix5\wix.exe"
NS = {"w": "http://wixtoolset.org/schemas/v4/wxs"}

passed = 0
failed = 0


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"OK   {name}")
    else:
        failed += 1
        print(f"FAIL {name}   {detail}")


def walk_dirs(node, prefix, out):
    """递归 Directory 树，把 File 的最终相对路径攒进 out。

    调用时传 INSTALLFOLDER 本身、prefix=None：它代表安装根，名字不进相对路径，
    但它的每个子目录名都是第一层。
    """
    for child in node:
        tag = child.tag.split("}")[-1]

        if tag == "Directory":
            name = child.get("Name")
            next_prefix = name if prefix is None else f"{prefix}/{name}"
            walk_dirs(child, next_prefix, out)

        elif tag == "Component":
            for f in child.findall("w:File", NS):
                fname = f.get("Name")
                if not fname:
                    continue
                rel = fname if prefix is None else f"{prefix}/{fname}"
                out.add(rel)


def main():
    msi = os.path.abspath(sys.argv[1])
    portable = os.path.abspath(sys.argv[2])
    work = os.path.abspath(os.path.join(os.path.dirname(msi), "..", "..", ".workbuddy", "tmp"))

    check("安装包存在", os.path.isfile(msi), msi)
    check("便携版目录存在", os.path.isdir(portable), portable)
    if failed:
        return 1

    os.makedirs(work, exist_ok=True)
    wxs = os.path.join(work, "verify_payload.wxs")
    if os.path.exists(wxs):
        os.remove(wxs)

    proc = subprocess.run(
        [WIX, "msi", "decompile", msi, "-o", wxs],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("MSI 可被反编译（数据库结构完整）", proc.returncode == 0 and os.path.isfile(wxs),
          proc.stderr[-300:] if proc.stderr else "")

    # ── 包级属性 ──
    with open(wxs, encoding="utf-8-sig") as fh:
        head = fh.read(4000)

    # 期望版本从 csproj 读，别写死 —— 写死会在下次升版本时误报（2026-10-17 踩过）
    expected_version = ""
    csproj = os.path.join(work, "..", "..", "ServerOpsHelper", "ServerOpsHelper.csproj")
    csproj = os.path.abspath(csproj)
    if os.path.isfile(csproj):
        with open(csproj, encoding="utf-8-sig") as fh:
            m = re.search(r"<Version>([^<]+)</Version>", fh.read())
            if m:
                expected_version = m.group(1).strip()

    msi_version = re.search(r'Version="([^"]+)"', head)
    msi_version = msi_version.group(1) if msi_version else ""

    check(f"包版本号与项目一致（期望 {expected_version or '未知'}）",
          bool(expected_version) and msi_version == expected_version,
          f"MSI 里是 {msi_version}")

    product_code = re.search(r'ProductCode="(\{[^}]+\})"', head)
    check("ProductCode 非通配符（同版本重出包不会变另一个产品）",
          bool(product_code) and product_code.group(1) != "{*}",
          product_code.group(1) if product_code else "读不到")

    check("保留了 DISABLEROLLBACK（否则装到 E 盘会报 1926）",
          'DISABLEROLLBACK' in head)

    # ── 重建安装布局 ──
    tree = ET.parse(wxs)
    root = tree.getroot()
    package = root.find("w:Package", NS)
    if package is None:
        package = root

    # Directory 可能挂在 <Directory> 下，也可能挂在 <StandardDirectory> 下
    # （WiX 对系统已知目录用后者），两种都要往下走。
    def find_install(node):
        for child in node:
            tag = child.tag.split("}")[-1]
            if tag in ("Directory", "StandardDirectory"):
                if child.get("Id") == "INSTALLFOLDER":
                    return child
                found = find_install(child)
                if found is not None:
                    return found
        return None

    install_folder = find_install(package)
    check("找得到 INSTALLFOLDER 这个安装根", install_folder is not None)
    if install_folder is None:
        return 1

    msi_paths = set()
    walk_dirs(install_folder, None, msi_paths)

    portable_paths = set()
    for dirpath, _, files in os.walk(portable):
        for name in files:
            full = os.path.join(dirpath, name)
            portable_paths.add(os.path.relpath(full, portable).replace("\\", "/"))

    print(f"\n   MSI 装 {len(msi_paths)} 个文件 / 参考目录 {len(portable_paths)} 个文件")

    only_msi = sorted(msi_paths - portable_paths)
    only_portable = sorted(portable_paths - msi_paths)

    check("文件集合完全一致（多一个少一个都算失败）",
          not only_msi and not only_portable,
          f"MSI 独有 {len(only_msi)} 个: {only_msi[:5]} / 便携版独有 {len(only_portable)} 个: {only_portable[:5]}")

    # ── 子目录层级：这是"拍平"事故的正面断言 ──
    subdirs = {p.split("/")[0] for p in msi_paths if "/" in p}
    print(f"   子目录：{sorted(subdirs)}")

    for must in ("TerminalAssets", "runtimes"):
        check(f"保留了子目录 {must}\\（拍平会让安装版终端打不开）",
              must in subdirs,
              f"实际子目录 {sorted(subdirs)}")

    # 反向断言：这几个文件绝不能出现在根目录
    flattened = [p for p in msi_paths if p in ("terminal.html", "xterm.js", "xterm.css", "addon-fit.js")]
    check("终端资源没有被拍到根目录", not flattened, f"发现 {flattened}")

    # ── 目录清理项：漏登记会让 ICE64 拦交付，也会留空目录 ──
    remove_folders = 0
    for elem in package.iter():
        if elem.tag.split("}")[-1] == "RemoveFolder":
            remove_folders += 1
    check("子目录都登记了卸载清理（RemoveFolder）", remove_folders >= 1,
          f"{remove_folders} 条")

    print(f"\n===== {passed} 通过 / {failed} 失败 =====")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
