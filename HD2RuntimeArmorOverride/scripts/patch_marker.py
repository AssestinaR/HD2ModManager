# -*- coding: utf-8 -*-
"""标记型补丁：证明分派 trampoline 到底有没有被执行。

背景：4-bank 重定位版补丁打上后游戏不崩，但 `type>=3` 的句柄仍然出现在
**bank C** 里，而不是我们自分配的缓冲。两种可能：

  A. trampoline 根本没被执行（`0x82024e` 不是实际的分派点）
  B. trampoline 执行了，但 `cmp edx,2` 把它判成了 `<=2`，走了 bank C

这个补丁把 trampoline 简化成"无条件往 bank C 的对应槽写哨兵 `0xDEADBEEF`"：

  * 如果 `bank C[slot]` 变成 `deadbeef` → **trampoline 执行了**（假设 B）
  * 如果 `bank C[slot]` 仍是真实句柄 → **trampoline 没执行**（假设 A）

不需要分配缓冲，也不需要 `VirtualAllocEx`，所以顺带绕开了
`ctypes` 默认 `c_int` 返回值截断的问题。

补丁点仍然只有 `0x82024e`（5 字节 jmp）+ 代码洞。
"""
import argparse
import ctypes
import ctypes.wintypes as w
import json
import os
import struct
import sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
# 关键：所有返回指针的 API 必须声明 restype，否则 64 位返回值会被截断成 c_int。
k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualAllocEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                               w.DWORD, w.DWORD]
k32.OpenProcess.restype = ctypes.c_void_p
k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]

ACCESS = 0x0400 | 0x0010 | 0x0020 | 0x0008
PAGE_RWX = 0x40

DISPATCH_RVA = 0x82024E
RESUME_RVA = 0x820256
CAVE_RVA = 0x1AF7673
SENTINEL = 0xDEADBEEF

ROLLBACK_DIR = os.path.join(
    os.environ.get('LOCALAPPDATA', ''), 'CowboyBingus', 'Helldivers2',
    'ArmorRegistry', 'bank-patch')


class ME(ctypes.Structure):
    _fields_ = [('dwSize', w.DWORD), ('a', w.DWORD), ('b', w.DWORD), ('c', w.DWORD),
                ('d', w.DWORD), ('modBaseAddr', ctypes.c_void_p),
                ('modBaseSize', w.DWORD), ('hModule', w.HMODULE),
                ('szModule', w.WCHAR * 256), ('szExePath', w.WCHAR * 260)]


def module_base(handle, pid, name='game.dll'):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    entry = ME()
    entry.dwSize = ctypes.sizeof(ME)
    base = None
    if k32.Module32FirstW(ctypes.c_void_p(snap), ctypes.byref(entry)):
        while True:
            if entry.szModule.lower() == name.lower():
                base = entry.modBaseAddr
                break
            if not k32.Module32NextW(ctypes.c_void_p(snap), ctypes.byref(entry)):
                break
    k32.CloseHandle(ctypes.c_void_p(snap))
    return base


def read(handle, address, size):
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                                 buf, size, ctypes.byref(got)):
        raise OSError(ctypes.get_last_error(), 'ReadProcessMemory 0x%x' % address)
    return buf.raw[:got.value]


def write(handle, address, data):
    buf = ctypes.create_string_buffer(data, len(data))
    got = ctypes.c_size_t(0)
    if not k32.WriteProcessMemory(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                                  buf, len(data), ctypes.byref(got)):
        raise OSError(ctypes.get_last_error(), 'WriteProcessMemory 0x%x' % address)


def build_marker(cave_rva):
    code = bytearray()
    code += bytes.fromhex('488b4c2468')                      # mov rcx,[rsp+0x68]  bankC
    code += bytes.fromhex('c70481') + struct.pack('<I', SENTINEL)  # mov dword [rcx+rax*4], 0xDEADBEEF
    rel = RESUME_RVA - (cave_rva + len(code) + 5)
    code += b'\xe9' + struct.pack('<i', rel)                 # jmp 0x820256
    return bytes(code)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('--rollback', action='store_true')
    args = ap.parse_args()

    handle = k32.OpenProcess(ACCESS, False, args.pid)
    if not handle:
        raise SystemExit('OpenProcess 失败：%d' % ctypes.get_last_error())
    try:
        base = module_base(handle, args.pid)
        if not base:
            raise SystemExit('找不到 game.dll')
        print('game.dll base 0x%x' % base)
        os.makedirs(ROLLBACK_DIR, exist_ok=True)
        path = os.path.join(ROLLBACK_DIR, 'marker-original.json')

        if args.rollback:
            with open(path, encoding='utf-8') as fh:
                original = json.load(fh)
            for key in ('dispatch', 'cave'):
                rva = original[key]['rva']
                data = bytes.fromhex(original[key]['bytes'])
                old = w.DWORD(0)
                k32.VirtualProtectEx(ctypes.c_void_p(handle),
                                     ctypes.c_void_p(base + rva), len(data),
                                     PAGE_RWX, ctypes.byref(old))
                write(handle, base + rva, data)
                k32.FlushInstructionCache(ctypes.c_void_p(handle),
                                          ctypes.c_void_p(base + rva), len(data))
                k32.VirtualProtectEx(ctypes.c_void_p(handle),
                                     ctypes.c_void_p(base + rva), len(data),
                                     old.value, ctypes.byref(old))
                print('  还原 0x%x' % rva)
            return 0

        marker = build_marker(CAVE_RVA)
        jump = b'\xe9' + struct.pack('<i', CAVE_RVA - (DISPATCH_RVA + 5))
        original = {
            'dispatch': {'rva': DISPATCH_RVA,
                         'bytes': read(handle, base + DISPATCH_RVA, 5).hex()},
            'cave': {'rva': CAVE_RVA,
                     'bytes': read(handle, base + CAVE_RVA, len(marker)).hex()},
            'moduleBase': base,
        }
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(original, fh, indent=2)
        print('  原字节 %s -> %s' % (DISPATCH_RVA and hex(DISPATCH_RVA),
                                     original['dispatch']['bytes']))
        print('  marker %d 字节: %s' % (len(marker), marker.hex()))

        for rva, data, label in ((CAVE_RVA, marker, 'marker'),
                                 (DISPATCH_RVA, jump, 'dispatch')):
            old = w.DWORD(0)
            if not k32.VirtualProtectEx(ctypes.c_void_p(handle),
                                        ctypes.c_void_p(base + rva), len(data),
                                        PAGE_RWX, ctypes.byref(old)):
                raise OSError(ctypes.get_last_error(), 'VirtualProtectEx 0x%x' % rva)
            write(handle, base + rva, data)
            k32.FlushInstructionCache(ctypes.c_void_p(handle),
                                      ctypes.c_void_p(base + rva), len(data))
            k32.VirtualProtectEx(ctypes.c_void_p(handle),
                                 ctypes.c_void_p(base + rva), len(data),
                                 old.value, ctypes.byref(old))
        print('  补丁已写入（哨兵 0x%08x）' % SENTINEL)
        return 0
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))


if __name__ == '__main__':
    sys.exit(main())
