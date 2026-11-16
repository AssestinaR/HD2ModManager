#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一键切换材质变体：把 `.dsh/variants/<名字>/` 里的文件覆盖到 mod 目录，然后跑部署校验。

为什么要有它：每个变体 = **基线 + 只挂一个能力**。这样一次上机只验证一个变量，
不会出现"两个改动同时在跑、结果无法归因"的情况（这个坑踩过好几轮）。

用法：
    python tools/use_variant.py --list
    python tools/use_variant.py <变体名> [--mod "<mod 目录>"] [--dry-run]
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VARIANTS = os.path.join(REPO, '.dsh', 'variants')
DEFAULT_MOD = r'E:\mod\【常服】Milfy 替换 CE-07-材质'


def sha16(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()[:16]


def list_variants():
    if not os.path.isdir(VARIANTS):
        raise SystemExit('没有变体库: %s' % VARIANTS)
    print('变体库 %s' % VARIANTS)
    for name in sorted(os.listdir(VARIANTS)):
        d = os.path.join(VARIANTS, name)
        if not os.path.isdir(d):
            continue
        files = sorted(f for f in os.listdir(d) if not f.startswith('.'))
        total = sum(os.path.getsize(os.path.join(d, f)) for f in files)
        print('\n  %-14s %d 个文件  %.1f MB' % (name, len(files), total / (1 << 20)))
        for f in files:
            print('      %-44s %10d B  %s' % (f, os.path.getsize(os.path.join(d, f)),
                                              sha16(os.path.join(d, f))))
        note = os.path.join(d, 'NOTE.txt')
        if os.path.isfile(note):
            for line in open(note, encoding='utf-8'):
                print('      | %s' % line.rstrip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('variant', nargs='?')
    ap.add_argument('--list', action='store_true')
    ap.add_argument('--mod', default=DEFAULT_MOD)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    if args.list or not args.variant:
        list_variants()
        return 0

    src = os.path.join(VARIANTS, args.variant)
    if not os.path.isdir(src):
        print('FAIL 没有变体 %s（先跑 --list 看有哪些）' % args.variant)
        return 1
    if not os.path.isdir(args.mod):
        print('FAIL mod 目录不存在: %s' % args.mod)
        return 1

    print('切换变体: %s  ->  %s' % (args.variant, args.mod))
    for f in sorted(os.listdir(src)):
        if f.startswith('.') or f == 'NOTE.txt':
            continue
        s, d = os.path.join(src, f), os.path.join(args.mod, f)
        print('  %-44s %10d B  %s%s' % (f, os.path.getsize(s), sha16(s),
                                        '  (dry-run)' if args.dry_run else ''))
        if not args.dry_run:
            shutil.copy2(s, d)
    if args.dry_run:
        print('\nDRY-RUN：未写入')
        return 0

    print('\n跑部署校验……')
    r = subprocess.run([sys.executable or 'python', os.path.join(REPO, 'tools', 'verify_deployed.py'),
                        '--mod', os.path.basename(args.mod.rstrip('\\/'))],
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    out = (r.stdout or '') + (r.stderr or '')
    print(out.strip()[-1500:])
    if r.returncode != 0:
        print('部署校验未通过，别进游戏')
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
