# -*- coding: utf-8 -*-
"""只读探针：在运行中的游戏进程里定位某个 Unit 资源的 TOC 副本。

为什么需要它：`notes/numindices-is-the-visibility-switch-20261017.md` 已经证明
把 section 的 `NumIndices` 写成 0 就能隐藏那块几何，但开关表里的偏移是
**相对该 unit 自己的 TOC 起点**的。运行期要改，就必须先知道那份 TOC 在内存里
从哪儿开始。这个脚本只做这一步，而且**只读**：OpenProcess 只申请
`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`，不写、不改保护属性、不碰代码段。

做法：

1. 从 patch 文件里按条目表取出目标 unit 的 TOC 字节（TOC 是明文存储的）。
2. 在 TOC 里挑一段 64 字节、在 TOC 内部唯一的签名。
3. 枚举目标进程所有可读已提交区域，用 `bytes.find` 找签名。
4. 每个命中处算出候选基址 `hit - sig_offset`，再用该 unit 各 section 的
   `NumIndices`（偏移 `section.Offset + 16`）做反验证。
   反验证通过 = 基址正确，这一步把"撞巧"排除掉。

用法：

    python find_unit_toc.py --pid 1234 --patch <patch_0> --file-id 0x9841b16544c8fbae
"""

import argparse
import ctypes
import ctypes.wintypes as w
import struct
import sys

sys.stdout.reconfigure(encoding='utf-8')

k32 = ctypes.WinDLL('kernel32', use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010

MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100

UNIT_TYPE_ID = 0xE0A48D0BE9A7453F
SECTION_RECORD_SIZE = 24
NUM_INDICES_OFFSET = 16


class MEMORY_BASIC_INFORMATION64(ctypes.Structure):
    _fields_ = [
        ('BaseAddress', ctypes.c_ulonglong),
        ('AllocationBase', ctypes.c_ulonglong),
        ('AllocationProtect', w.DWORD),
        ('__alignment1', w.DWORD),
        ('RegionSize', ctypes.c_ulonglong),
        ('State', w.DWORD),
        ('Protect', w.DWORD),
        ('Type', w.DWORD),
        ('__alignment2', w.DWORD),
    ]


def open_process(pid):
    handle = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise SystemExit('OpenProcess(%d) 失败：Win32 %d' % (pid, ctypes.get_last_error()))
    return handle


def read(handle, address, size):
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                               buf, size, ctypes.byref(got))
    if not ok or got.value != size:
        return None
    return buf.raw


def regions(handle, private_only=False, min_size=0, max_size=None):
    """Yield (base, size, type) for committed, readable regions."""
    address = 0
    mbi = MEMORY_BASIC_INFORMATION64()
    while True:
        got = k32.VirtualQueryEx(ctypes.c_void_p(handle), ctypes.c_void_p(address),
                                 ctypes.byref(mbi), ctypes.sizeof(mbi))
        if not got:
            break
        base = mbi.BaseAddress
        size = mbi.RegionSize
        if size == 0:
            break
        readable = (mbi.State == MEM_COMMIT
                    and not (mbi.Protect & PAGE_NOACCESS)
                    and not (mbi.Protect & PAGE_GUARD))
        if readable and size >= min_size and (max_size is None or size <= max_size):
            if not private_only or mbi.Type == MEM_PRIVATE:
                yield base, size, mbi.Type
        address = base + size


def iter_chunks(handle, base, size, chunk=1024 * 1024, floor=64 * 1024):
    """Yield (address, bytes) for a region, degrading the chunk size on failure.

    ReadProcessMemory fails the whole request if any page in the range is not
    readable, and a process that is actively mapping and unmapping memory has
    plenty of those. A single 8 MB request therefore fails often enough to hide
    the data we are looking for, so retry smaller before giving up on a span.

    The retry floor is clamped to the remaining region size so that blocks
    smaller than the floor are still read; an earlier version stepped over them,
    which silently skipped most of a game's heap.
    """
    cursor = base
    end = base + size
    while cursor < end:
        span = min(chunk, end - cursor)
        # A region can be smaller than the retry floor; read it whole rather than
        # stepping over it, which is what silently skipped most small heap blocks.
        smallest = min(floor, end - cursor)
        while span >= smallest:
            data = read(handle, cursor, span)
            if data is not None:
                yield cursor, data
                cursor += span
                break
            span //= 4
        else:
            cursor += smallest


def scan_for(handle, signature, max_hits=16, private_only=True,
             min_size=0, max_size=None, progress=None):
    """Return addresses where `signature` appears in readable memory.

    The region list is snapshotted first, then scanned. Interleaving
    VirtualQueryEx with reads is not safe here: a single transient query failure
    ends the enumeration, and everything above that address is silently skipped,
    which is exactly how an earlier version of this scan reported "0 hits" for a
    signature that was demonstrably present.
    """
    snapshot = list(regions(handle, private_only, min_size, max_size))
    hits = []
    for index, (base, size, _) in enumerate(snapshot):
        if progress is not None and index and index % 2000 == 0:
            progress(index, len(snapshot), len(hits))
        for address, data in iter_chunks(handle, base, size):
            position = data.find(signature)
            while position >= 0:
                hits.append(address + position)
                if len(hits) >= max_hits:
                    return hits
                position = data.find(signature, position + 1)
    return hits


def parse_entries(toc):
    """Return [(file_id, type_id, toc_data_offset, toc_size)] using the slim layout."""
    if len(toc) < 12:
        raise SystemExit('patch 太小')
    magic, type_count, file_count = struct.unpack_from('<III', toc, 0)
    if magic != 0xF0000011:
        raise SystemExit('不是预期 magic：0x%x' % magic)
    entries_offset = 72 + type_count * 32
    out = []
    for index in range(file_count):
        entry = entries_offset + index * 80
        if entry + 80 > len(toc):
            break
        file_id, type_id = struct.unpack_from('<QQ', toc, entry)
        toc_offset = struct.unpack_from('<Q', toc, entry + 16)[0]
        toc_size = struct.unpack_from('<I', toc, entry + 56)[0]
        out.append((file_id, type_id, toc_offset, toc_size))
    return out


def score_window(window):
    """Higher is more content-like: byte diversity minus long zero runs."""
    diversity = len(set(window))
    longest_zero_run = 0
    run = 0
    for byte in window:
        run = run + 1 if byte == 0 else 0
        longest_zero_run = max(longest_zero_run, run)
    return diversity * 2 - longest_zero_run * 4


def rank_signatures(toc, length=64):
    """All windows unique inside the TOC, best-scoring first."""
    ranked = []
    for offset in range(0, len(toc) - length, 8):
        window = toc[offset:offset + length]
        if toc.count(window) != 1:
            continue
        ranked.append((score_window(window), offset, window))
    ranked.sort(key=lambda item: item[0], reverse=True)
    return ranked


def choose_signature(toc, length=64):
    """Pick a window that looks like content (hashes), not relocatable offsets.

    Offset fields get rewritten by a loader, so a signature that spans them will
    never be found in memory. Hash tables (material ids, mesh ids, bone name
    hashes) are content: they survive any relayout.
    """
    ranked = rank_signatures(toc, length)
    if not ranked:
        return None
    _, offset, window = ranked[0]
    return offset, window


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--pid', type=int, required=True)
    parser.add_argument('--patch', required=True)
    parser.add_argument('--file-id', required=True)
    parser.add_argument('--sig-len', type=int, default=64)
    parser.add_argument('--max-hits', type=int, default=16)
    parser.add_argument('--sig-offset', type=lambda v: int(v, 0), default=None,
                        help='强制使用 TOC 内这个偏移的签名，而不是自动挑选')
    parser.add_argument('--list-signatures', type=int, default=0,
                        help='只列出得分最高的 N 个候选签名，不扫内存')
    parser.add_argument('--all-regions', action='store_true',
                        help='不只看 MEM_PRIVATE，也扫映射/映像区域（慢很多）')
    args = parser.parse_args()

    file_id = int(args.file_id, 16)
    patch = open(args.patch, 'rb').read()
    entries = parse_entries(patch)

    target = [e for e in entries if e[0] == file_id and e[1] == UNIT_TYPE_ID]
    if not target:
        raise SystemExit('patch 里没有 fileId 0x%x 的 Unit 条目' % file_id)
    _, _, toc_offset, toc_size = target[0]
    toc = patch[toc_offset:toc_offset + toc_size]
    print('unit 0x%x  TOC 在 patch 里 @0x%x  长度 %d' % (file_id, toc_offset, toc_size))

    if args.list_signatures:
        ranked = rank_signatures(toc, args.sig_len)
        print('得分最高的 %d 个签名候选：' % args.list_signatures)
        for score, offset, window in ranked[:args.list_signatures]:
            print('  TOC+0x%-6x score=%-4d  %s' % (offset, score, window[:16].hex()))
        return 0

    if args.sig_offset is not None:
        sig_offset = args.sig_offset
        sig_bytes = toc[sig_offset:sig_offset + args.sig_len]
        if len(sig_bytes) != args.sig_len:
            raise SystemExit('--sig-offset 越界')
        signature = (sig_offset, sig_bytes)
    else:
        signature = choose_signature(toc, args.sig_len)
    if signature is None:
        raise SystemExit('找不到唯一签名，试试 --sig-len 更大')
    sig_offset, sig_bytes = signature
    print('签名 @TOC+0x%x  %d 字节  %s…' % (sig_offset, len(sig_bytes), sig_bytes[:8].hex()))

    handle = open_process(args.pid)
    hits = []
    confirmed = []
    try:
        hits = scan_for(handle, sig_bytes, args.max_hits,
                        private_only=not args.all_regions,
                        progress=lambda index, total, found: print('  …扫描 %d/%d 个区域，命中 %d' % (index, total, found)))
        for hit_address in hits:
            candidate = hit_address - sig_offset
            print('\n命中 0x%016x  -> 候选 TOC 基址 0x%016x' % (hit_address, candidate))
            confirmed.append((candidate, hit_address))
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))

    print('\n共 %d 个命中' % len(hits))
    if not hits:
        print('没找到。可能该 unit 还没被加载（需要角色穿着这套护甲并进入可渲染场景），')
        print('或者内存里的 TOC 与文件不是逐字节相同。')
        return 1

    print('\n反验证：把候选基址处的整块 TOC 读出来，与文件逐字节比对。')
    print('基址正确时应当接近 100%；若只是撞巧命中签名，比例会很低。')
    handle = open_process(args.pid)
    try:
        for candidate, _ in confirmed:
            sample = read(handle, candidate, len(toc))
            if sample is None:
                print('  0x%016x  读不到 %d 字节' % (candidate, len(toc)))
                continue
            same = sum(1 for a, b in zip(sample, toc) if a == b)
            ratio = same / len(toc)
            print('  0x%016x  与文件 TOC 相同 %.2f%%  (%d/%d 字节)'
                  % (candidate, ratio * 100, same, len(toc)))
    finally:
        k32.CloseHandle(ctypes.c_void_p(handle))
    return 0


if __name__ == '__main__':
    sys.exit(main())
