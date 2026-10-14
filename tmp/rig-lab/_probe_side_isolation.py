# -*- coding: utf-8 -*-
"""READ-ONLY: prove a pass only touched the side it was asked to touch.

Given two patches built from the same source, decode the LOD0 mesh of both,
find the vertices whose position bytes differ, and report each changed vertex's
total skin weight on the LEFT arm subtree and on the RIGHT arm subtree.  If a
one-sided pass is correct, every changed vertex must have weight 1.0 on its own
side and 0.0 on the other.

usage:
    _probe_side_isolation.py <before> <after> [--unit 0x...]
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


def load(patch, unit):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    return U.Unit.read(pl['toc'], pl['gpu'])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('before')
    ap.add_argument('after')
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    args = ap.parse_args()

    unit = int(args.unit, 16)
    ua, ub = load(args.before, unit), load(args.after, unit)

    out = []
    for u, tag in ((ua, 'before'), (ub, 'after')):
        ti, bi = u.transform_info, u.bone_infos[0]
        names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
                 for s, i in enumerate(bi.real_indices)}
        slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
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
            mat_of[np.unique(flat[s.index_offset:s.index_offset + s.num_indices])] = s.material_index
        dec = bidx.copy()
        for material in np.unique(mat_of[mat_of >= 0]):
            which = mat_of == material
            remap = np.asarray(bi.remaps[material], np.int64)
            dec[which] = remap[np.minimum(bidx[which], len(remap) - 1)]

        def subtree(root):
            root_ti = bi.real_indices[slot_of[root]]
            out_s = []
            for slot, ti_idx in enumerate(bi.real_indices):
                seen, cur = set(), ti_idx
                while 0 <= cur < ti.count and cur not in seen:
                    if cur == root_ti:
                        out_s.append(slot)
                        break
                    seen.add(cur)
                    cur = ti.entries[cur][1]
            return out_s

        lw = (np.isin(dec, subtree('l_elbow')) * wgt).sum(axis=1)
        rw = (np.isin(dec, subtree('r_elbow')) * wgt).sum(axis=1)
        out.append(dict(pos=pos, lw=lw, rw=rw, mi=mi, u=u, ti=ti, bi=bi))

    a, b = out
    assert a['mi'].stream_index == b['mi'].stream_index
    diff = np.abs(a['pos'] - b['pos']).max(axis=1) > 0.0
    # a moved vertex shows up in both streams; compare per vertex on the first
    moved = np.nonzero(diff)[0]
    print('=' * 92)
    print('LOD0 vertices %d   moved %d' % (len(diff), int(diff.sum())))
    print('=' * 92)
    if not len(moved):
        print('  nothing moved')
        return 0
    lw, rw = a['lw'][moved], a['rw'][moved]
    print('  changed vertices: left-arm weight  min %.3f  max %.3f' % (lw.min(), lw.max()))
    print('                    right-arm weight min %.3f  max %.3f' % (rw.min(), rw.max()))
    both = ((lw > 1e-6) & (rw > 1e-6)).sum()
    neither = ((lw <= 1e-6) & (rw <= 1e-6)).sum()
    print('  changed vertices weighted on BOTH arms: %d' % int(both))
    print('  changed vertices weighted on NEITHER  : %d' % int(neither))
    print('  => %s' % ('isolated to one side' if both == 0 and neither == 0
                       else 'NOT isolated -- inspect'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
