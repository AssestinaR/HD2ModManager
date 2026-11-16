#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在 minidump 里搜"材质/载荷/DXBC"这三类结构，用来回答：
  · 引擎到底有没有加载我们注入的载荷？
  · 它加载了哪些材质 TocData（id / undat2 / parent）？
  · 内存里驻留了哪些 DXBC 记录（尺寸分布）？

三类指纹（都是实测定出来的）：
  载荷头    +0 = 0xb3(179)  +4 = 材质 id 高 32 位  +16 = 1
  材质TocData +0 = 288  +4 = 1  +8 = 24   (+12 EndOffset, +16 undat2, +24 parent, +64 nTex)
  DXBC 容器 "DXBC" + version==1 + 合理总长 + 合法块表

用法：
  python tools/dmp_scan.py <dump> [--ids 0xab344c46,0x9ba626af] [--outdir .dsh/analysis/dumpscan]
"""
import argparse
import os
import struct
import sys

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV DXIL'.split())


def va_map(d):
    """粗粒度：把文件偏移映射回虚拟地址（用 MemoryList/Memory64List）"""
    try:
        _, nstreams, sdir, _, _, _ = struct.unpack_from('<IIIIQQ', d, 4)
    except struct.error:
        return []
    st = {}
    for i in range(nstreams):
        typ, size, rva = struct.unpack_from('<III', d, sdir + i * 12)
        if typ not in st and size:
            st[typ] = (size, rva)
    regs = []
    if 5 in st:
        _, rva = st[5]
        n, = struct.unpack_from('<I', d, rva)
        if n < 100000:
            for i in range(n):
                va, sz, r = struct.unpack_from('<QII', d, rva + 4 + i * 16)
                regs.append((r, r + sz, va))
    if 9 in st:
        _, rva = st[9]
        n, base = struct.unpack_from('<QQ', d, rva)
        cur = base
        for i in range(n):
            va, sz = struct.unpack_from('<QQ', d, rva + 16 + i * 16)
            regs.append((cur, cur + sz, va))
            cur += sz
    return regs


def off_to_va(regs, off):
    for a, b, va in regs:
        if a <= off < b:
            return va + (off - a)
    return None


def scan_payload_headers(d):
    out, i = [], 0
    pat = struct.pack('<I', 0xb3)
    while True:
        j = d.find(pat, i)
        if j < 0:
            return out
        i = j + 1
        if j + 32 > len(d):
            continue
        u = struct.unpack_from('<8I', d, j)
        if u[4] == 1 and 1000 <= u[2] < 200 * 1024 * 1024 and u[1] != 0 and u[1] != 0xb3:
            out.append((j, u))


def scan_tocdata(d):
    out = []
    pat = struct.pack('<III', 288, 1, 24)
    i = 0
    while True:
        j = d.find(pat, i)
        if j < 0:
            return out
        i = j + 1
        if j + 128 > len(d):
            continue
        end, = struct.unpack_from('<I', d, j + 12)
        undat2, = struct.unpack_from('<Q', d, j + 16)
        parent, = struct.unpack_from('<Q', d, j + 24)
        ntex, = struct.unpack_from('<I', d, j + 64)
        if 200 <= end <= 64 * 1024 and ntex <= 64:
            out.append((j, end, undat2, parent, ntex))


def scan_dxbc(d):
    out, i = [], 0
    while True:
        j = d.find(b'DXBC', i)
        if j < 0:
            return out
        i = j + 1
        if j + 40 > len(d):
            continue
        ver, total, nch = struct.unpack_from('<III', d, j + 20)[0], struct.unpack_from('<I', d, j + 24)[0], struct.unpack_from('<I', d, j + 28)[0]
        if ver != 1 or not (40 < total < (1 << 24)) or not (1 <= nch <= 16) or j + total > len(d):
            continue
        try:
            offs = sorted(struct.unpack_from('<%dI' % nch, d, j + 32))
        except struct.error:
            continue
        if any(o < 32 or o + 8 > total for o in offs):
            continue
        chunks, ok = [], True
        for k, o in enumerate(offs):
            fcc = d[j + o:j + o + 4]
            if fcc not in CH:
                ok = False
                break
            end = offs[k + 1] if k + 1 < len(offs) else total
            chunks.append((fcc.decode(), end - o - 8))
        if ok:
            out.append((j, total, chunks))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dump')
    ap.add_argument('--ids', default='', help='关心的材质 id 高 32 位，逗号分隔，如 0xab344c46')
    ap.add_argument('--outdir', default=None)
    a = ap.parse_args()
    d = open(a.dump, 'rb').read()
    regs = va_map(d)
    want = set()
    for s in a.ids.split(','):
        s = s.strip()
        if s:
            want.add(int(s, 16) & 0xFFFFFFFF)

    lines = []
    def w(s=''):
        lines.append(s)
        print(s)

    w('dump %s  %.1f MB  内存区 %d' % (os.path.basename(a.dump), len(d) / 1048576, len(regs)))

    ph = scan_payload_headers(d)
    w('')
    w('=== 载荷头: %d 个 ===' % len(ph))
    for off, u in ph[:30]:
        va = off_to_va(regs, off)
        mark = ' ★关心的 id' if (u[1] & 0xFFFFFFFF) in want else ''
        w('   %s +4=0x%08x +8=%-10d +12=0x%08x +16=%d%s' % (
            ('VA=0x%016X' % va) if va else ('off=0x%x' % off), u[1], u[2], u[3], u[4], mark))

    td = scan_tocdata(d)
    w('')
    w('=== 材质 TocData: %d 个 ===' % len(td))
    for off, end, undat2, parent, ntex in td[:30]:
        va = off_to_va(regs, off)
        w('   %s EndOffset=%-6d undat2=%-10d parent=0x%016x ntex=%d' % (
            ('VA=0x%016X' % va) if va else ('off=0x%x' % off), end, undat2, parent, ntex))

    dx = scan_dxbc(d)
    w('')
    w('=== DXBC 容器: %d 个 ===' % len(dx))
    import collections
    cnt = collections.Counter(t for _, t, _ in dx)
    for size, n in sorted(cnt.items()):
        w('   %d B × %d' % (size, n))
    for off, total, chunks in dx[:12]:
        w('   off=0x%-8x %-7d %s' % (off, total, ' '.join('%s:%d' % c for c in chunks)))

    w('')
    if not ph:
        w('！没有载荷头。说明这份 dump 没覆盖到载荷页（不是"没加载"的证据）。')
    if a.outdir:
        os.makedirs(a.outdir, exist_ok=True)
        open(os.path.join(a.outdir, 'scan.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
        print('\n写出 %s' % os.path.join(a.outdir, 'scan.txt'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
