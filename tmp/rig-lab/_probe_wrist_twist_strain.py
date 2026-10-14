# -*- coding: utf-8 -*-
"""READ-ONLY: edge-length strain under a REALISTIC wrist twist.

Round 68 rejected the twist split because a synthetic 180 deg roll of l_hand
alone tore the forearm (p99 29 mm shipped vs 58 mm at twist_frac 0.6).  But that
test froze l_hand_twist.  The real player animation set says the game drives the
twist bone too, at ~0.78x the hand's twist with correlation 0.95..1.00
(_probe_wrist_split.py).  So the honest synthetic is

    l_hand       twists by  theta
    l_hand_twist twists by  k * theta      (k = 0.78 measured)

Both rotate about the same forearm axis through the wrist, so a vertex rotates
by  w_hand * theta + w_twist * k * theta  and edge length only changes where
that angle varies between neighbouring vertices.

Reports, per patch, the p99 / max edge-length change over the whole forearm
(same edge set for every patch: every edge with both ends within the near-axis
band), for theta = 45 / 90 deg and k = 0.78.  k=0 reproduces the old frozen-twist
number so the two assumptions can be compared directly.

Nothing is written.
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _probe_wrist_roll_render as R


def rot_about(pos, W, axis, deg):
    """rotate (pos-W) about unit axis by deg (per-vertex angles)."""
    v = pos - W
    par = np.outer(v @ axis, axis)
    perp = v - par
    th = np.deg2rad(deg)
    c, s = np.cos(th)[:, None], np.sin(th)[:, None]
    return W + par + perp * c + np.cross(np.broadcast_to(axis, v.shape), perp) * s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patches', nargs='+')
    ap.add_argument('--side', default='left')
    ap.add_argument('--theta', default='45,90')
    ap.add_argument('--k', default='0.78,0')
    args = ap.parse_args()

    for patch in args.patches:
        pos, tris, wh, E, W, axis, keep = R.load(patch, args.side, False)
        # weight on l_hand_twist (a sibling of l_hand, so NOT in the hand subtree)
        tw_slot = R.SIDES[args.side][0].replace('elbow', 'hand_twist')
        entries, _ = R.P.scan_entries(patch)
        e = next(x for x in entries if x['type_id'] == R.U.UNIT_ID and x['file_id'] == R.UNIT)
        pl = R.P.entry_payload(e, patch)
        u = R.U.Unit.read(pl['toc'], pl['gpu'])
        ti, bi = u.transform_info, u.bone_infos[0]
        names = {s: R.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
                 for s, i in enumerate(bi.real_indices)}
        slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
        mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
        verts = u.decode_vertices(mi.stream_index)
        bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
        wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
        wt = np.zeros(len(pos))
        if tw_slot in slot_of:
            for j in range(4):
                hit = bidx[:, j] == slot_of[tw_slot]
                wt[hit] += wgt[hit, j]

        ed = np.concatenate((tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]))
        band = keep[ed].all(axis=1)
        ee = ed[band]
        l0 = np.linalg.norm(pos[ee[:, 0]] - pos[ee[:, 1]], axis=1)
        ok = l0 > 1e-6
        ee, l0 = ee[ok], l0[ok]
        tmid = 0.5 * ((pos[ee[:, 0]] - W) @ axis + (pos[ee[:, 1]] - W) @ axis)
        print('\n%s  %d edges in the forearm band' % (patch, len(ee)))
        for k in [float(x) for x in args.k.split(',')]:
            for th in [float(x) for x in args.theta.split(',')]:
                ang = wh * th + wt * k * th
                newp = rot_about(pos, W, axis, ang)
                dl = np.abs(np.linalg.norm(newp[ee[:, 0]] - newp[ee[:, 1]], axis=1) - l0) * 1000.0
                p99, mx = float(np.percentile(dl, 99)), float(dl.max())
                worst_t = float(np.median(tmid[dl >= p99]) / np.linalg.norm(W - E))
                print('   k=%.2f  theta=%3.0f deg :  p99 %6.2f mm  max %6.2f mm  worst@t/L %.2f'
                      % (k, th, p99, mx, worst_t))
    return 0


if __name__ == '__main__':
    sys.exit(main())
