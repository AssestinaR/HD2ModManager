# -*- coding: utf-8 -*-
"""READ-ONLY: forearm weight profile for the MourningVeil unit, before vs after.

Usage:
    python _probe_mv_profile.py <before.patch_0> <after.patch_0> [unit_hex]

Prints, per side, the mean l_elbow / l_hand / l_hand_twist weight in 10 bands of
t/L along the elbow->hand axis (near-axis vertices only), for both patches, so
the shift of the transition band is visible.
"""
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
DEFAULT_UNIT = 0xcae35ee15168145f
NBAND = 10


def load(patch, unit_id):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit_id)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])

    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    si = [i for i, s in enumerate(u.stream_infos)
          if s.num_vertices > 1000
          and any(c.type == 7 for c in s.components)
          and any(c.type == 6 for c in s.components)][0]
    verts = u.decode_vertices(si)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    out = {}
    for side, el, hd, tw in (('left', 'l_elbow', 'l_hand', 'l_hand_twist'),
                             ('right', 'r_elbow', 'r_hand', 'r_hand_twist')):
        e_s, h_s = slot_of[el], slot_of[hd]
        t_s = slot_of.get(tw)
        E, W = heads[e_s], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        near = (rad < 0.10) & (t > 0) & (t < L)

        def wsum(slot):
            if slot is None:
                return np.zeros(len(pos))
            w = np.zeros(len(pos))
            for j in range(4):
                m = bidx[:, j] == slot
                w[m] += wgt[m, j]
            return w

        wh, we, wt = wsum(h_s), wsum(e_s), wsum(t_s)
        out[side] = dict(L=L, near=near, t=t, wh=wh, we=we, wt=wt)
    return out


def table(m):
    L = m['L']
    near, t = m['near'], m['t']
    edges = np.linspace(0.0, L, NBAND + 1)
    rows = []
    for a, b in zip(edges[:-1], edges[1:]):
        sel = np.nonzero(near & (t >= a) & (t < b))[0]
        if len(sel) == 0:
            rows.append((a / L, b / L, 0, None, None, None))
            continue
        rows.append((a / L, b / L, len(sel),
                     m['we'][sel].mean(), m['wh'][sel].mean(), m['wt'][sel].mean()))
    return rows


def main():
    before = sys.argv[1]
    after = sys.argv[2] if len(sys.argv) > 2 else before
    unit_id = int(sys.argv[3], 16) if len(sys.argv) > 3 else DEFAULT_UNIT
    B = load(before, unit_id)
    A = load(after, unit_id)
    for side in ('left', 'right'):
        rb, ra = table(B[side]), table(A[side])
        print()
        print('=' * 92)
        print('%s   L = %.1f mm   (near-axis vertices, radius < 0.10 m)'
              % (side, B[side]['L'] * 1000))
        print('  %-13s %-5s | %-22s | %-22s'
              % ('t/L', 'n', 'BEFORE  elbow/hand/twist', 'AFTER   elbow/hand/twist'))
        for (a0, b0, n0, e0, h0, t0), (a1, b1, n1, e1, h1, t1) in zip(rb, ra):
            def fmt(e, h, t):
                if e is None:
                    return '        --          '
                return '%6.3f %6.3f %6.3f' % (e, h, t)
            print('  %.1f~%.1f (n=%-4d) | %s | %s' % (a1, b1, (n1 or n0),
                                                      fmt(e0, h0, t0), fmt(e1, h1, t1)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
