# -*- coding: utf-8 -*-
"""READ-ONLY: offset of the arm mesh centreline from the bone, station by station.

Round 75 found that the forearm mesh sits ~35-46 mm off the elbow->wrist axis
while every other limb segment has its bone inside the mesh.  Before building a
corrective displacement field we need the shape of that offset along the whole
arm, so the fade-in/fade-out can be placed where the mesh actually meets its
neighbours.

Reports, per station along a bone chain (clavicle->elbow->wrist->hand):
  n        vertices in the slab (radius gate)
  mean     |mean perpendicular offset|      -- pulled by decorations
  med      |median perpendicular offset|    -- robust; this is the tube centre
  extent   projection of the slab onto the mean-offset direction

usage:
    _probe_arm_offset_profile.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_offset_profile.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL


def chain_profile(label, d, side, radius=0.13, step=0.05):
    pre = 'l' if side == 'left' else 'r'
    pos = d['pos']
    ti, bi = d['unit'].transform_info, d['unit'].bone_infos[0]
    names = {s: TL.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    segs = [(pre + '_clavicle', pre + '_elbow'), (pre + '_elbow', pre + '_hand')]
    print()
    print('=' * 104)
    print('%s   [%s side]   radius gate %.0f mm' % (label, side, radius * 1000))
    print('=' * 104)
    print('  %-22s %6s %6s %8s %8s %8s   %s'
          % ('segment', 's', 'n', '|mean|', '|median|', 'dir(deg)', 'extent along mean dir'))
    for a, b in segs:
        if a not in slot_of or b not in slot_of:
            print('  %-22s (missing)' % (a + '->' + b))
            continue
        A, B = heads[slot_of[a]], heads[slot_of[b]]
        ax = B - A
        L = float(np.linalg.norm(ax))
        ax = ax / L
        rel = pos - A
        t = rel @ ax
        perp = rel - np.outer(t, ax)
        rad = np.linalg.norm(perp, axis=1)
        for k in range(int(1.0 / step) + 1):
            s0 = k * step
            m = (t >= s0 * L) & (t < (s0 + step) * L) & (rad < radius)
            if int(m.sum()) < 6:
                continue
            P = perp[m]
            mu = P.mean(axis=0)
            md = np.median(P, axis=0)
            nd = mu / max(np.linalg.norm(mu), 1e-12)
            ext = P @ nd
            print('  %-22s %6.2f %6d %8.1f %8.1f %8.1f   %+7.1f .. %+7.1f mm'
                  % (a.replace(pre + '_', '') + '->' + b.replace(pre + '_', ''),
                     s0, int(m.sum()), np.linalg.norm(mu) * 1000,
                     np.linalg.norm(md) * 1000,
                     np.degrees(np.arctan2(mu[1], mu[0])),
                     ext.min() * 1000, ext.max() * 1000))
        print('  %-22s  L = %.1f mm' % ('', L * 1000))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--radius', type=float, default=0.13)
    ap.add_argument('--step', type=float, default=0.05)
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)
    chain_profile(label, d, args.side, args.radius, args.step)
    return 0


if __name__ == '__main__':
    sys.exit(main())
