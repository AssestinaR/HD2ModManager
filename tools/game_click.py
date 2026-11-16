#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""往 **HellForge 的**游戏窗口投递一次左键 —— 不动光标、不抢焦点。
⚠️⚠️ 仅限 HellForge（离线副本，无 EAC）使用 ⚠️⚠️
    零售版 Helldivers 2 有 EasyAntiCheat。**模拟输入是 EAC 重点检测的行为类别**，
    比抓帧危险得多（抓帧只是外部只读读取窗口内容，不碰进程）。

    因此本脚本内置**硬门禁**：先解析目标窗口所属进程的 exe 路径，
    只有路径位于 HellForge 安装目录下才允许执行；否则直接拒绝，没有 override。

用法：
    python tools/game_click.py --title HELLDIVERS
    python tools/game_click.py --title HELLDIVERS --repeat 5 --interval 1500
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

U = ctypes.WinDLL('user32', use_last_error=True)
K = ctypes.WinDLL('kernel32', use_last_error=True)

HF_ROOT = r'D:\Program Files\HellForge'

WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
MK_LBUTTON = 0x0001


def find_hwnd(substr):
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


def exe_of(hwnd):
    pid = wt.DWORD()
    U.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value:
        return None
    ph = K.OpenProcess(0x1000, False, pid.value)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not ph:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wt.DWORD(1024)
        if K.QueryFullProcessImageNameW(ph, 0, buf, ctypes.byref(size)):
            return buf.value
        return None
    finally:
        K.CloseHandle(ph)


def gate(hwnd):
    """硬门禁：只允许 HellForge 下的进程。返回 (ok, 说明)。"""
    exe = exe_of(hwnd)
    if not exe:
        return False, '拿不到目标进程的 exe 路径（进程可能已退出）'
    root = os.path.normcase(os.path.abspath(HF_ROOT))
    path = os.path.normcase(os.path.abspath(exe))
    if not path.startswith(root + os.sep):
        return False, ('拒绝执行：目标进程是 %s\n'
                       '    这个脚本只允许操作 %s 下的 HellForge 副本。\n'
                       '    零售版有 EasyAntiCheat，模拟输入会被判定为作弊，没有 override。'
                       % (exe, HF_ROOT))
    return True, exe


def post_click(hwnd):
    r = wt.RECT()
    U.GetClientRect(hwnd, ctypes.byref(r))
    w, h = r.right, r.bottom
    cx, cy = w // 2, h // 2
    lp = ((cy & 0xFFFF) << 16) | (cx & 0xFFFF)
    ok = [bool(U.PostMessageW(hwnd, WM_MOUSEMOVE, 0, lp)),
          bool(U.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp))]
    time.sleep(0.03)
    ok.append(bool(U.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)))
    return ok, (cx, cy), (w, h)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--title', required=True)
    ap.add_argument('--repeat', type=int, default=1)
    ap.add_argument('--interval', type=int, default=1500)
    args = ap.parse_args()

    hit = find_hwnd(args.title)
    if not hit:
        print('FAIL 没找到标题含 %r 的窗口' % args.title)
        return 1
    hwnd, title = hit
    print('窗口: %r  hwnd=0x%X' % (title, hwnd))

    ok, info = gate(hwnd)
    if not ok:
        print('FAIL ' + info)
        return 2
    print('目标进程: %s  ✔ 在 HellForge 目录下' % info)

    for i in range(args.repeat):
        res, pos, sz = post_click(hwnd)
        print('  %d) PostMessage 到 (%d,%d) / 客户区 %dx%d  结果 %s' % (
            i + 1, pos[0], pos[1], sz[0], sz[1], res))
        if i + 1 < args.repeat:
            time.sleep(args.interval / 1000.0)
    return 0


if __name__ == '__main__':
    sys.exit(main())
