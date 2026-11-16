#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 Windows minidump 拆成可分析产物：异常、模块、崩溃栈候选、内存里的 DXBC/DXIL blob。

用法:
    python tools/dmp_extract.py <dump.dmp> [--outdir .dsh/analysis/dump]

产出（outdir 下）:
    summary.txt       异常码/地址/模块归属、模块表、内存区统计
    stack.txt         崩溃线程上下文 + 栈上的返回地址候选（按模块+偏移）
    dxbc/*.bin        内存里每一个合法 DXBC 容器（含 DXIL），文件名带 VA 与大小
    dxbc_index.txt    每个 blob 的 VA、大小、chunk 列表、ISGN/OSGN 大小、SHEX 大小
    hits.txt          我方已知特征的命中（record 校验和 / 我们的 SHEX 尾部等）
"""
import argparse
import os
import struct
import sys

MOD = {3: 'ThreadList', 4: 'ModuleList', 5: 'MemoryList', 6: 'Exception', 7: 'SystemInfo',
       9: 'Memory64List', 10: 'CommentA', 11: 'CommentW', 12: 'HandleData',
       15: 'MiscInfo', 16: 'MemoryInfoList', 17: 'ThreadInfoList'}

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV DXIL'.split())


def parse_streams(d):
    _, nstreams, sdir, _, _, _ = struct.unpack_from('<IIIIQQ', d, 4)
    out = {}
    for i in range(nstreams):
        typ, size, rva = struct.unpack_from('<III', d, sdir + i * 12)
        if typ not in out and size:
            out[typ] = (size, rva)
    return out


def parse_modules(d, st):
    if 4 not in st:
        return []
    _, rva = st[4]
    n, = struct.unpack_from('<I', d, rva)
    mods = []
    for i in range(n):
        base = rva + 4 + i * 108
        b, = struct.unpack_from('<Q', d, base)
        sz, = struct.unpack_from('<I', d, base + 8)
        name_rva, = struct.unpack_from('<I', d, base + 20)
        ln, = struct.unpack_from('<I', d, name_rva)
        nm = d[name_rva + 4:name_rva + 4 + ln].decode('utf-16-le', 'replace').rstrip('\x00')
        mods.append((b, sz, nm))
    return mods


def memory_map(d, st):
    """返回 [(va, size, file_off)]"""
    regions = []
    if 5 in st:
        _, rva = st[5]
        n, = struct.unpack_from('<I', d, rva)
        if n > 100000:
            n = 0
        for i in range(n):
            va, sz, r = struct.unpack_from('<QII', d, rva + 4 + i * 16)
            regions.append((va, sz, r))
    if 9 in st:
        sz_, rva = st[9]
        n, base_rva = struct.unpack_from('<QQ', d, rva)
        cur = base_rva
        for i in range(n):
            va, sz = struct.unpack_from('<QQ', d, rva + 16 + i * 16)
            regions.append((va, sz, cur))
            cur += sz
    return regions


def va_to_off(regions, va, need=1):
    for a, sz, off in regions:
        if a <= va < a + sz and va + need <= a + sz:
            return off + (va - a)
    return None


def which_module(mods, va):
    for b, sz, nm in mods:
        if b <= va < b + sz:
            return nm, va - b
    return None, None


def find_dxbc(d, regions):
    """在内存区里找合法 DXBC 容器"""
    out = []
    pat = b'DXBC'
    i = 0
    seen = set()
    while True:
        j = d.find(pat, i)
        if j < 0:
            break
        i = j + 1
        if j in seen or j + 40 > len(d):
            continue
        ver, total, nch = struct.unpack_from('<I', d, j + 20)[0], struct.unpack_from('<I', d, j + 24)[0], struct.unpack_from('<I', d, j + 28)[0]
        if ver != 1 or not (40 < total < (1 << 24)) or not (1 <= nch <= 16):
            continue
        if j + total > len(d):
            continue
        try:
            offs = sorted(struct.unpack_from('<%dI' % nch, d, j + 32))
        except struct.error:
            continue
        if any(o < 32 or o + 8 > total for o in offs):
            continue
        chunks = []
        ok = True
        for k, o in enumerate(offs):
            fcc = d[j + o:j + o + 4]
            if fcc not in CH:
                ok = False
                break
            end = offs[k + 1] if k + 1 < len(offs) else total
            chunks.append((fcc.decode(), end - o - 8))
        if not ok:
            continue
        seen.add(j)
        out.append((j, total, chunks))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dump')
    ap.add_argument('--outdir', default='.dsh/analysis/dump')
    a = ap.parse_args()
    d = open(a.dump, 'rb').read()
    os.makedirs(a.outdir, exist_ok=True)
    os.makedirs(os.path.join(a.outdir, 'dxbc'), exist_ok=True)
    st = parse_streams(d)
    mods = parse_modules(d, st)
    regions = memory_map(d, st)

    lines = []
    def w(s=''):
        lines.append(s)
        print(s)

    w('=== 转储 ===')
    w('文件 %s  %d B (%.1f MB)' % (a.dump, len(d), len(d) / 1048576))
    w('流: ' + ', '.join('%s(%d)' % (MOD.get(t, t), sz) for t, (sz, _) in sorted(st.items())))
    w('内存区 %d 个，覆盖 %.1f MB 虚拟地址' % (len(regions), sum(s for _, s, _ in regions) / 1048576))

    # 异常
    exc_addr = None
    if 6 in st:
        _, rva = st[6]
        tid, _ = struct.unpack_from('<II', d, rva)
        code, flags, rec, addr, np, pad = struct.unpack_from('<IIQQII', d, rva + 8)
        info = struct.unpack_from('<15Q', d, rva + 8 + 32)
        exc_addr = addr
        w('')
        w('=== 异常 ===')
        w('  线程 %d  异常码 0x%08X  地址 0x%016X  NumberParameters=%d' % (tid, code, addr, np))
        w('  ExceptionInformation[0..3] = %s' % ', '.join('0x%X' % x for x in info[:4]))
        nm, off = which_module(mods, addr)
        w('  归属: %s + 0x%X' % (nm.split('\\')[-1] if nm else '(模块外)', off or 0))
        # 线程上下文
        csz, crva = struct.unpack_from('<II', d, rva + 160)
        w('  ThreadContext: off=0x%x size=%d' % (crva, csz))
        if 0 < crva < len(d) and csz > 200:
            # CONTEXT_AMD64: Rip @ 0xF8, Rsp @ 0x98, Rbp @ 0xA0
            try:
                rip, = struct.unpack_from('<Q', d, crva + 0xF8)
                rsp, = struct.unpack_from('<Q', d, crva + 0x98)
                rbp, = struct.unpack_from('<Q', d, crva + 0xA0)
                w('  RIP=0x%016X  RSP=0x%016X  RBP=0x%016X' % (rip, rsp, rbp))
                # 栈扫描：把 RSP 起 8KB 里的 8 字节值当返回地址候选
                st_lines = []
                base = va_to_off(regions, rsp, 8192)
                if base:
                    for k in range(0, 8192, 8):
                        v, = struct.unpack_from('<Q', d, base + k)
                        nm2, off2 = which_module(mods, v)
                        if nm2:
                            st_lines.append((rsp + k, v, nm2, off2))
                with open(os.path.join(a.outdir, 'stack.txt'), 'w', encoding='utf-8') as f:
                    f.write('RIP=0x%016X RSP=0x%016X RBP=0x%016X\n' % (rip, rsp, rbp))
                    f.write('栈上落在模块内的地址（返回地址候选，按栈位置升序）：\n')
                    f.write('  %-18s %-18s %s\n' % ('栈地址', '值', '模块+偏移'))
                    for sa, v, nm2, off2 in st_lines:
                        f.write('  0x%016X 0x%016X %s + 0x%X\n' % (sa, v, nm2.split('\\')[-1], off2))
                w('  栈候选 %d 条 -> stack.txt' % len(st_lines))
            except struct.error:
                w('  线程上下文解析失败')
    else:
        w('（无异常流）')

    # 模块
    with open(os.path.join(a.outdir, 'modules.txt'), 'w', encoding='utf-8') as f:
        for b, sz, nm in sorted(mods, key=lambda x: -x[1]):
            f.write('0x%016X %10d %s\n' % (b, sz, nm))

    # DXBC 扫描
    blobs = find_dxbc(d, regions)
    w('')
    w('=== 内存里的 DXBC 容器: %d 个 ===' % len(blobs))
    idx = ['VA          size    chunks']
    for n, (off, total, chunks) in enumerate(blobs):
        va = None
        for a0, sz, o0 in regions:
            if o0 <= off < o0 + sz:
                va = a0 + (off - o0)
                break
        name = 'dxbc/%03d_%s_%d.bin' % (n, ('%016X' % va) if va else 'n%05x' % off, total)
        open(os.path.join(a.outdir, name), 'wb').write(d[off:off + total])
        idx.append('%s %8d  %s' % (('0x%016X' % va) if va else '?', total,
                                    ' '.join('%s:%d' % c for c in chunks)))
    open(os.path.join(a.outdir, 'dxbc_index.txt'), 'w', encoding='utf-8').write('\n'.join(idx) + '\n')
    for l in idx[:25]:
        w('  ' + l)

    # 我方特征命中：已知记录的存储校验和 / 我们的 NOP 尾部
    hits = []
    known = {
        '原始 matpass 存储校验和': bytes.fromhex('91718adc4104626e4ae7dc3853a20774'),
        '原始 adv_main 校验和': bytes.fromhex('3f6860a256410510219a7624a1653cfb'),
        '我们的 NOP 尾(3A)': struct.pack('<I', 0x0100003A) * 6,
        '我们的 magenta 校验和': bytes.fromhex('b643bc4ed5dbfbb87168ef368915c958'),
        '我们的 rough7 校验和': bytes.fromhex('0b8bdb9078a56f915761ddead163faca'),
    }
    for k, pat in known.items():
        c = d.count(pat)
        hits.append('%s: %d 次' % (k, c))
    open(os.path.join(a.outdir, 'hits.txt'), 'w', encoding='utf-8').write('\n'.join(hits) + '\n')
    w('')
    w('=== 已知特征命中 ===')
    for h in hits:
        w('  ' + h)

    open(os.path.join(a.outdir, 'summary.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print('\n产物目录: %s' % os.path.abspath(a.outdir))


if __name__ == '__main__':
    sys.exit(main())
