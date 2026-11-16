#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""谁定义了这个 fileId？—— 扫 data\\ 下所有 9ba patch 的 TOC，按索引列出定义者。

用途：覆盖本体根材质不生效时，第一件事是确认没有更高索引的 patch 也定义同一个 id
（那样我们的条目会被盖掉），以及确认优先方向。

用法：
    python tools/who_defines.py 0xef9b3fc2fc1ba61e [--archive 9ba626afa44a3aa3]
    python tools/who_defines.py            # 不带 id：列出每个索引的条目数与前几个 fileId
"""
import argparse
import os
import re
import struct
import sys

DEFAULT_DATA = r'D:\SteamLibrary\steamapps\common\Helldivers 2\data'
PATCH_RE = re.compile(r'^(?P<archive>[0-9a-fA-F]{16})\.patch_(?P<idx>\d+)$')
TYPE_NAMES = {
    0xEAC0B497876ADEDF: 'Material',
}


def entries(d):
    if len(d) < 72:
        return []
    magic = struct.unpack_from('<I', d, 0)[0]
    if magic != 0xF0000011:
        return []
    type_n, entry_n = struct.unpack_from('<II', d, 4)
    off = 72 + type_n * 32
    out = []
    for k in range(entry_n):
        o = off + k * 80
        if o + 80 > len(d):
            break
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'fileId': f[0], 'typeId': f[1], 'tocSize': f[7], 'gpuSize': f[9]})
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('ids', nargs='*', help='要查的 fileId（十六进制，可多个）')
    ap.add_argument('--data', default=DEFAULT_DATA)
    ap.add_argument('--archive', default='9ba626afa44a3aa3')
    args = ap.parse_args()

    want = {int(x, 16) for x in args.ids}
    rows = []
    for name in os.listdir(args.data):
        m = PATCH_RE.match(name)
        if not m or m.group('archive').lower() != args.archive.lower():
            continue
        full = os.path.join(args.data, name)
        try:
            d = open(full, 'rb').read()
        except OSError as e:
            print('  (读不到 %s: %s)' % (name, e))
            continue
        es = entries(d)
        for e in es:
            if not want or e['fileId'] in want:
                rows.append((int(m.group('idx')), name, e))
        if not want:
            kinds = {}
            for e in es:
                kinds[e['typeId']] = kinds.get(e['typeId'], 0) + 1
            print('%3d  %-40s entries=%-4d gpu=%d' % (
                int(m.group('idx')), name, len(es),
                os.path.getsize(full + '.gpu_resources') if os.path.exists(full + '.gpu_resources') else 0))

    if want:
        print()
        rows.sort(key=lambda r: r[0])
        for idx, name, e in rows:
            print('index %-3d  fileId=0x%016x  %-9s toc=%-6d gpu=%-9d  (%s)' % (
                idx, e['fileId'], TYPE_NAMES.get(e['typeId'], '0x%x' % e['typeId']),
                e['tocSize'], e['gpuSize'], name))
        if not rows:
            print('没有任何 patch 定义这些 id')
        else:
            for x in sorted(want):
                hit = [r[0] for r in rows if r[2]['fileId'] == x]
                if hit:
                    print('\n0x%016x 由这些索引定义: %s  → 生效的应是 %s（按高索引优先）' % (
                        x, sorted(hit), max(hit)))


if __name__ == '__main__':
    sys.exit(main())
