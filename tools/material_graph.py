#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""材质图谱：扫一个 mod（或一批 patch）里所有 Material 条目，判断「谁自带载荷、谁是子材质」。

判据（实测，见 docs/agent/hd2-payload-host-answer-20261115b.md §3）：
    parent != 0                                  -> 子材质（着色器载荷继承自 parent，自身不带）
    parent == 0 且 gpuSize > 0 且 undat2 == gpuSize -> 根材质，载荷就在本 patch 的 .gpu_resources 的 gpuOff 处
    gpuSize == 0                                 -> 该条目是 delta，本身不带载荷
    载荷头 +4 必须 == 材质 id 的高 32 位；不等就标「自洽✗」

用法：
    python tools/material_graph.py <mod 目录> [...]
    python tools/material_graph.py <某个 patch_0> [...]
"""
import glob
import os
import struct
import sys

MAT = 0xEAC0B497876ADEDF
MAGIC = 0xF0000011


def parse_patch(d):
    if len(d) < 12 or struct.unpack_from('<I', d, 0)[0] != MAGIC:
        return []
    tn, en = struct.unpack_from('<II', d, 4)
    eoff = 72 + tn * 32
    out = []
    for k in range(en):
        o = eoff + k * 80
        if o + 80 > len(d):
            break
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'fileId': f[0], 'typeId': f[1], 'tocOff': f[2],
                    'gpuOff': f[4], 'tocSize': f[7], 'gpuSize': f[9]})
    return out


def main():
    targets = []
    for a in sys.argv[1:]:
        if a.startswith('--'):
            continue
        if os.path.isdir(a):
            targets += sorted(p for p in glob.glob(os.path.join(a, '*.patch_*'))
                              if not p.endswith(('.gpu_resources', '.stream', '.json')))
        else:
            targets.append(a)

    kinds = {}
    for p in targets:
        if p.endswith(('.gpu_resources', '.stream', '.json')):
            continue
        try:
            d = open(p, 'rb').read()
        except OSError:
            continue
        ents = parse_patch(d)
        mats = [e for e in ents if e['typeId'] == MAT]
        if not mats:
            continue
        gp = p + '.gpu_resources'
        gd = open(gp, 'rb').read() if os.path.exists(gp) else b''
        print('\n== %s  (toc %d B, gpu %d B, 条目 %d, 材质 %d)' % (
            os.path.basename(p), len(d), len(gd), len(ents), len(mats)))
        print('   %-18s %-18s %-10s %-10s %-11s %-4s %s' % (
            '材质 id', 'parent', 'undat2', 'gpuSize', '载荷头+4', '槽', 'kind'))
        for e in mats:
            t = d[e['tocOff']:e['tocOff'] + e['tocSize']]
            if len(t) < 136:
                continue
            undat2, = struct.unpack_from('<Q', t, 16)
            parent, = struct.unpack_from('<Q', t, 24)
            ntex, = struct.unpack_from('<I', t, 64)
            h4, kind = '-', ''
            if e['gpuSize'] and gd and e['gpuOff'] + e['gpuSize'] <= len(gd):
                h = struct.unpack_from('<5I', gd, e['gpuOff'])
                if h[0] == 0xB3:
                    h4 = '0x%08x' % h[1]
                    kind = 'ROOT+载荷' if h[1] == (e['fileId'] >> 32) else '自洽✗(头≠id)'
            if not kind:
                kind = '子材质' if parent else 'delta(无载荷)'
            kinds[kind] = kinds.get(kind, 0) + 1
            print('   0x%016x  0x%016x  %-10d %-10d %-11s %-4d %s' % (
                e['fileId'], parent, undat2, e['gpuSize'], h4, ntex, kind))

    if kinds:
        print('\n合计：' + '，'.join('%s %d' % kv for kv in sorted(kinds.items())))


if __name__ == '__main__':
    sys.exit(main())
