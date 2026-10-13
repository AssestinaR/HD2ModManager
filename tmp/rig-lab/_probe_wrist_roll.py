# -*- coding: utf-8 -*-
"""READ-ONLY: locate the wrist seam and characterise the weight profile there.

Round 68.  The user reports a hard disconnect between the forearm and the hand
whenever the arm AND wrist move, worst case "the wrist turned ~180 deg while the
forearm did not".  Round-67 made the forearm rigid to l_elbow; the only way that
produces a *gap* rather than a smooth crease is if the transition band carries no
geometry, or if the round-67 "rigid component" rule gave a wrist-spanning piece a
single uniform ratio.

This probe
  1. prints the hand-weight profile along the forearm (binned by t/L),
  2. lists every connected component that touches the wrist band, with its
     t-span, its size, and its mean hand weight (i.e. which rule it hit),
  3. simulates a pure ROLL of the hand subtree about the forearm axis and reports
     the largest displacement JUMP across a mesh edge -- the actual seam size.

Usage: python _probe_wrist_roll.py <patch_0> [<patch_0> ...] [--angle 180]
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
SIDES = (('left', 'l_elbow', 'l_hand'), ('right', 'r_elbow', 'r_hand'))


def rodrigues(axis, a):
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K


def analyse(patch, angle_deg):
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
    nv = len(pos)

    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64)
    idx = idx.reshape(-1, 3)
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
    graph = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])),
                       shape=(nv, nv))
    _, labels = connected_components(graph, directed=False)

    print('=' * 78)
    print('patch %s   (roll %.0f deg)' % (patch, angle_deg))
    print('mesh stream %d, %d verts, %d components'
          % (mi.stream_index, nv, labels.max() + 1))

    out = {}
    for side, el, hd in SIDES:
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
        wh = np.zeros(nv)
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
        tn = t / L

        near = (rad < 0.10) & (t > 0.0) & (t < L)

        # ---- 1. hand-weight profile along the forearm
        print('  [%s] L=%.1f mm' % (side, L * 1000))
        print('     t/L   n    hand-w  (rad<100mm, t in [0,L])')
        edges_b = np.linspace(0.0, 1.2, 25)
        for k in range(len(edges_b) - 1):
            sel = near & (tn >= edges_b[k]) & (tn < edges_b[k + 1])
            if sel.sum() == 0:
                continue
            print('     %4.2f  %5d  %6.3f'
                  % (0.5 * (edges_b[k] + edges_b[k + 1]), int(sel.sum()),
                     float(wh[sel].mean())))

        # ---- 2. components touching the wrist band
        band = (rad < 0.12) & (t > 0.75 * L) & (t < 1.15 * L)
        comps = np.unique(labels[band])
        print('     components touching t in [0.75L,1.15L], rad<120mm:')
        for c in comps:
            sel = np.nonzero(labels == c)[0]
            selb = np.nonzero((labels == c) & band)[0]
            if len(selb) == 0:
                continue
            span = float(t[selb].max() - t[selb].min()) * 1000.0
            full = float(t[sel].max() - t[sel].min()) * 1000.0
            print('       comp %4d  band-verts %4d  comp-verts %6d  '
                  'band t-span %7.1f mm  full t-span %8.1f mm  hand-w mean %.3f'
                  % (c, len(selb), len(sel), span, full, float(wh[selb].mean())))

        # ---- 3. seam size under a pure roll
        a = np.deg2rad(angle_deg)
        R = rodrigues(axis, a)
        d = wh[:, None] * (((pos - W) @ R.T) - (pos - W))
        mag = np.linalg.norm(d, axis=1)

        wi = band
        print('     wrist band: %d verts, hand-w mean %.3f, |disp| med %.1f max %.1f mm'
              % (int(wi.sum()), float(wh[wi].mean()),
                 float(np.median(mag[wi]) * 1000), float(mag[wi].max() * 1000)))
        both = wi[edges[:, 0]] & wi[edges[:, 1]]
        if both.sum():
            jump = np.linalg.norm(d[edges[both, 0]] - d[edges[both, 1]], axis=1) * 1000
            print('     edge JUMP inside band: n=%d  med %.2f  p99 %.2f  MAX %.1f mm'
                  % (int(both.sum()), float(np.median(jump)),
                     float(np.percentile(jump, 99)), float(jump.max())))
            order = np.argsort(jump)[::-1][:6]
            e_b = edges[both]
            print('     worst edges (t/L, hand-w, comp, jump mm):')
            for k in order:
                i, j = e_b[k, 0], e_b[k, 1]
                print('       %6.3f/%6.3f  w %.3f/%.3f  comp %5d/%5d  rad %.0f/%.0f mm  -> %.1f'
                      % (tn[i], tn[j], wh[i], wh[j], labels[i], labels[j],
                         rad[i] * 1000, rad[j] * 1000, jump[k]))
            out[side] = float(jump.max())
        else:
            out[side] = None
    return out


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
