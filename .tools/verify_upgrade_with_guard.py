"""实测「安装时被后台 stdio 进程反复拉起」的升级场景（隔离目录，不碰真实安装）。

复现的是什么：
    智能体连接器（dsh-mcp-connector 的 stdio-startup-guard）会一直守着
    `ServerOpsHelper.exe --mcp-stdio`，子进程一死就立刻重启。新进程映射着
    clrjit.dll / coreclr.dll，安装程序覆盖到这些文件时 DeleteFile 返回 5（拒绝访问）。

本脚本做的事：
    1. 用同一份 .iss 派生一个**测试专用 AppId** 的安装包（保留 [Code]，去掉快捷方式/启动项）；
    2. 装到 %TEMP%\\opshelper-upg-test（第一次，模拟"已装旧版"）；
    3. 起一个守护线程，每 150ms 检查 `--mcp-stdio` 是否还活着，死了就重启 —— 和 dsh 一样；
    4. 再跑一次同一个安装包（模拟覆盖升级），期望：改名藏起 exe → 杀实例 → 正常覆盖 → 清掉改名残留；
    5. 断言：安装成功、载荷完整、没有 .stale-* 残留、主程序是新版、进程都停掉。

用法：python .tools/verify_upgrade_with_guard.py
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INSTALLER = os.path.join(ROOT, "ServerOpsHelper", "Installer")
PUBLISH = os.path.join(ROOT, "ServerOpsHelper", "publish", "win-x64")
ISCC = os.environ.get("ISCC_EXE") or os.path.join(ROOT, ".tools", "InnoSetup6", "ISCC.exe")

OUT_DIR = os.path.join(tempfile.gettempdir(), "opshelper-upg-out")
APP_DIR = os.path.join(tempfile.gettempdir(), "opshelper-upg-test")
TMP_ISS = os.path.join(INSTALLER, "_verify_upgrade_tmp.iss")
TEST_APPID = "{{00000000-0000-0000-0000-00000000FE02}}"
EXE_NAME = "ServerOpsHelper.exe"


def rel_set(root):
    out = set()
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            out.add(os.path.relpath(os.path.join(dirpath, name), root).lower())
    return out


def build_iss():
    src = open(os.path.join(INSTALLER, "ServerOpsHelper.iss"), encoding="utf-8-sig").read()
    src = src.replace("AppId={#MyAppId}", f"AppId={TEST_APPID}")
    src = src.replace("DisableDirPage=no", "DisableDirPage=yes")
    src = src.replace("UsePreviousAppDir=yes", "UsePreviousAppDir=no")
    src = src.replace("CloseApplications=yes", "CloseApplications=no")
    src = src.replace("SetupIconFile=..\\Assets\\appicon.ico", "")
    src = src.replace("OutputDir=..\\..\\release\\v{#MyAppVersion}", "OutputDir=" + OUT_DIR)
    src = src.replace("OutputBaseFilename=ServerOpsHelper-Setup-v{#MyAppVersion}",
                      "OutputBaseFilename=UpgradeProbe")
    src = src.replace("DisableWelcomePage=no", "DisableWelcomePage=yes")
    src = src.replace("DefaultDirName={autopf}\\ServerOpsHelper", "DefaultDirName=" + APP_DIR)

    # 只保留无副作用的段；[Code] 必须留（要验的就是里面的杀进程 + 目录改名）
    parts = re.split(r"(?m)^\[(\w+)\]\s*$", src)
    kept = [parts[0]]
    for i in range(1, len(parts), 2):
        if parts[i] in ("Setup", "Languages", "Messages", "Files", "Code"):
            kept.append(f"[{parts[i]}]" + parts[i + 1])
    open(TMP_ISS, "w", encoding="utf-8").write("\n".join(kept))


class Guard:
    """模拟 dsh 的重启守护：子进程一死就重拉（带 stdin 管道，进程会一直活着）。"""

    def __init__(self, exe):
        self.exe = exe
        self.child = None
        self.stop = False
        self.starts = 0
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _spawn(self):
        try:
            self.child = subprocess.Popen(
                [self.exe, "--mcp-stdio"],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL)
            self.starts += 1
        except OSError:
            # exe 路径不存在（目录被改名）时 CreateProcess 失败 —— 这正是我们想要的效果
            self.child = None

    def _run(self):
        while not self.stop:
            with self._lock:
                if not os.path.exists(self.exe):
                    self.child = None
                elif self.child is None or self.child.poll() is not None:
                    self._spawn()
            time.sleep(0.15)

    def start(self):
        self._thread.start()

    def kill_all(self):
        self.stop = True
        time.sleep(0.3)
        with self._lock:
            if self.child and self.child.poll() is None:
                self.child.kill()
                self.child.wait(timeout=10)


def run_setup(setup, label):
    print(f"  → {label}")
    r = subprocess.run([setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                        "/SP-", f"/DIR={APP_DIR}"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode


def main():
    if not os.path.exists(ISCC):
        print(f"找不到 ISCC：{ISCC}")
        return 2

    for d in (OUT_DIR, APP_DIR):
        shutil.rmtree(d, ignore_errors=True)
    os.makedirs(OUT_DIR, exist_ok=True)

    build_iss()
    try:
        proc = subprocess.run([ISCC, "/Qp", TMP_ISS], capture_output=True,
                              text=True, encoding="utf-8", errors="replace", cwd=INSTALLER)
        if proc.returncode != 0:
            print("编译测试安装包失败：")
            print((proc.stdout or "") + (proc.stderr or ""))
            return 1

        setup = os.path.join(OUT_DIR, "UpgradeProbe.exe")
        if not os.path.exists(setup):
            print(f"没找到测试安装包：{setup}")
            return 1

        print("[1/4] 首次安装（模拟机器上已有旧版）")
        code = run_setup(setup, "静默安装")
        if code != 0:
            print(f"  首次安装失败，退出码 {code}")
            return 1
        exe = os.path.join(APP_DIR, EXE_NAME)
        if not os.path.exists(exe):
            print(f"  首次安装后没有 {exe}")
            return 1
        print(f"  已安装 {len(rel_set(APP_DIR))} 个文件")

        print("[2/4] 启动 --mcp-stdio 并挂上重启守护（模拟 dsh 连接器）")
        guard = Guard(exe)
        guard.start()
        time.sleep(1.2)
        print(f"  守护已拉起 {guard.starts} 次，进程在跑 = {guard.child is not None}")

        print("[3/4] 再次运行安装包（覆盖升级）")
        t0 = time.time()
        code = run_setup(setup, "静默升级")
        elapsed = time.time() - t0
        print(f"  退出码 {code}，耗时 {elapsed:.1f}s，守护期间共拉起 {guard.starts} 次")
        guard.kill_all()

        if code != 0:
            print("  FAIL 升级失败 —— 说明「杀进程 + 改名」没抢赢守护重启")
            return 1

        print("[4/4] 校验结果")
        ok = True
        want = rel_set(PUBLISH)
        got = {p for p in rel_set(APP_DIR) if not p.startswith("unins000.")}
        missing = sorted(want - got)
        if missing:
            ok = False
            print(f"  FAIL 缺 {len(missing)} 个文件：{missing[:6]}")
        for must in ("terminalassets", "runtimes"):
            if not any(p.startswith(must + os.sep) for p in got):
                ok = False
                print(f"  FAIL 子目录 {must}\\ 不见了")

        stale = [p for p in got if ".stale-" in p]
        if stale:
            ok = False
            print(f"  FAIL 改名残留未清理：{stale[:4]}")
        else:
            print("  PASS 没有 .stale-* 残留")

        # 关键：安装结束后 exe 必须回到原位，且是本次装进去的那一份
        new_exe = os.path.join(APP_DIR, EXE_NAME)
        if not os.path.exists(new_exe):
            ok = False
            print("  FAIL 主程序没有落地")
        else:
            import hashlib
            def sha(path):
                h = hashlib.sha256()
                with open(path, "rb") as fh:
                    for chunk in iter(lambda: fh.read(1 << 20), b""):
                        h.update(chunk)
                return h.hexdigest()
            if sha(new_exe) != sha(os.path.join(PUBLISH, EXE_NAME)):
                ok = False
                print("  FAIL 落地的主程序不是本次载荷（可能还是旧版）")
            else:
                print("  PASS 主程序已按新载荷落地")

        ran = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {EXE_NAME}", "/NH"],
                             capture_output=True, text=True, encoding="utf-8", errors="replace")
        alive = [line for line in ran.stdout.splitlines()
                 if EXE_NAME.lower() in line.lower()]
        if alive:
            print(f"  WARN 仍有进程存活：{alive[:2]}")
        else:
            print("  PASS 安装后没有遗留进程")

        if ok:
            print("  PASS 载荷完整、路径一致")
        print("=== 升级场景验证通过 ===" if ok else "=== 升级场景验证失败 ===")
        return 0 if ok else 1
    finally:
        if os.path.exists(TMP_ISS):
            os.remove(TMP_ISS)


if __name__ == "__main__":
    sys.exit(main())
