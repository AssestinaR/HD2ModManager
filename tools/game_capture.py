#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
⚠️⚠️ 仅限 HellForge（离线副本，无 EAC）使用 ⚠️⚠️
    零售版 Helldivers 2 有 EasyAntiCheat。本脚本属于"外部自动化"（抓帧/注入输入），
    与 HF 内的 code mod 一样，**只能在 HellForge 里用，绝不要对着零售版跑**。
    零售版只允许走 `data\*.patch_N` 文件层（我们已验证的那条路），不要注入、不要模拟输入、不要挂钩。
外部抓帧器：按节拍把游戏窗口抓成 PNG。不用任何新库（PIL + ctypes）。

为什么必须从外部抓
------------------
HellForge SDK 里**没有任何截图 / 回读接口**（没有 render_target、没有 readback、没有 screenshot）。
它有的是：相机 unit 的 pose 可以设、`EngineRenderStep` 会取相机 pose、
`UnitSetVisibilityGroup(unit, group, visible)` 可以按 section 显隐。
所以分工是：

    mod（Zig）   负责摆姿势：切 6 个机位、按 section 开关显隐、每步写一个"就绪"标记文件
    本工具（py）  负责取像素：盯标记文件，一变就抓一帧

用法
----
    # 1) 定速抓（mod 按固定节拍换姿势时用这个）
    python tools/game_capture.py --title Helldivers --out E:/tmp/shots --count 36 --interval 500

    # 2) 标记驱动（推荐：mod 每摆好一个姿势就写一次标记文件）
    python tools/game_capture.py --title Helldivers --out E:/tmp/shots --watch E:/tmp/pose.txt

    # 3) 只抓一张，看看窗口找没找到
    python tools/game_capture.py --title Helldivers --out E:/tmp/shots --count 1 --dry-run
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import ImageGrab

USER32 = ctypes.WinDLL('user32', use_last_error=True)


def dpi_aware():
    """让坐标是物理像素，否则 125% 缩放下抓出来的位置会偏。"""
    try:
        ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
    except Exception:
        try:
            USER32.SetProcessDPIAware()
        except Exception:
            pass


def find_window(title):
    """按标题子串找可见窗口，返回 (hwnd, (l, t, r, b), 标题)。"""
    hits = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not USER32.IsWindowVisible(hwnd):
            return True
        n = USER32.GetWindowTextLengthW(hwnd)
        if n <= 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        USER32.GetWindowTextW(hwnd, buf, n + 1)
        text = buf.value
        if title.lower() in text.lower():
            r = wt.RECT()
            USER32.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.right - r.left, r.bottom - r.top
            if w > 200 and h > 200:
                hits.append((hwnd, (r.left, r.top, r.right, r.bottom), text, w * h))
        return True

    USER32.EnumWindows(cb, 0)
    if not hits:
        return None
    hits.sort(key=lambda x: -x[3])
    return hits[0][0], hits[0][1], hits[0][2]


def grab(bbox, out_path, hwnd=None, method='screen'):
    """method='screen' 抓屏幕区域（要求窗口在最前）；'printwindow' 让窗口自己重绘（可抓被遮挡的窗口）。"""
    if method == 'printwindow' and hwnd:
        im = _printwindow(hwnd)
        if im is not None:
            im.save(out_path)
            return im.size
    im = ImageGrab.grab(bbox=bbox, all_screens=True)
    im.save(out_path)
    return im.size


GDI32 = ctypes.WinDLL('gdi32', use_last_error=True)


def _printwindow(hwnd):
    """PrintWindow + PW_RENDERFULLCONTENT(2)：很多 D3D 窗口即使被遮挡也能重绘出来。"""
    r = wt.RECT()
    if not USER32.GetWindowRect(hwnd, ctypes.byref(r)):
        return None
    w, h = r.right - r.left, r.bottom - r.top
    if w <= 0 or h <= 0:
        return None
    hdc = USER32.GetWindowDC(hwnd)
    mdc = GDI32.CreateCompatibleDC(hdc)
    bmp = GDI32.CreateCompatibleBitmap(hdc, w, h)
    GDI32.SelectObject(mdc, bmp)
    ok = USER32.PrintWindow(hwnd, mdc, 2)
    class BMI(ctypes.Structure):
        _fields_ = [('biSize', wt.DWORD), ('biWidth', wt.LONG), ('biHeight', wt.LONG),
                    ('biPlanes', wt.WORD), ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                    ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', wt.LONG), ('biYPelsPerMeter', wt.LONG),
                    ('biClrUsed', wt.DWORD), ('biClrImportant', wt.DWORD)]
    bi = BMI()
    bi.biSize = ctypes.sizeof(BMI)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    got = GDI32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    GDI32.DeleteObject(bmp)
    GDI32.DeleteDC(mdc)
    USER32.ReleaseDC(hwnd, hdc)
    if not ok or got == 0:
        return None
    from PIL import Image
    im = Image.frombuffer('RGBA', (w, h), buf, 'raw', 'BGRA', 0, 1)
    # 全黑说明没抓到内容
    if im.convert('L').getextrema()[1] < 8:
        return None
    return im.convert('RGB')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--title', required=True, help='窗口标题子串，例如 Helldivers')
    ap.add_argument('--out', required=True)
    ap.add_argument('--count', type=int, default=1, help='定速模式下抓几张')
    ap.add_argument('--interval', type=int, default=500, help='定速模式间隔（毫秒）')
    ap.add_argument('--watch', default=None, help='标记文件；内容一变就抓一帧')
    ap.add_argument('--timeout', type=int, default=600, help='watch 模式最长等多少秒')
    ap.add_argument('--prefix', default='shot')
    ap.add_argument('--dry-run', action='store_true', help='只找窗口，不抓')
    ap.add_argument('--method', choices=['screen','printwindow'], default='screen', help='screen=抓屏幕(要求在最前)；printwindow=让窗口自绘(可抓遮挡)')
    args = ap.parse_args()

    dpi_aware()
    os.makedirs(args.out, exist_ok=True)

    hit = find_window(args.title)
    if not hit:
        print('FAIL 没找到标题含 %r 的可见窗口' % args.title)
        return 1
    hwnd, bbox, text = hit
    print('窗口: %r' % text)
    print('位置: %s   尺寸: %dx%d' % (bbox, bbox[2] - bbox[0], bbox[3] - bbox[1]))
    if args.dry_run:
        return 0

    n = 0
    if args.watch:
        last = None
        t0 = time.time()
        print('监听标记文件 %s ……（内容每变一次抓一帧）' % args.watch)
        while time.time() - t0 < args.timeout:
            try:
                cur = open(args.watch, 'r', encoding='utf-8', errors='replace').read().strip()
            except OSError:
                cur = None
            if cur is not None and cur != last:
                last = cur
                p = os.path.join(args.out, '%s_%03d.png' % (args.prefix, n))
                size = grab(bbox, p, hwnd, args.method)
                print('  抓第 %d 张（标记=%r）%dx%d -> %s' % (n + 1, cur, size[0], size[1], p))
                n += 1
            time.sleep(0.05)
        print('超时结束，共 %d 张' % n)
        return 0 if n else 1

    for i in range(args.count):
        p = os.path.join(args.out, '%s_%03d.png' % (args.prefix, i))
        size = grab(bbox, p, hwnd, args.method)
        print('  抓第 %d/%d 张 %dx%d -> %s' % (i + 1, args.count, size[0], size[1], p))
        n += 1
        if i + 1 < args.count:
            time.sleep(args.interval / 1000.0)
    return 0


if __name__ == '__main__':
    sys.exit(main())
