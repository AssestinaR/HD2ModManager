#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""比较两份材质载荷的「pass 描述符」集合。

载荷内部结构（实测，不是猜想）：
    头(约 251 KB，含 pass 表)  +  [ 描述符块 | DXBC 记录 ] × N
描述符块 = 紧贴在 DXBC 之前的若干字节，头 4 字节是一个哈希（推测是 pass 名哈希）。

用法：
    python payload_passes.py <载荷 A> <载荷 B> [...]
"""
import struct
import sys
import collections

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())


def records(d):
    out, i = [], 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            break
        i = s + 4
        if s + 32 > len(d):
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if not (32 < total < (1 << 23)) or not (1 <= nch <= 16) or s + total > len(d):
            continue
        try:
            offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
        except struct.error:
            continue
        if any(o < 32 or o >= total for o in offs):
            continue
        ok = all(bytes(d[s + o:s + o + 4]) in CH for o in offs)
        if ok:
            out.append((s, total))
    return out


def desc_hashes(d):
    recs = records(d)
    out = []
    prev_end = 0
    for s, t in recs:
        if s > prev_end and s - prev_end >= 8:
            out.append((struct.unpack_from('<I', d, prev_end)[0], s - prev_end, s))
        prev_end = max(prev_end, s + t)
    return recs, out


def main():
    names, sets = [], []
    for p in sys.argv[1:]:
        d = open(p, 'rb').read()
        recs, ds = desc_hashes(d)
        c = collections.Counter(h for h, _, _ in ds)
        names.append(p.split('\\')[-1])
        sets.append((c, recs, ds, d))
        hdr = struct.unpack_from('<5I', d, 0)
        print('\n%-28s %d B  记录 %d  描述符 %d  头+4=0x%08x 头+12=%d' % (
            names[-1], len(d), len(recs), len(ds), hdr[1], hdr[3]))
        print('   pass 哈希（出现次数）：')
        for h, n in c.most_common(12):
            print('     0x%08x  x%d' % (h, n))
        if len(c) > 12:
            print('     … 共 %d 个不同哈希' % len(c))

    if len(sets) >= 2:
        base = set(sets[0][0])
        for k in range(1, len(sets)):
            other = set(sets[k][0])
            inter = base & other
            print('\n%s ∩ %s = %d 个共同 pass；仅前者 %d；仅后者 %d' % (
                names[0], names[k], len(inter), len(base - other), len(other - base)))


if __name__ == '__main__':
    sys.exit(main())
