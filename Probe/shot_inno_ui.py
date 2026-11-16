"""截 Inno 安装向导的图，确认「中文 + 安装路径可见」这两个老板在意的点。

和 shot_zh_ui.py 的区别：那个用 uiautomation（沙箱里没装这个模块），这里只用
ctypes + PIL：
  · 按 PID 枚举顶层窗口找向导（不用标题匹配，标题随语言变）；
  · 用 **Enter** 翻页 —— Inno 每页的默认按钮就是「下一步」，比模拟鼠标点坐标稳；
  · 截图用 ImageGrab 抓窗口矩形。

用法：python shot_inno_ui.py [安装包路径]
产物：Probe/shots/inno_0N_*.png
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import ImageGrab

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.dirname(HERE)
SHOT_DIR = os.path.join(HERE, "shots")

user32 = ctypes.windll.user32
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]

# 高 DPI 缩放下 GetWindowRect 给的是逻辑像素、ImageGrab 抓的是物理像素，
# 两者不一致会截出「框错位」的图（2026-10-17 实测：截到了窗口旁边的聊天窗口）。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

TITLE_KEY = "服务器运维助手"


def windows_by_title(key=TITLE_KEY):
    """按标题找向导窗口。

    ⚠️ 不能按 PID 找：Inno 的 SetupLdr 在 64 位系统上会把自身重新执行一次
    （%TEMP%\\is-XXXX.tmp\\Setup.tmp），真正的窗口属于**另一个进程**，
    拿 subprocess 的 pid 去枚举永远是空的（2026-10-17 实测）。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _l):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if key in buf.value:
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            if r.right - r.left > 200 and r.bottom - r.top > 150:
                found.append((hwnd, r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumWindows(cb, 0)
    return found


def wait_window(_pid=None, timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        w = windows_by_title()
        if w:
            return w[0][0]
        time.sleep(0.4)
    return None


def shot(hwnd, name):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    os.makedirs(SHOT_DIR, exist_ok=True)
    path = os.path.join(SHOT_DIR, name)
    img.save(path)
    print(f"[截图] {path}  {img.size[0]}x{img.size[1]}")
    return path


def press_enter(hwnd):
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.3)
    user32.keybd_event(0x0D, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(0x0D, 0, 2, 0)


def find_setup():
    if len(sys.argv) > 1:
        return os.path.abspath(sys.argv[1])
    release = os.path.join(WORKSPACE, "release")
    best = None
    for name in os.listdir(release):
        d = os.path.join(release, name)
        if not os.path.isdir(d) or not name.startswith("v"):
            continue
        for f in os.listdir(d):
            if f.endswith(".exe") and f.startswith("ServerOpsHelper-Setup"):
                p = os.path.join(d, f)
                if best is None or os.path.getmtime(p) > os.path.getmtime(best):
                    best = p
    return best


def main():
    exe = find_setup()
    if not exe or not os.path.exists(exe):
        print("找不到安装包")
        return 2
    print(f"安装包：{exe}")

    proc = subprocess.Popen([exe, "/NORESTART"])
    try:
        hwnd = wait_window(proc.pid)
        if not hwnd:
            print("FAIL 等不到安装向导窗口")
            return 1
        time.sleep(0.8)
        shot(hwnd, "inno_01_welcome.png")

        for idx, page in enumerate(("02_scope", "03_dir", "04_tasks"), start=2):
            press_enter(hwnd)
            time.sleep(1.2)
            shot(hwnd, f"inno_{idx}_{page}.png")

        # Esc = 取消，退出向导（不真的重装）
        user32.SetForegroundWindow(hwnd)
        user32.keybd_event(0x1B, 0, 0, 0)
        user32.keybd_event(0x1B, 0, 2, 0)
        time.sleep(1.0)
        return 0
    finally:
        for _ in range(10):
            if proc.poll() is not None:
                break
            time.sleep(0.5)
        if proc.poll() is None:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
