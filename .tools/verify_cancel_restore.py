"""验证「安装中途退出时，藏起来的主程序会被还原」。

为什么要单独验这个：
    prepareToInstall 里为了绕开"文件被映射"会把 {app}\\ServerOpsHelper.exe 改名成
    .stale-*，那一瞬间安装目录里是没有主程序的。如果用户在这之后取消安装（或安装失败），
    程序就会变成「双击图标提示找不到文件」—— 这比安装失败本身更糟。
    兜底逻辑在 DeinitializeSetup 里，本脚本用一个「故意中止」的测试安装包把它逼出来。

做法：
    1. 从真实 .iss 派生测试包（测试 AppId + 临时目录）；
    2. 先正常装一次（让 exe 就位）；
    3. 再跑一个「PrepareToInstall 藏完 exe 就返回错误」的变体；
    4. 断言：安装确实被中止，且 exe 已还原、没有 .stale-* 残留。

用法：python .tools/verify_cancel_restore.py
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INSTALLER = os.path.join(ROOT, "ServerOpsHelper", "Installer")
PUBLISH = os.path.join(ROOT, "ServerOpsHelper", "publish", "win-x64")
ISCC = os.environ.get("ISCC_EXE") or os.path.join(ROOT, ".tools", "InnoSetup6", "ISCC.exe")

OUT_DIR = os.path.join(tempfile.gettempdir(), "opshelper-cancel-out")
APP_DIR = os.path.join(tempfile.gettempdir(), "opshelper-cancel-test")
TMP_ISS = os.path.join(INSTALLER, "_verify_cancel_tmp.iss")
EXE_NAME = "ServerOpsHelper.exe"

ABORT_SNIPPET = """  Result := '';
  NeedsRestart := False;
  StashAppExe();
  Result := '测试用：故意在复制文件之前中止安装';
  Exit;
"""


def build_iss(abort):
    src = open(os.path.join(INSTALLER, "ServerOpsHelper.iss"), encoding="utf-8-sig").read()
    src = src.replace("AppId={#MyAppId}", "AppId={{00000000-0000-0000-0000-00000000FE03}}")
    src = src.replace("DisableDirPage=no", "DisableDirPage=yes")
    src = src.replace("UsePreviousAppDir=yes", "UsePreviousAppDir=no")
    src = src.replace("CloseApplications=yes", "CloseApplications=no")
    src = src.replace("SetupIconFile=..\\Assets\\appicon.ico", "")
    src = src.replace("OutputDir=..\\..\\release\\v{#MyAppVersion}", "OutputDir=" + OUT_DIR)
    src = src.replace("OutputBaseFilename=ServerOpsHelper-Setup-v{#MyAppVersion}",
                      "OutputBaseFilename=CancelProbe")
    src = src.replace("DisableWelcomePage=no", "DisableWelcomePage=yes")
    src = src.replace("DefaultDirName={autopf}\\ServerOpsHelper", "DefaultDirName=" + APP_DIR)

    if abort:
        old = "  Result := '';\n  NeedsRestart := False;\n"
        assert src.count(old) == 1, "PrepareToInstall 开头未匹配"
        src = src.replace(old, ABORT_SNIPPET)

    parts = re.split(r"(?m)^\[(\w+)\]\s*$", src)
    kept = [parts[0]]
    for i in range(1, len(parts), 2):
        if parts[i] in ("Setup", "Languages", "Messages", "Files", "Code"):
            kept.append(f"[{parts[i]}]" + parts[i + 1])
    open(TMP_ISS, "w", encoding="utf-8").write("\n".join(kept))


def run_setup(setup):
    r = subprocess.run([setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                        "/SP-", f"/DIR={APP_DIR}"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode


def compile_probe(abort):
    build_iss(abort)
    try:
        proc = subprocess.run([ISCC, "/Qp", TMP_ISS], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", cwd=INSTALLER)
        if proc.returncode != 0:
            print("编译失败：")
            print((proc.stdout or "") + (proc.stderr or ""))
            return None
        setup = os.path.join(OUT_DIR, "CancelProbe.exe")
        os.replace(setup, os.path.join(OUT_DIR, "CancelProbe_abort.exe" if abort else "CancelProbe_ok.exe"))
        return os.path.join(OUT_DIR, "CancelProbe_abort.exe" if abort else "CancelProbe_ok.exe")
    finally:
        if os.path.exists(TMP_ISS):
            os.remove(TMP_ISS)


def main():
    if not os.path.exists(ISCC):
        print(f"找不到 ISCC：{ISCC}")
        return 2
    for d in (OUT_DIR, APP_DIR):
        shutil.rmtree(d, ignore_errors=True)
    os.makedirs(OUT_DIR, exist_ok=True)

    ok_setup = compile_probe(False)
    abort_setup = compile_probe(True)
    if not ok_setup or not abort_setup:
        return 1

    print("[1/3] 先正常装一次")
    code = run_setup(ok_setup)
    exe = os.path.join(APP_DIR, EXE_NAME)
    if code != 0 or not os.path.exists(exe):
        print(f"  FAIL 首次安装失败（退出码 {code}）")
        return 1
    print("  PASS 已安装，主程序就位")

    print("[2/3] 跑「藏完 exe 就中止」的变体")
    code = run_setup(abort_setup)
    print(f"  退出码 {code}（预期非 0）")
    if code == 0:
        print("  FAIL 中止没有生效，这个用例没有意义")
        return 1

    print("[3/3] 校验还原")
    ok = True
    if not os.path.exists(exe):
        ok = False
        print("  FAIL 主程序没被还原 —— 用户双击图标会提示找不到文件")
    else:
        print("  PASS 主程序已还原到原位")

    stale = [n for n in os.listdir(APP_DIR) if ".stale-" in n]
    if stale:
        ok = False
        print(f"  FAIL 仍有改名残留在目录里：{stale}")
    else:
        print("  PASS 没有 .stale-* 残留")

    print("=== 中止还原验证通过 ===" if ok else "=== 中止还原验证失败 ===")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
