# -*- coding: utf-8 -*-
"""READ-ONLY: what exactly are the hand>=0.95 vertices inside the forearm ROI?

The reweight deliberately leaves them alone.  If they are a separate surface
(the palm/glove shell overlapping the forearm cylinder) that is safe; if they
are interleaved with the forearm skin, excluding them would introduce a shear
and the exclusion rule must change.

Usage: python _probe_handrigid.py [patch_0] [unit_hex]
"""
import json
import os
import sys

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
DEFAULT_PATCH = 'E:/tmp/rig-lab/cand-m1/9ba626afa44a3aa3.patch_0'
DEFAULT_UNIT = 0xcae35ee15168145f
MANIFEST = 'E:/mod/Lunalice-Native-RE2310/lunalice-native-re2310.manifest.json'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PATCH
    unit_id = int(sys.argv[2], 16) if len(sys.argv) > 2 else DEFAULT_UNIT
    manifest = json.load(open(MANIFEST, encoding='utf-8'))
    by_id = {int(m['id'], 16): m['name'].split('\\')[-1] for m in manifest['materials']}

    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit_id)
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
    s = mi.stream_index
    verts = u.decode_vertices(s)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[s], np.uint32).astype(np.int64).reshape(-1, 3)
    vmat = np.full(len(pos), '', object)
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        asset = u.material_ids[u.section_ids.index(mi.material_ids[sec.material_index])]
        nm = by_id.get(asset, '?')
        for t3 in idx[sec.index_offset // 3:(sec.index_offset + sec.num_indices) // 3]:
            vmat[t3] = nm
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
    g = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])),
                   shape=(len(pos), len(pos)))
    _, labels = connected_components(g, directed=False)

    for side, el, hd in (('left', 'l_elbow', 'l_hand'), ('right', 'r_elbow', 'r_hand')):
        h_s = slot_of[hd]
        wh = np.zeros(len(pos))
        for j in range(4):
            m = bidx[:, j] == h_s
            wh[m] += wgt[m, j]
        E, W = heads[slot_of[el]], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        near = (rad < 0.10) & (t > 0) & (t < L)
        rigid = near & (wh >= 0.95)
        print()
        print('=' * 96)
        print('%s: %d of %d near-axis vertices are already hand>=0.95'
              % (side, int(rigid.sum()), int(near.sum())))
        sel = np.nonzero(rigid)[0]
        rows = {}
        for i in sel:
            rows.setdefault(int(labels[i]), []).append(i)
        print('  %-8s %-7s %-16s %-12s %-14s %s'
              % ('comp', 'n', 't mm', 'radius mm', 'materials', 'component size'))
        for comp, ids in sorted(rows.items(), key=lambda kv: -len(kv[1])):
            ids = np.asarray(ids)
            mats = {vmat[i] for i in ids}
            print('  %-8d %-7d %6.0f..%-8.0f %5.1f..%-7.1f %-14s %d'
                  % (comp, len(ids), t[ids].min() * 1000, t[ids].max() * 1000,
                     rad[ids].min() * 1000, rad[ids].max() * 1000,
                     ','.join(sorted(mats))[:14], int((labels == comp).sum())))
        # do the excluded vertices sit at a different radius than the skin there?
        band = near & (t > 0.70 * L) & (t < 0.95 * L)
        for label, m in (('hand>=0.95', band & (wh >= 0.95)),
                         ('hand<0.95 ', band & (wh < 0.95))):
            if m.sum() == 0:
                continue
            print('  %s in t 0.70..0.95L: n=%d  radius %.1f..%.1f mm (mean %.1f)'
                  % (label, int(m.sum()), rad[m].min() * 1000, rad[m].max() * 1000,
                     rad[m].mean() * 1000))
    return 0


if __name__ == '__main__':
    sys.exit(main())
