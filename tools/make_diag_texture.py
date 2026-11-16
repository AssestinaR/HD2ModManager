#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""诊断贴图：把 alpha 做成**密集海浪状**的 0..255 往返，颜色做成高对比色带，孔洞照旧。

目的只有一个：让"透明度到底有没有在动"肉眼无法看漏。
    R   ：255 保留 / 0 打孔（蜂窝，与 make_stocking_texture 一致）
    G,B ：高对比色带（绿 / 蓝 / 白 循环），不带渐变，纯色块
    A   ：海浪状密集往返 约 8(全透) -> 255(全不透)

alpha 公式：a = 0.5 + 0.5 * sin( 2π * (y*WAVES + AMP*sin(2π*x*SWIRL)) )
    WAVES 越大，上下方向的"波段"越密；SWIRL 越大，波形越像海浪。

用法：
    python tools/make_diag_texture.py --out <目录> [--size 4096] [--waves 10] [--swirl 5]
        [--amin 0.03] [--amax 1.0] [--bands 24]
"""
import argparse
import math
import os
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--size', type=int, default=4096)
    ap.add_argument('--pitch', type=int, default=512)
    ap.add_argument('--radius', type=int, default=90)
    ap.add_argument('--waves', type=float, default=10.0, help='纵向波段数（越大越密）')
    ap.add_argument('--swirl', type=float, default=5.0, help='横向扭曲，海浪感')
    ap.add_argument('--amp', type=float, default=0.35, help='扭曲幅度（以波段为单位）')
    ap.add_argument('--amin', type=float, default=0.03, help='最透处（0=完全消失）')
    ap.add_argument('--amax', type=float, default=1.00)
    ap.add_argument('--bands', type=int, default=24, help='色带条数')
    args = ap.parse_args()

    import numpy as np
    from PIL import Image, ImageDraw

    n = args.size
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32) / float(n)

    # ---- alpha：海浪状密集往返 ----
    w = np.sin(2.0 * np.pi * (yy * args.waves + args.amp * np.sin(2.0 * np.pi * xx * args.swirl)))
    a01 = 0.5 + 0.5 * w
    a = (args.amin + (args.amax - args.amin) * a01)
    alpha = np.clip(a * 255.0, 0, 255).astype(np.uint8)

    # ---- 色带：四色循环 ----
    # 注意着色器的取色式是 `float3(s.g, s.b, s.g)`，输出形如 (g, b, g)：
    #   输出的 R 和 B 永远是同一个值，所以**纯蓝是做不到的**（要 R=0 且 B=255 同时成立）。
    #   能稳定区分的只有：白 (255,255) / 洋红 (255,40) / 绿 (40,255) / 灰 (40,40)。
    #   下表就是按这个约束写的，每行是 (G, B)。早先这里按三元素行写过，导致某个波段
    #   的 G 和 B 凑成 40/40，屏幕上变成灰色而不是蓝色。
    idx = (np.floor(yy * args.bands).astype(np.int32)) % 4
    pal = np.array([[255, 255],   # -> (255,255,255) 白
                    [255, 40],    # -> (255, 40,255) 洋红
                    [40, 255],    # -> ( 40,255, 40) 绿
                    [40, 40]],    # -> ( 40, 40, 40) 灰
                   dtype=np.uint8)
    g = pal[idx, 0]
    b = pal[idx, 1]

    im = Image.merge('RGBA', (Image.fromarray(np.full((n, n), 255, np.uint8)),
                              Image.fromarray(g), Image.fromarray(b), Image.fromarray(alpha)))

    # ---- 镂空：把 R 打出孔 ----
    mask = Image.new('L', (n, n), 255)
    dr = ImageDraw.Draw(mask)
    row, y = 0, 0.0
    step = args.pitch * math.sqrt(3) / 2
    while y < n + args.pitch:
        x = (args.pitch * 0.5) if (row % 2) else 0.0
        while x < n + args.pitch:
            dr.ellipse([x - args.radius, y - args.radius, x + args.radius, y + args.radius], fill=0)
            x += args.pitch
        y += step
        row += 1
    im.putalpha(Image.fromarray(alpha))
    im = Image.merge('RGBA', (mask, im.split()[1], im.split()[2], Image.fromarray(alpha)))

    os.makedirs(args.out, exist_ok=True)
    p = os.path.join(args.out, 'diag.png')
    im.save(p)
    print('写出 %s (%dx%d)' % (p, n, n))
    print('  alpha 范围 %d..%d，纵向波段 %.0f 条，海浪扭曲 %.0f 次，色带 %d 条（绿/蓝/白）' % (
        int(alpha.min()), int(alpha.max()), args.waves, args.swirl, args.bands))
    print('  孔洞 %.1f%%' % (100.0 * (mask.histogram()[0]) / (n * n)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
