# -*- coding: utf-8 -*-
"""READ-ONLY: how far is the forearm MESH tube from the bone axis?  (round 75)

The bones of our unit and the vanilla arm skin are byte-identical, and after
round 74 the twist weight profile matches vanilla, so the remaining error has to
be the mesh.  This measures, over the forearm band (mid-forearm, well clear of
the wrist and the elbow):

  * tilt    -- angle between the mesh's own principal axis (PCA) and the
               elbow->wrist bone axis
  * d_perp  -- mean distance of the surface points from the bone axis line
               (a tube of radius r centred on the axis gives d_perp ~ r)
  * off     -- distance of the band centroid from the axis

Nothing is written.

usage:
    _probe_arm_axis.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_axis.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL


def measure(d, side, lo=-0.26, hi=-0.05, radius=0.12):
    pos, E, W = d['pos'], d['E'], d['W']
    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    rel = pos - W
    t = rel @ axis
    perp = rel - np.outer(t, axis)
    rad = np.linalg.norm(perp, axis=1)
    band = (t >= lo) & (t <= hi) & (rad < radius)
    P = rel[band]
    if len(P) < 8:
        return None
    Pc = P - P.mean(axis=0)
    _, _, vt = np.linalg.svd(Pc, full_matrices=False)
    ax2 = vt[0]
    tilt = float(np.degrees(np.arccos(min(1.0, abs(float(ax2 @ axis))))))
    dperp = np.linalg.norm(P - np.outer(P @ axis, axis), axis=1)
    off = float(np.linalg.norm(P.mean(axis=0) - (P.mean(axis=0) @ axis) * axis))
    return dict(n=int(band.sum()), L=L, tilt=tilt,
                dperp_mean=float(dperp.mean()) * 1000,
                dperp_min=float(dperp.min()) * 1000,
                dperp_max=float(dperp.max()) * 1000,
                off=off * 1000)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)

    print()
    print('=' * 92)
    print('%s   [%s]   forearm band t in [-260, -50] mm, rad < 120 mm' % (label, args.side))
    print('=' * 92)
    r = measure(d, args.side)
    if r is None:
        print('  not enough vertices')
        return 0
    print('  n = %d   forearm L = %.1f mm' % (r['n'], r['L']))
    print('  MESH tube axis vs BONE axis (elbow->wrist) tilt : %.2f deg' % r['tilt'])
    print('  surface distance from the bone axis              : mean %.1f mm  (%.1f .. %.1f)'
          % (r['dperp_mean'], r['dperp_min'], r['dperp_max']))
    print('  band centroid offset from the bone axis          : %.1f mm' % r['off'])
    print()
    print('  reference: a tube of radius r centred on the axis has')
    print('             d_perp ~ r and centroid offset ~ 0;')
    print('             a tube shifted by s has centroid offset ~ s;')
    print('             a tube tilted by a cuts the axis plane obliquely.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
