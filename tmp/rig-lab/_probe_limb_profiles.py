# -*- coding: utf-8 -*-
"""READ-ONLY: limb weight profiles for the three shipped models, same yardstick.

Answers "why is it only the forearm that feels like a rubber band" by putting
the forearm, the upper arm and the shin side by side on identical axes.

Usage:
    python _probe_limb_profiles.py <patch_0> [...]
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
UNIT = 0xcae35ee15168145f
NBAND = 12

# label, proximal bone, distal bone, extra bones to report
CHAINS = [
    ('upperarm  l_shoulder->l_elbow', 'l_shoulder', 'l_elbow',
     ('l_shoulder', 'l_elbow', 'l_hand')),
    ('forearm   l_elbow->l_hand', 'l_elbow', 'l_hand',
     ('l_shoulder', 'l_elbow', 'l_hand')),
    ('shin      l_knee->l_foot', 'l_knee', 'l_foot',
     ('l_thigh', 'l_knee', 'l_foot')),
    ('thigh     l_hip->l_knee', 'l_hip', 'l_knee',
     ('l_hip', 'l_knee', 'l_foot')),
]


def load(patch):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
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
    return u, ti, bi, names, heads, slot_of, pos, bidx, wgt


def wsum(bidx, wgt, slot, nv):
    if slot is None:
        return np.zeros(nv)
    w = np.zeros(nv)
    for j in range(4):
        m = bidx[:, j] == slot
        w[m] += wgt[m, j]
    return w


def profile(patch, label):
    u, ti, bi, names, heads, slot_of, pos, bidx, wgt = load(patch)
    nv = len(pos)
    print()
    print('#' * 100)
    print('# %s   unit 0x%016x   %d verts' % (label, UNIT, nv))
    print('#' * 100)
    for title, prox, dist, report in CHAINS:
        if prox not in slot_of or dist not in slot_of:
            print('\n  %s  -- SKIPPED (missing %s)' %
                  (title, prox if prox not in slot_of else dist))
            continue
        P_, D_ = heads[slot_of[prox]], heads[slot_of[dist]]
        axis = D_ - P_
        L = float(np.linalg.norm(axis))
        if L < 1e-6:
            print('\n  %s  -- degenerate length' % title)
            continue
        axis /= L
        rel = pos - P_
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        near = (rad < 0.095) & (t > 0.0) & (t < L)
        ws = {nm: wsum(bidx, wgt, slot_of.get(nm), nv) for nm in report}
        print('\n  %s   L = %.1f mm   near-axis verts = %d (rad < 95 mm)'
              % (title, L * 1000, int(near.sum())))
        print('    %-14s %-5s | %s' % ('t/L', 'n',
                                       ' '.join('%-14s' % nm for nm in report)))
        edges = np.linspace(0.0, L, NBAND + 1)
        for a, b in zip(edges[:-1], edges[1:]):
            sel = np.nonzero(near & (t >= a) & (t < b))[0]
            if len(sel) == 0:
                continue
            print('    %4.2f~%-8.2f %-5d | %s'
                  % (a / L, b / L, len(sel),
                     ' '.join('%-14.3f' % ws[nm][sel].mean() for nm in report)))
        # scalar summary: how far does the distal bone leak into the proximal half?
        half = near & (t < 0.5 * L)
        if half.sum():
            print('    => distal bone mean weight in the PROXIMAL half (t<0.5L): %.3f'
                  % ws[dist][half].mean())
        end = near & (t > 0.9 * L)
        if end.sum():
            print('    => distal bone mean weight at the FAR end  (t>0.9L): %.3f'
                  % ws[dist][end].mean())
    return 0


def main():
    for i, patch in enumerate(sys.argv[1:]):
        profile(patch, os.path.basename(os.path.dirname(patch)) or patch)
    return 0


if __name__ == '__main__':
    sys.exit(main())
