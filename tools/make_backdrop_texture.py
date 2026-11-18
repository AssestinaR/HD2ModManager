"""定位板贴图：白底 + 灰色外框 + 四周 ArUco（放在主板覆盖不到的环带上）。

设计要点（按需求）：
  · 白底、灰边 —— 尽量不干扰主板颜色
  · 8 个 ArUco（4 角 + 4 边中），全部落在外侧环带
  · 主板占中央 1.2/1.9 ≈ 63%，所以环带宽度约 18%，标记只在环带内

用法：
    python tools/make_backdrop_texture.py --out E:\\tmp\\backdrop --size 2048
"""
import argparse
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--size', type=int, default=2048)
    ap.add_argument('--border', type=float, default=0.030, help='灰色外框宽度（相对边长）')
    ap.add_argument('--marker', type=float, default=0.105, help='单个 ArUco 边长（相对边长）')
    ap.add_argument('--probe', type=float, default=0.085, help='标记中心距边缘的距离（相对边长）')
    args = ap.parse_args()

    n = args.size
    img = np.full((n, n, 3), 255, np.uint8)

    # 灰色外框
    b = int(n * args.border)
    cv2.rectangle(img, (0, 0), (n - 1, n - 1), (128, 128, 128), b)

    d = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    m = int(n * args.marker)
    p = int(n * args.probe)

    # 4 角 + 4 边中，共 8 个标记（都在环带内，主板覆盖不到）
    spots = [
        (p, p), (n - p, p), (p, n - p), (n - p, n - p),          # 四角
        (n // 2, p), (n // 2, n - p), (p, n // 2), (n - p, n // 2),  # 四边中点
    ]
    for i, (cx, cy) in enumerate(spots):
        marker = cv2.aruco.generateImageMarker(d, i, m)
        x0, y0 = cx - m // 2, cy - m // 2
        img[y0:y0 + m, x0:x0 + m] = cv2.cvtColor(marker, cv2.COLOR_GRAY2BGR)
        print('  标记 #%d  中心 (%d, %d)  边长 %d' % (i, cx, cy, m))

    os.makedirs(args.out, exist_ok=True)
    png = os.path.join(args.out, 'backdrop.png')
    cv2.imwrite(png, img)
    print('写出 %s (%dx%d)' % (png, n, n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
