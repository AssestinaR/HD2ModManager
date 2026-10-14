# -*- coding: utf-8 -*-
"""READ-ONLY: is the HAND mesh centred on the hand bone?

Round 75, deciding how far the alignment window may reach.  Candidate D corrects
the wrist fully, which drags the hand's root by ~21 mm.  That is only acceptable
if the hand was NOT already centred on its own bone -- otherwise we would be
introducing a fresh error at the free end.

The hand bone's local +X (its direction, in unit space) is column 0 of its rest
rotation, because the unit stores `pos_unit = pos_local @ R + t`.

usage:
    _probe_hand_axis.py <patch_0> [--unit 0x...] [--side left] [--seg hand,middle_finger1]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL
import _probe_arm_tube_axis as TA


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--radius', type=float, default=0.12)
    ap.add_argument('--step', type=float, default=0.06)
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)

    u = d['unit']
    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: TL.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    pre = 'l' if args.side == 'left' else 'r'

    pos, limb = TA.arm_only_mask(d, args.side)

    def head(nm):
        i = bi.real_indices[slot_of[pre + '_' + nm]]
        return np.array(ti.matrices[i][12:15], np.float64)

    def rotcol(nm, c):
        i = bi.real_indices[slot_of[pre + '_' + nm]]
        M = np.asarray(ti.matrices[i], np.float64).reshape(4, 4)
        return M[:3, c].copy()

    W = head('hand')
    axis = rotcol('hand', 0)
    axis = axis / np.linalg.norm(axis)
    print()
    print('=' * 92)
    print('%s  [%s]   hand bone +X = [%.4f %.4f %.4f]' % (label, args.side, *axis))
    print('=' * 92)

    # a plane perpendicular to the hand axis, sampled along it
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(axis @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(axis, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, axis); V_ /= np.linalg.norm(V_)

    rel = pos - W
    t = rel @ axis
    perp = rel - np.outer(t, axis)
    rad = np.linalg.norm(perp, axis=1)
    x2, y2 = perp @ N_, perp @ V_

    print('  %7s %5s %9s %9s %9s %8s' %
          ('t(mm)', 'n', 'off(mm)', 'dir(deg)', 'r(mm)', 'rms(mm)'))
    for k in range(0, int(0.30 / args.step) + 1):
        s0 = k * args.step
        m = limb & (t >= s0) & (t < s0 + args.step) & (rad < args.radius)
        if int(m.sum()) < 8:
            continue
        cx, cy, r, rms = TA.circle_fit_2d(x2[m], y2[m])
        print('  %7.0f %5d %9.1f %9.1f %9.1f %8.2f'
              % (s0 * 1000, int(m.sum()), float(np.hypot(cx, cy)) * 1000,
                 np.degrees(np.arctan2(cy, cx)), r * 1000, rms * 1000))
    return 0


if __name__ == '__main__':
    sys.exit(main())
