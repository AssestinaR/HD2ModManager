# -*- coding: utf-8 -*-
"""给游戏窗口发键盘/鼠标输入，用来在无人值守时自己进游戏、摆姿势。

为什么需要它：无人值守时必须能自己越过"无法加入房间"弹窗（单击左键）、
等开场动画结束后让玩家转身面向摄像机（按 S）。

用法：
    python tools/game-input.py --pid 1234 click
    python tools/game-input.py --pid 1234 key s --repeat 1 --hold 0.2
    python tools/game-input.py --pid 1234 wait --seconds 30

全部用 SendInput 而不是 PostMessage：Stingray 用 Raw Input 读键，
PostMessage 的 WM_KEYDOWN 它收不到。
"""

import argparse
import ctypes
import ctypes.wintypes as w
import sys
import time

user32 = ctypes.WinDLL('user32', use_last_error=True)

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1

KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [('dx', ctypes.c_long), ('dy', ctypes.c_long),
                ('mouseData', ctypes.c_ulong), ('dwFlags', ctypes.c_ulong),
                ('time', ctypes.c_ulong), ('dwExtraInfo', ctypes.POINTER(ctypes.c_ulong))]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [('wVk', ctypes.c_ushort), ('wScan', ctypes.c_ushort),
                ('dwFlags', ctypes.c_ulong), ('time', ctypes.c_ulong),
                ('dwExtraInfo', ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [('mi', MOUSEINPUT), ('ki', KEYBDINPUT)]
    _anonymous_ = ('u',)
    _fields_ = [('type', ctypes.c_ulong), ('u', _U)]


def set_dpi_aware():
    try:
        ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
    except Exception:
        user32.SetProcessDPIAware()


def find_hwnd(pid):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def callback(hwnd, _param):
        owner = w.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            if user32.GetWindowTextLengthW(hwnd):
                found.append(hwnd)
        return True

    user32.EnumWindows(callback, None)
    best, best_area = None, 0
    for hwnd in found:
        rect = w.RECT()
        user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
        area = (rect.right - rect.left) * (rect.bottom - rect.top)
        if area > best_area:
            best, best_area = hwnd, area
    return best


def focus(hwnd):
    """把游戏窗口拉到前台。Stingray 只在前台时读输入。"""
    if user32.IsIconic(ctypes.c_void_p(hwnd)):
        user32.ShowWindow(ctypes.c_void_p(hwnd), 9)  # SW_RESTORE
    user32.SetForegroundWindow(ctypes.c_void_p(hwnd))
    time.sleep(0.25)


def send(inp):
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def click():
    down = INPUT(type=INPUT_MOUSE)
    down.mi = MOUSEINPUT(0, 0, 0, MOUSEEVENTF_LEFTDOWN, 0, None)
    up = INPUT(type=INPUT_MOUSE)
    up.mi = MOUSEINPUT(0, 0, 0, MOUSEEVENTF_LEFTUP, 0, None)
    send(down)
    time.sleep(0.05)
    send(up)


# 只列会用到的键：扫描码是 Stingray 真正读的东西
SCANCODES = {
    's': 0x1F, 'w': 0x11, 'a': 0x1E, 'd': 0x20,
    'space': 0x39, 'esc': 0x01, 'enter': 0x1C,
    'f1': 0x3B, 'f2': 0x3C, 'f3': 0x3D,
}


def key(name, hold):
    scan = SCANCODES[name]
    down = INPUT(type=INPUT_KEYBOARD)
    down.ki = KEYBDINPUT(0, scan, KEYEVENTF_SCANCODE, 0, None)
    up = INPUT(type=INPUT_KEYBOARD)
    up.ki = KEYBDINPUT(0, scan, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, 0, None)
    send(down)
    time.sleep(hold)
    send(up)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('action', choices=['click', 'key', 'wait', 'focus'])
    ap.add_argument('name', nargs='?')
    ap.add_argument('--repeat', type=int, default=1)
    ap.add_argument('--hold', type=float, default=0.12)
    ap.add_argument('--interval', type=float, default=0.4)
    ap.add_argument('--seconds', type=float, default=10)
    args = ap.parse_args()

    set_dpi_aware()
    hwnd = find_hwnd(args.pid)
    if hwnd is None:
        raise SystemExit('pid %d 没有可见窗口' % args.pid)
    focus(hwnd)

    if args.action == 'focus':
        print('已聚焦 0x%x' % hwnd)
    elif args.action == 'wait':
        time.sleep(args.seconds)
        print('等待 %.1f 秒完成' % args.seconds)
    elif args.action == 'click':
        for _ in range(args.repeat):
            click()
            time.sleep(args.interval)
        print('已点击 %d 次' % args.repeat)
    else:
        if not args.name:
            raise SystemExit('key 需要键名，例如 s')
        for _ in range(args.repeat):
            key(args.name, args.hold)
            time.sleep(args.interval)
        print('已按键 %s x%d' % (args.name, args.repeat))
    return 0


if __name__ == '__main__':
    sys.exit(main())
