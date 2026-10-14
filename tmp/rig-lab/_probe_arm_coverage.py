# -*- coding: utf-8 -*-
"""READ-ONLY: is the forearm a FULL TUBE or a HALF-OPEN SHELL?

Round 75, before writing any correction field.  A circle fit finds the centre of
curvature of a ring *or* of an arc, so it alone cannot tell us which we have --
but the two need different corrections.  This probe bins the vertices of each
cross-section slab into 24 azimuth buckets (around the fitted centre) and prints
the coverage, so we can see at a glance whether the mesh wraps all the way
around the bone or only covers one side.

Also prints, per station, the fitted centre offset and the mean radius, so the
same run doubles as the clean offset profile used to design the fade.

usage:
    _probe_arm_coverage.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_coverage.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL
import _probe_arm_tube_axis as TA


def coverage(d, side, seg, radius, step, arm_only, t0=0.0, t1=1.0):
    pre = 'l' if side == 'left' else 'r'
    pos = d['pos']
    limb = None
    if arm_only:
        pos, limb = TA.arm_only_mask(d, side)
    ti, bi = d['unit'].transform_info, d['unit'].bone_infos[0]
    names = {s: TL.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    A = heads[slot_of[pre + '_' + seg[0]]]
    B = heads[slot_of[pre + '_' + seg[1]]]
    ax = B - A
    L = float(np.linalg.norm(ax))
    ax = ax / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(ax @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(ax, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, ax); V_ /= np.linalg.norm(V_)

    rel = pos - A
    t = rel @ ax
    perp = rel - np.outer(t, ax)
    rad = np.linalg.norm(perp, axis=1)

    rows = []
    k0 = int(np.floor(t0 / step))
    k1 = int(np.ceil(t1 / step))
    for k in range(k0, k1 + 1):
        s0 = k * step
        m = (t >= s0 * L) & (t < (s0 + step) * L) & (rad < radius)
        if limb is not None:
            m = m & limb
        n = int(m.sum())
        if n < 8:
            continue
        x, y = perp[m] @ N_, perp[m] @ V_
        cx, cy, r, rms = TA.circle_fit_2d(x, y)
        ang = np.degrees(np.arctan2(y - cy, x - cx)) % 360.0
        # 24 buckets -> which are populated (>=2 vertices)
        hist, _ = np.histogram(ang, bins=24, range=(0.0, 360.0))
        filled = int((hist >= 2).sum())
        # largest angular gap (deg) between consecutive populated buckets
        occ = np.where(hist >= 2)[0]
        if len(occ) >= 2:
            gaps = np.diff(np.concatenate([occ, occ[:1] + 24])) * 15.0
            max_gap = float(gaps.max())
        else:
            max_gap = 360.0
        rows.append((s0, n, float(np.hypot(cx, cy)), r, rms, filled, max_gap, hist))
    return rows, L


def bar(hist):
    chars = ' .:-=+*#@'
    return ''.join(chars[min(len(chars) - 1, int(round(v / max(1.0, hist.max()) * 9)))]
                   for v in hist)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--radius', type=float, default=0.13)
    ap.add_argument('--step', type=float, default=0.04)
    ap.add_argument('--seg', default='elbow,hand')
    ap.add_argument('--arm-only', action='store_true')
    ap.add_argument('--t0', type=float, default=0.0,
                    help='first station, in units of L (t measured from the '
                         'proximal bone head; may be negative to reach back '
                         'along the parent, or > 1 to reach into the child)')
    ap.add_argument('--t1', type=float, default=1.0, help='last station, in units of L')
    ap.add_argument('--label', default=None)
    args = ap.parse_args()
    seg = tuple(x.strip() for x in args.seg.split(','))

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)

    rows, L = coverage(d, args.side, seg, args.radius, args.step, args.arm_only,
                       args.t0, args.t1)
    print()
    print('=' * 104)
    print('%s  [%s]  %s -> %s  L = %.1f mm   azimuth coverage (24 bins, 15 deg each)'
          % (label, args.side, seg[0], seg[1], L * 1000))
    print('=' * 104)
    print('  %6s %5s %8s %7s %7s %7s %8s   %s' %
          ('t(mm)', 'n', 'off(mm)', 'r(mm)', 'rms(mm)', 'filled', 'maxgap', '0..360 deg'))
    for s0, n, off, r, rms, filled, max_gap, hist in rows:
        print('  %6.0f %5d %8.1f %7.1f %7.2f %5d/24 %7.0f   %s'
              % (s0 * L * 1000, n, off * 1000, r * 1000, rms * 1000,
                 filled, max_gap, bar(hist)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
