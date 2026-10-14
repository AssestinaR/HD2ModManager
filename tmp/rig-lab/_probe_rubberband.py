# -*- coding: utf-8 -*-
"""READ-ONLY: rotation-amplitude asymmetry and residual rubber-band metrics.

Two independent reasons why the forearm is the only limb that feels elastic:

  R1  DOF asymmetry.  The elbow is a hinge (one axis, limited range); the wrist
      is a universal joint (flex/extend + radial/ulnar deviation) plus the whole
      hand subtree can pronate/supinate with the forearm.  A vertex carrying
      distal weight therefore feels a MUCH larger transform at the wrist than at
      the elbow.

  R2  Partner-subtree asymmetry.  Under the elbow sits 1 primary bone (l_hand is
      a *descendant* of l_elbow too, but the game drives the whole arm chain);
      under the wrist sits the whole hand: 17 bones, 14 of them finger joints
      that animate independently.

This probe quantifies the *residual* rubber-band effect that survives the
round-69 reweight: for each limb, the worst mesh-edge stretch when only the
DISTAL subtree moves.

Usage: python _probe_rubberband.py <patch_0> [...] [--deg 30]
"""
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = '.'
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
UNIT = 0xcae35ee15168145f

SEGMENTS = [
    ('upperarm', 'l_shoulder', 'l_elbow'),
    ('forearm ', 'l_elbow', 'l_hand'),
    ('shin    ', 'l_knee', 'l_foot'),
]


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
    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64).reshape(-1, 3)
    return ti, bi, names, heads, slot_of, pos, bidx, wgt, idx


def subtree_slots(ti, bi, root_slot):
    root_ti = bi.real_indices[root_slot]
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


def main():
    argv = sys.argv[1:]
    deg = 30.0
    if '--deg' in argv:
        k = argv.index('--deg')
        deg = float(argv[k + 1])
        argv = argv[:k] + argv[k + 2:]

    for patch in argv:
        ti, bi, names, heads, slot_of, pos, bidx, wgt, idx = load(patch)
        edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
        print()
        print('=' * 104)
        print('patch %s   --  distal subtree rotated %.0f deg about the segment axis'
              % (patch, deg))
        print('=' * 104)
        print('  %-10s %-9s %-7s %-11s %-11s %-11s %-10s'
              % ('segment', 'len mm', 'sub n', 'med disp mm', 'p99 disp', 'MAX edge',
                 'edge mid t/L'))
        for seg, prox, dist in SEGMENTS:
            pn, dn = 'l_' + prox[2:], 'l_' + dist[2:]
            if pn not in slot_of or dn not in slot_of:
                continue
            sub = subtree_slots(ti, bi, slot_of[dn])
            wd = np.zeros(len(pos))
            for j in range(4):
                m = np.isin(bidx[:, j], np.asarray(sub))
                wd[m] += wgt[m, j]
            Pp, Dd = heads[slot_of[pn]], heads[slot_of[dn]]
            axis = Dd - Pp
            L = float(np.linalg.norm(axis))
            axis /= L
            rel = pos - Pp
            t = rel @ axis
            rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
            band = (rad < 0.095) & (t > 0.05 * L) & (t < 1.05 * L)

            R = rodrigues(axis, np.deg2rad(deg))
            d = wd[:, None] * (((pos - Dd) @ R.T) - (pos - Dd))
            mag = np.linalg.norm(d, axis=1)
            both = band[edges[:, 0]] & band[edges[:, 1]]
            if not both.any():
                print('  %-10s (no edges in band)' % seg)
                continue
            jump = np.linalg.norm(d[edges[both, 0]] - d[edges[both, 1]], axis=1) * 1000
            e_b = edges[both]
            kmax = int(np.argmax(jump))
            i, j = e_b[kmax]
            print('  %-10s %-9.1f %-7d %-11.2f %-11.2f %-11.1f %-10.2f'
                  % (seg, L * 1000, len(sub), float(np.median(mag[band]) * 1000),
                     float(np.percentile(mag[band], 99) * 1000),
                     float(jump.max()), 0.5 * (t[i] + t[j]) / L))
    return 0


if __name__ == '__main__':
    sys.exit(main())
