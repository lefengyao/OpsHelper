"""不碰真实安装，验证 Inno 安装包的文件树是否完整（路径集合逐条比对）。

背景：`Probe/verify_inno_payload.py` 在本机已有同产品安装时会**主动跳过**
（否则会覆盖真实的「设置 → 应用」记录）。但"跳过"等于这一版没验过载荷。
这里换一条零风险的路：拿同一份 `.iss` 造一个**测试专用 AppId** 的安装包，
把 [Icons] / [Run] / [Code] 全去掉，只留 [Files]，静默装到 %TEMP% 再卸掉。

验的是同一件事：安装出来的相对路径集合 == publish 目录的相对路径集合
（历史 bug 是 MSI 把子目录拍平，文件总数一样、路径错 17 个，只数个数发现不了）。

用法：python .tools/verify_payload_safe.py
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INSTALLER = os.path.join(ROOT, "ServerOpsHelper", "Installer")
PUBLISH = os.path.join(ROOT, "ServerOpsHelper", "publish", "win-x64")
ISCC = os.environ.get("ISCC_EXE") or os.path.join(ROOT, ".tools", "InnoSetup6", "ISCC.exe")
TMP_ISS = os.path.join(INSTALLER, "_verify_payload_tmp.iss")

TEST_APPID = "{{00000000-0000-0000-0000-00000000FE01}}"


def rel_set(root):
    out = set()
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            out.add(os.path.relpath(os.path.join(dirpath, name), root).lower())
    return out


def build_iss():
    """从正式 .iss 派生一份只保留 [Files] 的测试脚本。"""
    src = open(os.path.join(INSTALLER, "ServerOpsHelper.iss"), encoding="utf-8-sig").read()

    src = src.replace('AppId={#MyAppId}', f"AppId={TEST_APPID}")
    src = src.replace("DisableDirPage=no", "DisableDirPage=yes")
    src = src.replace("UsePreviousAppDir=yes", "UsePreviousAppDir=no")
    src = src.replace("CloseApplications=yes", "CloseApplications=no")
    src = src.replace("SetupIconFile=..\\Assets\\appicon.ico", "")
    src = src.replace("OutputDir=..\\..\\release\\v{#MyAppVersion}", "OutputDir=" + OUTPUT_DIR)
    src = src.replace("OutputBaseFilename=ServerOpsHelper-Setup-v{#MyAppVersion}",
                      "OutputBaseFilename=VerifyPayload")
    src = src.replace("DisableWelcomePage=no", "DisableWelcomePage=yes")
    src = src.replace("DefaultDirName={autopf}\\ServerOpsHelper",
                      "DefaultDirName=" + INSTALL_DIR)

    # 只保留无副作用的段：[Setup] / [Languages] / [Messages] / [Files]
    # 注意不能"从某段开始整段截断" —— [Tasks] 在 [Files] 前面，那样会把 [Files] 一起切掉。
    parts = re.split(r"(?m)^\[(\w+)\]\s*$", src)
    kept = [parts[0]]
    for i in range(1, len(parts), 2):
        section, body = parts[i], parts[i + 1]
        if section in ("Setup", "Languages", "Messages", "Files"):
            kept.append(f"[{section}]" + body)
    src = "\n".join(kept)
    # 版权页之类不需要
    open(TMP_ISS, "w", encoding="utf-8").write(src)


OUTPUT_DIR = os.path.join(tempfile.gettempdir(), "opshelper-payload-check")
INSTALL_DIR = os.path.join(tempfile.gettempdir(), "opshelper-payload-install")


def main():
    if not os.path.exists(ISCC):
        print(f"找不到 ISCC.exe：{ISCC}")
        return 2
    if not os.path.isdir(PUBLISH):
        print(f"找不到 publish：{PUBLISH}")
        return 2

    shutil.rmtree(OUTPUT_DIR, ignore_errors=True)
    shutil.rmtree(INSTALL_DIR, ignore_errors=True)
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    build_iss()
    try:
        proc = subprocess.run([ISCC, "/Qp", TMP_ISS], capture_output=True,
                              text=True, encoding="utf-8", errors="replace", cwd=INSTALLER)
        if proc.returncode != 0:
            print("编译测试安装包失败：")
            print((proc.stdout or "") + (proc.stderr or ""))
            return 1

        setup = os.path.join(OUTPUT_DIR, "VerifyPayload.exe")
        if not os.path.exists(setup):
            print(f"没找到测试安装包：{setup}")
            return 1

        print(f"[1/3] 静默安装到 {INSTALL_DIR}")
        r = subprocess.run([setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                            "/SP-", f"/DIR={INSTALL_DIR}"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            print(f"安装失败，退出码 {r.returncode}\n{(r.stdout or '') + (r.stderr or '')}")
            return 1

        want = rel_set(PUBLISH)
        got = rel_set(INSTALL_DIR)
        missing = sorted(want - got)
        # 卸载程序自己的两个文件不是载荷，别算成"多装的东西"
        extra = sorted(p for p in (got - want) if not p.startswith("unins000."))

        print(f"[2/3] 比对路径：publish {len(want)} 个，安装后 {len(got)} 个")
        ok = True
        if missing:
            ok = False
            print(f"    FAIL 缺少 {len(missing)} 个：{missing[:8]}")
        if extra:
            ok = False
            print(f"    FAIL 多出 {len(extra)} 个：{extra[:8]}")
        # 子目录必须还在（拍平的话文件都在根下）
        for must in ("terminalassets", "runtimes"):
            if not any(p.startswith(must + os.sep) for p in got):
                ok = False
                print(f"    FAIL 子目录 {must}\\ 不存在 —— 文件被拍平了")
        if ok:
            print("    PASS 路径集合完全一致，子目录层级完好")

        print("[3/3] 静默卸载")
        unins = os.path.join(INSTALL_DIR, "unins000.exe")
        if os.path.exists(unins):
            subprocess.run([unins, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
                           capture_output=True, text=True, timeout=180)

        # Inno 的卸载程序会把自己复制到 %TEMP% 再删原目录，所以要等一会儿；
        # 立刻取 listdir 会看到还没消失的 unins000.exe，误判成"卸载不干净"。
        for _ in range(30):
            if not os.path.isdir(INSTALL_DIR) or not os.listdir(INSTALL_DIR):
                break
            time.sleep(0.5)

        left = os.listdir(INSTALL_DIR) if os.path.isdir(INSTALL_DIR) else []
        if left:
            print(f"    WARN 卸载后仍有残留：{left[:6]}")
        else:
            print("    PASS 安装目录已清空")

        shutil.rmtree(OUTPUT_DIR, ignore_errors=True)
        return 0 if ok else 1
    finally:
        if os.path.exists(TMP_ISS):
            os.remove(TMP_ISS)


if __name__ == "__main__":
    sys.exit(main())
