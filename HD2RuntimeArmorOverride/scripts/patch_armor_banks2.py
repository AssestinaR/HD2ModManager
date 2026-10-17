# -*- coding: utf-8 -*-
"""4-bank 重定位版：`type>=3` 的句柄写进我们自分配的缓冲区。

与第一版（单点补丁）的区别 —— 第一版把 type>=3 写到 `rec+0x546bc4`，
那里是 `(3,1)` 两个 slot 分派值，踩掉就崩（已实测）。

这一版：
  * bank C（type==2）**保持原位不动** —— 消费者 `0x81ed60` 和销毁 `0x81e010`
    仍然读写它，所以这两处完全不用改。
  * type>=3 的 bank 落到 `VirtualAllocEx` 出来的缓冲：
        addr = buf + index*0x1b8 + (type-3)*40
    其中 `index*0x1b8` 就是 trampoline 里的 `r12`，不需要额外求值。

所以补丁仍然只有**两处**：分派 trampoline + 代码洞里的 8 字节缓冲地址。

trampoline（52 字节，洞容量 93）：
    488b4c2468        mov  rcx, [rsp+0x68]     ; bankC
    418b54240c        mov  edx, [r12+0xc]      ; piece_type
    83fa02            cmp  edx, 2
    7615              jbe  store               ; type<=2 -> 仍用 bankC
    83ea03            sub  edx, 3
    6bd228            imul edx, edx, 0x28
    4c8b15<disp32>    mov  r10, [rip+disp32]   ; ourBuf
    4d01e2            add  r10, r12            ; + index*0x1b8
    89d1              mov  ecx, edx
    4c01d1            add  rcx, r10
    store:
    893c81            mov  [rcx+rax*4], edi    ; bank[slot] = handle
    e9<rel32>         jmp  0x820256
    <8 字节>          ourBuf
"""
import argparse
import ctypes
import ctypes.wintypes as w
import json
import os
import struct
import sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
# 关键：返回指针的 API 必须声明 restype。ctypes 默认按 c_int 返回，64 位地址会被
# 截断成 32 位，导致 VirtualAllocEx 拿到的缓冲地址是错的（表现为写入跑偏、且
# ReadProcessMemory 读自己分配的内存返回 error 299）。
k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualAllocEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                               w.DWORD, w.DWORD]
k32.OpenProcess.restype = ctypes.c_void_p
k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
ACCESS = 0x0400 | 0x0010 | 0x0020 | 0x0008
PAGE_RWX = 0x40
PAGE_RW = 0x04
MEM_COMMIT_RESERVE = 0x1000 | 0x2000

DISPATCH_RVA = 0x82024E
RESUME_RVA = 0x820256
CAVE_RVA = 0x1AF7673
BUFFER_BYTES = 64 * 1024 * 1024   # 覆盖 index 到 ~148k，bank 基址仍远在 5.5MB 内

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


def build_trampoline(cave_rva, buffer_address):
    code = bytearray()
    code += bytes.fromhex('488b4c2468')                    # mov rcx, [rsp+0x68]
    code += bytes.fromhex('418b54240c')                    # mov edx, [r12+0xc]
    code += bytes.fromhex('83fa02')                        # cmp edx, 2
    jump_at = len(code)
    code += bytes.fromhex('7600')                          # jbe store（回填）
    code += bytes.fromhex('83ea03')                        # sub edx, 3
    code += bytes.fromhex('6bd228')                        # imul edx, edx, 0x28
    disp_at = len(code)
    code += bytes.fromhex('4c8b1500000000')                # mov r10, [rip+0]（回填）
    code += bytes.fromhex('4d01e2')                        # add r10, r12
    code += bytes.fromhex('89d1')                          # mov ecx, edx
    code += bytes.fromhex('4c01d1')                        # add rcx, r10
    store_at = len(code)
    code += bytes.fromhex('893c81')                        # mov [rcx+rax*4], edi
    rel = RESUME_RVA - (cave_rva + len(code) + 5)
    code += b'\xe9' + struct.pack('<i', rel)               # jmp 0x820256
    data_at = len(code)
    code += struct.pack('<Q', buffer_address)
    # 回填 jbe：从 jbe 末尾跳到 store
    code[jump_at + 1] = store_at - (jump_at + 2)
    # 回填 mov r10 的 rip 位移：指向 data_at
    struct.pack_into('<i', code, disp_at + 3, data_at - (disp_at + 7))
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
        path = os.path.join(ROLLBACK_DIR, 'bank4r-original.json')

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

        buffer_address = k32.VirtualAllocEx(ctypes.c_void_p(handle), None, BUFFER_BYTES,
                                            MEM_COMMIT_RESERVE, PAGE_RW)
        if not buffer_address:
            raise SystemExit('VirtualAllocEx 失败：%d' % ctypes.get_last_error())
        print('新 bank 缓冲 0x%x（%d MB）'
              % (buffer_address, BUFFER_BYTES // (1024 * 1024)))

        tramp = build_trampoline(CAVE_RVA, buffer_address)
        jump = b'\xe9' + struct.pack('<i', CAVE_RVA - (DISPATCH_RVA + 5))
        print('trampoline %d 字节' % len(tramp))

        original = {
            'dispatch': {'rva': DISPATCH_RVA,
                         'bytes': read(handle, base + DISPATCH_RVA, 5).hex()},
            'cave': {'rva': CAVE_RVA,
                     'bytes': read(handle, base + CAVE_RVA, len(tramp)).hex()},
            'moduleBase': base,
            'buffer': buffer_address,
        }
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
            print('  %-11s 0x%x 写入 %d 字节' % (label, rva, len(data)))

        print('\n验证：')
        print('  0x%x = %s' % (DISPATCH_RVA,
                               read(handle, base + DISPATCH_RVA, 5).hex()))
        print('  0x%x = %s' % (CAVE_RVA, read(handle, base + CAVE_RVA, 60).hex()))
        print('  缓冲地址 0x%x（读回校验用）' % buffer_address)
        return 0
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))


if __name__ == '__main__':
    sys.exit(main())
