#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 patch 里某个贴图条目的 GPU 数据包成 DDS 再解成 PNG（只为看一眼内容）。

HD2 的贴图条目在 .gpu_resources 里是裸像素数据，没有 DDS 头。这里按常见规格
（2048² BC7 / BC1，或 1024² RGBA8）套一个头，交给 texconv 解码。

用法：
    python tools/dump_texture.py --patch <patch_1> --texture 0x<id> --out <png> [--fmt bc7|bc1|rgba8] [--size 2048]
"""
import argparse
import os
import struct
import subprocess
import sys

TEXCONV = r'E:\Data\source\repos\WpfApp1\tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe'
MAGIC = 0xF0000011
DXGI = {'bc7': 98, 'bc1': 71, 'rgba8': 28}


def entries(d):
    tn, en = struct.unpack_from('<II', d, 4)
    eoff = 72 + tn * 32
    out = []
    for k in range(en):
        o = eoff + k * 80
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'id': f[0], 'type': f[1], 'tocOff': f[2], 'gpuOff': f[4],
                    'tocSize': f[7], 'gpuSize': f[9]})
    return out


def dds_header(w, h, fmt):
    dxgi = DXGI[fmt]
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000   # caps|height|width|pitch|linearsize
    hdr = bytearray(148)
    hdr[0:4] = b'DDS '
    struct.pack_into('<I', hdr, 4, 124)                 # dwSize
    struct.pack_into('<I', hdr, 8, flags)
    struct.pack_into('<I', hdr, 12, h)
    struct.pack_into('<I', hdr, 16, w)
    struct.pack_into('<I', hdr, 20, w * h if fmt in ('bc7', 'bc1') else w * 4)  # pitchOrLinearSize
    struct.pack_into('<I', hdr, 28, 1)                  # mipMapCount
    struct.pack_into('<I', hdr, 76, 32)                 # ddspf.dwSize
    struct.pack_into('<I', hdr, 80, 0x4)                # DDPF_FOURCC
    hdr[84:88] = b'DX10'
    struct.pack_into('<I', hdr, 108, 0x1000)            # caps: TEXTURE
    struct.pack_into('<I', hdr, 128, dxgi)              # dxgiFormat
    struct.pack_into('<I', hdr, 132, 3)                 # resourceDimension: TEXTURE2D
    struct.pack_into('<I', hdr, 136, 1)                 # miscFlag
    struct.pack_into('<I', hdr, 140, 1)                 # arraySize
    struct.pack_into('<I', hdr, 144, 0)                 # miscFlags2
    return bytes(hdr)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--patch', required=True)
    ap.add_argument('--texture', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--fmt', default='bc7', choices=list(DXGI))
    ap.add_argument('--size', type=int, default=2048)
    args = ap.parse_args()

    fid = int(args.texture, 16)
    toc = open(args.patch, 'rb').read()
    e = next((x for x in entries(toc) if x['id'] == fid), None)
    if not e:
        raise SystemExit('patch 里没有 0x%016x' % fid)
    side = open(args.patch + '.gpu_resources', 'rb').read()
    data = side[e['gpuOff']:e['gpuOff'] + e['gpuSize']]

    w = h = args.size
    if args.fmt in ('bc7', 'bc1'):
        need = w * h
    else:
        need = w * h * 4
    if len(data) != need:
        print('注意：数据 %d B，期望 %d B（%d² %s）；仍按 %d² 套头' % (
            len(data), need, w, args.fmt, w))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or '.', exist_ok=True)
    dds = args.out + '.dds'
    with open(dds, 'wb') as f:
        f.write(dds_header(w, h, args.fmt))
        f.write(data)
    r = subprocess.run([TEXCONV, '-nologo', '-ft', 'png', '-m', '1', '-y',
                        '-o', os.path.dirname(os.path.abspath(args.out)), dds],
                       capture_output=True, text=True)
    print('texconv:', (r.stdout or '').strip().splitlines()[-1:] , (r.stderr or '').strip()[:200])
    produced = os.path.join(os.path.dirname(os.path.abspath(args.out)),
                            os.path.splitext(os.path.basename(dds))[0] + '.png')
    if os.path.isfile(produced):
        if produced != os.path.abspath(args.out):
            os.replace(produced, args.out)
        print('PNG ->', args.out)
        return 0
    print('解码失败')
    return 1


if __name__ == '__main__':
    sys.exit(main())
