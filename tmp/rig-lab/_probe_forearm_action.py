# -*- coding: utf-8 -*-
"""READ-ONLY offline action test: how much does a wrist bend drag the forearm?

Not a captured game frame.  We rotate the hand bone AND every descendant 30
degrees about a transverse axis through the wrist joint and linear-skin the
result with the package's own weights.  A correct forearm stays put (0 mm);
the round-60 forearm was dragged ~52 mm at its middle.

Usage: python _probe_forearm_action.py <patch_0> [<patch_0> ...]
"""
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
UNIT = 0xcae35ee15168145f


def analyse(patch):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    ti = u.transform_info
    bi = u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    out = {}
    for side, el, hd in (('left', 'l_elbow', 'l_hand'), ('right', 'r_elbow', 'r_hand')):
        h_s = slot_of[hd]
        hand_ti = bi.real_indices[h_s]
        subtree = []
        for slot, ti_idx in enumerate(bi.real_indices):
            seen, cur = set(), ti_idx
            while 0 <= cur < ti.count and cur not in seen:
                if cur == hand_ti:
                    subtree.append(slot)
                    break
                seen.add(cur)
                cur = ti.entries[cur][1]
        wh = np.zeros(len(pos))
        for j in range(4):
            m = np.isin(bidx[:, j], subtree)
            wh[m] += wgt[m, j]

        E, W = heads[slot_of[el]], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)

        rot = np.cross(axis, [0.0, 0.0, 1.0])
        rot /= np.linalg.norm(rot)
        a = np.deg2rad(30.0)
        K = np.array([[0.0, -rot[2], rot[1]], [rot[2], 0.0, -rot[0]],
                      [-rot[1], rot[0], 0.0]])
        R = np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K
        disp = wh[:, None] * ((pos - W) @ R.T - (pos - W))
        mag = np.linalg.norm(disp, axis=1)

        near = (rad < 0.10) & (t > 0) & (t < L)
        mid = near & (t >= 0.25 * L) & (t < 0.70 * L)
        out[side] = dict(
            length_mm=L * 1000,
            mid_n=int(mid.sum()),
            mid_median_mm=float(np.median(mag[mid]) * 1000),
            mid_max_mm=float(mag[mid].max() * 1000),
            tip_median_mm=float(np.median(mag[near & (t > 0.9 * L)]) * 1000),
            mid_hand_mean=float(wh[mid].mean()))
    return out


def main():
    print('%-58s %-6s %8s %8s %8s %8s' %
          ('patch', 'side', 'mid med', 'mid max', 'mid hand', 'tip med'))
    for patch in sys.argv[1:]:
        r = analyse(patch)
        tag = patch.split('/')[-2] if '/' in patch else patch
        for side in ('left', 'right'):
            v = r[side]
            print('%-58s %-6s %8.1f %8.1f %8.3f %8.1f'
                  % (tag[-56:], side, v['mid_median_mm'], v['mid_max_mm'],
                     v['mid_hand_mean'], v['tip_median_mm']))
    print()
    print('mid = forearm vertices t in [0.25L, 0.70L], radius < 100 mm')
    print('displacement in mm under a 30 deg wrist rotation (synthetic, not a game frame)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
