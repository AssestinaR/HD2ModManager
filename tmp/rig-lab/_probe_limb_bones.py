# -*- coding: utf-8 -*-
"""READ-ONLY: why do some hand vertices not carry the full elbow-subtree weight?

Round 75c.  A rigid move of "everything distal to the elbow" tears the hand
(t/L ~1.39, edge length x7.3), so some hand vertices must be weighted to bones
that are NOT in the elbow subtree.  This prints the bone tree of one limb and
the weight histogram over the hand.

usage:
    _probe_limb_bones.py <patch_0> [--unit 0x...] [--side left] [--tmin 1.15]
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--tmin', type=float, default=1.15)
    args = ap.parse_args()

    entries, _ = P.scan_entries(args.patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID
             and x['file_id'] == int(args.unit, 16))
    pl = P.entry_payload(e, args.patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])

    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    pre = 'l' if args.side == 'left' else 'r'

    # ---- bone tree -----------------------------------------------------------
    print('=' * 96)
    print('bones of limb %s   (slot, name, parent name, head)' % pre)
    print('=' * 96)
    for slot, ti_idx in enumerate(bi.real_indices):
        nm = names[slot]
        if not nm.startswith(pre + '_'):
            continue
        par_ti = ti.entries[ti_idx][1]
        par = 'root'
        for s2, i2 in enumerate(bi.real_indices):
            if i2 == par_ti:
                par = names[s2]
                break
        m = ti.matrices[ti_idx]
        print('  slot %3d  %-24s parent %-24s head %8.4f %8.4f %8.4f'
              % (slot, nm, par, m[12], m[13], m[14]))

    # ---- subtree of the elbow ------------------------------------------------
    root_ti = bi.real_indices[slot_of[pre + '_elbow']]
    sub = set()
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                sub.add(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    print()
    print('  elbow subtree = %d slots: %s'
          % (len(sub), ', '.join(sorted(names[s] for s in sub))))

    # ---- weights in the hand -------------------------------------------------
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

    E = np.array([ti.matrices[bi.real_indices[slot_of[pre + '_elbow']]][12:15]],
                 np.float64)[0]
    W = np.array([ti.matrices[bi.real_indices[slot_of[pre + '_hand']]][12:15]],
                 np.float64)[0]
    ax = W - E
    L = float(np.linalg.norm(ax))
    ax = ax / L
    t = (pos - E) @ ax

    limbw = (np.isin(decoded, list(sub)) * wgt).sum(axis=1)
    m = t > args.tmin * L
    print()
    print('  vertices with t/L > %.2f : %d' % (args.tmin, int(m.sum())))
    print('  limbW histogram (elbow subtree):')
    for lo in np.arange(0.0, 1.0, 0.1):
        c = int(((limbw[m] >= lo) & (limbw[m] < lo + 0.1)).sum())
        if c:
            print('    %.1f .. %.1f   %5d   %s' % (lo, lo + 0.1, c, '#' * min(c // 5, 60)))
    c = int((limbw[m] >= 0.999).sum())
    print('    1.00           %5d' % c)
    print()
    print('  dominant bone of the vertices with limbW < 0.999:')
    bad = m & (limbw < 0.999)
    if int(bad.sum()):
        dom = decoded[bad][np.arange(int(bad.sum())),
                           np.argmax(wgt[bad], axis=1)]
        for s in np.unique(dom):
            c = int((dom == s).sum())
            print('    %-24s %5d   (in subtree: %s)'
                  % (names.get(int(s), '?'), c, int(s) in sub))
    else:
        print('    none')
    return 0


if __name__ == '__main__':
    sys.exit(main())
