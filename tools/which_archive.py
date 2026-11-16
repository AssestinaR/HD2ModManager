#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查一件事：mod 的 patch（都挂在 9ba626afa44a3aa3 下）到底能不能覆盖**本体归档**里的资源？

做法：把 data\\ 里所有「基础归档文件」（名字就是 16 位十六进制，无 .patch_N 后缀）的
fileId 集合扫出来，再看某个 patch 定义的 fileId 有多少落在这些集合里。

若某个**实机确认生效**的 mod（例如 index 0 的隐藏披风）大量命中本体 fileId，
则 9ba 覆盖本体是成立的；反之则说明 mod 只能新增 id。

用法：
    python tools/which_archive.py                     # 列出基础归档与条目数
    python tools/which_archive.py --patch 0           # 检查 index 0 的命中情况
"""
import argparse
import os
import re
import struct
import sys

DEFAULT_DATA = r'D:\SteamLibrary\steamapps\common\Helldivers 2\data'
BASE_RE = re.compile(r'^[0-9a-fA-F]{16}$')
PATCH_RE = re.compile(r'^(?P<archive>[0-9a-fA-F]{16})\.patch_(?P<idx>\d+)$')
MAGIC = 0xF0000011


def toc_entries(path, limit_bytes=64 << 20):
    """只读 TOC 的头部与条目表，不读 TocData 区。"""
    size = os.path.getsize(path)
    with open(path, 'rb') as f:
        head = f.read(72)
        if len(head) < 72 or struct.unpack_from('<I', head, 0)[0] != MAGIC:
            return None
        type_n, entry_n = struct.unpack_from('<II', head, 4)
        need = 72 + type_n * 32 + entry_n * 80
        if need > size or need > limit_bytes:
            # 太大就只读条目区
            f.seek(0)
            f.read(72 + type_n * 32)
            raw = f.read(entry_n * 80)
            base = 0
        else:
            f.seek(0)
            raw = f.read(need)
            base = 72 + type_n * 32
    out = []
    for k in range(entry_n):
        o = base + k * 80
        if o + 80 > len(raw):
            break
        f7 = struct.unpack_from('<QQQQQQQ', raw, o)
        out.append(f7[0])
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', default=DEFAULT_DATA)
    ap.add_argument('--patch', default=None, help='要检查的 patch 索引（如 0）')
    args = ap.parse_args()

    base_files = sorted(n for n in os.listdir(args.data) if BASE_RE.match(n))
    print('基础归档文件 %d 个:' % len(base_files))
    owners = {}
    for n in base_files:
        p = os.path.join(args.data, n)
        ids = toc_entries(p)
        if ids is None:
            print('  %s  %12d B  (不是 %08x TOC，跳过)' % (n, os.path.getsize(p), MAGIC))
            continue
        print('  %s  %12d B  entries=%d' % (n, os.path.getsize(p), len(ids)))
        for fid in ids:
            owners.setdefault(fid, []).append(n)
    print('\n本体 fileId 总数（去重）: %d' % len(owners))

    if args.patch is None:
        return

    pat = os.path.join(args.data, '9ba626afa44a3aa3.patch_%s' % args.patch)
    if not os.path.exists(pat):
        pat = None
        for n in os.listdir(args.data):
            m = PATCH_RE.match(n)
            if m and m.group('idx') == str(args.patch):
                pat = os.path.join(args.data, n)
    if not pat:
        print('找不到 patch_%s' % args.patch)
        return
    ids = toc_entries(pat) or []
    hit = [i for i in ids if i in owners]
    print('\npatch_%s（%s）定义 %d 个 fileId' % (args.patch, os.path.basename(pat), len(ids)))
    print('  命中本体归档的: %d 个' % len(hit))
    for i in hit[:20]:
        print('    0x%016x ← 本体里也有，属于 %s' % (i, ','.join(owners[i])))
    if not hit:
        print('  （全部是新 id —— 这个 mod 只新增资源，不覆盖本体）')


if __name__ == '__main__':
    sys.exit(main())
