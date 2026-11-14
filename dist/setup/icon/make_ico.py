# -*- coding: utf-8 -*-
"""把渲染出来的图标截图裁成方形，生成多尺寸 .ico。

输入：icon-new.png（600x600，含外边距和阴影）
做法：自动找到圆角图标的边界（非纯黑区域），裁掉外边距，
      再缩放到各尺寸。小尺寸做锐化，否则 16px 会糊。
"""
import os
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "icon-new.png")
ICO = os.path.join(HERE, "sp2.ico")
PNG256 = os.path.join(HERE, "sp2-256.png")
PREVIEW = os.path.join(HERE, "sp2-preview.png")

SIZES = [16, 24, 32, 48, 64, 128, 256]


def crop_to_icon(im):
    """裁掉黑色外边距：找亮度和背景明显不同的区域。"""
    im = im.convert("RGB")
    w, h = im.size
    px = im.load()
    # 背景是 #080a0e 附近，图标区域更亮
    def is_bg(c):
        return c[0] < 16 and c[1] < 18 and c[2] < 22

    left, right, top, bottom = w, 0, h, 0
    step = 2
    for y in range(0, h, step):
        for x in range(0, w, step):
            if not is_bg(px[x, y]):
                if x < left: left = x
                if x > right: right = x
                if y < top: top = y
                if y > bottom: bottom = y
    if right <= left or bottom <= top:
        return im
    # 取正方形，以图标中心为准
    cx, cy = (left + right) // 2, (top + bottom) // 2
    side = max(right - left, bottom - top)
    half = side // 2
    box = (cx - half, cy - half, cx + half, cy + half)
    return im.crop(box)


def rounded(im, radius_ratio=0.225):
    """套圆角蒙版（Windows 图标本身就是圆角矩形）。"""
    size = im.size[0]
    mask = Image.new("L", (size, size), 0)
    from PIL import ImageDraw
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * radius_ratio), fill=255)
    out = im.convert("RGBA")
    out.putalpha(mask)
    return out


def main():
    if not os.path.exists(SRC):
        raise SystemExit("缺少 icon-new.png，先跑渲染")

    im = Image.open(SRC)
    im = crop_to_icon(im)
    print("裁切后:", im.size)

    # 统一到 1024 再降采样，边缘更干净
    big = im.resize((1024, 1024), Image.LANCZOS)
    big = rounded(big)
    big.save(PNG256 if False else os.path.join(HERE, "sp2-1024.png"))
    big.resize((256, 256), Image.LANCZOS).save(PNG256)

    frames = []
    for s in SIZES:
        f = big.resize((s, s), Image.LANCZOS)
        # 小尺寸锐化，避免糊成一团
        if s <= 32:
            f = f.filter(ImageFilter.UnsharpMask(radius=0.8, percent=140, threshold=2))
        frames.append(f)

    frames[-1].save(ICO, format="ICO", sizes=[(s, s) for s in SIZES])
    print("已生成:", os.path.basename(ICO), os.path.getsize(ICO), "字节")

    # 预览拼图：真实尺寸 + 放大
    pad, gap, scale = 10, 12, 3
    W = sum(s * scale for s in SIZES) + gap * (len(SIZES) + 1)
    H = max(s * scale for s in SIZES) + pad * 2
    sheet = Image.new("RGB", (W, H), (245, 246, 248))
    x = gap
    for f in frames:
        im2 = f.resize((f.size[0] * scale, f.size[1] * scale), Image.NEAREST)
        sheet.paste(im2, (x, (H - im2.height) // 2), im2)
        x += im2.width + gap
    sheet.save(PREVIEW)
    print("预览:", os.path.basename(PREVIEW))


if __name__ == "__main__":
    main()
