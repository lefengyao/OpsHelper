"""生成「服务器运维助手」的应用图标。

设计：圆角方形底（蓝色渐变）+ 终端提示符 `>` + 青色光标下划线。
选这个形状是因为它缩到 16×16 还认得出，而且正好对应本应用的旗舰功能（内置 Web 控制台）。

用法：
    python make_icon.py

产物（都在本目录）：
    appicon.ico        多尺寸图标（16/20/24/32/40/48/64/128/256），供 EXE、窗口、快捷方式使用
    appicon-256.png    256 单图，供 README / 商店展示
    preview.png        多尺寸＋深浅底色对照图，用来肉眼检查小尺寸是否还看得清

几何全部用 0..1 归一化坐标描述，所以每个尺寸都能「按目标尺寸原生绘制」再降采样，
而不是从大图缩下去 —— 小尺寸的清晰度就是这么保住的。
"""
import io
import os
import struct

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))

ICON_PATH = os.path.join(HERE, "appicon.ico")
PNG_PATH = os.path.join(HERE, "appicon-256.png")
PREVIEW_PATH = os.path.join(HERE, "preview.png")

# 底与前景配色（与应用的 Windows 蓝强调色同一族）
BG_TOP = (0x2E, 0x93, 0xF0)
BG_BOTTOM = (0x00, 0x4E, 0xA0)
PROMPT = (0xFF, 0xFF, 0xFF)
CURSOR = (0x9A, 0xE8, 0xFF)

# 各尺寸的取整策略（SS = 每像素超采样倍数）
SS = 4


def lerp(a, b, t):
    return a + (b - a) * t


def background_gradient(size):
    """对角渐变。先在小图上逐像素算好再放大，比在大图上循环快得多。"""
    n = 256
    buf = bytearray(n * n * 4)
    denom = 2 * (n - 1)
    i = 0
    for y in range(n):
        for x in range(n):
            t = (x + y) / denom
            buf[i] = int(lerp(BG_TOP[0], BG_BOTTOM[0], t))
            buf[i + 1] = int(lerp(BG_TOP[1], BG_BOTTOM[1], t))
            buf[i + 2] = int(lerp(BG_TOP[2], BG_BOTTOM[2], t))
            buf[i + 3] = 255
            i += 4
    small = Image.frombytes("RGBA", (n, n), bytes(buf))
    return small.resize((size, size), Image.BILINEAR)


def render(size):
    """按目标尺寸原生绘制一张图标。"""
    r = size * SS
    img = background_gradient(r)

    # 小尺寸直接铺满画布（像系统图标那样），大尺寸才留圆角留白
    if size <= 24:
        margin, radius = 0.0, 0.20
    elif size <= 48:
        margin, radius = 0.055, 0.225
    else:
        margin, radius = 0.045, 0.215

    # 圆角遮罩
    mask = Image.new("L", (r, r), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [margin * r, margin * r, (1 - margin) * r, (1 - margin) * r],
        radius=radius * r,
        fill=255,
    )
    img.putalpha(mask)

    draw = ImageDraw.Draw(img)

    # 大尺寸加一点内高光，让底色不那么平（小尺寸加会糊）
    if size >= 48:
        gloss = Image.new("RGBA", (r, r), (0, 0, 0, 0))
        ImageDraw.Draw(gloss).ellipse(
            [-0.35 * r, -0.85 * r, 1.35 * r, 0.72 * r],
            fill=(255, 255, 255, 30),
        )
        gloss = gloss.filter(ImageFilter.GaussianBlur(r * 0.06))
        img.alpha_composite(Image.composite(gloss, Image.new("RGBA", (r, r), (0, 0, 0, 0)),
                                            mask))

    # 小尺寸要用更粗的笔画，否则缩下来只剩灰影
    if size <= 24:
        stroke = 0.150
        chevron = [(0.285, 0.290), (0.520, 0.500), (0.285, 0.710)]
        cursor = [(0.590, 0.710), (0.720, 0.710)]
    elif size <= 48:
        stroke = 0.132
        chevron = [(0.290, 0.295), (0.520, 0.500), (0.290, 0.705)]
        cursor = [(0.588, 0.705), (0.716, 0.705)]
    else:
        stroke = 0.122
        chevron = [(0.292, 0.297), (0.520, 0.500), (0.292, 0.703)]
        cursor = [(0.586, 0.703), (0.712, 0.703)]

    w = max(2, int(round(stroke * r)))

    def polyline(points, color):
        pts = [(x * r, y * r) for x, y in points]
        draw.line(pts, fill=color, width=w, joint="curve")
        # Pillow 的 line 没有圆头，端点各补一个圆
        for x, y in pts:
            draw.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=color)

    polyline(chevron, PROMPT)
    polyline(cursor, CURSOR)

    return img.resize((size, size), Image.LANCZOS)


def write_ico(images, path):
    """把多张 PNG 手写进一个 ICO 容器。

    不用 Pillow 自带的 ICO 保存：那个是从单张大图缩下去的，会毁掉我们按尺寸原生绘制的效果。
    """
    entries, blobs = [], []
    offset = 6 + 16 * len(images)

    for img in images:
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        data = buf.getvalue()
        s = img.width
        entries.append(struct.pack(
            "<BBBBHHII",
            s if s < 256 else 0,   # 256 在 ICO 里用 0 表示
            s if s < 256 else 0,
            0, 0, 1, 32,
            len(data), offset,
        ))
        blobs.append(data)
        offset += len(data)

    with open(path, "wb") as fh:
        fh.write(struct.pack("<HHH", 0, 1, len(images)))
        for e in entries:
            fh.write(e)
        for b in blobs:
            fh.write(b)


def build_preview(images, path):
    """深浅底色对照图，用来肉眼确认小尺寸还看得清。"""
    pad, gap, label_h = 18, 14, 26
    cell = max(i.width for i in images)
    total_w = pad * 2 + sum(i.width for i in images) + gap * (len(images) - 1)
    total_h = pad * 2 + label_h + cell * 2 + 46

    canvas = Image.new("RGB", (total_w, total_h), (0xF3, 0xF3, 0xF3))
    draw = ImageDraw.Draw(canvas)

    # 上下两条对比底色：浅色（资源管理器常用）+ 深色（深色任务栏）
    draw.rectangle([0, 0, total_w, pad + label_h + cell + 12], fill=(0xF3, 0xF3, 0xF3))
    draw.rectangle([0, pad + label_h + cell + 12, total_w, total_h], fill=(0x20, 0x20, 0x20))

    x = pad
    for img in images:
        y = pad + label_h + (cell - img.height) // 2
        canvas.paste(img, (x, y), img)              # 浅底
        y2 = pad + label_h + cell + 34 + (cell - img.height) // 2
        canvas.paste(img, (x, y2), img)             # 深底
        draw.text((x, y2 + cell + 2), f"{img.width}px", fill=(0xC8, 0xC8, 0xC8))
        x += img.width + gap

    canvas.save(path)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [render(s) for s in sizes]
    by_size = {i.width: i for i in images}

    write_ico(images, ICON_PATH)
    by_size[256].save(PNG_PATH)
    build_preview([by_size[s] for s in [16, 20, 24, 32, 48, 64, 128, 256]], PREVIEW_PATH)

    print(f"appicon.ico      {os.path.getsize(ICON_PATH):>8} B  尺寸 {sizes}")
    print(f"appicon-256.png  {os.path.getsize(PNG_PATH):>8} B")
    print(f"preview.png      {os.path.getsize(PREVIEW_PATH):>8} B")


if __name__ == "__main__":
    main()
