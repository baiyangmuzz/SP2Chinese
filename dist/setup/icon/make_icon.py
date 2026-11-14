# -*- coding: utf-8 -*-
"""生成安装器图标。

设计约束（图标不是海报）：
  · 16×16 也要认得出 → 只用大字 + 大色块，不放细线、纹理、多行文字
  · 圆角方形底 + 单一主体
  · 主体用原封面里的「SP2」+ 红蓝斜条意象，但压到 2 个元素

输出：
  icon.ico          多尺寸（16/24/32/48/64/128/256），给 exe 用
  icon-256.png      站点/文档预览
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.dirname(os.path.abspath(__file__))
FONT_BOLD = r"C:\Windows\Fonts\arialbd.ttf"
FONT_CN = r"C:\Windows\Fonts\msyhbd.ttc"

# 原封面色
NAVY = (16, 24, 43)
NAVY2 = (22, 33, 56)
BLUE = (32, 140, 232)
RED = (226, 62, 62)
WHITE = (255, 255, 255)


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make_base(size):
    """深蓝渐变底 + 圆角。"""
    img = Image.new("RGB", (size, size), NAVY)
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / max(1, size - 1)
        c = tuple(int(NAVY[i] + (NAVY2[i] - NAVY[i]) * t) for i in range(3))
        d.line([(0, y), (size, y)], fill=c)
    return img


def draw_sp2(img, size):
    """主体：SP2 三个字符，P 用红色强调。"""
    d = ImageDraw.Draw(img)
    # 字号按宽度反推，让 SP2 占约 78% 宽
    fs = int(size * 0.60)
    while fs > 8:
        f = ImageFont.truetype(FONT_BOLD, fs)
        box = d.textbbox((0, 0), "SP2", font=f)
        w = box[2] - box[0]
        if w <= size * 0.80:
            break
        fs -= 2
    f = ImageFont.truetype(FONT_BOLD, fs)
    box = d.textbbox((0, 0), "SP2", font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    x = (size - w) / 2 - box[0]
    y = (size - h) / 2 - box[1]

    # 分三段画，P 上色：先量 S / P / 2 的位置
    parts = ["S", "P", "2"]
    widths = []
    for p in parts:
        b = d.textbbox((0, 0), p, font=f)
        widths.append(b[2] - b[0])
    total = sum(widths)
    cx = (size - total) / 2
    for p, wd in zip(parts, widths):
        color = RED if p == "P" else WHITE
        d.text((cx - d.textbbox((0, 0), p, font=f)[0], y), p, font=f, fill=color)
        cx += wd
    return img


def draw_slash(img, size):
    """底部一条红蓝斜条，替代原图的两条长带（缩小后仍可辨）。"""
    d = ImageDraw.Draw(img)
    h = max(3, int(size * 0.085))
    y0 = int(size * 0.755)
    skew = int(size * 0.10)
    # 蓝
    d.polygon([(int(size*0.10), y0), (int(size*0.60), y0),
               (int(size*0.60) - skew, y0 + h), (int(size*0.10) - skew, y0 + h)], fill=BLUE)
    # 红
    d.polygon([(int(size*0.585), y0), (int(size*0.90), y0),
               (int(size*0.90) - skew, y0 + h), (int(size*0.585) - skew, y0 + h)], fill=RED)
    return img


def variant_a(size):
    """A：深蓝底 + SP2 + 斜条（最接近原封面）"""
    img = make_base(size)
    draw_slash(img, size)
    draw_sp2(img, size)
    return img


def variant_b(size):
    """B：纯字标，无斜条，最简洁、小尺寸最清楚"""
    img = make_base(size)
    draw_sp2(img, size)
    return img


def variant_c(size):
    """C：蓝色圆角块 + 白字，像现代应用图标"""
    img = Image.new("RGB", (size, size), BLUE)
    d = ImageDraw.Draw(img)
    fs = int(size * 0.58)
    while fs > 8:
        f = ImageFont.truetype(FONT_BOLD, fs)
        if d.textbbox((0, 0), "SP2", font=f)[2] <= size * 0.78:
            break
        fs -= 2
    f = ImageFont.truetype(FONT_BOLD, fs)
    box = d.textbbox((0, 0), "SP2", font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((size - w) / 2 - box[0], (size - h) / 2 - box[1]), "SP2", font=f, fill=WHITE)
    return img


def save_ico(render, name):
    sizes = [16, 24, 32, 48, 64, 128, 256]
    imgs = []
    for s in sizes:
        im = render(s)
        im = im.convert("RGBA")
        im.putalpha(rounded_mask(s, int(s * 0.20)))
        imgs.append(im)
    big = imgs[-1]
    big.save(os.path.join(OUT, name + ".ico"), format="ICO",
             sizes=[(s, s) for s in sizes])
    big.save(os.path.join(OUT, name + "-256.png"))
    # 预览拼图：各尺寸并排放大，方便肉眼判断
    pad, gap = 8, 10
    prev_sizes = [16, 24, 32, 48, 64, 128]
    scale = 3
    W = sum(s * scale for s in prev_sizes) + gap * (len(prev_sizes) + 1)
    H = max(s * scale for s in prev_sizes) + pad * 2
    sheet = Image.new("RGB", (W, H), (245, 246, 248))
    x = gap
    for s in prev_sizes:
        im = render(s).convert("RGBA")
        im.putalpha(rounded_mask(s, int(s * 0.20)))
        im = im.resize((s * scale, s * scale), Image.NEAREST)
        sheet.paste(im, (x, (H - im.height) // 2), im)
        x += im.width + gap
    sheet.save(os.path.join(OUT, name + "-preview.png"))
    print("  %-10s -> %s.ico / -256.png / -preview.png" % (name, name))


if __name__ == "__main__":
    print("生成图标：")
    save_ico(variant_a, "icon-a")
    save_ico(variant_b, "icon-b")
    save_ico(variant_c, "icon-c")
    print("\n预览拼图里从左到右是 16/24/32/48/64/128 放大 3 倍。")
