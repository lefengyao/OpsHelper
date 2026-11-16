"""验证安装程序界面已中文化 + 欢迎页已有说明文字（一键跑完，出截图 + 文本判定）。

为什么要专门写这个（2026-09-21 踩坑记录，都是"看不见界面"导致的）：
  1. 【界面不在父进程】Burn 的 UI 由**独立子进程 wixstdba.exe** 绘制。
     按父进程 pid 去 EnumWindows 只能拿到一个 0x0 的 'WixBurnMessageWindow'，
     真正的窗口在子进程里 —— 必须先查子进程 pid 再枚举，见 find_ui_windows()。
  2. 【窗口先建好再画】窗口刚创建时 GetWindowRect 返回 (0,0,0,0)，
     必须等宽高都 > 200 才算可以截图，否则截出 0x0 空图。
  3. 【句柄参数必须声明 argtypes】ctypes 默认把句柄当 32 位 int，
     hwnd 一大就 OverflowError: int too long to convert。
     但 GetWindowRect 的 rect 参数要声明成 POINTER(RECT)，
     写成 POINTER(c_void_p) 会让 byref(RECT()) 直接被拒。
  4. 【UIA 必须包在 UIAutomationInitializerInThread 里】子线程没有 STA，
     直接调 uiautomation 会报「尚未调用 CoInitialize」。
  5. 【点击必须用 UIA Invoke，不要用鼠标坐标】实测 SetCursorPos+SendInput
     光标确实移到了按钮中心（GetCursorPos 验证过），但 wixstdba 的按钮
     不响应合成鼠标事件；只有 UIAutomation 的 InvokePattern 能真正触发。

判定与截图一一对应，截图存到 Probe/shots/，可直接给人看。
用法：python shot_zh_ui.py
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import threading
import time

from PIL import Image
import uiautomation as auto

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.dirname(HERE)
SHOT_DIR = os.path.join(HERE, "shots")


def find_setup_exe():
    """自动定位最新的安装程序：`release/v<版本>/ServerOpsHelper-Setup-v<版本>.exe`。

    以前这里是**硬编码的完整路径 + 版本号**，每发一版都得回来改一次；
    2026-09-29 发现它还指着早就被删掉的 v1.5.1 —— 忘改就会去验一个不存在的包。
    现在改成扫 `release/` 下所有版本目录、按版本号取最新。
    """
    root = os.path.join(WORKSPACE, "release")
    if not os.path.isdir(root):
        return None

    prefix, suffix = "ServerOpsHelper-Setup-v", ".exe"
    candidates = []
    for ver_dir in os.listdir(root):
        d = os.path.join(root, ver_dir)
        if not os.path.isdir(d):
            continue
        for name in os.listdir(d):
            if name.startswith(prefix) and name.endswith(suffix):
                candidates.append(os.path.join(d, name))

    if not candidates:
        return None

    def version_key(path):
        raw = os.path.basename(path)[len(prefix):-len(suffix)]
        try:
            return tuple(int(x) for x in raw.split("."))
        except ValueError:
            return (0,)

    return max(candidates, key=version_key)


EXE = find_setup_exe() or r"<未找到 release/ 下的安装程序>"

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32


class RECT(ctypes.Structure):
    _fields_ = [("left", wt.LONG), ("top", wt.LONG), ("right", wt.LONG), ("bottom", wt.LONG)]


# 见文件头第 3 条：句柄参数必须声明类型，RECT 指针要声明成 POINTER(RECT)
user32.GetWindowDC.argtypes = [wt.HWND]
user32.GetWindowDC.restype = ctypes.c_void_p
user32.ReleaseDC.argtypes = [wt.HWND, ctypes.c_void_p]
user32.ReleaseDC.restype = ctypes.c_int
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(RECT)]
user32.GetWindowRect.restype = wt.BOOL
user32.PrintWindow.argtypes = [wt.HWND, ctypes.c_void_p, wt.UINT]
user32.PrintWindow.restype = wt.BOOL

gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
gdi32.CreateCompatibleBitmap.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = ctypes.c_void_p
gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.SelectObject.restype = ctypes.c_void_p
gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
gdi32.DeleteObject.restype = wt.BOOL
gdi32.DeleteDC.argtypes = [ctypes.c_void_p]
gdi32.DeleteDC.restype = wt.BOOL
gdi32.GetDIBits.argtypes = [
    ctypes.c_void_p, ctypes.c_void_p, wt.UINT, wt.UINT,
    ctypes.c_void_p, ctypes.c_void_p, wt.UINT,
]
gdi32.GetDIBits.restype = ctypes.c_int

# 进程 DPI 感知：不做的话 GetWindowRect 给逻辑像素、PrintWindow 给物理像素，
# 截图会只剩左上角一小块（上一轮踩过：498x336 残图）
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except Exception:  # noqa: BLE001
    user32.SetProcessDPIAware()

PW_CLIENTONLY = 0x00000001
PW_RENDERFULLCONTENT = 0x00000002
DIB_RGB_COLORS = 0

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def child_pids(pid):
    """取某进程的直接子进程 id（见文件头第 1 条：界面在 wixstdba.exe 子进程里）。"""
    try:
        ps = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             f"(Get-CimInstance Win32_Process -Filter \"ParentProcessId={pid}\").ProcessId"],
            capture_output=True, text=True, timeout=20,
        )
        return [int(x) for x in ps.stdout.split() if x.strip().isdigit()]
    except Exception:  # noqa: BLE001
        return []


def _enum_for_pid(pid):
    found = []

    def cb(hwnd, _lparam):
        wpid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value != pid or not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(length + 2)
        user32.GetWindowTextW(hwnd, buf, length + 2)
        r = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        found.append((hwnd, buf.value, (r.left, r.top, r.right, r.bottom)))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def find_ui_windows(pid):
    """在 pid 及其全部后代进程里找可见窗口（界面在 wixstdba 子进程）。"""
    pids = {pid} | set(child_pids(pid))
    for c in list(pids):
        if c != pid:
            pids |= set(child_pids(c))
    wins = []
    for p in pids:
        wins.extend(_enum_for_pid(p))
    return wins


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def print_window(hwnd, path):
    """PrintWindow 抓整个窗口（wixstdba 用 DirectComposition 渲染，必须带 RENDERFULLCONTENT）。"""
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    if w <= 0 or h <= 0:
        return None, (w, h)

    hdc = user32.GetWindowDC(hwnd)
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    if not (hdc and memdc and bmp):
        return None, (w, h)
    gdi32.SelectObject(memdc, bmp)

    if not user32.PrintWindow(hwnd, memdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT):
        user32.PrintWindow(hwnd, memdc, 0)

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h  # 负 = 自上而下
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32

    buf = ctypes.create_string_buffer(w * h * 4)
    got = gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bmi), DIB_RGB_COLORS)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(memdc)
    user32.ReleaseDC(hwnd, hdc)

    if not got:
        return None, (w, h)

    img = Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1).convert("RGB")
    img.save(path)
    return img, (w, h)


def read_controls(hwnd, max_depth=2):
    """读窗口内控件 (类型, 名字)。见文件头第 4 条：必须包 InitializerInThread。"""
    out = []

    def work():
        with auto.UIAutomationInitializerInThread():
            win = auto.ControlFromHandle(hwnd)
            for c in win.GetChildren():
                try:
                    out.append((c.ControlTypeName, c.Name or ""))
                    if max_depth > 1:
                        for cc in c.GetChildren():
                            out.append(("  " + cc.ControlTypeName, cc.Name or ""))
                except Exception:  # noqa: BLE001
                    continue

    th = threading.Thread(target=work, daemon=True)
    th.start()
    th.join(timeout=45)
    return out


def invoke_button(hwnd, keyword):
    """按名字关键字找按钮并 Invoke。见文件头第 5 条：只能用 UIA Invoke。"""
    hit = {"ok": False}

    def work():
        with auto.UIAutomationInitializerInThread():
            win = auto.ControlFromHandle(hwnd)
            for c in win.GetChildren():
                try:
                    if c.ControlTypeName == "ButtonControl" and keyword in (c.Name or ""):
                        c.GetInvokePattern().Invoke()
                        hit["ok"] = True
                        return
                except Exception:  # noqa: BLE001
                    continue

    th = threading.Thread(target=work, daemon=True)
    th.start()
    th.join(timeout=45)
    return hit["ok"]


def main():
    os.makedirs(SHOT_DIR, exist_ok=True)
    if not os.path.exists(EXE):
        print(f"找不到安装程序：{EXE}")
        return 2

    print(f"启动：{os.path.basename(EXE)}", flush=True)
    proc = subprocess.Popen([EXE])

    hwnd = None
    for i in range(40):
        time.sleep(0.7)
        cand = [w for w in find_ui_windows(proc.pid)
                if (w[2][2] - w[2][0]) > 200 and (w[2][3] - w[2][1]) > 200]
        if cand:
            hwnd, title, rect = max(cand, key=lambda x: (x[2][2] - x[2][0]) * (x[2][3] - x[2][1]))
            print(f"第 {i + 1} 次探测到已就绪窗口：{title!r} {rect}", flush=True)
            break

    if not hwnd:
        print("28 秒内没等到有尺寸的窗口", flush=True)
        proc.kill()
        return 1

    time.sleep(2.0)  # 等首帧画完
    results = {}

    # ---- 欢迎页 ----
    p1 = os.path.join(SHOT_DIR, "zh_01_welcome.png")
    _, size1 = print_window(hwnd, p1)
    ctrls1 = read_controls(hwnd)
    texts1 = [n for _t, n in ctrls1 if n.strip()]
    print(f"\n欢迎页截图：{p1}  尺寸={size1}", flush=True)
    for t, n in ctrls1:
        if n.strip():
            print(f"  [{t}] {n!r}", flush=True)

    # ---- 选项页（点「选项」）----
    print("\n点击「选项」…", flush=True)
    clicked = invoke_button(hwnd, "选项")
    time.sleep(2.5)
    p2 = os.path.join(SHOT_DIR, "zh_02_options.png")
    _, size2 = print_window(hwnd, p2)
    ctrls2 = read_controls(hwnd)
    texts2 = [n for _t, n in ctrls2 if n.strip()]
    print(f"选项页截图：{p2}  尺寸={size2}", flush=True)
    for t, n in ctrls2:
        if n.strip():
            print(f"  [{t}] {n!r}", flush=True)

    # ---- 判定 ----
    j1, j2 = "\n".join(texts1), "\n".join(texts2)
    checks = [
        ("[欢迎页] 标题中文化（欢迎使用）", "欢迎使用" in j1),
        ("[欢迎页] 说明文字已出现且提到「选项」指引", "如需更改安装位置" in j1 and "选项" in j1),
        ("[欢迎页] 按钮全中文：安装/选项/取消", "安装(I)" in j1 and "选项(O)" in j1 and "取消(C)" in j1),
        ("[欢迎页] 版本号显示 版本 1.5.1", "版本 1.5.1" in j1),
        ("[选项页] 成功打开（点「选项」有反应）", clicked),
        ("[选项页] 标题中文化（安装选项）", "安装选项" in j2),
        ("[选项页] 安装位置标签中文化", "安装位置" in j2),
        ("[选项页] 浏览按钮中文化", "浏览(B)" in j2),
        ("[选项页] 确定/取消中文化", "确定(O)" in j2 and "取消(C)" in j2),
        ("[选项页] 存在可编辑的安装位置输入框", any(t.strip() == "EditControl" for t, _n in ctrls2)),
    ]

    print("\n=== 判定结果 ===", flush=True)
    allok = True
    for label, ok in checks:
        print(f"  {'[OK]  ' if ok else '[FAIL]'} {label}", flush=True)
        allok = allok and ok

    print("\n=== 残留英文检查（都不应出现）===", flush=True)
    for bad in ("Welcome", "Setup Options", "Install location", "&Browse", "&Install", "&Options"):
        hit = (bad in j1) or (bad in j2)
        print(f"  {'[!!] 仍出现' if hit else '[OK] 未出现'} {bad!r}", flush=True)

    print("\n关闭安装程序（不执行安装）…", flush=True)
    user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
    time.sleep(1.5)
    if proc.poll() is None:
        proc.kill()
    print("已关闭。", flush=True)

    return 0 if allok else 1


if __name__ == "__main__":
    sys.exit(main())
