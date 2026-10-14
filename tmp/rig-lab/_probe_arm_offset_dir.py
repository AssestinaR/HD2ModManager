# -*- coding: utf-8 -*-
"""READ-ONLY: in which 3-D direction is the forearm tube offset from the bone?

Round 75b.  The centreline probe says our forearm tube runs a straight line
~41 mm (at the elbow) to ~23 mm (at the wrist) off the elbow->hand bone, and
vanilla only ~12 mm.  Before choosing a correction we need to know WHERE that
offset points, because that decides what a shape-preserving fix can even do.

Everything is expressed in the forearm frame the probes already use
    ax  = (hand - elbow)/L            the bone axis
    N_  = normalize(ax x Z)           horizontal, perpendicular to the arm
    V_  = normalize(N_ x ax)          the other perpendicular

and compared against the two directions that matter:
    U_  the UPPER ARM axis, perpendicular component   ("extend the upper arm")
    P_  the normal of the plane spanned by the two bone axes (out of the V)

Nothing is written.

usage:
    _probe_arm_offset_dir.py <centreline.json> [<centreline.json> ...]
"""
import json
import sys

import numpy as np


def unit(v):
    return v / np.linalg.norm(v)


def analyse(path):
    d = json.load(open(path, encoding='utf-8'))
    S = np.array(d['S']); E = np.array(d['E']); W = np.array(d['W'])
    ax = unit(W - E)
    N_ = np.array(d['forearm']['N_']); V_ = np.array(d['forearm']['V_'])

    # the upper-arm axis, perpendicular to the forearm axis
    ua = unit(E - S)
    u_perp = ua - float(ua @ ax) * ax
    u_perp = unit(u_perp)
    # out of the V plane
    P_ = unit(np.cross(ua, ax))

    def ang(v):
        return np.degrees(np.arctan2(float(v @ V_), float(v @ N_)))

    print()
    print('=' * 104)
    print('%s   [%s]   forearm L = %.1f mm   upper arm L = %.1f mm'
          % (d['label'], d['side'], d['forearm']['L'] * 1000,
             np.linalg.norm(E - S) * 1000))
    print('=' * 104)
    print('  in the (N_,V_) plane:')
    print('    N_   = (%+.3f %+.3f %+.3f)' % tuple(N_))
    print('    V_   = (%+.3f %+.3f %+.3f)' % tuple(V_))
    print('    U_   upper-arm axis, perpendicular part   angle %+7.1f deg  '
          '(=(%+.3f %+.3f %+.3f))'
          % (ang(u_perp), u_perp @ N_, u_perp @ V_, u_perp @ P_))
    print('    P_   normal of the bone V plane           angle %+7.1f deg'
          % ang(P_))
    print('    the offset is  %s  the V plane'
          % ('IN' if abs(float(u_perp @ P_)) < 0.5 else 'mostly OUT of'))
    print()
    print('  %6s %9s %9s %8s   %9s %9s   %9s'
          % ('t/L', 'off(mm)', 'dir(deg)', 'along U', 'U-comp', 'P-comp', 'residual'))
    rows = [r for r in d['forearm']['rows'] if r['n'] >= 8 and r['rms'] < 0.008]
    for r in rows:
        vec = r['cx'] * N_ + r['cy'] * V_
        aU = float(vec @ u_perp)
        aP = float(vec @ P_)
        print('  %6.2f %9.1f %9.1f %8.1f   %9.1f %9.1f   %9.1f'
              % (r['u'], r['off'] * 1000, r['ang'], aU * 1000, aU * 1000,
                 aP * 1000, np.hypot(aU, aP) * 1000))
    if rows:
        V = np.stack([r['cx'] * N_ + r['cy'] * V_ for r in rows])
        mean = V.mean(axis=0)
        print()
        print('  mean offset vector over %d clean stations: %6.1f mm  angle %+6.1f deg'
              % (len(rows), np.linalg.norm(mean) * 1000, ang(mean)))
        print('     along U (extend the upper arm) %6.1f mm'
              % (float(mean @ u_perp) * 1000))
        print('     along P (out of the V plane)   %6.1f mm'
              % (float(mean @ P_) * 1000))
        print('     spread of |offset|  %.1f .. %.1f mm   (a rigid move leaves '
              'the difference as residual)'
              % (np.linalg.norm(V, axis=1).min() * 1000,
                 np.linalg.norm(V, axis=1).max() * 1000))


def main():
    for p in sys.argv[1:]:
        analyse(p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
