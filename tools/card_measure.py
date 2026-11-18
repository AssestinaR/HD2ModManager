#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从游戏截图里量色卡：ArUco 求单应 → 拉平 → 每格取中位数 → 和期望值对比。

为什么需要单应：板子在游戏里被透视、旋转、缩放，屏幕上的像素位置和贴图坐标不是线性关系。
ArUco 的四个角能给出精确对应，`getPerspectiveTransform` 反算之后，就能按贴图坐标去采样，
量到的是材质本身的颜色，而不是"这个角度下看起来的颜色"。

用法：
    python tools/card_measure.py --shot E:\\tmp\\shots\\shot.png [--card-size 2048] [--json out.json]

输出：每个色块的 期望 sRGB / 实测中位 sRGB / 偏差，以及 ArUco 的四个角与单应矩阵。
"""
import argparse
import json
import os
import sys

import cv2
import numpy as np

# 与 make_card_texture.py 保持同一套版面公式
BORDER_RATIO = 0.0234
MARKER_RATIO = 0.5
MARKER_TOP_RATIO = 0.06
MARGIN_RATIO = 0.04
BAR_GAP_RATIO = 0.05

from card_layout import SWATCHES, GRAYS  # 版面与色块表由 card_layout 统一维护


def linear_to_srgb255(x):
    x = max(0.0, min(1.0, x))
    s = 12.92 * x if x <= 0.0031308 else 1.055 * (x ** (1 / 2.4)) - 0.055
    return int(round(s * 255))


def layout(n, flip_v=False):
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

    return {
        'size': n,
        'marker_rect': marker_rect,
        'cells': cells,
        'bar': (margin, bar_y0, n - margin, bar_y0 + bar_h),
    }


def order_quad(pts):
    pts = pts.reshape(4, 2).astype(np.float32)
    s = pts.sum(axis=1)
    d = np.diff(pts, axis=1).reshape(-1)
    return np.array([pts[np.argmin(s)], pts[np.argmin(d)], pts[np.argmax(s)], pts[np.argmax(d)]], np.float32)


def detect_by_outline(img, min_area_ratio=0.02):
    """兜底：找最大的四边形轮廓当板子。ArUco 不可用（镜像/太小）时用。"""
    h, w = img.shape[:2]
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 40, 120)
    edges = cv2.dilate(edges, np.ones((3, 3), np.uint8), iterations=2)
    contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    best, best_area = None, 0.0
    for c in contours:
        area = cv2.contourArea(c)
        if area < h * w * min_area_ratio:
            continue
        peri = cv2.arcLength(c, True)
        approx = cv2.approxPolyDP(c, 0.02 * peri, True)
        if len(approx) != 4:
            continue
        x, y, bw, bh = cv2.boundingRect(approx)
        # 贴边的四边形通常是屏幕/UI 边框，排除掉
        if x <= 2 or y <= 2 or x + bw >= w - 2 or y + bh >= h - 2:
            continue
        if area > h * w * 0.6:
            continue
        if area > best_area:
            best, best_area = approx, area
    if best is None:
        return None
    return order_quad(best)


def detect_by_swatches(img, cells, min_area=800):
    """用红/绿/蓝三块色条求单应。返回 4 角（贴图坐标 -> 屏幕坐标的对应关系由 H 体现）。

    返回 (H, 说明) 或 None。H 把贴图坐标映射到屏幕坐标。
    """
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    ranges = {
        'red': [((0, 120, 60), (10, 255, 255)), ((170, 120, 60), (180, 255, 255))],
        'green': [((45, 120, 60), (75, 255, 255))],
        'blue': [((88, 70, 35), (148, 255, 255))],
    }
    by_name = {c['name']: c for c in cells}
    src_pts, dst_pts = [], []
    for name, rs in ranges.items():
        cell = by_name.get(name)
        if cell is None:
            continue
        mask = np.zeros(hsv.shape[:2], np.uint8)
        for lo, hi in rs:
            mask |= cv2.inRange(hsv, np.array(lo, np.uint8), np.array(hi, np.uint8))
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8))
        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not contours:
            print('  %s 色块没找到' % name)
            continue
        c = max(contours, key=cv2.contourArea)
        if cv2.contourArea(c) < min_area:
            print('  %s 色块太小（%d px²）' % (name, int(cv2.contourArea(c))))
            continue
        rect = cv2.minAreaRect(c)
        box = cv2.boxPoints(rect)
        box = order_quad(box)
        x0, y0, x1, y1 = cell['rect']
        quad_src = np.array([[x0, y0], [x1, y0], [x1, y1], [x0, y1]], np.float32)
        src_pts.extend(quad_src)
        dst_pts.extend(box)
        print('  %s 色块定位成功（面积 %d px²）' % (name, int(cv2.contourArea(c))))
    print('  对应点 %d 个' % len(src_pts))
    if len(src_pts) < 8:
        return None
    H, _ = cv2.findHomography(np.array(src_pts, np.float32), np.array(dst_pts, np.float32), cv2.RANSAC, 8.0)
    print('  单应求解: %s' % ('OK' if H is not None else '失败'))
    return H


def detect_mirror_tolerant(img):
    """检测 ArUco；正面不行就试上下/左右/两者翻转，再把角点映射回原图。

    为什么需要：贴图的 V 方向错了、或者模型 UV 镜像时，ArUco 在画面上是镜像的，
    标准检测器直接认不出。翻回去再检测，等于让"镜像"不再是致命问题。
    """
    h, w = img.shape[:2]
    variants = [
        ('原图', img, lambda pts: pts),
        ('上下翻转', cv2.flip(img, 0), lambda pts: np.stack([pts[:, 0], h - 1 - pts[:, 1]], axis=1)),
        ('左右翻转', cv2.flip(img, 1), lambda pts: np.stack([w - 1 - pts[:, 0], pts[:, 1]], axis=1)),
        ('旋转180', cv2.flip(img, -1), lambda pts: np.stack([w - 1 - pts[:, 0], h - 1 - pts[:, 1]], axis=1)),
    ]
    for name, variant, back in variants:
        found = detect(variant)
        if found is not None:
            if name != '原图':
                print('  ArUco 在「%s」下检出，已换算回原坐标' % name)
            return back(found.astype(np.float32))
    return None


def detect(img):
    d = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    params = cv2.aruco.DetectorParameters()
    params.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    det = cv2.aruco.ArucoDetector(d, params)
    corners, ids, _ = det.detectMarkers(img)
    if ids is None or len(ids) == 0:
        return None
    k = int(np.argmax([cv2.contourArea(c.reshape(-1, 1, 2)) for c in corners]))
    return corners[k].reshape(4, 2).astype(np.float32)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--shot', required=True)
    ap.add_argument('--card-size', type=int, default=2048)
    ap.add_argument('--no-aruco', action='store_true', help='跳过 ArUco，直接用外轮廓定位')
    ap.add_argument('--roi', default=None, help='只在 x0,y0,x1,y1 区域内找板子（避开画面里其他四边形）')
    ap.add_argument('--card-flip-v', action='store_true',
                    help='贴图在生成时做过垂直翻转（HD2 的 V 方向与图片相反）')
    ap.add_argument('--json', default=None)
    ap.add_argument('--save-rectified', default=None)
    args = ap.parse_args()

    img = cv2.imread(args.shot, cv2.IMREAD_COLOR)
    if img is None:
        raise SystemExit('读不到截图：%s' % args.shot)
    print('截图 %dx%d' % (img.shape[1], img.shape[0]))

    L = layout(args.card_size, args.card_flip_v)
    mx0, my0, mx1, my1 = L['marker_rect']
    src = np.array([[mx0, my0], [mx1, my0], [mx1, my1], [mx0, my1]], np.float32)

    roi = None
    if args.roi:
        x0, y0, x1, y1 = (int(float(v)) for v in args.roi.split(','))
        roi = (x0, y0, x1, y1)
        print('限定搜索区域: %s' % (roi,))

    Hswatch = None
    quad = None
    if not args.no_aruco:
        search = img if roi is None else img[roi[1]:roi[3], roi[0]:roi[2]]
        quad = detect_mirror_tolerant(search)
        if quad is not None and roi is not None:
            quad = quad + np.array([roi[0], roi[1]], np.float32)
    if quad is None:
        print('ArUco 不可用，改用纯色块定位…')
        Hswatch = detect_by_swatches(img if roi is None else img[roi[1]:roi[3], roi[0]:roi[2]], L['cells'])
        if Hswatch is not None and roi is not None:
            offset = np.array([[1, 0, roi[0]], [0, 1, roi[1]], [0, 0, 1]], np.float64)
            Hswatch = offset @ Hswatch
    if quad is None and Hswatch is None:
        print('纯色块也不可用，改用外轮廓定位…')
        search = img if roi is None else img[roi[1]:roi[3], roi[0]:roi[2]]
        quad = detect_by_outline(search)
        if quad is not None and roi is not None:
            quad = quad + np.array([roi[0], roi[1]], np.float32)
    if quad is None and Hswatch is None:
        print('FAIL 没检测到 ArUco。可能原因：板子不在画面里 / 太小 / 被遮挡 / 纹理被压缩糊掉')
        return 2
    if quad is not None:
        print('ArUco 四角：')
        for i, pt in enumerate(quad):
            print('   c%d = (%.1f, %.1f)' % (i, pt[0], pt[1]))

    if quad is not None:
        H = cv2.getPerspectiveTransform(src, quad)      # 贴图坐标 -> 屏幕坐标
    else:
        H = Hswatch
        print('用纯色块求得的单应（12 点 RANSAC）')
    Hinv = np.linalg.inv(H)

    # 拉平整张卡（便于人眼核对）
    n = args.card_size
    rect = cv2.warpPerspective(img, Hinv, (n, n), flags=cv2.INTER_LINEAR)
    if args.save_rectified:
        os.makedirs(os.path.dirname(os.path.abspath(args.save_rectified)) or '.', exist_ok=True)
        cv2.imwrite(args.save_rectified, rect)
        print('已写出拉平图：%s' % args.save_rectified)

    rows, worst = [], 0
    print()
    print('%-8s %-16s %-16s %s' % ('色块', '期望 sRGB', '实测中位 sRGB', '偏差'))
    for cell in L['cells']:
        x0, y0, x1, y1 = cell['rect']
        # 内缩 20%，避开边缘混色
        dx = int((x1 - x0) * 0.2)
        dy = int((y1 - y0) * 0.2)
        patch = rect[y0 + dy:y1 - dy, x0 + dx:x1 - dx]
        if patch.size == 0:
            print('  %-8s 取样区域为空（板子太小？）' % cell['name'])
            continue
        med = np.median(patch.reshape(-1, 3), axis=0)     # BGR
        got = (int(round(med[2])), int(round(med[1])), int(round(med[0])))
        exp = cell['expect_srgb']
        delta = tuple(got[i] - exp[i] for i in range(3))
        worst = max(worst, max(abs(d) for d in delta))
        print('%-8s %-16s %-16s %s' % (cell['name'], str(exp), str(got), str(delta)))
        rows.append({'name': cell['name'], 'linear': cell['linear'], 'expect': exp,
                     'got': got, 'delta': delta})

    print()
    print('最大通道偏差：%d（0~255 尺度）' % worst)

    # ---- 受光情况下的可判读信号 ----
    by_name = {row['name']: row for row in rows}
    print()
    print('通道顺序检查（红→R 最大、绿→G 最大、蓝→B 最大）：')
    for name, channel in (('red', 0), ('green', 1), ('blue', 2)):
        row = by_name.get(name)
        if not row:
            continue
        got = row['got']
        top = max(range(3), key=lambda i: got[i])
        verdict = 'OK' if top == channel else '✗ 主通道是 %s' % 'RGB'[top]
        print('  %-6s 实测 %-16s -> %s' % (name, str(got), verdict))

    gray = by_name.get('gray50') or by_name.get('gray18')
    if gray:
        g = gray['got']
        print()
        print('灰阶通道平衡（受光下应按比例相等）：R=%d G=%d B=%d，极差 %d' % (
            g[0], g[1], g[2], max(g) - min(g)))
        level = max(1.0, sum(g) / 3.0)
        print('以灰卡为参照的归一化（各色块 / 灰卡亮度，预期值同样归一）：')
        for row in rows:
            exp = row['expect']
            got = row['got']
            expn = tuple(round(c / max(1.0, sum(exp) / 3.0 or 1.0), 3) for c in exp)
            gotn = tuple(round(c / level, 3) for c in got)
            print('  %-8s 期望 %-22s 实测 %s' % (row['name'], str(expn), str(gotn)))
    print('判读：灰阶偏差大 → 贴图解码/gamma 链路；纯色偏差大 → 通道顺序/打包；全偏 → 整体曝光')

    if args.json:
        os.makedirs(os.path.dirname(os.path.abspath(args.json)) or '.', exist_ok=True)
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump({'shot': args.shot, 'quad': quad.tolist(), 'homography': H.tolist(),
                       'rows': rows, 'max_channel_delta': worst}, f, ensure_ascii=False, indent=1)
        print('JSON -> %s' % args.json)
    return 0


if __name__ == '__main__':
    sys.exit(main())
