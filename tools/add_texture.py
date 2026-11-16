#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""往一个 patch 里**追加贴图条目**（贴图数据接在侧车末尾）。

为什么需要：材质槽要指向「有真实 alpha/亮度结构」的贴图才能做出镂空与自发光，
而现成 patch 里的贴图往往是占位图（见 docs/.../七之八）。所以必须能把外部贴图搬进来。

输入条目目录格式（和 `%TEMP%\\milfy-inspect\\entries\\*` 一致）：
    toc.bin            贴图 TocData（实测 340 B）
    gpu_resources.bin  贴图数据

用法：
    python tools/add_texture.py --base <patch_0> \
        --add-texture 0x01ba0fdde7c605a2=<条目目录> [--add-texture ...] \
        --out <输出目录> [--name 9ba626afa44a3aa3.patch_0]
"""
import argparse
import os
import struct
import sys

MAGIC = 0xF0000011
TEX_TYPE = 0xCD4238C6A0C69E32


def parse_patch(d):
    magic, = struct.unpack_from('<I', d, 0)
    if magic != MAGIC:
        raise SystemExit('不是 patch TOC')
    tn, en = struct.unpack_from('<II', d, 4)
    types = [d[72 + k * 32:72 + (k + 1) * 32] for k in range(tn)]
    eoff = 72 + tn * 32
    entries = []
    for k in range(en):
        o = eoff + k * 80
        raw = bytearray(d[o:o + 80])
        f = struct.unpack_from('<QQQQQQQIIIIII', raw, 0)
        entries.append({'raw': raw, 'fileId': f[0], 'typeId': f[1], 'tocOff': f[2],
                        'gpuOff': f[4], 'tocSize': f[7], 'gpuSize': f[9], 'index': f[12]})
    return d[:72], types, entries


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', required=True)
    ap.add_argument('--add-texture', nargs='+', default=[], required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='9ba626afa44a3aa3.patch_0')
    args = ap.parse_args()

    base = open(args.base, 'rb').read()
    header, types, entries = parse_patch(base)
    if not any(struct.unpack_from('<Q', t, 8)[0] == TEX_TYPE for t in types):
        raise SystemExit('base 的类型表里没有贴图类型 0x%016x' % TEX_TYPE)
    sidecar = bytearray(open(args.base + '.gpu_resources', 'rb').read())
    template = next(e for e in entries if e['typeId'] == TEX_TYPE)

    extras = []
    for spec in args.add_texture:
        fid_s, d = spec.split('=')
        fid = int(fid_s, 16)
        if any(e['fileId'] == fid for e in entries + extras):
            raise SystemExit('0x%016x 已存在' % fid)
        toc = open(os.path.join(d, 'toc.bin'), 'rb').read()
        gpu = open(os.path.join(d, 'gpu_resources.bin'), 'rb').read()
        while len(sidecar) % 4:
            sidecar.append(0)
        gpu_off = len(sidecar)
        sidecar += gpu
        raw = bytearray(template['raw'])
        struct.pack_into('<Q', raw, 0, fid)
        struct.pack_into('<Q', raw, 8, TEX_TYPE)
        struct.pack_into('<Q', raw, 32, gpu_off)
        struct.pack_into('<I', raw, 56, len(toc))
        struct.pack_into('<I', raw, 60, 0)
        struct.pack_into('<I', raw, 64, len(gpu))
        extras.append({'raw': raw, 'fileId': fid, 'typeId': TEX_TYPE, 'tocOff': 0,
                       'gpuOff': gpu_off, 'tocSize': len(toc), 'gpuSize': len(gpu),
                       'toc': toc, 'gpu': gpu})
        print('  追加贴图 0x%016x  TocData %d B  数据 %d B @0x%x' % (fid, len(toc), len(gpu), gpu_off))

    all_entries = entries + extras
    for i, e in enumerate(all_entries):
        struct.pack_into('<I', e['raw'], 76, i + 1)     # entryIndex 1-based

    head_len = 72 + len(types) * 32
    cur = head_len + len(all_entries) * 80
    blobs = []
    for e in all_entries:
        blob = e['toc'] if 'toc' in e else base[e['tocOff']:e['tocOff'] + e['tocSize']]
        blobs.append((e, blob, cur))
        cur += len(blob)

    out = bytearray(header)
    struct.pack_into('<I', out, 8, len(all_entries))
    for t in types:
        out += t
    for (e, blob, off) in blobs:
        raw = bytearray(e['raw'])
        struct.pack_into('<Q', raw, 16, off)
        struct.pack_into('<I', raw, 56, len(blob))
        out += raw
    for (e, blob, off) in blobs:
        out += blob

    os.makedirs(args.out, exist_ok=True)
    toc_out = os.path.join(args.out, args.name)
    open(toc_out, 'wb').write(bytes(out))
    open(toc_out + '.gpu_resources', 'wb').write(bytes(sidecar))
    print('\n写出 %s (%d B) + 侧车 (%d B)' % (toc_out, len(out), len(sidecar)))

    h2, t2, e2 = parse_patch(bytes(out))
    for e in e2:
        print('  0x%016x tocOff=%-5d tocSize=%-5d gpuOff=%-9d gpuSize=%-9d' % (
            e['fileId'], e['tocOff'], e['tocSize'], e['gpuOff'], e['gpuSize']))
    ok = True
    for e in extras:
        got = [x for x in e2 if x['fileId'] == e['fileId']][0]
        if bytes(out)[got['tocOff']:got['tocOff'] + got['tocSize']] != e['toc']:
            ok = False
            print('  FAIL 0x%016x TocData 不一致' % e['fileId'])
        if bytes(sidecar[got['gpuOff']:got['gpuOff'] + got['gpuSize']]) != e['gpu']:
            ok = False
            print('  FAIL 0x%016x 数据不一致' % e['fileId'])
    print('  自检：%s' % ('OK' if ok else '有失败项'))


if __name__ == '__main__':
    sys.exit(main())
