# -*- coding: utf-8 -*-
"""截取指定进程的窗口（默认按 GetWindowRect），落成 PNG。

为什么要它：`peek_armor_banks.py` 读的是**内存里的句柄 bank**，只能说明
"引擎给哪些槽分配了实例"。而"玩家实际看到什么"是另一回事 ——
观察记录里"身体部件消失"可能只是没渲染，也可能是被换回原版。
两条证据必须交叉验证，所以需要一个不改游戏状态、只读画面的取景工具。

注意：进程本身不做 DPI 感知的话，GetWindowRect 在缩放显示器上会给出
逻辑坐标，抓出来会偏。这里先 SetProcessDpiAwareness 再取矩形。
"""
import argparse
import ctypes
import ctypes.wintypes as w
import os
import sys

user32 = ctypes.WinDLL('user32', use_last_error=True)


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


def set_dpi_aware():
    try:
        # PROCESS_PER_MONITOR_DPI_AWARE = 2
        ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass


def window_rect(hwnd):
    rect = RECT()
    if not user32.GetWindowRect(wintypes_hwnd(hwnd), ctypes.byref(rect)):
        raise OSError(ctypes.get_last_error(), 'GetWindowRect 失败')
    return rect.left, rect.top, rect.right, rect.bottom


def wintypes_hwnd(hwnd):
    return ctypes.c_void_p(hwnd)


def find_hwnd(pid):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def callback(hwnd, _param):
        owner = w.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length:
                found.append(hwnd)
        return True

    user32.EnumWindows(callback, None)
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('--output', required=True)
    ap.add_argument('--bbox', help='手动指定 left,top,right,bottom，覆盖窗口矩形')
    args = ap.parse_args()

    set_dpi_aware()
    if args.bbox:
        box = tuple(int(x) for x in args.bbox.split(','))
    else:
        handles = find_hwnd(args.pid)
        if not handles:
            raise SystemExit('pid %d 没有可见窗口' % args.pid)
        # 取面积最大的那个窗口，游戏偶尔会带小的工具/覆盖窗口
        best = None
        for hwnd in handles:
            rect = window_rect(hwnd)
            area = (rect[2] - rect[0]) * (rect[3] - rect[1])
            if best is None or area > best[0]:
                best = (area, hwnd, rect)
        _area, hwnd, box = best
        title_length = user32.GetWindowTextLengthW(ctypes.c_void_p(hwnd))
        buffer = ctypes.create_unicode_buffer(title_length + 1)
        user32.GetWindowTextW(ctypes.c_void_p(hwnd), buffer, title_length + 1)
        print('窗口 0x%x  %s' % (hwnd, buffer.value))

    print('区域 %d,%d -> %d,%d（%dx%d）'
          % (box[0], box[1], box[2], box[3], box[2] - box[0], box[3] - box[1]))

    from PIL import ImageGrab
    image = ImageGrab.grab(bbox=box, all_screens=True)
    parent = os.path.dirname(os.path.abspath(args.output))
    os.makedirs(parent, exist_ok=True)
    image.save(args.output)
    print('已写出 %s（%dx%d）' % (args.output, image.width, image.height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
