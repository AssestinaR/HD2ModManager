# -*- coding: utf-8 -*-
"""READ-ONLY: the visible "V" of the arm mesh, measured close to the elbow.

The upper arm and the forearm are each a straight tube, so the eye reads the
angle between the two tube DIRECTIONS as the arm's V.  The full-length fit
(_probe_arm_centerline.py, t/L 0.20..0.96) is fine while the tube stays
straight, but once a correction makes it converge toward the bone axis that
window is no longer a straight line and the reported angle drifts.

This probe fits each tube over a short window NEXT TO THE ELBOW, where both are
still clean, and reports

    upper tube direction vs upper bone direction   (tilt)
    fore  tube direction vs fore  bone direction   (tilt)
    mesh V (between the two tube directions)  vs  bone V

usage:
    _probe_arm_v_angle.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_v_angle.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_arm_centerline as C
import hd2_patch as P
import hd2_unit as U

SLIM = 0xcae35ee15168145f


def load(args):
    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        u = U.Unit.read(toc, gpu)
        return u, args.label or os.path.basename(args.local)
    entries, _ = P.scan_entries(args.patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID
             and x['file_id'] == int(args.unit, 16))
    pl = P.entry_payload(e, args.patch)
    return U.Unit.read(pl['toc'], pl['gpu']), args.label or os.path.basename(args.patch)


def ang(a, b):
    a = np.asarray(a, np.float64); a = a / np.linalg.norm(a)
    b = np.asarray(b, np.float64); b = b / np.linalg.norm(b)
    return float(np.degrees(np.arccos(np.clip(float(a @ b), -1.0, 1.0))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--step', type=float, default=0.02)
    ap.add_argument('--radius', type=float, default=0.13)
    ap.add_argument('--min-n', type=int, default=8)
    ap.add_argument('--upper-lo', type=float, default=0.55)
    ap.add_argument('--upper-hi', type=float, default=0.95)
    ap.add_argument('--fore-lo', type=float, default=0.05)
    ap.add_argument('--fore-hi', type=float, default=0.35)
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    u, label = load(args)
    d = C.live_lod0(u)
    names, heads, slot_of = C.bone_table(u)
    pre = 'l' if args.side == 'left' else 'r'
    mask = C.limb_mask(d['dec'], d['wgt'], names, pre)

    m = C.measure(d['pos'], mask, names, heads, slot_of, args.side,
                  args.step, args.radius, args.min_n, args.fore_lo, args.fore_hi)
    fu = C.straight_fit(m['upper'], args.upper_lo, args.upper_hi)
    ff = C.straight_fit(m['forearm'], args.fore_lo, args.fore_hi)

    S, E, W = m['S'], m['E'], m['W']
    ax_u, ax_f = E - S, W - E
    bone_v = 180.0 - ang(ax_u, ax_f)

    print('=' * 92)
    print('%s   [%s]   upper window %.2f..%.2f (%s st, rms %.2f mm)'
          % (label, args.side, args.upper_lo, args.upper_hi,
             fu['n'] if fu else '-', (fu['rms'] * 1000) if fu else float('nan')))
    print('            forearm window %.2f..%.2f (%s st, rms %.2f mm)'
          % (args.fore_lo, args.fore_hi,
             ff['n'] if ff else '-', (ff['rms'] * 1000) if ff else float('nan')))
    print('-' * 92)
    if fu is None or ff is None:
        print('  not enough clean stations')
        return 1
    mesh_v = 180.0 - ang(fu['d'], ff['d'])
    print('  bone V interior              %7.2f deg' % bone_v)
    print('  mesh V interior (tube dirs)  %7.2f deg   (delta %+6.2f)'
          % (mesh_v, mesh_v - bone_v))
    print('  upper tube vs upper bone     %7.2f deg' % ang(fu['d'], ax_u))
    print('  fore  tube vs fore  bone     %7.2f deg' % ang(ff['d'], ax_f))
    print('  upper tube off at shoulder   %7.1f mm' % (fu['off'] * 1000))
    print('  fore  tube off at elbow      %7.1f mm' % (ff['off'] * 1000))
    print('=' * 92)
    return 0


if __name__ == '__main__':
    sys.exit(main())
