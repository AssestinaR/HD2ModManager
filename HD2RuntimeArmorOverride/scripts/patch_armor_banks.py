# -*- coding: utf-8 -*-
"""把护甲 bank 从 3 扩到 4：只打**一处**补丁（分派 trampoline）。

为什么只打一处：把 bank C 指针重定位到自分配缓冲，会牵连消费者 `0x81ed60`
和销毁 `0x81e010`（它们仍读写旧地址），补丁面立刻变成四处。所以第一步
**不重定位**，而是让 `piece_type >= 3` 落到 `bankC + 40`，也就是紧挨着
bank C 的第 4 个 bank 槽位（`rec + 0x546bc4`）。

代价：`rec+0x546bc4/+0x546bc8` 原本存 `(3, 1)` 两个"slot3/slot2 分派值"，
会被 bank D 覆盖。这是**已知的有意牺牲**，用来换取"只改 5 字节 + 一个代码洞"
的最小验证面。

安全措施：
  * 打补丁前把原始字节写进 registry 目录，`--rollback` 可一键还原
  * 补丁后恢复页保护为 RX
  * trampoline 把 type 夹到 0..1（即 bank C/D），type 异常也不会写出界
"""
import argparse
import ctypes
import ctypes.wintypes as w
import json
import os
import struct
import sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
ACCESS = 0x0400 | 0x0010 | 0x0020 | 0x0008
PAGE_RWX = 0x40

DISPATCH_RVA = 0x82024E
DISPATCH_ORIG = bytes.fromhex('488b4c2468')
RESUME_RVA = 0x820256
CAVE_RVA = 0x1AF7673
MAX_EXTRA_BANKS = 1  # 只多一个 bank（C 之后的 D）

ROLLBACK_DIR = os.path.join(
    os.environ.get('LOCALAPPDATA', ''), 'CowboyBingus', 'Helldivers2',
    'ArmorRegistry', 'bank-patch')


class ME(ctypes.Structure):
    _fields_ = [('dwSize', w.DWORD), ('a', w.DWORD), ('b', w.DWORD), ('c', w.DWORD),
                ('d', w.DWORD), ('modBaseAddr', ctypes.POINTER(ctypes.c_byte)),
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
                base = ctypes.cast(entry.modBaseAddr, ctypes.c_void_p).value
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


def build_trampoline(cave_rva):
    code = bytearray()
    code += bytes.fromhex('488b4c2468')      # mov rcx, [rsp+0x68]   ; bankC
    code += bytes.fromhex('418b54240c')      # mov edx, [r12+0xc]    ; piece_type
    code += bytes.fromhex('83ea02')          # sub edx, 2
    code += bytes.fromhex('83fa01')          # cmp edx, 1
    code += bytes.fromhex('7604')            # jbe +4
    code += bytes.fromhex('ba01000000')      # mov edx, 1            ; 夹到 bank D
    code += bytes.fromhex('6bd228')          # imul edx, edx, 0x28
    code += bytes.fromhex('4801d1')          # add rcx, rdx
    code += bytes.fromhex('893c81')          # mov [rcx+rax*4], edi
    rel = RESUME_RVA - (cave_rva + len(code) + 5)
    code += b'\xe9' + struct.pack('<i', rel)  # jmp 0x820256
    return bytes(code)


def apply_patch(handle, base):
    tramp = build_trampoline(CAVE_RVA)
    jump = b'\xe9' + struct.pack('<i', CAVE_RVA - (DISPATCH_RVA + 5))
    original = {
        'dispatch': {'rva': DISPATCH_RVA, 'bytes': read(
            handle, base + DISPATCH_RVA, len(DISPATCH_ORIG)).hex()},
        'cave': {'rva': CAVE_RVA, 'bytes': read(
            handle, base + CAVE_RVA, len(tramp)).hex()},
        'moduleBase': base,
    }
    os.makedirs(ROLLBACK_DIR, exist_ok=True)
    path = os.path.join(ROLLBACK_DIR, 'bank4-original.json')
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(original, fh, indent=2)
    print('  原始字节已存 %s' % path)

    for rva, data, label in ((CAVE_RVA, tramp, 'trampoline'),
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
        print('  %-11s 0x%x 写入 %d 字节，保护恢复 0x%x'
              % (label, rva, len(data), old.value))
    return path


def rollback(handle, base, path):
    with open(path, encoding='utf-8') as fh:
        original = json.load(fh)
    for key in ('dispatch', 'cave'):
        rva = original[key]['rva']
        data = bytes.fromhex(original[key]['bytes'])
        old = w.DWORD(0)
        k32.VirtualProtectEx(ctypes.c_void_p(handle), ctypes.c_void_p(base + rva),
                             len(data), PAGE_RWX, ctypes.byref(old))
        write(handle, base + rva, data)
        k32.FlushInstructionCache(ctypes.c_void_p(handle),
                                  ctypes.c_void_p(base + rva), len(data))
        k32.VirtualProtectEx(ctypes.c_void_p(handle), ctypes.c_void_p(base + rva),
                             len(data), old.value, ctypes.byref(old))
        print('  还原 0x%x（%d 字节）' % (rva, len(data)))


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
        path = os.path.join(ROLLBACK_DIR, 'bank4-original.json')
        if args.rollback:
            rollback(handle, base, path)
            return 0
        print('补丁：分派 0x%x -> 代码洞 0x%x，type>=3 落到 bank D'
              % (DISPATCH_RVA, CAVE_RVA))
        apply_patch(handle, base)
        print('\n验证：')
        print('  0x%x = %s' % (DISPATCH_RVA,
                               read(handle, base + DISPATCH_RVA, 5).hex()))
        print('  0x%x = %s' % (CAVE_RVA,
                               read(handle, base + CAVE_RVA, 32).hex()))
        return 0
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))


if __name__ == '__main__':
    sys.exit(main())
