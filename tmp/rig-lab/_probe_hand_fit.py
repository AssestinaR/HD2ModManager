# -*- coding: utf-8 -*-
"""READ-ONLY: is the HAND sitting on its own bones?

Round 75c.  The rigid forearm fix moves everything distal to the elbow, which
includes the hand.  That is only acceptable if the hand is off its bones too --
if the hand were correctly placed, moving it 40 mm would pull it off the weapon.

The user's own convention is the test: finger bones must lie INSIDE the mesh
(only fingers get that guarantee).  So:

  * centroid of the hand mesh vertices  vs  centroid of the hand bone heads
  * for every hand/finger bone head, the distance to the nearest hand vertex

A hand that sits on its bones has a small centroid gap and every finger bone
head within a finger radius of the surface.  A hand shifted off its bones shows
a gap of the same size and direction as the forearm offset.

usage:
    _probe_hand_fit.py <patch_0> [--unit 0x...] [--side left]
    _probe_hand_fit.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
SLIM = 0xcae35ee15168145f

# Bones that live under *_hand but carry no hand geometry (the right hand has
# `weapon_aim`, 1.9 m away).  They are excluded from the "is the hand on its
# bones" statistics or they drown the real fingers.
HELPER_TOKENS = ('weapon', 'ik_', 'helper', 'target', 'aim')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--label', default=None)
    ap.add_argument('--keep-helpers', action='store_true',
                    help='do not drop helper bones (weapon_aim / ik_ / target ...)')
    args = ap.parse_args()

    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        u = U.Unit.read(toc, gpu)
        label = args.label or os.path.basename(args.local)
    else:
        entries, _ = P.scan_entries(args.patch)
        e = next(x for x in entries if x['type_id'] == U.UNIT_ID
                 and x['file_id'] == int(args.unit, 16))
        pl = P.entry_payload(e, args.patch)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        label = args.label or os.path.basename(args.patch)

    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    pre = 'l' if args.side == 'left' else 'r'

    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    mat_of = np.full(n, -1, np.int32)
    flat = np.asarray(u.index_list(mi.stream_index), np.int64)
    for s in mi.sections:
        sv = np.unique(flat[s.index_offset:s.index_offset + s.num_indices])
        mat_of[sv] = s.material_index
    decoded = bidx.copy()
    for material in np.unique(mat_of[mat_of >= 0]):
        which = mat_of == material
        remap = np.asarray(bi.remaps[material], np.int64)
        decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]
    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    pos = pos @ M[:3, :3] + M[3, :3]
    live = mat_of >= 0
    pos, wgt, decoded = pos[live], wgt[live], decoded[live]

    # ---- the hand = l_hand and everything below it ---------------------------
    root_ti = bi.real_indices[slot_of[pre + '_hand']]
    hand_slots = set()
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                hand_slots.add(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    w_hand = (np.isin(decoded, list(hand_slots)) * wgt).sum(axis=1)

    geo_slots, dropped = set(), []
    for s in hand_slots:
        nm = names.get(s, '')
        if not args.keep_helpers and any(t in nm for t in HELPER_TOKENS):
            dropped.append(nm)
            continue
        geo_slots.add(s)

    E = heads[slot_of[pre + '_elbow']]
    W = heads[slot_of[pre + '_hand']]
    ax = (W - E) / np.linalg.norm(W - E)
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(ax @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(ax, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, ax); V_ /= np.linalg.norm(V_)

    sel = w_hand > 0.5
    Cm = pos[sel].mean(axis=0)
    bp = np.stack([heads[s] for s in sorted(geo_slots) if s in heads])
    Cb = bp.mean(axis=0)
    d = Cm - Cb
    print()
    print('=' * 96)
    print('%s   [%s]   hand mesh verts %d, hand bones %d (helper bones dropped: %s)'
          % (label, args.side, int(sel.sum()), len(bp),
             ', '.join(dropped) if dropped else 'none'))
    print('=' * 96)
    print('  centroid gap |mesh - bones| = %6.1f mm   '
          '(along N_ %+.1f, along V_ %+.1f, along the arm %+.1f)'
          % (np.linalg.norm(d) * 1000, float(d @ N_) * 1000,
             float(d @ V_) * 1000, float(d @ ax) * 1000))
    # how far is each hand bone head from the nearest hand-mesh vertex?
    P_ = pos[sel]
    dist = []
    for s in sorted(geo_slots):
        if s not in heads:
            continue
        dd = np.linalg.norm(P_ - heads[s], axis=1).min()
        dist.append((names[s], dd))
    dist.sort(key=lambda x: -x[1])
    print('  bone head -> nearest hand vertex (mm):  mean %.1f   max %.1f'
          % (np.mean([x[1] for x in dist]) * 1000, dist[0][1] * 1000))
    print('    worst 6:  ' + '   '.join('%s %.0f' % (nm.replace(pre + '_', ''), dd * 1000)
                                        for nm, dd in dist[:6]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
