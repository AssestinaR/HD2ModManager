#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""等长 dword 模式替换（用于在 SHEX 里改指令），并**用 fxc 复验**。

设计原则（本轮踩过的坑换来的）：
  · **不靠计算偏移**，靠 dword 模式搜索 —— 自己的 token 遍历已经两次错位；
  · 必须**等长**（dword 个数相同），否则会牵动块尺寸（历史上尺寸一变就不生效）；
  · 改完必须把记录再交给 fxc 反汇编，能正常 dump 且出现预期指令才算成功。

用法：
    python tools/shex_patch.py --record <in.dxbc> \
        --find "00102082 00000000 00004001 3e800000" \
        --replace "00102082 00000000 00004001 3f400000" \
        --out <out.dxbc> [--fxc <fxc.exe>] [--expect "0.750000"]
"""
import argparse
import os
import struct
import subprocess
import sys

DEFAULT_FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'


def parse_dwords(text):
    ws = [w for w in text.replace(',', ' ').split() if w]
    return b''.join(struct.pack('<I', int(w, 16)) for w in ws)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--record', required=True)
    ap.add_argument('--find', required=True)
    ap.add_argument('--replace', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--fxc', default=DEFAULT_FXC)
    ap.add_argument('--expect', default=None, help='反汇编里应出现的文本片段')
    args = ap.parse_args()

    rec = bytearray(open(args.record, 'rb').read())
    find = parse_dwords(args.find)
    repl = parse_dwords(args.replace)
    if len(find) != len(repl):
        raise SystemExit('find/replace 长度必须相同（%d vs %d dwords）' % (len(find) // 4, len(repl) // 4))

    hits = []
    i = 0
    while True:
        j = bytes(rec).find(find, i)
        if j < 0:
            break
        hits.append(j)
        i = j + 1
    print('模式命中 %d 处: %s' % (len(hits), ', '.join('0x%x' % h for h in hits[:8])))
    if len(hits) != 1:
        raise SystemExit('要求唯一命中，实际 %d 处' % len(hits))

    off = hits[0]
    print('原字节: %s' % bytes(rec[off:off + len(find)]).hex())
    rec[off:off + len(repl)] = repl
    print('新字节: %s' % bytes(rec[off:off + len(repl)]).hex())
    open(args.out, 'wb').write(bytes(rec))
    print('已写出 %s（%d B，长度未变）' % (args.out, len(rec)))

    # ── fxc 复验 ──────────────────────────────────────────────
    asm = args.out + '.asm'
    r = subprocess.run([args.fxc, '/nologo', '/dumpbin', '/Fc', asm, args.out],
                       capture_output=True, text=True)
    if r.returncode != 0 or not os.path.isfile(asm):
        print('  ✗ fxc 无法反汇编（编码非法）')
        print((r.stdout or '')[-800:])
        print((r.stderr or '')[-800:])
        return 1
    text = open(asm, encoding='utf-8', errors='replace').read()
    print('  ✓ fxc 反汇编成功（%d 行）' % text.count('\n'))
    if args.expect:
        ok = args.expect in text
        print('  %s 期望片段 %r %s' % ('✓' if ok else '✗', args.expect, '出现' if ok else '未出现'))
        if not ok:
            return 1
    # 打印含该指令的行，便于人工核对
    for line in text.splitlines():
        if 'o0.w' in line or (args.expect and args.expect in line):
            print('    | %s' % line.strip())
    return 0


if __name__ == '__main__':
    sys.exit(main())
