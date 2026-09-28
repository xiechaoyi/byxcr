#!/usr/bin/env python3
"""生成 byxcr 飞牛 fnOS 应用图标。

图标尺寸按 fnOS 约定：包根 ICON.PNG=64、ICON_256.PNG=256，
桌面入口用 app/ui/images/icon_64.png 与 icon_256.png。

用法（需 Pillow）：python3 fnos/tools/make-icons.py
"""
from __future__ import annotations

import os
from PIL import Image, ImageDraw

# 先在 4 倍尺寸上绘制再降采样，避免小尺寸下边缘发毛
SS = 4
BASE = 256

BG_TOP = (24, 72, 148)      # 深蓝
BG_BOTTOM = (47, 158, 226)  # 亮蓝
INK = (18, 81, 143)         # 前景蓝色（盒子上的胶带与箭头）
WHITE = (255, 255, 255, 255)


def vertical_gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    grad = Image.new("RGB", (1, size))
    px = grad.load()
    for y in range(size):
        t = y / max(size - 1, 1)
        px[0, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return grad.resize((size, size), Image.NEAREST)


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def build_master() -> Image.Image:
    s = BASE * SS

    # 圆角渐变底
    layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    layer.paste(vertical_gradient(s, BG_TOP, BG_BOTTOM), (0, 0), rounded_mask(s, 56 * SS))

    d = ImageDraw.Draw(layer)

    # 圆角坐标助手
    def r(*v: int) -> list[int]:
        return [x * SS for x in v]

    # ---- 箱体（白色纸箱，象征「搬运箱」） ----
    d.rounded_rectangle(r(66, 98, 190, 200), radius=13 * SS, fill=WHITE)

    # ---- 箱盖分界线 ----
    d.rectangle(r(66, 126, 190, 133), fill=INK + (255,))

    # ---- 顶部封箱胶带 ----
    d.rectangle(r(121, 98, 135, 126), fill=INK + (255,))

    # ---- 箱内向下箭头（下载 / 搬运） ----
    d.rectangle(r(120, 146, 136, 172), fill=INK + (255,))
    d.polygon(
        [tuple(x * SS for x in p) for p in [(107, 168), (149, 168), (128, 189)]],
        fill=INK + (255,),
    )

    return layer


def main() -> None:
    here = os.path.dirname(os.path.abspath(__file__))
    pkg = os.path.dirname(here)
    master = build_master()

    targets = [
        (os.path.join(pkg, "ICON.PNG"), 64),
        (os.path.join(pkg, "ICON_256.PNG"), 256),
        (os.path.join(pkg, "app", "ui", "images", "icon_64.png"), 64),
        (os.path.join(pkg, "app", "ui", "images", "icon_256.png"), 256),
    ]

    for path, size in targets:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        master.resize((size, size), Image.LANCZOS).save(path, "PNG", optimize=True)
        print(f"wrote {path} ({size}x{size})")


if __name__ == "__main__":
    main()
