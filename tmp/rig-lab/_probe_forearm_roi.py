# -*- coding: utf-8 -*-
"""READ-ONLY: identify the forearm shells and what each one *is*.

The round-60 review showed the earlier forearm numbers mixed different shells
(skin, sleeve, cuff, ornaments) because the selection was a bare cylinder test.
This probe keeps the same fixed geometry selection but splits it by connected
component AND by the material slot its triangles use, so each row is a named
piece of the model rather than an anonymous blob.

Usage:
    python _probe_forearm_roi.py [patch_0] [unit_hex]
"""
import json
import os
import sys

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

import hd2_asset as A
import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
DEFAULT_PATCH = 'E:/mod/Lunalice-RE2310-M1/9ba626afa44a3aa3.patch_0'
DEFAULT_UNIT = 0xcae35ee15168145f
MANIFEST = 'E:/mod/Lunalice-Native-RE2310/lunalice-native-re2310.manifest.json'

BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))


def load_unit(patch, unit_id):
    entries, _ = P.scan_entries(patch)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == unit_id:
            pl = P.entry_payload(e, patch)
            return U.Unit.read(pl['toc'], pl['gpu']), entries
    raise LookupError('unit 0x%016x not in %s' % (unit_id, patch))


def material_names(patch, entries, u, manifest):
    """material asset id -> a readable name from the build manifest."""
    by_id = {int(m['id'], 16): m['name'] for m in manifest['materials']}
    return {asset: by_id.get(asset, '0x%016x' % asset) for asset in u.material_ids}


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PATCH
    unit_id = int(sys.argv[2], 16) if len(sys.argv) > 2 else DEFAULT_UNIT
    manifest = json.load(open(MANIFEST, encoding='utf-8'))

    u, entries = load_unit(patch, unit_id)
    ti = u.transform_info
    bi = u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    matname = material_names(patch, entries, u, manifest)

    # pick the richest visible LOD (lod0)
    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    stream = mi.stream_index
    print('unit 0x%016x  lod0 mesh: stream %d  %d sections  %d materials'
          % (unit_id, stream, mi.num_sections, len(mi.material_ids)))

    verts = u.decode_vertices(stream)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[stream], np.uint32).astype(np.int64).reshape(-1, 3)

    # triangle -> material asset id (via the section table the game reads).
    # asset ids are 64-bit and do not fit int64, so store a small index.
    asset_order = []
    asset_index = {}
    tri_mat = np.full(len(idx), -1, np.int64)
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        slot_id = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(slot_id)]
        if asset not in asset_index:
            asset_index[asset] = len(asset_order)
            asset_order.append(asset)
        a0 = sec.index_offset // 3
        a1 = (sec.index_offset + sec.num_indices) // 3
        tri_mat[a0:a1] = asset_index[asset]
    assert (tri_mat >= 0).all(), 'some triangles carry no material'

    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
    graph = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])),
                       shape=(len(pos), len(pos)))
    ncomp, labels = connected_components(graph, directed=False)

    for side, el, hd, sh in (('left', 'l_elbow', 'l_hand', 'l_shoulder'),
                             ('right', 'r_elbow', 'r_hand', 'r_shoulder')):
        e, h, s = slot_of[el], slot_of[hd], slot_of[sh]
        E, Hh = heads[e], heads[h]
        axis = Hh - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        near = (rad < 0.10) & (t > 0) & (t < L)

        print()
        print('=' * 108)
        print('%s forearm   axis %s -> %s   length %.4f m   near-axis vertices %d'
              % (side, el, hd, L, int(near.sum())))
        rows = []
        for comp in np.unique(labels[near]):
            sel = np.nonzero(near & (labels == comp))[0]
            if len(sel) < 10:
                continue
            tri_sel = (labels[idx[:, 0]] == comp) & near[idx[:, 0]] | \
                      (labels[idx[:, 1]] == comp) & near[idx[:, 1]] | \
                      (labels[idx[:, 2]] == comp) & near[idx[:, 2]]
            mats, cnt = np.unique(tri_mat[tri_sel], return_counts=True)
            top = ', '.join('%s:%d' % (matname.get(asset_order[m], '?').split('\\')[-1], c)
                            for m, c in sorted(zip(mats, cnt), key=lambda x: -x[1])[:3])
            rows.append(dict(
                comp=int(comp), whole=int((labels == comp).sum()), n=len(sel),
                t0=float(t[sel].min() * 1000), t1=float(t[sel].max() * 1000),
                rad=float(rad[sel].mean() * 1000),
                hand=float(wgt[sel, :][bidx[sel, :] == h].sum() / len(sel)),
                elbow=float(wgt[sel, :][bidx[sel, :] == e].sum() / len(sel)),
                shoulder=float(wgt[sel, :][bidx[sel, :] == s].sum() / len(sel)),
                mats=top))
        rows.sort(key=lambda r: -r['n'])
        print('  %-6s %-7s %-8s %-9s %-9s %-8s %-8s %-8s  %s'
              % ('comp', 'size', 'near', 't0..t1 mm', 'rad mm',
                 'hand', 'elbow', 'shoulder', 'materials'))
        for r in rows:
            print('  %-6d %-7d %-8d %5.0f..%-5.0f %-9.1f %-8.3f %-8.3f %-8.3f  %s'
                  % (r['comp'], r['whole'], r['n'], r['t0'], r['t1'], r['rad'],
                     r['hand'], r['elbow'], r['shoulder'], r['mats']))
        print('  components (>=10 near vertices): %d' % len(rows))
    return 0


if __name__ == '__main__':
    sys.exit(main())
