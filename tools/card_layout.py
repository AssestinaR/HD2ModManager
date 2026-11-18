#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""色卡版面与色块表：生成器（make_card_texture）和测量器（card_measure）共用这一份。

为什么抽出来：两边各写一份的时候，改了一边忘了另一边 → 采样位置全部错位，
量出来的数字看着"有点偏"，实际是拿错了格子。这种错很难从数值上看出来。

灰阶设计：8 级密集采样，用来拟合显示端的 tonemap 曲线。
    0.00 0.05 0.10 0.18 0.30 0.50 0.70 0.90
再加纯 R/G/B（查通道顺序与串色）和肤色（查综合）。
"""
import numpy as np

# 版面比例（相对贴图边长）
BORDER_RATIO = 0.0234
MARKER_RATIO = 0.5
MARKER_TOP_RATIO = 0.06
MARGIN_RATIO = 0.04
BAR_GAP_RATIO = 0.05

# (名字, 线性 RGB)
SWATCHES = [
    ('red',     (1.00, 0.00, 0.00)),
    ('green',   (0.00, 1.00, 0.00)),
    ('blue',    (0.00, 0.00, 1.00)),
    ('gray00',  (0.00, 0.00, 0.00)),
    ('gray05',  (0.05, 0.05, 0.05)),
    ('gray10',  (0.10, 0.10, 0.10)),
    ('gray18',  (0.18, 0.18, 0.18)),
    ('gray30',  (0.30, 0.30, 0.30)),
    ('gray50',  (0.50, 0.50, 0.50)),
    ('gray70',  (0.70, 0.70, 0.70)),
    ('gray90',  (0.90, 0.90, 0.90)),
    ('skin',    (0.62, 0.38, 0.30)),
]

GRAYS = [n for n, _ in SWATCHES if n.startswith('gray')]


def linear_to_srgb255(x):
    x = max(0.0, min(1.0, x))
    s = 12.92 * x if x <= 0.0031308 else 1.055 * (x ** (1 / 2.4)) - 0.055
    return int(round(s * 255))


def srgb255_to_linear(v):
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def linear_to_srgb_float(x):
    x = max(0.0, min(1.0, x))
    return 12.92 * x if x <= 0.0031308 else 1.055 * (x ** (1 / 2.4)) - 0.055


def layout(n, flip_v=False):
    """返回版面矩形（贴图坐标）。flip_v=True 时把矩形按垂直翻转后的图算。"""
    border = int(n * BORDER_RATIO)
    marker = int(n * MARKER_RATIO)
    mx = (n - marker) // 2
    my = int(n * MARKER_TOP_RATIO) + border
    margin = int(n * MARGIN_RATIO)
    bar_y0 = my + marker + int(n * BAR_GAP_RATIO)
    bar_h = n - bar_y0 - margin
    cell_w = (n - 2 * margin) // len(SWATCHES)

    cells = []
    for i, (name, lin) in enumerate(SWATCHES):
        x0 = margin + i * cell_w
        cells.append({
            'name': name,
            'linear': lin,
            'expect_srgb': tuple(linear_to_srgb255(c) for c in lin),
            'rect': (x0 + 4, bar_y0, x0 + cell_w - 4, bar_y0 + bar_h),
        })

    def flip_rect(r):
        x0, y0, x1, y1 = r
        return (x0, n - 1 - y1, x1, n - 1 - y0)

    marker_rect = (mx, my, mx + marker, my + marker)
    if flip_v:
        marker_rect = flip_rect(marker_rect)
        for cell in cells:
            cell['rect'] = flip_rect(cell['rect'])

    return {'size': n, 'marker_rect': marker_rect, 'cells': cells,
            'bar': (margin, bar_y0, n - margin, bar_y0 + bar_h)}


def draw(img):
    """在给定图像上画色卡（原地）。img 为 BGR uint8 方形数组。"""
    import cv2
    n = img.shape[0]
    img[:] = 255
    border = int(n * BORDER_RATIO)
    cv2.rectangle(img, (0, 0), (n - 1, n - 1), (0, 0, 0), border)

    aruco_dict = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    marker_px = int(n * MARKER_RATIO)
    marker = cv2.aruco.generateImageMarker(aruco_dict, 0, marker_px)
    mx = (n - marker_px) // 2
    my = int(n * MARKER_TOP_RATIO) + border
    img[my:my + marker_px, mx:mx + marker_px] = cv2.cvtColor(marker, cv2.COLOR_GRAY2BGR)

    L = layout(n)
    for cell in L['cells']:
        x0, y0, x1, y1 = cell['rect']
        bgr = tuple(linear_to_srgb255(c) for c in cell['linear'][::-1])
        cv2.rectangle(img, (x0, y0), (x1, y1), bgr, -1)
    return img
