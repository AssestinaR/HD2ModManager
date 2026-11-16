#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把一个 DDS 的像素数据**原地**写进 patch 里某个贴图条目（不动任何长度字段）。

为什么这样做：贴图条目的 `gpuSize` 决定了它在侧车里占多少字节，只要新数据的像素
长度与它相等，就可以直接覆盖那段区间 —— `tocSize`、`gpuSize`、侧车总长全都不用改，
不会引入任何新的耦合字段。

用法：
    python tools/embed_texture_data.py --patch <patch_0> --texture 0x<贴图fileId> \
        --dds <文件.dds> [--dry-run]

退出码：0 = 已写入/可写入；1 = 尺寸不符或找不到条目
"""
import argparse
import hashlib
import os
import struct
import sys

MAGIC = 0xF0000011


def parse_patch(d):
    magic, = struct.unpack_from('<I', d, 0)
    if magic != MAGIC:
        raise SystemExit('不是 patch TOC')
    tn, en = struct.unpack_from('<II', d, 4)
    eoff = 72 + tn * 32
    out = []
    for k in range(en):
        o = eoff + k * 80
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'fileId': f[0], 'typeId': f[1], 'tocOff': f[2], 'gpuOff': f[4],
                    'tocSize': f[7], 'gpuSize': f[9]})
    return out


def dds_payload(path):
    d = open(path, 'rb').read()
    if d[:4] != b'DDS ':
        raise SystemExit('不是 DDS 文件: %s' % path)
    # BC7 等需要 DX10 扩展头（额外 20 字节）
    hdr = 148 if d[84:88] == b'DX10' else 128
    return d[hdr:], hdr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--patch', required=True)
    ap.add_argument('--texture', required=True)
    ap.add_argument('--dds', required=True)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    fid = int(args.texture, 16)
    toc = open(args.patch, 'rb').read()
    entries = parse_patch(toc)
    hit = [e for e in entries if e['fileId'] == fid]
    if not hit:
        print('FAIL patch 里没有贴图 0x%016x' % fid)
        return 1
    e = hit[0]
    data, hdr = dds_payload(args.dds)
    print('贴图 0x%016x  gpuOff=%d  gpuSize=%d' % (fid, e['gpuOff'], e['gpuSize']))
    print('DDS 头 %d B，像素数据 %d B%s' % (hdr, len(data), '（数据长度与条目一致 ✓）' if len(data) == e['gpuSize'] else ' ✗'))

    side_path = args.patch + '.gpu_resources'
    if not os.path.isfile(side_path):
        print('FAIL 找不到侧车 %s' % side_path)
        return 1
    if len(data) != e['gpuSize']:
        print('FAIL 尺寸不符：条目 %d B，新数据 %d B —— 不能原地替换（需要换一个同尺寸条目，'
              '或改条目长度并同步所有耦合字段）' % (e['gpuSize'], len(data)))
        return 1

    side = open(side_path, 'rb').read()
    end = e['gpuOff'] + e['gpuSize']
    if end > len(side):
        print('FAIL 区间超出侧车（%d > %d）' % (end, len(side)))
        return 1
    before = hashlib.sha256(side[e['gpuOff']:end]).hexdigest()[:16]
    print('原区间 sha16 = %s' % before)
    if args.dry_run:
        print('DRY-RUN：可原地替换')
        return 0

    out = bytearray(side)
    out[e['gpuOff']:end] = data
    open(side_path, 'wb').write(bytes(out))
    after = hashlib.sha256(bytes(out[e['gpuOff']:end])).hexdigest()[:16]
    print('写入完成  新区间 sha16 = %s' % after)
    print('侧车长度未变: %s' % (len(out) == len(side)))
    if after != hashlib.sha256(data).hexdigest()[:16]:
        print('FAIL 写入后校验不一致')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
