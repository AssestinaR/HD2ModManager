# -*- coding: utf-8 -*-
"""READ-ONLY: worst mesh-edge JUMP over the WHOLE forearm under a 180 deg roll.

_probe_wrist_roll.py only looks at t > 0.75L, so it is blind to a tear in the
middle of the forearm -- which is exactly where round-66's problem lives.

This probe simulates a pure roll of the hand subtree about the elbow->hand axis
and reports the largest edge jump over every near-axis vertex with
0.05L < t < 1.05L, plus a coarse map of where that jump is.

Usage: python _probe_roll_whole.py <patch_0> [...] [--angle 180] [--twist]
"""
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
UNIT = 0xcae35ee15168145f
SIDES = (('left', 'l_elbow', 'l_hand', 'l_hand_twist'),
         ('right', 'r_elbow', 'r_hand', 'r_hand_twist'))


def rodrigues(axis, a):
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K


def analyse(patch, angle_deg, count_twist):
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

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64)
    idx = idx.reshape(-1, 3)
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])

    print('=' * 88)
    print('patch %s   (roll %.0f deg)%s' % (patch, angle_deg,
                                            '  [twist counts]' if count_twist else ''))
    for side, el, hd, tw in SIDES:
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
        if count_twist and tw in slot_of:
            for j in range(4):
                m = bidx[:, j] == slot_of[tw]
                wh[m] += wgt[m, j]

        E, W = heads[slot_of[el]], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        band = (rad < 0.10) & (t > 0.05 * L) & (t < 1.05 * L)

        R = rodrigues(axis, np.deg2rad(angle_deg))
        d = wh[:, None] * (((pos - W) @ R.T) - (pos - W))
        both = band[edges[:, 0]] & band[edges[:, 1]]
        if not both.any():
            print('  [%s] no edges in band' % side)
            continue
        jump = np.linalg.norm(d[edges[both, 0]] - d[edges[both, 1]], axis=1) * 1000
        e_b = edges[both]
        print('  [%-5s] L=%.1f mm  band verts %d  edges %d  '
              'jump med %.2f  p99 %.2f  MAX %.1f mm'
              % (side, L * 1000, int(band.sum()), len(jump),
                 float(np.median(jump)), float(np.percentile(jump, 99)),
                 float(jump.max())))
        # where are the worst jumps along t/L?
        order = np.argsort(jump)[::-1]
        worst = e_b[order[:200]]
        tn = (t / L)
        loc = tn[np.concatenate([worst[:, 0], worst[:, 1]])]
        hist, _ = np.histogram(loc, bins=np.linspace(0.0, 1.1, 12))
        print('        top-200 worst edges by t/L: %s'
              % ' '.join('%d' % c for c in hist))
        for k in order[:4]:
            i, j = e_b[k, 0], e_b[k, 1]
            print('        t/L %5.2f/%5.2f  w %.3f/%.3f  rad %.0f/%.0f -> %.1f mm'
                  % (tn[i], tn[j], wh[i], wh[j], rad[i] * 1000, rad[j] * 1000, jump[k]))
    return 0


def main():
    argv = sys.argv[1:]
    angle = 180.0
    count_twist = False
    if '--twist' in argv:
        argv.remove('--twist')
        count_twist = True
    if '--angle' in argv:
        k = argv.index('--angle')
        angle = float(argv[k + 1])
        argv = argv[:k] + argv[k + 2:]
    for patch in argv:
        analyse(patch, angle, count_twist)
    return 0


if __name__ == '__main__':
    sys.exit(main())
