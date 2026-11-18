#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成测量用色卡贴图：一个大 ArUco 定位标 + 一排已知色块 + 黑边框。

版面与色块表定义在 `tools/card_layout.py`（生成器和测量器共用同一份，
避免两边各写一份导致采样位置错位）。

灰阶是 8 级密集采样（0/0.05/0.10/0.18/0.30/0.50/0.70/0.90），
用来拟合显示端的 tonemap 曲线；再加纯 R/G/B（查通道顺序）与肤色（查综合）。

约定：
  · 图片保持正向。**不要用翻转图片来对齐 V 方向**——翻图会把 ArUco 也镜像掉，
    检测器直接认不出。V 方向在几何 UV 上翻（CanonicalPrimitiveBuilder.FlipV，默认开）。

用法：
    python tools/make_card_texture.py --out E:\\tmp\\card2048 --size 2048
"""
import argparse
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from card_layout import SWATCHES, draw, linear_to_srgb255  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--size', type=int, default=2048)
    ap.add_argument('--mask', choices=['none','grid','hex'], default='none',
                    help='在 alpha 通道写网孔遮罩（供着色器做 discard / 半透明）')
    ap.add_argument('--hole', type=float, default=0.35, help='遮罩的孔洞比例 0..1')
    args = ap.parse_args()

    img = np.zeros((args.size, args.size, 3), np.uint8)
    draw(img)

    print('色块 sRGB 编码：')
    for name, lin in SWATCHES:
        print('  %-8s 线性 %-20s -> sRGB %s'
              % (name, tuple(round(c, 3) for c in lin),
                 tuple(linear_to_srgb255(c) for c in lin)))

    if args.mask != 'none':
        import cv2 as _cv2
        import numpy as _np
        n = args.size
        alpha = _np.full((n, n), 255, _np.uint8)
        if args.mask == 'grid':
            step = max(8, int(n / 64))
            t = max(2, int(step * args.hole))
            for y in range(0, n, step):
                alpha[y:y+t, :] = 0
            for x in range(0, n, step):
                alpha[:, x:x+t] = 0
        else:  # hex：六边形孔阵
            step = max(8, int(n / 48))
            r = max(2, int(step * args.hole * 0.5))
            for j, y in enumerate(range(0, n, step)):
                off = (step // 2) if (j % 2) else 0
                for x in range(off, n, step):
                    _cv2.circle(alpha, (x, y), r, 0, -1)
        # 四角保留实体，避免 ArUco 被切碎
        m = int(n * 0.18)
        alpha[:m, :] = 255; alpha[-m:, :] = 255
        alpha[:, :m] = 255; alpha[:, -m:] = 255
        bgra = _cv2.cvtColor(img, _cv2.COLOR_BGR2BGRA)
        bgra[:, :, 3] = alpha
        img = bgra
        print('alpha 遮罩: %s  孔洞比例约 %.0f%%' % (args.mask, 100*args.hole))

    os.makedirs(args.out, exist_ok=True)
    png = os.path.join(args.out, 'card.png')
    cv2.imwrite(png, img)
    print('写出 %s (%dx%d)' % (png, args.size, args.size))
    return 0


if __name__ == '__main__':
    sys.exit(main())
