# -*- coding: utf-8 -*-
"""中轴线剖面（在 model.npz 上量，用于比较 dump_blender 的两版输出）。

复用 `_probe_arm_centerline.py` 的逐站圆拟合（`measure()`），只是把输入从
unit 换成 npz：`ours_bind` = 网格，`w_bone/w_val` = HD2 骨权重，`b_head` = 骨位置。

⚠️ 与 unit 版探针的差别：npz 是**全量网格**（不做 LOD0 引用过滤），所以绝对值会
与 unit 版略有出入；但两版 npz 之间是**同口径**，比较差值有效。

usage:
    _probe_arm_npz_centerline.py --npz DIR --label NAME [--side left|right]
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from _probe_arm_centerline import measure


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--npz', required=True, help='directory holding model.npz + meta.json')
    ap.add_argument('--label', default='')
    ap.add_argument('--side', default='both', choices=('left', 'right', 'both'))
    ap.add_argument('--step', type=float, default=0.05)
    ap.add_argument('--radius', type=float, default=0.10)
    ap.add_argument('--min-n', type=int, default=6)
    ap.add_argument('--fit-lo', type=float, default=0.20)
    ap.add_argument('--fit-hi', type=float, default=0.96)
    args = ap.parse_args()

    z = np.load(os.path.join(args.npz, 'model.npz'))
    meta = json.load(open(os.path.join(args.npz, 'meta.json'), encoding='utf-8'))
    bones = meta['bones']

    pos = np.asarray(z['ours_bind'], np.float64)
    wb = np.asarray(z['w_bone'], np.int64)
    wv = np.asarray(z['w_val'], np.float64)
    heads = {i: np.asarray(z['b_head'][i], np.float64) for i in range(len(bones))}
    names = {i: bones[i] for i in range(len(bones))}
    slot_of = {bones[i]: i for i in range(len(bones))}

    dom = np.argmax(wv, axis=1)
    domb = wb[np.arange(len(wb)), dom]
    domw = wv[np.arange(len(wb)), dom]

    sides = ('left', 'right') if args.side == 'both' else (args.side,)
    for side in sides:
        pre = 'l' if side == 'left' else 'r'
        mask = np.zeros(len(pos), bool)
        for i, nm in names.items():
            if nm.startswith(pre + '_'):
                mask |= (domb == i) & (domw > 0.5)
        m = measure(pos, mask, names, heads, slot_of, side, args.step, args.radius,
                    args.min_n, args.fit_lo, args.fit_hi)
        E, W = m['E'], m['W']
        print('=== %s  %s arm ===' % (args.label, side))
        print('  bones: shoulder %s  elbow %s  hand %s'
              % (np.round(m['S'], 4), np.round(E, 4), np.round(W, 4)))
        print('  forearm bone length %.1f mm' % (m['forearm']['L'] * 1000))
        print('  %6s %7s %5s %9s %8s %7s' % ('t/L', 't(mm)', 'n', 'off(mm)', 'r(mm)', 'rms(mm)'))
        for r in m['forearm']['rows']:
            print('  %6.2f %7.0f %5d %9.1f %8.1f %7.2f'
                  % (r['u'], r['t'] * 1000, r['n'], r['off'] * 1000,
                     r['r'] * 1000, r['rms'] * 1000))
        f = m['fit_forearm']
        if f:
            print('  forearm straight fit %.2f..%.2f: tilt %6.2f deg   offset at joint %6.1f mm'
                  '   straightness rms %5.2f mm'
                  % (f['lo'], f['hi'], f['tilt'], f['off'] * 1000, f['rms'] * 1000))
        fu = m['fit_upper']
        if fu:
            print('  upper   straight fit %.2f..%.2f: tilt %6.2f deg   offset at joint %6.1f mm'
                  % (fu['lo'], fu['hi'], fu['tilt'], fu['off'] * 1000))
        print('  mesh V %.2f deg   bone V %.2f deg'
              % (m['mesh_v'] if m['mesh_v'] is not None else float('nan'), m['bone_v']))


if __name__ == '__main__':
    main()
