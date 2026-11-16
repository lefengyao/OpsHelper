"""出一版完整的交付物：发布 → Inno 安装包 → 便携包 zip → 刷新便携版文件夹 → 清理中间产物。

用法：
    cd ServerOpsHelper/Installer
    python release_all.py

**产物落点**：`<工作区>/release/v<版本>/`，一个版本一个目录：

    release/
      v1.9.3/
        ServerOpsHelper-Setup-v1.9.3.exe      单文件安装程序（主推）
        ServerOpsHelper-便携版-v1.9.3.zip     便携压缩包
        ServerOpsHelper-便携版-v1.9.3/        同上解压版（可直接双击运行）

    以前所有产物平铺在一个「发布」目录里，出新版必须先把旧版删掉，
    否则文件名混在一起分不清，历史交付物也留不下来。
    改成按版本号分目录后，出新版不影响旧版，要回溯哪一版都能找到。

**2026-10-17 换掉了 MSI/Burn**：以前是 WiX 出 MSI 再套一层 Burn 引导程序，
MSI 的事务回滚会在目标盘根建 `E:\\Config.Msi`，那个目录的 ACL 只有 SYSTEM+管理员，
非提权安装/修复必然报「错误 5 / 1926」，而且每次安装都被重置 —— 治不了本。
现在改用 Inno Setup 6：编译出来就是普通 exe，不碰 Windows Installer，
这一整类故障从根上消失。WiX 的 `build_installer.py` / `build_bundle.py` / `*.wxs`
保留在仓库里备查，但**不再参与出包**。

版本号唯一来源是 `ServerOpsHelper.csproj` 的 `<Version>`，由 build_inno.py 读出来传给 ISCC，
不会出现「脚本里写的版本」和「实际编出来的版本」不一致。

依赖：
  · dotnet 包装器（沙箱里需要它注入 Windows 环境变量，否则 NuGet 会报
    「Value cannot be null. (Parameter 'path1')」）。默认取 %TEMP%\\dotbuild.py，
    可用环境变量 DOTBUILD_PY 指定。
  · ISCC.exe（Inno Setup 6）。默认取 <工作区>/.tools/InnoSetup6/ISCC.exe，
    可用环境变量 ISCC_EXE 指定。
"""
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(HERE)
ROOT = os.path.dirname(PROJ)

PUBLISH = os.path.join(PROJ, "publish", "win-x64")

# 交付物根目录：<工作区>/release/
RELEASE_ROOT = os.path.join(ROOT, "release")

# 当前版本的交付目录，由 setup_release_dir() 在 main() 里按版本号填好。
# 保留这个名字是为了让下面各处引用不用改。
RELEASE = ""

TOOLS_DIR = os.path.join(os.path.expanduser("~"), ".workbuddy", "tools")


def _pick(env_name, durable, legacy_name):
    """工具优先取环境变量，其次取持久目录，最后退回 %TEMP%。

    之所以有"持久目录"这一层：2026-09-20 系统清理了一次 %TEMP%，
    把构建包装器和 wix5 全删了（连带一次"构建通过"其实是假绿），
    所以这些工具现在固定放在 ~/.workbuddy/tools 下。
    """
    from_env = os.environ.get(env_name)

    if from_env:
        return from_env

    if os.path.exists(durable):
        return durable

    return os.path.join(tempfile.gettempdir(), legacy_name)


DOTBUILD = _pick("DOTBUILD_PY", os.path.join(TOOLS_DIR, "dotbuild.py"), "dotbuild.py")
ISCC = _pick("ISCC_EXE",
             os.path.join(ROOT, ".tools", "InnoSetup6", "ISCC.exe"),
             os.path.join("InnoSetup6", "ISCC.exe"))


def mb(path):
    if os.path.isfile(path):
        return os.path.getsize(path) / 1024 / 1024
    total = 0
    for root, _d, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total / 1024 / 1024


def read_version():
    """版本号唯一来源：csproj 的 <Version>（与 build_inno.py 保持一致）。"""
    with open(os.path.join(PROJ, "ServerOpsHelper.csproj"), encoding="utf-8") as fh:
        m = re.search(r"<Version>([\d.]+)</Version>", fh.read())
    if not m:
        raise SystemExit("csproj 里找不到 <Version>，无法确定版本号")
    return m.group(1)


def setup_release_dir(version):
    """确定并创建当前版本的交付目录 `<工作区>/release/v<版本>/`。

    按版本号分目录的好处：出新版不用先删旧版（旧版各自留档），
    也不会出现"同名文件混在一个目录里分不清是哪一版"的情况。
    """
    global RELEASE
    RELEASE = os.path.join(RELEASE_ROOT, f"v{version}")
    os.makedirs(RELEASE, exist_ok=True)
    return RELEASE


def run(label, cwd, args):
    print(f"--- {label} ---")
    proc = subprocess.run(
        [sys.executable, DOTBUILD, cwd] + args,
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    for line in (proc.stdout or "").splitlines():
        if "error" in line.lower() or "警告" in line or "错误" in line or "->" in line:
            print("   ", line.strip()[:180])
    if proc.returncode != 0:
        print(f"    {label} 失败（退出码 {proc.returncode}）")
        if proc.stderr:
            print(proc.stderr.strip()[:600])
        sys.exit(1)


def make_zip(version):
    """打便携包 zip。

    直接以 "w" 模式打开就能覆盖同名旧文件（zipfile 自己会截断），
    **不需要**先 `os.remove` —— 多一次显式删除没有好处，在受限环境里
    还可能被"批量删除"策略拦下来，导致整个出包流程断在半路（2026-09-29 撞过）。
    """
    path = os.path.join(RELEASE, f"ServerOpsHelper-便携版-v{version}.zip")
    top = f"ServerOpsHelper-便携版-v{version}"
    n = 0
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _d, files in os.walk(PUBLISH):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, os.path.join(top, os.path.relpath(full, PUBLISH)))
                n += 1
    print(f"[便携包] {os.path.basename(path)}  {n} 个文件  {mb(path):.1f} MB")
    return path


def refresh_portable(version):
    """把已解压的便携版文件夹刷成当前版本（内容与 zip 逐项一致）。

    位置：`release/v<版本>/ServerOpsHelper-便携版-v<版本>/`。

    ⚠️ 实现上**先把新内容复制到 staging 目录，复制完再整体替换旧目录**，
    而不是"先把旧目录清空、再往里复制"。后者一旦中途失败（磁盘满、文件被占用、
    或沙箱的批量删除拦截 —— 2026-09-29 就撞上过），会留下"删了一半又放进一半"的
    混合状态：文件数看着不少、其实是新旧混杂，从外面完全看不出来。
    用 staging 的话，中途失败时旧目录仍是完整的，不会产出半成品。
    """
    target = os.path.join(RELEASE, f"ServerOpsHelper-便携版-v{version}")
    staging = target + ".__staging__"

    # 兼容旧布局：便携版文件夹以前放在工作区根目录，若还留着就搬进来（只搬不删）
    legacy = os.path.join(ROOT, f"ServerOpsHelper-便携版-v{version}")
    if os.path.isdir(legacy) and not os.path.exists(target):
        os.makedirs(RELEASE, exist_ok=True)
        shutil.move(legacy, target)
        print(f"[便携版文件夹] 已从工作区根目录迁入 release/v{version}/")

    # 1) 先把新内容完整复制到 staging（这一步失败不会破坏现有 target）
    if os.path.isdir(staging):
        shutil.rmtree(staging)
    os.makedirs(staging)

    for name in os.listdir(PUBLISH):
        s = os.path.join(PUBLISH, name)
        d = os.path.join(staging, name)
        shutil.copytree(s, d) if os.path.isdir(s) else shutil.copyfile(s, d)

    # 2) 复制成功后再替换（这一步失败也只是留下一个 staging 目录，target 仍是完整的）
    if os.path.isdir(target):
        shutil.rmtree(target)
    os.rename(staging, target)

    print(f"[便携版文件夹] release/v{version}/{os.path.basename(target)}  {mb(target):.1f} MB")


def cleanup(version):
    """删掉可再生的中间产物。

    ⚠️ 刻意**不再删除旧版本交付物**：产物现在按 `release/v<版本>/` 分目录存放，
    历史版本各自留档、互不干扰 —— 这正是改成这个结构的目的。
    以前平铺在一个目录时不得不"出新版就删旧版"，现在不需要了。

    这里只清理 bin/obj/publish 这类「每次构建都会重新生成、留着纯占地方」的中间产物。
    """
    freed = 0
    stale = [
        (os.path.join(PROJ, "bin", "Release"), "bin/Release（自包含中间产物，可再生）"),
        (os.path.join(PROJ, "obj", "Release"), "obj/Release"),
        (PUBLISH, "publish（内容已复制进便携版文件夹）"),
        (os.path.join(HERE, "ServerOpsHelper.wixpdb"), "WiX 符号库残留"),
    ]

    # 旧布局遗留：工作区根目录下的便携版文件夹。
    # 内容已经复制进 release/ 里了，这里清掉根目录的副本，免得两处占空间、还容易改错地方。
    for d in glob.glob(os.path.join(ROOT, "ServerOpsHelper-便携版-v*")):
        if os.path.isdir(d):
            stale.append((d, os.path.basename(d) + "（旧布局遗留，已迁入 release/）"))

    for path, label in stale:
        if not os.path.exists(path):
            continue
        size = mb(path)
        try:
            shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)
        except Exception as exc:  # noqa: BLE001 —— 见下方说明：清理失败不许影响交付
            # 清理是「顺手省点地方」，不是交付物的一部分：
            # 被占用或被安全策略拦下来时**跳过并继续**，绝不能让出包流程卡在最后一步。
            # （2026-10-17：bin/Release 里 269 个文件触发了批量删除确认，直接把流程打断了）
            print(f"[清理] {label:46} 跳过（{exc.__class__.__name__}）")
            continue
        freed += size
        print(f"[清理] {label:46} 释放 {size:7.1f} MB")

    print(f"[清理] 合计释放约 {freed:.1f} MB")


def main():
    if not os.path.exists(DOTBUILD):
        print(f"找不到 dotnet 包装器：{DOTBUILD}\n可用环境变量 DOTBUILD_PY 指定路径。")
        return 2
    if not os.path.exists(ISCC):
        print(f"找不到 Inno Setup 编译器：{ISCC}\n可用环境变量 ISCC_EXE 指定路径"
              f"（把 Inno Setup 6 便携版放到 .tools/InnoSetup6/ 即可）。")
        return 2

    version = read_version()
    release_dir = setup_release_dir(version)
    print(f"版本：{version}")
    print(f"交付目录：{release_dir}\n")

    run("重新发布（自包含 win-x64）", PROJ,
        ["publish", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "-p:PublishReadyToRun=false", "-p:DebugType=none", "-o", "publish/win-x64",
         "-v", "q", "--nologo"])

    print("--- 打单文件 EXE 安装程序（Inno Setup 6）---")
    proc = subprocess.run(
        [sys.executable, os.path.join(HERE, "build_inno.py")],
        cwd=HERE,
        env={**os.environ, "ISCC_EXE": ISCC},
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    for line in (proc.stdout or "").splitlines():
        s = line.strip()
        if any(k in s for k in ("已生成", "载荷", "error", "错误", "失败")):
            print("   ", s[:180])
    if proc.returncode != 0:
        print("    打 EXE 安装程序失败（已阻断，不产出交付物）")
        if proc.stdout:
            print(proc.stdout.strip()[-1200:])
        if proc.stderr:
            print(proc.stderr.strip()[:600])
        return 1

    # ── 装/卸回归 ──
    # 真的在临时目录里装一遍、卸一遍：比对相对路径集合（不是文件总数 ——
    # 当年 MSI 两边都是 269 个、路径却错了 17 个），确认程序目录能清干净、
    # 数据目录一个文件都没动。本机已安装时会自动跳过（否则会覆盖真实的
    # 「设置→应用」注册项），那种情况请先卸载再单独跑这个脚本。
    print("--- 校验安装包载荷与安装/卸载（临时目录，不碰数据）---")
    verifier = os.path.join(ROOT, "Probe", "verify_inno_payload.py")
    setup_exe = os.path.join(release_dir, f"ServerOpsHelper-Setup-v{version}.exe")
    proc = subprocess.run(
        [sys.executable, verifier, setup_exe, PUBLISH, "--skip-if-installed"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env={**os.environ, "PYTHONIOENCODING": "utf-8"},
    )
    for line in (proc.stdout or "").splitlines():
        s = line.strip()
        if s.startswith(("PASS", "FAIL", "=====", "结果", "[跳过]", "安装包载荷")):
            print("   ", s[:180])
    if proc.returncode != 0:
        print("    安装包校验失败（已阻断，不产出交付物）")
        if proc.stdout:
            print(proc.stdout.strip()[-1200:])
        if proc.stderr:
            print(proc.stderr.strip()[:600])
        return 1

    make_zip(version)
    refresh_portable(version)
    cleanup(version)

    print()
    print("=== 交付目录 ===")
    for name in sorted(os.listdir(RELEASE)):
        print(f"   {mb(os.path.join(RELEASE, name)):>7.1f} MB  {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
