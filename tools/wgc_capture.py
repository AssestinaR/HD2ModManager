#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
⚠️⚠️ 仅限 HellForge（离线副本，无 EAC）使用 ⚠️⚠️
    零售版 Helldivers 2 有 EasyAntiCheat。本脚本属于"外部自动化"（抓帧/注入输入），
    与 HF 内的 code mod 一样，**只能在 HellForge 里用，绝不要对着零售版跑**。
    零售版只允许走 `data\*.patch_N` 文件层（我们已验证的那条路），不要注入、不要模拟输入、不要挂钩。
用 Windows Graphics Capture 抓某个窗口——**被遮挡也能抓**，贴到后台也能抓。

为什么换掉 GDI：`ImageGrab` 抓的是屏幕（要求窗口在最前），`PrintWindow` 在 D3D12 游戏窗口上会阻塞。
WGC 抓的是 **DWM 里那个窗口的合成内容**，与它是否在前台无关（OBS 的 window capture 也是这条路线）。

需要：`pip install windows-capture`（已装 2.0.1）

用法：
    python tools/wgc_capture.py --title "HELLDIVERS" --out E:/tmp/shots --count 1
    python tools/wgc_capture.py --title "HELLDIVERS" --out E:/tmp/shots --count 12 --interval 500
    python tools/wgc_capture.py --hwnd 0x12345 --out ...            # 已知 hwnd 时更准
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import threading
import time

from windows_capture import WindowsCapture


def find_hwnd(substr):
    U = ctypes.WinDLL('user32', use_last_error=True)
    hits = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if not U.IsWindowVisible(h):
            return True
        n = U.GetWindowTextLengthW(h)
        if n <= 0:
            return True
        b = ctypes.create_unicode_buffer(n + 1)
        U.GetWindowTextW(h, b, n + 1)
        if substr.lower() in b.value.lower():
            r = wt.RECT()
            U.GetWindowRect(h, ctypes.byref(r))
            hits.append((h, b.value, (r.right - r.left) * (r.bottom - r.top)))
        return True

    U.EnumWindows(cb, 0)
    if not hits:
        return None
    hits.sort(key=lambda x: -x[2])
    return hits[0][0], hits[0][1]


class Grabber:
    def __init__(self, hwnd=None, window_name=None):
        self.hwnd = hwnd
        self.name = window_name
        self.frames = []
        self.lock = threading.Lock()
        self.stop = False
        self.got = threading.Event()
        kw = dict(cursor_capture=False, draw_border=False)
        if hwnd:
            kw['window_hwnd'] = hwnd
        else:
            kw['window_name'] = window_name
        self.cap = WindowsCapture(**kw)

        @self.cap.event
        def on_frame_arrived(frame, capture_control):
            with self.lock:
                self.frames.append(frame.frame_buffer.copy())
            self.got.set()
            if self.stop:
                capture_control.stop()

        @self.cap.event
        def on_closed():
            pass

    def start(self):
        def run():
            try:
                self.cap.start()
            except Exception as e:      # 把线程里的异常留下来，否则只会看到超时
                self.error = e
                print('  [capture 线程异常] %s: %s' % (type(e).__name__, e))

        self.error = None
        self.t = threading.Thread(target=run, daemon=True)
        self.t.start()

    def take(self, timeout=5.0):
        self.got.clear()
        with self.lock:
            self.frames.clear()
        if not self.got.wait(timeout):
            return None
        with self.lock:
            return self.frames[-1] if self.frames else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--title', default=None)
    ap.add_argument('--hwnd', default=None)
    ap.add_argument('--out', required=True)
    ap.add_argument('--count', type=int, default=1)
    ap.add_argument('--interval', type=int, default=500)
    ap.add_argument('--prefix', default='shot')
    ap.add_argument('--crop', default=None, help='裁切区域 x0,y0,x1,y1（像素）')
    ap.add_argument('--scale', type=float, default=1.0, help='缩放，例如 0.5')
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    hwnd = int(args.hwnd, 0) if args.hwnd else None
    name = None
    if not hwnd:
        if not args.title:
            raise SystemExit('给 --title 或 --hwnd')
        r = find_hwnd(args.title)
        if not r:
            raise SystemExit('没找到标题含 %r 的窗口' % args.title)
        hwnd, name = r
        print('窗口: %r  hwnd=0x%X' % (name, hwnd))

    g = Grabber(hwnd=hwnd, window_name=name)
    g.start()
    time.sleep(0.3)

    n = 0
    for i in range(args.count):
        buf = g.take()
        if buf is None:
            print('  第 %d 张超时（窗口可能没有在绘制）' % (i + 1))
        else:
            from PIL import Image
            p = os.path.join(args.out, '%s_%03d.png' % (args.prefix, n))
            im = Image.fromarray(buf[:, :, [2, 1, 0]])   # WGC 给的是 BGRA，按 RGBA 存会 R/B 互换（整片发黄）
            if args.crop:
                x0, y0, x1, y1 = (int(v) for v in args.crop.split(','))
                im = im.crop((x0, y0, x1, y1))
            if args.scale and args.scale != 1.0:
                im = im.resize((max(1, int(im.width * args.scale)), max(1, int(im.height * args.scale))))
            im.save(p)
            print('  抓第 %d/%d 张 %dx%d -> %s' % (i + 1, args.count, buf.shape[1], buf.shape[0], p))
            n += 1
        if i + 1 < args.count:
            time.sleep(args.interval / 1000.0)

    g.stop = True
    print('共 %d 张' % n)
    return 0 if n else 1


if __name__ == '__main__':
    sys.exit(main())
