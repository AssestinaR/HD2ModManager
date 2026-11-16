#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成我们自己的「丝袜」贴图：R=镂空遮罩、G/B=底色、A=透明度。

为什么要换图：现在用的那张是水平渐变，**大部分像素 R 和 A 都很低**，于是
`R < 阈值` 会把大半个身体丢掉（实机现象：只有少数几块留下面料，其余全裸）。

这张图的分布是我们自己定的：
    R   ：默认 255（保留），按蜂窝打孔，孔洞约占 11% 面积
    G,B ：140（中灰底），不含图案，保证底色是中性的
    A   ：竖向渐变 255(上) -> 150(下)，让透明过渡看得见又不至于消失

用法：
    python tools/make_stocking_texture.py --out <目录> [--size 4096] [--pitch 512] [--radius 90]
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
    ap.add_argument('--base', type=int, default=140)
    ap.add_argument('--a-top', type=int, default=255)
    ap.add_argument('--a-bottom', type=int, default=150)
    args = ap.parse_args()

    from PIL import Image, ImageDraw

    n = args.size
    # 先画灰度：255 = 保留，0 = 打孔；后续把灰度搬进 R
    mask = Image.new('L', (n, n), 255)
    dr = ImageDraw.Draw(mask)
    row = 0
    y = 0.0
    step = args.pitch * math.sqrt(3) / 2
    while y < n + args.pitch:
        x = (args.pitch * 0.5) if (row % 2) else 0.0
        while x < n + args.pitch:
            dr.ellipse([x - args.radius, y - args.radius, x + args.radius, y + args.radius], fill=0)
            x += args.pitch
        y += step
        row += 1

    # 竖向渐变做 alpha
    grad = Image.new('L', (1, n))
    for yy in range(n):
        t = yy / float(n - 1)
        grad.putpixel((0, yy), int(round(args.a_top * (1 - t) + args.a_bottom * t)))
    alpha = grad.resize((n, n), Image.NEAREST)

    im = Image.merge('RGBA', (mask, Image.new('L', (n, n), args.base),
                              Image.new('L', (n, n), args.base), alpha))
    os.makedirs(args.out, exist_ok=True)
    p = os.path.join(args.out, 'stocking.png')
    im.save(p)

    holes = sum(1 for v in mask.getdata() if v == 0)
    total = n * n
    print('写出 %s  (%dx%d)' % (p, n, n))
    print('  孔洞面积 %.1f%%   R 均值 %.1f   A 范围 %d..%d   G/B = %d' % (
        100.0 * holes / total, sum(mask.getdata()) / total, args.a_bottom, args.a_top, args.base))
    return 0


if __name__ == '__main__':
    sys.exit(main())
