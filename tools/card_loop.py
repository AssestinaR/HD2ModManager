#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一条命令跑完整条色卡测量：抓帧 → 定位 → 拉平 → 偏差表 → 结论。

等价于手动两 step：
    python tools/wgc_capture.py --title HELLDIVERS --out <dir> --count 1
    python tools/card_measure.py --shot <dir>/*.png --card-size 2048 ...

用法：
    python tools/card_loop.py [--prefix g] [--out E:\\tmp\\shots\\card] [--card-size 2048] [--roi ...]
    python tools/card_loop.py --shots 3        # 连抓 3 帧，看重复性
"""
import argparse
import glob
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable


def run(args):
    r = subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return r.returncode, (r.stdout or '') + (r.stderr or '')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=r'E:\tmp\shots\card')
    ap.add_argument('--prefix', default='loop')
    ap.add_argument('--shots', type=int, default=1)
    ap.add_argument('--card-size', type=int, default=2048)
    ap.add_argument('--roi', default=None)
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    for old in glob.glob(os.path.join(args.out, args.prefix + '_*.png')):
        os.remove(old)

    code, out = run([PY, '-B', os.path.join(HERE, 'wgc_capture.py'),
                     '--title', 'HELLDIVERS', '--out', args.out,
                     '--count', str(args.shots), '--prefix', args.prefix])
    if code != 0:
        print(out.strip())
        return 2
    shots = sorted(glob.glob(os.path.join(args.out, args.prefix + '_*.png')))
    if not shots:
        print('没抓到任何帧')
        return 2

    gray_medians = []
    for shot in shots:
        cmd = [PY, '-B', os.path.join(HERE, 'card_measure.py'), '--shot', shot,
               '--card-size', str(args.card_size)]
        if args.roi:
            cmd += ['--roi', args.roi]
        cmd += ['--save-rectified', os.path.join(args.out, os.path.basename(shot) + '.rect.png')]
        code, out = run(cmd)
        print('=== %s ===' % os.path.basename(shot))
        print(out.strip())
        m = re.search(r'gray50\s+\(\d+, \d+, \d+\)\s+\((\d+), (\d+), (\d+)\)', out)
        if m:
            gray_medians.append(tuple(int(x) for x in m.groups()))

    if len(gray_medians) >= 2:
        print()
        print('=== 重复性（gray50 中位，0~255）===')
        for i, g in enumerate(gray_medians, 1):
            print('   第 %d 帧: %s' % (i, g))
        spread = [max(g[c] for g in gray_medians) - min(g[c] for g in gray_medians) for c in range(3)]
        print('   逐通道极差: %s  → 噪声底约 %d 级' % (spread, max(spread)))

    return 0


if __name__ == '__main__':
    sys.exit(main())
