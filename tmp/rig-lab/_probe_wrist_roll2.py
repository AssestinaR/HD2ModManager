# -*- coding: utf-8 -*-
"""READ-ONLY: which bone should carry the distal forearm?

Round 68.  The measured defect: round-67 narrowed the hand-weight band to
0.78L..0.92L, which is SHORTER than a 33 mm mesh edge that crosses it, so the
whole 0.945 weight change lands on one edge -> 83 mm tear at a 180 deg roll
(round-66's wide band only produced a 41 mm *geometric* jump, weights equal).

The rig offers l_hand_twist / r_hand_twist: leaf bones co-located with the hand,
children of the elbow, currently carrying ZERO weight.  If the game drives them,
the forearm roll can live there -- which decouples roll from bend.

This probe reports, for every candidate patch:
  * the hand-weight and twist-weight profiles along the forearm,
  * the largest displacement jump across a mesh edge for a roll of l_hand,
  * the same for a roll of l_hand_twist.

Usage: python _probe_wrist_roll2.py <patch_0> [...] [--angle 180]
"""
import sys

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

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

    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64)
    idx = idx.reshape(-1, 3)
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
    graph = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])),
                       shape=(len(pos), len(pos)))
    _, labels = connected_components(graph, directed=False)
    return ti, bi, names, heads, slot_of, pos, bidx, wgt, edges, labels


def subtree_of(ti, bi, slot_of, name):
    root_ti = bi.real_indices[slot_of[name]]
    out = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                out.append(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    return out


def mass_of(bidx, wgt, slots):
    m = np.zeros(len(bidx))
    for j in range(4):
        hit = np.isin(bidx[:, j], slots)
        m[hit] += wgt[hit, j]
    return m


def analyse(patch, angle_deg):
    ti, bi, names, heads, slot_of, pos, bidx, wgt, edges, labels = load(patch)
    print('=' * 78)
    print('patch %s   roll %.0f deg' % (patch, angle_deg))
    for side, el, hd, tw in SIDES:
        wh = mass_of(bidx, wgt, subtree_of(ti, bi, slot_of, hd))
        wt = mass_of(bidx, wgt, [slot_of[tw]]) if tw in slot_of else np.zeros(len(pos))
        E, W = heads[slot_of[el]], heads[slot_of[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        tn = t / L

        print('  [%s] L=%.1f mm   mass on hand=%.1f  on twist=%.1f'
              % (side, L * 1000, wh.sum(), wt.sum()))
        band = (rad < 0.12) & (t > 0.75 * L) & (t < 1.15 * L)
        print('     t/L   n     hand-w   twist-w')
        eb = np.linspace(0.0, 1.15, 24)
        for k in range(len(eb) - 1):
            sel = (rad < 0.10) & (t > 0) & (t < L) & (tn >= eb[k]) & (tn < eb[k + 1])
            if sel.sum() == 0:
                continue
            print('     %4.2f  %5d   %6.3f   %6.3f'
                  % (0.5 * (eb[k] + eb[k + 1]), int(sel.sum()),
                     float(wh[sel].mean()), float(wt[sel].mean())))

        a = np.deg2rad(angle_deg)
        R = rodrigues(axis, a)
        bend_axis = np.cross(axis, [0.0, 0.0, 1.0])
        bend_axis /= np.linalg.norm(bend_axis)
        Rb = rodrigues(bend_axis, np.deg2rad(45.0))
        for tag, w, RR in (('roll l_hand', wh, R),
                           ('roll l_hand_twist', wt, R),
                           ('roll BOTH', np.clip(wh + wt, 0, 1), R),
                           ('bend l_hand 45d', wh, Rb)):
            if w.max() <= 0.0:
                print('     %-18s : no weight, nothing moves' % tag)
                continue
            d = w[:, None] * (((pos - W) @ RR.T) - (pos - W))
            mag = np.linalg.norm(d, axis=1)
            both = band[edges[:, 0]] & band[edges[:, 1]]
            jump = np.linalg.norm(d[edges[both, 0]] - d[edges[both, 1]], axis=1) * 1000
            print('     %-18s : band |disp| med %6.1f max %6.1f mm | '
                  'edge JUMP med %5.2f p99 %6.2f MAX %6.1f mm'
                  % (tag, float(np.median(mag[band]) * 1000),
                     float(mag[band].max() * 1000), float(np.median(jump)),
                     float(np.percentile(jump, 99)), float(jump.max())))
    return 0


def main():
    argv = sys.argv[1:]
    angle = 180.0
    if '--angle' in argv:
        k = argv.index('--angle')
        angle = float(argv[k + 1])
        argv = argv[:k] + argv[k + 2:]
    for patch in argv:
        analyse(patch, angle)
    return 0


if __name__ == '__main__':
    sys.exit(main())
