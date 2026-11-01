"""
生成 Mcdl 的应用图标（多尺寸 .ico）。

设计：
- 圆角方块底，绿色渐变（与应用主题色 #2E7D5B 同族）
- 白色向下箭头 + 底部托盘，保持"下载器"语义
- 托盘/箭头可选「方块化」处理，呼应 Minecraft 的像素感

用法：python make_icon.py <输出目录>
"""

import os
import sys
from PIL import Image, ImageDraw

MASTER = 1024
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

TOP = (58, 168, 118)
BOTTOM = (24, 82, 58)
WHITE = (255, 255, 255, 255)
TRAY_ALPHA = 232


def vertical_gradient(size, top, bottom):
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        t = (y / (size - 1)) ** 1.15
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(size):
            px[x, y] = color
    out = img.convert("RGBA")
    return out


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def draw_tray(d, master, R, blocks: bool):
    y0, y1 = R(824), R(888)
    cx = master // 2
    if blocks:
        gap = R(40)
        bw = R(384) // 3
        total = bw * 3 + gap * 2
        x = cx - total // 2
        for _ in range(3):
            d.rounded_rectangle([x, y0, x + bw, y1], radius=R(26), fill=WHITE[:3] + (TRAY_ALPHA,))
            x += bw + gap
    else:
        d.rounded_rectangle([cx - R(200), y0, cx + R(200), y1], radius=R(32),
                            fill=WHITE[:3] + (TRAY_ALPHA,))


def draw_arrow(d, master, R, stepped: bool):
    cx = master // 2
    if not stepped:
        w = R(150)
        d.rounded_rectangle([cx - w // 2, R(210), cx + w // 2, R(556)], radius=R(28), fill=WHITE)
        d.polygon([(cx - R(232), R(516)), (cx + R(232), R(516)), (cx, R(790))], fill=WHITE)
        return

    # 像素阶梯箭头：三级台阶的箭杆 + 阶梯收窄的箭头
    step = R(56)
    widths = [R(150), R(150), R(250), R(250), R(350), R(350)]
    y = R(206)
    for i, w in enumerate(widths):
        h = step if i < len(widths) - 1 else R(70)
        d.rectangle([cx - w // 2, y, cx + w // 2, y + h], fill=WHITE)
        y += h
    # 箭头尖
    d.rectangle([cx - R(150), y, cx + R(150), y + R(56)], fill=WHITE)
    d.rectangle([cx - R(56), y + R(56), cx + R(56), y + R(112)], fill=WHITE)


def build(variant: str) -> Image.Image:
    base = vertical_gradient(MASTER, TOP, BOTTOM)
    base.putalpha(rounded_mask(MASTER, int(MASTER * 0.225)))

    layer = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    s = MASTER / 1024.0
    R = lambda v: max(1, round(v * s))

    stepped = variant in ("C", "D")
    blocks = variant in ("A", "C")

    draw_arrow(d, MASTER, R, stepped)
    draw_tray(d, MASTER, R, blocks)

    return Image.alpha_composite(base, layer)


def render(master, size):
    return master.resize((size, size), Image.LANCZOS)


def sheet(masters, out_path):
    """每个变体一行：大尺寸 + 小尺寸原尺寸 + 16/24/32 的 8 倍放大（看图检查像素可读性）"""
    row_h, pad = 300, 26
    width = 960
    img = Image.new("RGBA", (width, pad + len(masters) * row_h), (245, 246, 247, 255))
    d = ImageDraw.Draw(img)

    for row, (name, master) in enumerate(masters.items()):
        top = pad + row * row_h
        d.text((24, top - 2), name, fill=(27, 31, 35, 255))

        # 大尺寸
        img.alpha_composite(render(master, 200), (24, top + 24))

        # 实际尺寸
        x = 250
        for size in [16, 24, 32, 48, 64]:
            icon = render(master, size)
            img.alpha_composite(icon, (x, top + 24 + (120 - size) // 2))
            d.text((x, top + 150), f"{size}", fill=(120, 126, 134, 255))
            x += size + 30

        # 放大的 16px / 32px，检查像素级可读性
        x = 250
        for size, zoom in [(16, 6), (24, 4), (32, 3)]:
            icon = render(master, size).resize((size * zoom, size * zoom), Image.NEAREST)
            img.alpha_composite(icon, (x, top + 176))
            d.text((x, top + 176 + size * zoom + 4), f"{size}px x{zoom}", fill=(120, 126, 134, 255))
            x += size * zoom + 26

    img.save(out_path)


def main():
    ico_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    docs_dir = sys.argv[2] if len(sys.argv) > 2 else ico_dir
    os.makedirs(ico_dir, exist_ok=True)
    os.makedirs(docs_dir, exist_ok=True)

    variants = {
        "A: solid arrow + 3 blocks": build("A"),
        "B: solid arrow + 1 bar (SHIPPED)": build("B"),
        "C: pixel-stepped arrow": build("C"),
    }

    # 对比图（设计过程留档）
    sheet(variants, os.path.join(docs_dir, "icon-sheet.png"))

    chosen = variants["B: solid arrow + 1 bar (SHIPPED)"]
    render(chosen, 256).save(os.path.join(docs_dir, "icon.png"))
    render(chosen, 256).save(os.path.join(ico_dir, "app-256.png"))

    frames = [render(chosen, s) for s in SIZES]
    frames[-1].save(os.path.join(ico_dir, "app.ico"), format="ICO",
                    sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print("written ico to", ico_dir, "and preview to", docs_dir)


if __name__ == "__main__":
    main()
