# -*- coding: utf-8 -*-
"""READ-ONLY: how is the forearm weight split along t/L?

Mechanism check for the forearm rubber-band.  Real game data (see
_probe_wrist_rest.py) shows

  * l_elbow   local translation 0.30274 m  -> upper arm
  * l_hand    local translation 0.28718 m  -> wrist, 3 DOF
  * l_hand_twist local translation 0.28718 m -> same head as l_hand, but its
    local rotation is *always* [x,0,0,w]: a pure 1-DOF twist about the forearm.

So both l_hand and l_hand_twist pivot at the wrist.  A mid-forearm vertex
weighted to l_hand swings on a ~14 cm lever whenever the wrist flexes (the
rubber band); the same vertex weighted to l_hand_twist can only twist about the
forearm axis (~3 cm radius), which is safe.

This probe bins the near-axis forearm band by t/L and prints, per bin, the mean
weight on l_elbow / l_hand(+fingers) / l_hand_twist / other, for the shipped
patch.  If l_hand carries weight across the mid-forearm, the mechanism holds.

Nothing is written.
"""
import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _probe_forearm_shells as S

UNITS = S.UNITS
NBINS = 20


def profile(D, side):
    E, W, axis, L, t, rad, hs, es, hand_sub = S.side_axis(D, side)
    hand_set = list(hand_sub)
    band = (rad < 0.10) & (t > 0.02 * L) & (t < 1.00 * L)
    idxs = np.where(band)[0]
    tl = t[idxs] / L
    w_elbow = np.where(D['bidx'][idxs] == es, D['wgt'][idxs], 0.0).sum(axis=1)
    w_hand = np.where(np.isin(D['bidx'][idxs], hand_set), D['wgt'][idxs], 0.0).sum(axis=1)
    ts = D['slot_of'].get(side + '_hand_twist', None)
    if ts is None:
        w_twist = np.zeros(len(idxs))
    else:
        w_twist = np.where(D['bidx'][idxs] == ts, D['wgt'][idxs], 0.0).sum(axis=1)
    rows = []
    edges = np.linspace(0.0, 1.0, NBINS + 1)
    for i in range(NBINS):
        m = (tl >= edges[i]) & (tl < edges[i + 1])
        if m.sum() < 4:
            rows.append((0.5 * (edges[i] + edges[i + 1]), 0, np.nan, np.nan, np.nan, np.nan))
            continue
        rows.append((0.5 * (edges[i] + edges[i + 1]), int(m.sum()),
                     float(w_elbow[m].mean()), float(w_hand[m].mean()),
                     float(w_twist[m].mean()),
                     float((w_elbow[m] + w_hand[m] + w_twist[m]).mean())))
    return rows, L, int(band.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='+')
    ap.add_argument('--unit', action='append', default=[])
    args = ap.parse_args()
    units = [int(x, 16) for x in args.unit] or list(UNITS)

    for patch in args.patch:
        for unit in units:
            try:
                D = S.load(patch, unit)
            except StopIteration:
                continue
            print('\n' + '=' * 96)
            print('%s  unit=0x%016x  verts=%d' % (patch, unit, len(D['pos'])))
            for side in ('l', 'r'):
                rows, L, n = profile(D, side)
                print('\n  %s forearm  L=%.1f mm  near-axis band verts=%d'
                      % (side.upper(), L * 1000, n))
                print('   %5s %6s %8s %8s %8s %8s' %
                      ('t/L', 'n', 'elbow', 'hand+', 'twist', 'sum'))
                for tl, cnt, we, wh, wt, ws in rows:
                    if cnt == 0:
                        continue
                    flag = '   <-- mid-forearm' if 0.25 <= tl <= 0.70 and wh > 0.15 else ''
                    print('   %5.2f %6d %8.2f %8.2f %8.2f %8.2f%s'
                          % (tl, cnt, we, wh, wt, ws, flag))
                # scalar summary
                band = (np.asarray([r[0] for r in rows]) >= 0.25) & \
                       (np.asarray([r[0] for r in rows]) <= 0.70)
                wh = np.asarray([r[3] for r in rows], float)
                print('   mid-forearm (0.25-0.70 t/L) mean hand weight = %.3f' % np.nanmean(wh[band]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
