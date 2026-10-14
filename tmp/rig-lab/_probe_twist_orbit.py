# -*- coding: utf-8 -*-
"""READ-ONLY: how far does the forearm ORBIT when the wrist twists?

This is the user's complaint measured directly.  A pure spin about the bone axis
leaves every point's distance-to-axis unchanged and moves the tube axis nowhere;
an eccentric tube (tube axis e away from the bone axis) instead ORBITS by
2 * e * sin(theta/2).

For each t/L band we take the mesh centroid, apply the realistic wrist twist
(l_hand by theta, l_hand_twist by 0.78*theta, both pivoting on the wrist along
the forearm axis) and report

    orbit   = |centroid(theta) - centroid(0)|          in mm
    axis    = the same for a band that is weighted to the elbow only (control:
              it must not move at all)

A well-centred forearm gives an orbit near 0.  Because the eccentricity varies
along the arm, the per-band orbit vectors differ, and that difference is the
shear that bends the "clock hand".

usage:
    _probe_twist_orbit.py <patch_0> [--unit 0x...] [--side left] [--theta 90]
    _probe_twist_orbit.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_wrist_roll_render as R
import _probe_wrist_twist_render as T

BANDS = [(0.20, 0.35), (0.35, 0.50), (0.50, 0.65), (0.65, 0.80),
         (0.80, 0.90), (0.90, 1.00), (1.00, 1.10)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--side', default='left')
    ap.add_argument('--theta', type=float, default=90.0)
    ap.add_argument('--k', type=float, default=0.78)
    ap.add_argument('--radius', type=float, default=0.12)
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        label = args.label or os.path.basename(args.local)
    else:
        import hd2_patch as P
        import hd2_unit as U
        entries, _ = P.scan_entries(args.patch)
        e = next(x for x in entries if x['type_id'] == U.UNIT_ID
                 and x['file_id'] == int(args.unit, 16))
        pl = P.entry_payload(e, args.patch)
        toc, gpu = pl['toc'], pl['gpu']
        label = args.label or os.path.basename(args.patch)

    import hd2_unit as U
    pos, w_hand, w_tw, w_el, E, W, tri, mat = T.assemble(U.Unit.read(toc, gpu), args.side)

    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(axis @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(axis, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, axis); V_ /= np.linalg.norm(V_)

    # t is measured FROM THE ELBOW towards the wrist (so 0 .. L is the forearm,
    # > L is the hand).  Measuring from W would put the forearm at negative t and
    # silently select hand geometry instead.
    rel = pos - E
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    tl = t / L
    limb = (w_el + w_tw + w_hand) > 0.9

    Rm = R.rodrigues(axis, np.deg2rad(args.theta))
    Rk = R.rodrigues(axis, np.deg2rad(args.theta * args.k))
    d = (w_hand[:, None] * (((pos - W) @ Rm.T) - (pos - W))
         + w_tw[:, None] * (((pos - W) @ Rk.T) - (pos - W)))
    new = pos + d

    print()
    print('=' * 96)
    print('%s  [%s]  L = %.1f mm   wrist twist %.0f deg (twist bone at %.2f)'
          % (label, args.side, L * 1000, args.theta, args.k))
    print('=' * 96)
    print('  %12s %6s %10s %10s %10s %10s' %
          ('band t/L', 'n', 'orbit(mm)', 'along(mm)', 'across(mm)', 'mean f'))
    prev = None
    worst = 0.0
    for lo, hi in BANDS:
        m = (tl >= lo) & (tl < hi) & (rad < args.radius) & limb
        if int(m.sum()) < 6:
            continue
        c0 = pos[m].mean(axis=0)
        c1 = new[m].mean(axis=0)
        dv = c1 - c0
        along = float(dv @ axis) * 1000.0
        across = float(np.linalg.norm(dv - (dv @ axis) * axis)) * 1000.0
        f = float((w_hand[m] + args.k * w_tw[m]).mean())
        worst = max(worst, across)
        print('  %5.2f-%-6.2f %6d %10.1f %10.2f %10.1f %10.3f'
              % (lo, hi, int(m.sum()), across, along, across, f))
        prev = (lo, hi, dv)
    print()
    print('  worst cross-axis orbit: %.1f mm   (= 2*e*sin(theta/2) => e = %.1f mm)'
          % (worst, worst / (2.0 * np.sin(np.deg2rad(args.theta) / 2.0))))
    return 0


if __name__ == '__main__':
    sys.exit(main())
