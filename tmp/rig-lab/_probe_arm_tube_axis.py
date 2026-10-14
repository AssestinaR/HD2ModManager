# -*- coding: utf-8 -*-
"""READ-ONLY: fit the forearm's TUBE AXIS by least squares, station by station.

Round 75: the forearm mesh sits to one side of the elbow->wrist bone.  A centroid
is a poor centre estimate -- armour plates and a frilled bracelet pull it, and a
half-open shell would bias it too.  A least-squares CIRCLE fit does not care:
whether the section is a full ring or a half ring, the fitted centre is still the
centre of curvature, i.e. the tube axis.

Per station this prints
  n      vertices in the slab (radius gate)
  fit    fitted circle centre, |offset| from the bone axis and its direction
  r      fitted radius
  rms    residual of the fit  (small = the section really is a circular arc)

usage:
    _probe_arm_tube_axis.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_tube_axis.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL


def circle_fit_2d(x, y):
    """Kasa algebraic fit -> (cx, cy, r, rms)."""
    A = np.column_stack([2.0 * x, 2.0 * y, np.ones_like(x)])
    b = x * x + y * y
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    cx, cy = sol[0], sol[1]
    r = float(np.sqrt(max(sol[2] + cx * cx + cy * cy, 0.0)))
    d = np.hypot(x - cx, y - cy)
    return float(cx), float(cy), r, float(np.sqrt(((d - d.mean()) ** 2).mean()))


def arm_only_mask(d, side):
    """True for vertices whose dominant bone belongs to this limb (name starts
    with l_/r_); the torso, spine and pelvis armour is excluded."""
    import numpy as np
    u = d['unit']
    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: TL.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    pos, wgt, dec, mat = TL._live_lod0(u, mi)
    pre = 'l' if side == 'left' else 'r'
    limb = np.zeros(len(pos), bool)
    for j in range(4):
        for s in np.unique(dec[:, j]):
            nm = names.get(int(s), '')
            if nm.startswith(pre + '_'):
                limb |= (dec[:, j] == s) & (wgt[:, j] > 0.5)
    return pos, limb


def profile(label, d, side, radius=0.13, step=0.04, min_n=8, seg=('elbow', 'hand'),
            arm_only=False):
    pre = 'l' if side == 'left' else 'r'
    pos = d['pos']
    limb = None
    if arm_only:
        pos, limb = arm_only_mask(d, side)
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

    print()
    print('=' * 96)
    print('%s  [%s]  %s -> %s  L = %.1f mm   circle fit per %.0f mm station%s'
          % (label, side, seg[0], seg[1], L * 1000, step * 1000,
             '   (limb-weighted vertices only)' if arm_only else ''))
    print('=' * 96)
    print('  %6s %5s %8s %8s %7s %7s %8s' %
          ('t(mm)', 'n', 'off(mm)', 'dir(deg)', 'r(mm)', 'rms(mm)', 't/L'))
    out = []
    for k in range(int(1.0 / step) + 1):
        s0 = k * step
        m = (t >= s0 * L) & (t < (s0 + step) * L) & (rad < radius)
        if limb is not None:
            m = m & limb
        if int(m.sum()) < min_n:
            continue
        x, y = perp[m] @ N_, perp[m] @ V_
        cx, cy, r, rms = circle_fit_2d(x, y)
        off = float(np.hypot(cx, cy))
        out.append((s0, cx, cy, off, r, rms, int(m.sum())))
        print('  %6.0f %5d %8.1f %8.1f %7.1f %7.2f %8.2f'
              % (s0 * L * 1000, int(m.sum()), off * 1000,
                 np.degrees(np.arctan2(cy, cx)), r * 1000, rms * 1000, s0))
    return out, L, (N_, V_, ax)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--radius', type=float, default=0.13)
    ap.add_argument('--step', type=float, default=0.04)
    ap.add_argument('--label', default=None)
    ap.add_argument('--seg', default='elbow,hand',
                    help='bone-name suffixes without the l_/r_ prefix, e.g. '
                         'clavicle,elbow or elbow,hand')
    ap.add_argument('--arm-only', action='store_true',
                    help='keep only vertices whose dominant weight is on this '
                         'limb, so shoulder/torso armour cannot bias the fit')
    args = ap.parse_args()

    seg = tuple(x.strip() for x in args.seg.split(','))

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)
    profile(label, d, args.side, args.radius, args.step, seg=seg,
            arm_only=args.arm_only)
    return 0


if __name__ == '__main__':
    sys.exit(main())
