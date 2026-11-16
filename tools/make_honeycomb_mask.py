#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成「蜂窝打孔」掩码贴图，并原地替换 patch 里某个同尺寸贴图条目的像素数据。

用途：给 `OpacityClipMap`（镂空掩码）提供真实图案。白=保留、黑=打孔。

前提（由调用方保证）：目标贴图条目的 gpuSize 必须等于新 DDS 的像素数据长度
（BC7 1024² 无 mip = 1,048,576 B），否则不能原地替换。

用法：
    python tools/make_honeycomb_mask.py --out <png/dds 输出目录> [--size 1024]
        [--bg 255] [--hole 0] [--radius 34] [--pitch 104]
"""
import argparse
import math
import os
import subprocess
import sys

TEXCONV = r'E:\Data\source\repos\WpfApp1\tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe'


def make_png(path, size, bg, hole, radius, pitch):
    from PIL import Image, ImageDraw
    # 用 RGBA 且四通道同值：无论引擎从 RGB 还是 A 取掩码，图案都一样
    im = Image.new('RGBA', (size, size), (bg, bg, bg, bg))
    dr = ImageDraw.Draw(im)
    row = 0
    y = 0.0
    while y < size + pitch:
        x = (pitch * 0.5) if (row % 2) else 0.0
        while x < size + pitch:
            dr.ellipse([x - radius, y - radius, x + radius, y + radius], fill=(hole, hole, hole, hole))
            x += pitch
        y += pitch * math.sqrt(3) / 2
        row += 1
    im.save(path)
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--size', type=int, default=1024)
    ap.add_argument('--bg', type=int, default=255)
    ap.add_argument('--hole', type=int, default=0)
    ap.add_argument('--radius', type=int, default=34)
    ap.add_argument('--pitch', type=int, default=104)
    ap.add_argument('--name', default='honeycomb')
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    png = os.path.join(args.out, args.name + '.png')
    make_png(png, args.size, args.bg, args.hole, args.radius, args.pitch)
    print('PNG -> %s (%dx%d, 白底 %d / 孔 %d, r=%d, pitch=%d)' % (
        png, args.size, args.size, args.bg, args.hole, args.radius, args.pitch))

    r = subprocess.run([TEXCONV, '-nologo', '-f', 'BC7_UNORM', '-m', '1', '-y',
                        '-o', args.out, png], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-2000:])
        print(r.stderr[-2000:])
        raise SystemExit('texconv 失败')
    dds = os.path.join(args.out, args.name + '.dds')
    n = os.path.getsize(dds)
    with open(dds, 'rb') as fh:
        head = fh.read(148)
    # BC7 需要 DX10 扩展头（额外 20 字节）
    hdr = 148 if head[84:88] == b'DX10' else 128
    payload = n - hdr
    expect = args.size * args.size          # BC7 = 1 B/px
    print('DDS -> %s  %d B（头 %d + 数据 %d；期望数据 %d）%s' % (
        dds, n, hdr, payload, expect, ' ✓' if payload == expect else ' ✗'))
    if payload != expect:
        raise SystemExit('数据长度不符，不能原地替换')


if __name__ == '__main__':
    sys.exit(main())
