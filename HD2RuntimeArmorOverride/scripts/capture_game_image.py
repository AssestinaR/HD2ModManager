# -*- coding: utf-8 -*-
"""只读抓取运行中游戏进程里的某个模块（默认 game.dll），落成**镜像布局**文件。

为什么必须从进程内存抓，而不是读盘上的 game.dll：

  * 盘上 `data/game/game.dll` 只有 15,522,408 B（0xECDA68），
    而它 PE 头里的 `SizeOfImage` 是 74,727,424 B（0x4744000）——
    和运行时 `runtime.status` 报的 `game_image_size` 完全一致。
    盘上那份是压缩/加密的壳，RVA 只对**加载后的内存**有意义。
  * `notes/slot-audit-20260927.md` 里那些 `game.dll-*.asm.txt` 的地址
    （如 0x00ad4520）也超过盘上文件长度，同样只能来自内存。

镜像布局 = 文件偏移与 RVA 恒等（把每个 section 放在它的 RVA 处，
未提交的页留 0，文件长度 = SizeOfImage）。这样消费者不需要再做
RVA→文件偏移映射，`disasm_armor_consumer.py --image` 直接切片即可。

只读承诺：OpenProcess 只请求 `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`，
不写进程、不挂线程、不改保护属性。VirtualQueryEx 只用来跳过不可读的页，
避免 ReadProcessMemory 在 guard / noaccess 页上反复失败。
"""
import argparse
import ctypes
import ctypes.wintypes as w
import json
import os
import struct
import sys

k32 = ctypes.WinDLL('kernel32', use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010

TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010

MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}


class MEMORY_BASIC_INFORMATION64(ctypes.Structure):
    _fields_ = [
        ('BaseAddress', ctypes.c_ulonglong),
        ('AllocationBase', ctypes.c_ulonglong),
        ('AllocationProtect', w.DWORD),
        ('alignment1', w.DWORD),
        ('RegionSize', ctypes.c_ulonglong),
        ('State', w.DWORD),
        ('Protect', w.DWORD),
        ('Type', w.DWORD),
        ('alignment2', w.DWORD),
    ]


class MODULEENTRY32W(ctypes.Structure):
    _fields_ = [
        ('dwSize', w.DWORD),
        ('th32ModuleID', w.DWORD),
        ('th32ProcessID', w.DWORD),
        ('GlblcntUsage', w.DWORD),
        ('ProccntUsage', w.DWORD),
        ('modBaseAddr', ctypes.POINTER(ctypes.c_byte)),
        ('modBaseSize', w.DWORD),
        ('hModule', w.HMODULE),
        ('szModule', w.WCHAR * 256),
        ('szExePath', w.WCHAR * 260),
    ]


def _check(ok, what):
    if not ok:
        raise OSError(ctypes.get_last_error(), what)


def open_process(pid):
    handle = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    _check(handle, 'OpenProcess(pid=%d) 失败（进程已退出，或被反作弊拒绝）' % pid)
    return handle


def list_modules(handle, pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == ctypes.c_void_p(-1).value or snap is None:
        raise OSError(ctypes.get_last_error(), 'CreateToolhelp32Snapshot 失败')
    entry = MODULEENTRY32W()
    entry.dwSize = ctypes.sizeof(MODULEENTRY32W)
    out = []
    ok = k32.Module32FirstW(ctypes.c_void_p(snap), ctypes.byref(entry))
    while ok:
        out.append({
            'name': entry.szModule,
            'path': entry.szExePath,
            'base': ctypes.cast(entry.modBaseAddr, ctypes.c_void_p).value,
            'size': int(entry.modBaseSize),
        })
        ok = k32.Module32NextW(ctypes.c_void_p(snap), ctypes.byref(entry))
    k32.CloseHandle(ctypes.c_void_p(snap))
    return out


def read_memory(handle, address, size):
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                               buf, size, ctypes.byref(got))
    if not ok:
        return None, ctypes.get_last_error()
    return buf.raw[:got.value], 0


def readable_regions(handle, base, size):
    """把 [base, base+size) 切成可读/不可读区间，返回 [(rva, length, readable)]。"""
    regions = []
    address = base
    limit = base + size
    mbi = MEMORY_BASIC_INFORMATION64()
    while address < limit:
        got = k32.VirtualQueryEx(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                                 ctypes.byref(mbi), ctypes.sizeof(mbi))
        if not got:
            # 查不到就保守地当整段不可读，避免盲目读触发异常。
            regions.append((address - base, limit - address, False))
            break
        span = min(mbi.RegionSize, limit - address)
        protect = mbi.Protect & 0xFF
        ok = (mbi.State == MEM_COMMIT
              and not (mbi.Protect & PAGE_GUARD)
              and protect != PAGE_NOACCESS
              and protect in READABLE)
        regions.append((address - base, span, ok))
        address += mbi.RegionSize
    return regions


def parse_pe_header(image, size_of_image_hint):
    """从内存镜像里读 PE 头（内存里 e_lfanew 指向的是已映射的头，可直接用）。"""
    if image[:2] != b'MZ':
        return None
    pe = struct.unpack_from('<I', image, 0x3C)[0]
    if image[pe:pe + 4] != b'PE\0\0':
        return None
    machine, count = struct.unpack_from('<HH', image, pe + 4)
    opt_size = struct.unpack_from('<H', image, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', image, opt)[0]
    if magic == 0x20B:
        image_base = struct.unpack_from('<Q', image, opt + 24)[0]
        size_of_image = struct.unpack_from('<I', image, opt + 56)[0]
    else:
        image_base = struct.unpack_from('<I', image, opt + 28)[0]
        size_of_image = struct.unpack_from('<I', image, opt + 56)[0]
    table = opt + opt_size
    sections = []
    for i in range(count):
        at = table + i * 40
        name = image[at:at + 8].rstrip(b'\0').decode('latin-1')
        vsize, vaddr = struct.unpack_from('<II', image, at + 8)
        chars = struct.unpack_from('<I', image, at + 36)[0]
        sections.append({
            'name': name,
            'vaddr': vaddr,
            'vsize': vsize,
            'executable': bool(chars & 0x20000000),
            'readable': bool(chars & 0x40000000),
        })
    return {
        'machine': machine,
        'magic': magic,
        'image_base': image_base,
        'size_of_image': size_of_image or size_of_image_hint,
        'sections': sections,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pid', type=int, required=True)
    ap.add_argument('--module', default='game.dll')
    ap.add_argument('--output', help='镜像布局的输出文件；不给则只探测')
    ap.add_argument('--start', type=lambda x: int(x, 0), default=None,
                    help='只抓这一段 RVA（默认整模块）')
    ap.add_argument('--length', type=lambda x: int(x, 0), default=None)
    ap.add_argument('--probe', action='store_true', help='只报告模块与可读区间，不落盘')
    ap.add_argument('--meta', help='把模块信息/区间统计写成 JSON')
    args = ap.parse_args()

    handle = open_process(args.pid)
    try:
        modules = list_modules(handle, args.pid)
        target = None
        for entry in modules:
            if entry['name'].lower() == args.module.lower():
                target = entry
                break
        if target is None:
            names = ', '.join(sorted(m['name'] for m in modules)[:40])
            raise SystemExit('模块 %s 不在 pid %d 里；可见模块：%s'
                             % (args.module, args.pid, names))

        header, err = read_memory(handle, target['base'], 0x1000)
        if header is None:
            raise SystemExit('读 PE 头失败，Win32 错误 %d' % err)
        pe = parse_pe_header(header, target['size'])
        if pe is None:
            raise SystemExit('内存里的 PE 头不成立（MZ/PE 签名缺失）')

        size_of_image = pe['size_of_image']
        print('模块 %s' % target['name'])
        print('  路径        %s' % target['path'])
        print('  基址        0x%016x' % target['base'])
        print('  SizeOfImage %d (0x%x)' % (size_of_image, size_of_image))
        print('  ImageBase   0x%x   machine=0x%x  magic=0x%x'
              % (pe['image_base'], pe['machine'], pe['magic']))
        print('  section：%s' % ', '.join('%s@0x%x+0x%x%s'
                                         % (s['name'], s['vaddr'], s['vsize'],
                                            'X' if s['executable'] else '')
                                         for s in pe['sections']))
        on_disk = None
        try:
            on_disk = os.path.getsize(target['path'])
        except OSError:
            pass
        if on_disk:
            print('  盘上文件    %d (0x%x)  <- 比 SizeOfImage %s，%s'
                  % (on_disk, on_disk,
                     '小' if on_disk < size_of_image else '大',
                     '所以盘上那份不能直接反汇编' if on_disk < size_of_image
                     else '可直接反汇编'))

        start = args.start if args.start is not None else 0
        length = args.length if args.length is not None else size_of_image - start
        if start < 0 or length <= 0 or start + length > size_of_image:
            raise SystemExit('RVA 范围 0x%x+0x%x 越出 SizeOfImage 0x%x'
                             % (start, length, size_of_image))

        regions = readable_regions(handle, target['base'] + start, length)
        committed = sum(span for _rva, span, ok in regions if ok)
        print('  区间 0x%x+0x%x：%d 段，其中可读 %d B（%.1f%%）'
              % (start, length, len(regions), committed,
                 100.0 * committed / length))

        meta = {
            'schema': 1,
            'pid': args.pid,
            'module': target['name'],
            'modulePath': target['path'],
            'moduleBase': target['base'],
            'sizeOfImage': size_of_image,
            'imageBase': pe['image_base'],
            'onDiskBytes': on_disk,
            'sections': pe['sections'],
            'start': start,
            'length': length,
            'committedBytes': committed,
            'regions': [{'rva': r, 'length': s, 'readable': ok} for r, s, ok in regions],
        }

        if args.probe or not args.output:
            print('\n（--probe：不落盘）')
        else:
            image = bytearray(length)
            failed = 0
            for rva, span, ok in regions:
                if not ok:
                    continue
                chunk, err = read_memory(handle, target['base'] + start + rva, span)
                if chunk is None:
                    failed += 1
                    continue
                image[rva:rva + len(chunk)] = chunk
            parent = os.path.dirname(os.path.abspath(args.output))
            os.makedirs(parent, exist_ok=True)
            with open(args.output, 'wb') as fh:
                fh.write(image)
            nonzero = sum(1 for b in bytes(image[:min(length, 1 << 22)]) if b)
            print('\n已写出 %s（%d B，RVA==偏移，起始 RVA 0x%x）'
                  % (args.output, length, start))
            print('  前 4 MiB 非零字节 %d；读取失败的区间 %d 段' % (nonzero, failed))
            meta['output'] = os.path.abspath(args.output)
            meta['failedRegions'] = failed

        if args.meta:
            parent = os.path.dirname(os.path.abspath(args.meta))
            os.makedirs(parent, exist_ok=True)
            with open(args.meta, 'w', encoding='utf-8') as fh:
                json.dump(meta, fh, ensure_ascii=False, indent=2)
            print('  元数据 %s' % args.meta)
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))
    return 0


if __name__ == '__main__':
    sys.exit(main())
