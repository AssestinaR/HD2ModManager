#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""列出一个 patch 里的所有条目（默认只列材质与贴图），并给出可直接用的 id。

用途：换一个模型时，第一步就是搞清楚「这个 patch 里有哪些材质/贴图、各自 id 是什么」。

用法：
    python tools/list_patch_entries.py <patch_0 路径> [--all]
    python tools/list_patch_entries.py <patch_0> --detail 0x<materialId>
"""
import argparse
import struct
import sys

MAGIC = 0xF0000011
TYPES = {
    0xEAC0B497876ADEDF: 'Material',
    0xCD4238C6A0C69E32: 'Texture',
}
# 已知的槽位键（来自 SDK hashlists/shadervariables.txt）
SLOT_NAMES = {
    0x1d57dcf3: 'NormalXyAoRoughMap',
    0xcbde381b: 'OpacityClipMap',
    0x848ba63b: 'BaseColorMetalMap',
    0x13ce14bb: 'ParallaxMap',
    0x4c567810: 'Nar',
    0x90b84a53: 'IlluminateData',
    0xe67ac0c7: 'AlbedoEmissive',
    0x479fb1ef: 'SclarFieldOpacity',
    0x479fb1ef | 0: 'SclarFieldOpacity',
}
VAR_NAMES = {
    0x529a4aaf: 'OpacityThreshold',
    0x02f6dc5b: 'EmissiveIntensity',
    0xc985395a: 'EmissiveColor',
    0x7415ab88: 'EmissiveAnimation',
    0x14f7ceac: 'EmissiveWaveSpeed',
    0xd0a0e0fc: 'EmissiveWaveSize',
    0x3de979ac: 'EmissiveWaveGradient',
    0x3412359b: 'EmissiveUVDirection',
    0x519c1733: 'EmissiveMultiplier',
    0x7da4d360: 'Metallic',
    0xcd72af93: 'MetallicOpacity',
    0xe2721bdb: 'BaseColorOpacity',
    0x57a7bab2: 'RoughnessOpacity',
    0x53a2e583: 'NormalCcnormOpacity',
}


def parse(d):
    magic, = struct.unpack_from('<I', d, 0)
    if magic != MAGIC:
        raise SystemExit('不是 patch TOC（magic=0x%08x）' % magic)
    tn, en = struct.unpack_from('<II', d, 4)
    eoff = 72 + tn * 32
    out = []
    for k in range(en):
        o = eoff + k * 80
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'fileId': f[0], 'typeId': f[1], 'tocOff': f[2], 'gpuOff': f[4],
                    'tocSize': f[7], 'gpuSize': f[9]})
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('--all', action='store_true')
    ap.add_argument('--detail', default=None, help='打印某材质的槽位与变量')
    args = ap.parse_args()

    d = open(args.patch, 'rb').read()
    entries = parse(d)
    print('%s  条目 %d' % (args.patch, len(entries)))

    if args.detail:
        want = int(args.detail, 16)
        e = next((x for x in entries if x['fileId'] == want), None)
        if not e:
            raise SystemExit('没有 0x%016x' % want)
        t = d[e['tocOff']:e['tocOff'] + e['tocSize']]
        ntex, = struct.unpack_from('<I', t, 64)
        nvar, = struct.unpack_from('<I', t, 104)
        parent, = struct.unpack_from('<Q', t, 24)
        undat2, = struct.unpack_from('<Q', t, 16)
        print('材质 0x%016x  TocData=%d B  gpuSize=%d  undat2=%d  parent=0x%016x  槽=%d  变量=%d' % (
            want, len(t), e['gpuSize'], undat2, parent, ntex, nvar))
        for i in range(ntex):
            key, = struct.unpack_from('<I', t, 136 + 4 * i)
            tid, = struct.unpack_from('<Q', t, 136 + 4 * ntex + 8 * i)
            print('   槽 %d  0x%08x %-24s tex=0x%016x' % (i, key, SLOT_NAMES.get(key, '(未知)'), tid))
        rbase = 136 + 12 * ntex
        vbase = rbase + 20 * nvar
        for i in range(nvar):
            klass, elem, vid, off, stride = struct.unpack_from('<5I', t, rbase + 20 * i)
            vo = vbase + off
            val = struct.unpack_from('<f', t, vo)[0] if vo + 4 <= len(t) else None
            print('   变量 0x%08x %-24s off=0x%04x 值=%s' % (
                vid, VAR_NAMES.get(vid, ''), off, ('%.5g' % val) if val is not None else '-'))
        return

    for e in entries:
        name = TYPES.get(e['typeId'], '0x%016x' % e['typeId'])
        if not args.all and name not in ('Material', 'Texture'):
            continue
        print('  %-9s 0x%016x  tocOff=%-6d tocSize=%-5d gpuOff=%-9d gpuSize=%-9d' % (
            name, e['fileId'], e['tocOff'], e['tocSize'], e['gpuOff'], e['gpuSize']))


if __name__ == '__main__':
    sys.exit(main())
