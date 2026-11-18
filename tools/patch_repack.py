#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""通用 patch 重打包：增删条目、改字段，并保证两条硬规则。

两条规则（2026-11-17 各踩过一次）：
  1. **类型表第 3 个 u64 = 该类型的条目数** —— 增删条目必须同步，否则引擎启动即崩。
  2. **侧车里每个非空 GPU 载荷按 64 字节对齐**。

手写这些逻辑很容易漏，所以集中在这一处，并在写盘后自检。

用法：
    # 新增一条条目（类型:新id:TocData文件:GPU文件）
    python tools/patch_repack.py <patch> --out <新patch> \
        --add-entry eac0b497876adedf:02e8377209ba1000:root_toc.bin:root_magenta.gpu

    # 改某个条目里的一个 u64 字段（类型:id:偏移:新值）
    python tools/patch_repack.py <patch> --out <新patch> \
        --set-u64 eac0b497876adedf:def7dc54e86d6002:24:02e8377209ba1000
"""
import argparse
import os
import struct
import sys

MAGIC = 0xF0000011


def read_patch(path):
    d = open(path, 'rb').read()
    g = open(path + '.gpu_resources', 'rb').read()
    if struct.unpack_from('<I', d, 0)[0] != MAGIC:
        raise SystemExit('不是 patch: %s' % path)
    tn, en = struct.unpack_from('<II', d, 4)
    ttypes = []
    for k in range(tn):
        o = 72 + k * 32
        _, tid, cnt, _ = struct.unpack_from('<4Q', d, o)
        ttypes.append((tid, cnt))
    eo = 72 + tn * 32
    entries = []
    for k in range(en):
        o = eo + k * 80
        raw = bytearray(d[o:o + 80])
        fid, = struct.unpack_from('<Q', raw, 0)
        typ, = struct.unpack_from('<Q', raw, 8)
        to, = struct.unpack_from('<Q', raw, 16)
        go, = struct.unpack_from('<Q', raw, 32)
        ts, = struct.unpack_from('<I', raw, 56)
        gs, = struct.unpack_from('<I', raw, 64)
        entries.append({'raw': raw, 'fileId': fid, 'typeId': typ,
                        'toc': bytearray(d[to:to + ts]), 'gpu': bytearray(g[go:go + gs])})
    return d[:72], ttypes, entries


def write_patch(path, head, entries):
    # 类型表按 entries 实际数量重算，顺序沿用原有类型出现顺序
    order, seen = [], set()
    for e in entries:
        if e['typeId'] not in seen:
            seen.add(e['typeId'])
            order.append(e['typeId'])
    counts = {}
    for e in entries:
        counts[e['typeId']] = counts.get(e['typeId'], 0) + 1

    tn = len(order)
    en = len(entries)
    header = bytearray(head)
    struct.pack_into('<II', header, 4, tn, en)

    ttable = bytearray()
    for tid in order:
        ttable += struct.pack('<4Q', 0, tid, counts[tid], 0x0000004000000010)

    toc_start = 72 + tn * 32 + en * 80
    out = bytearray(header) + ttable
    toc_blob = bytearray()
    gpu_blob = bytearray()
    for i, e in enumerate(entries):
        while len(gpu_blob) % 64:
            gpu_blob += b'\x00'
        raw = bytearray(e['raw'])
        struct.pack_into('<Q', raw, 0, e['fileId'])
        struct.pack_into('<Q', raw, 8, e['typeId'])
        struct.pack_into('<Q', raw, 16, toc_start + len(toc_blob))
        struct.pack_into('<Q', raw, 32, len(gpu_blob))
        struct.pack_into('<I', raw, 56, len(e['toc']))
        struct.pack_into('<I', raw, 64, len(e['gpu']))
        struct.pack_into('<I', raw, 76, i + 1)
        out += raw
        toc_blob += e['toc']
        gpu_blob += e['gpu']
    out += toc_blob
    open(path, 'wb').write(bytes(out))
    open(path + '.gpu_resources', 'wb').write(bytes(gpu_blob))
    return len(out), len(gpu_blob)


def verify(path):
    d = open(path, 'rb').read()
    g = open(path + '.gpu_resources', 'rb').read()
    tn, en = struct.unpack_from('<II', d, 4)
    eo = 72 + tn * 32
    declared = {}
    for k in range(tn):
        _, tid, cnt, _ = struct.unpack_from('<4Q', d, 72 + k * 32)
        declared[tid] = cnt
    actual, prev = {}, 0
    for k in range(en):
        o = eo + k * 80
        typ, = struct.unpack_from('<Q', d, o + 8)
        go, = struct.unpack_from('<Q', d, o + 32)
        gs, = struct.unpack_from('<I', d, o + 64)
        actual[typ] = actual.get(typ, 0) + 1
        if gs:
            if go % 64:
                raise SystemExit('GPU 未对齐: %d' % go)
            if go < prev:
                raise SystemExit('GPU 重叠: %d' % go)
            if go + gs > len(g):
                raise SystemExit('GPU 越界: %d+%d > %d' % (go, gs, len(g)))
            prev = go + gs
    if declared != actual:
        raise SystemExit('类型表计数不一致: 声明 %s / 实际 %s'
                         % ({hex(k): v for k, v in declared.items()}, {hex(k): v for k, v in actual.items()}))
    print('自检通过：条目 %d，类型 %d，侧车 %d B' % (en, tn, len(g)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('--out', required=True)
    ap.add_argument('--add-entry', action='append', default=[], nargs=4,
                    metavar=('TYPE', 'ID', 'TOC', 'GPU'),
                    help='类型id 新id toc文件 gpu文件')
    ap.add_argument('--set-gpu', action='append', default=[], nargs=3,
                    metavar=('TYPE', 'ID', 'GPU'),
                    help='替换已存在条目的 GPU 载荷（长度必须与 TocData 里的 undat2 一致）')
    ap.add_argument('--set-u32-new', action='append', default=[], nargs=4,
                    metavar=('TYPE', 'ID', 'OFF', 'VAL'),
                    help='给新条目设 u32 字段（新条目默认全零，需从原条目继承的字段用它补）')
    ap.add_argument('--set-u64', action='append', default=[], nargs=4,
                    metavar=('TYPE', 'ID', 'OFF', 'VAL'),
                    help='类型id 条目id 偏移 新值')
    args = ap.parse_args()

    head, ttypes, entries = read_patch(args.patch)

    for spec in args.set_u64:
        typ_s, fid_s, off_s, val_s = spec
        typ, fid, off, val = int(typ_s, 16), int(fid_s, 16), int(off_s), int(val_s, 16)
        hit = [e for e in entries if e['typeId'] == typ and e['fileId'] == fid]
        if not hit:
            raise SystemExit('找不到条目 %s:%s' % (typ_s, fid_s))
        struct.pack_into('<Q', hit[0]['toc'], off, val)
        print('改字段 %s@%s +%d = 0x%016x' % (typ_s, fid_s, off, val))

    for spec in args.add_entry:
        typ_s, fid_s, toc_path, gpu_path = spec
        typ, fid = int(typ_s, 16), int(fid_s, 16)
        if any(e['typeId'] == typ and e['fileId'] == fid for e in entries):
            raise SystemExit('条目已存在 %s:%s' % (typ_s, fid_s))
        toc = bytearray(open(toc_path, 'rb').read())
        gpu = bytearray(open(gpu_path, 'rb').read()) if gpu_path and os.path.exists(gpu_path) else bytearray()
        raw = bytearray(80)
        entries.append({'raw': raw, 'fileId': fid, 'typeId': typ, 'toc': toc, 'gpu': gpu})
        print('新增条目 %s:%s  TocData=%d B  GPU=%d B' % (typ_s, fid_s, len(toc), len(gpu)))

    for spec in args.set_u32_new:
        typ_s, fid_s, off_s, val_s = spec
        typ, fid, off, val = int(typ_s, 16), int(fid_s, 16), int(off_s), int(val_s, 0)
        hit = [e for e in entries if e['typeId'] == typ and e['fileId'] == fid]
        if not hit:
            raise SystemExit('找不到新条目 %s:%s' % (typ_s, fid_s))
        struct.pack_into('<I', hit[0]['raw'], off, val)
        print('新条目 %s@%s +%d = %d' % (typ_s, fid_s, off, val))

    for spec in args.set_gpu:
        typ_s, fid_s, gpu_path = spec
        typ, fid = int(typ_s, 16), int(fid_s, 16)
        hit = [e for e in entries if e['typeId'] == typ and e['fileId'] == fid]
        if not hit:
            raise SystemExit('找不到条目 %s:%s' % (typ_s, fid_s))
        gpu = bytearray(open(gpu_path, 'rb').read())
        hit[0]['gpu'] = gpu
        # 同步 TocData 里的 undat2（+16 的 u32）
        if len(hit[0]['toc']) >= 20:
            struct.pack_into('<I', hit[0]['toc'], 16, len(gpu))
        print('替换载荷 %s:%s -> %d B' % (typ_s, fid_s, len(gpu)))

    n1, n2 = write_patch(args.out, head, entries)
    print('写出 %s（patch %d B，侧车 %d B）' % (args.out, n1, n2))
    verify(args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
