# -*- coding: utf-8 -*-
"""只读探针：直接从运行中的游戏进程读护甲的三个实例句柄 bank。

代码链路（全部来自 `game.dll` 的内存镜像，见 `capture_game_image.py`）：

    G = *(game.dll + 0x3326d40)                    ; 0x796497: add r14, [rip+0x2b90882]
    rec(k)      = G + k * 0x1b8                    ; 0x79648e / 0x81e031 / 0x877be9: imul r*, rax, 0x1b8
    bankA(type0) = rec(k) + 0x546b4c               ; 三个 bank 连续，步长 0x28
    bankB(type1) = rec(k) + 0x546b74
    bankC(type>=2) = rec(k) + 0x546b9c
    bank = uint32[10]                              ; 0x81e02c: mov esi, 0xa

写入侧 `0x81f470` 在 `0x8201f5` 按 `piece_type` 选 bank，然后
`bank[slot] = handle`（`0x820214 / 0x820253`，寻址 `bank + slot*4`）。
所以同一个 (type, slot) 上只有**最后写入**的句柄能活 —— 这就是"别名"。

这个脚本不写进程、不挂线程，只 OpenProcess(PROCESS_VM_READ)。
"""
import argparse
import ctypes
import ctypes.wintypes as w
import sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010


class MODULEENTRY32W(ctypes.Structure):
    _fields_ = [
        ('dwSize', w.DWORD), ('th32ModuleID', w.DWORD), ('th32ProcessID', w.DWORD),
        ('GlblcntUsage', w.DWORD), ('ProccntUsage', w.DWORD),
        ('modBaseAddr', ctypes.POINTER(ctypes.c_byte)),
        ('modBaseSize', w.DWORD), ('hModule', w.HMODULE),
        ('szModule', w.WCHAR * 256), ('szExePath', w.WCHAR * 260),
    ]


def open_process(pid):
    handle = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise SystemExit('OpenProcess(%d) 失败：Win32 %d' % (pid, ctypes.get_last_error()))
    return handle


def module_base(pid, name):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    entry = MODULEENTRY32W()
    entry.dwSize = ctypes.sizeof(MODULEENTRY32W)
    ok = k32.Module32FirstW(ctypes.c_void_p(snap), ctypes.byref(entry))
    while ok:
        if entry.szModule.lower() == name.lower():
            base = ctypes.cast(entry.modBaseAddr, ctypes.c_void_p).value
            k32.CloseHandle(ctypes.c_void_p(snap))
            return base
        ok = k32.Module32NextW(ctypes.c_void_p(snap), ctypes.byref(entry))
    k32.CloseHandle(ctypes.c_void_p(snap))
    raise SystemExit('找不到模块 %s' % name)


def read(handle, address, size):
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                               buf, size, ctypes.byref(got))
    if not ok or got.value != size:
        return None
    return buf.raw


def parse_ranges(text):
    out = []
    for part in text.split(','):
        part = part.strip()
        if not part:
            continue
        if '-' in part:
            lo, hi = part.split('-', 1)
            out.extend(range(int(lo, 0), int(hi, 0) + 1))
        else:
            out.append(int(part, 0))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('--module', default='game.dll')
    ap.add_argument('--global-rva', default='0x3326d40')
    ap.add_argument('--stride', default='0x1b8')
    ap.add_argument('--banks', default='0x546b4c,0x546b74,0x546b9c')
    ap.add_argument('--indices', default='0-11')
    ap.add_argument('--slots', type=int, default=10)
    ap.add_argument('--bulk', type=int, default=0,
                    help='不按索引逐个读，而是一次读这么多条记录再扫（省几千次系统调用）')
    ap.add_argument('--save', help='把非零记录写成 JSON，便于跨配置差分')
    ap.add_argument('--label', default='', help='写进 JSON 的标签，如 clean / S8 / Y')
    args = ap.parse_args()

    stride = int(args.stride, 0)
    bank_offsets = [int(x, 0) for x in args.banks.split(',')]
    indices = parse_ranges(args.indices)

    handle = open_process(args.pid)
    try:
        base = module_base(args.pid, args.module)
        global_address = base + int(args.global_rva, 0)
        raw = read(handle, global_address, 8)
        if raw is None:
            raise SystemExit('读全局 0x%x 失败' % global_address)
        array_base = int.from_bytes(raw, 'little')
        print('模块基址      0x%016x' % base)
        print('全局 +0x%s  0x%016x -> 数组基址 0x%016x'
              % (args.global_rva.replace('0x', ''), global_address, array_base))

        found = 0
        if args.bulk:
            import struct
            low = min(bank_offsets)
            span = args.bulk * stride
            start_address = array_base + low
            chunk = read(handle, start_address, span)
            if chunk is None:
                print('  整块读 0x%x + 0x%x 失败' % (start_address, span))
                return 1
            print('\n整块读 0x%016x .. +0x%x（%d 条记录）'
                  % (start_address, span, args.bulk))
            summary = []
            snapshot = {
                'label': args.label,
                'moduleBase': base,
                'globalRva': int(args.global_rva, 0),
                'arrayBase': array_base,
                'stride': stride,
                'bankOffsets': bank_offsets,
                'slots': args.slots,
                'bulk': args.bulk,
                'records': [],
            }
            for index in range(args.bulk):
                hits = []
                for offset in bank_offsets:
                    at = index * stride + (offset - low)
                    if at + 4 * args.slots > len(chunk):
                        continue
                    values = list(struct.unpack_from('<%dI' % args.slots, chunk, at))
                    if any(values):
                        hits.append((offset, values))
                if not hits:
                    continue
                found += 1
                used_total = sum(1 for _o, v in hits for x in v if x)
                summary.append((used_total, index))
                snapshot['records'].append({
                    'index': index,
                    'address': array_base + index * stride,
                    'banks': {('%06x' % offset): values for offset, values in hits},
                })
                print('\n  rec[%d] 0x%016x  非零槽 %d'
                      % (index, array_base + index * stride, used_total))
                for offset, values in hits:
                    used = [i for i, v in enumerate(values) if v]
                    print('    bank+0x%06x  %s  槽=%s'
                          % (offset, ' '.join('%08x' % v if v else '    .   '
                                              for v in values), used))
            if summary:
                summary.sort(reverse=True)
                print('\n按非零槽数排序：%s'
                      % ', '.join('rec[%d]=%d' % (i, n) for n, i in summary[:12]))
            print('\n在 %d 条记录里找到 %d 条非零。' % (args.bulk, found))
            if args.save:
                import json
                with open(args.save, 'w', encoding='utf-8') as fh:
                    json.dump(snapshot, fh, ensure_ascii=False, indent=2)
                print('快照 %s（%d 条记录）' % (args.save, len(snapshot['records'])))
            return 0

        for index in indices:
            rec = array_base + index * stride
            banks = []
            for offset in bank_offsets:
                chunk = read(handle, rec + offset, 4 * args.slots)
                if chunk is None:
                    banks.append(None)
                    continue
                banks.append([int.from_bytes(chunk[4 * i:4 * i + 4], 'little')
                              for i in range(args.slots)])
            if all(b is None for b in banks):
                print('  rec[%2d] 0x%016x  读不到' % (index, rec))
                continue
            nonzero = sum(1 for b in banks if b for v in b if v)
            if nonzero == 0:
                continue
            found += 1
            print('\n  rec[%2d] 0x%016x  非零槽 %d' % (index, rec, nonzero))
            for offset, bank in zip(bank_offsets, banks):
                label = {0x546b4c: 'A(type0)', 0x546b74: 'B(type1)',
                         0x546b9c: 'C(type>=2)'}.get(offset, 'bank?')
                if bank is None:
                    print('    %-11s +0x%06x  读不到' % (label, offset))
                    continue
                cells = ' '.join('%08x' % v if v else '    .   ' for v in bank)
                used = [i for i, v in enumerate(bank) if v]
                print('    %-11s +0x%06x  %s   槽=%s'
                      % (label, offset, cells, used if used else '-'))
        print('\n在 %d 个索引里找到 %d 条非零记录。' % (len(indices), found))
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))
    return 0


if __name__ == '__main__':
    sys.exit(main())
