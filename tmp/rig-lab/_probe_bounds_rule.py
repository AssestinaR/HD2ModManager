# -*- coding: utf-8 -*-
"""Read-only: prove the rule for MeshInfo's seven leading floats.

Hypothesis (from `UnitMeshReader.cs:624-626`, which reads them as
`UnitMeshCullingBounds`):
    [0:3]  min   of the vertices the mesh's own sections reference
    [3:6]  max   of the same set
    [6]    max(|v|) over the same set
all expressed in the mesh's **local** space (i.e. before its TransformIndex
matrix is applied).

We test it on units whose mesh transform is the identity (Chiffon donor) *and*
on one whose transform is not (the DS-10 carrier), because that separates
"local space" from "character space".

usage: _probe_bounds_rule.py
"""
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

DONOR_PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
DONOR_UNIT = 0x9841B16544C8FBAE


def load_patch_unit(path, key):
    entries, _ = P.scan_entries(path)
    for e in entries:
        if e['file_id'] == key and e['type_id'] == U.UNIT_ID:
            pl = P.entry_payload(e, path)
            return U.Unit.read(pl['toc'], pl['gpu'])
    raise SystemExit('unit not found')


def referenced_vertices(u, mi):
    """Positions of the vertices this mesh's sections actually draw."""
    verts = u.decode_vertices(mi.stream_index)
    allpos = np.asarray([v['position'] for v in verts], np.float64)
    lo, hi = None, None
    for sec in mi.sections:
        a = sec.vertex_offset
        b = a + sec.num_vertices
        lo = a if lo is None else min(lo, a)
        hi = b if hi is None else max(hi, b)
        sub = allpos[a:b]
        print('        section mat=%d v[%d,%d) i[%d,%d) -> bbox min=%s max=%s'
              % (sec.material_index, a, b, sec.index_offset,
                 sec.index_offset + sec.num_indices,
                 np.round(sub.min(axis=0), 4), np.round(sub.max(axis=0), 4)))
    pos = allpos[lo:hi]
    return pos, lo, hi


def report(u, label):
    print('=' * 78)
    print(label)
    ti = u.transform_info
    for i, mi in enumerate(u.mesh_infos):
        stored = struct.unpack_from('<7f', mi.unk2, 0)
        pos, lo, hi = referenced_vertices(u, mi)
        mn = pos.min(axis=0)
        mx = pos.max(axis=0)
        rad = float(np.linalg.norm(pos, axis=1).max())
        T = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
        ident = np.allclose(T, np.eye(4), atol=1e-6)
        print('  mesh %d lod=%-3d tf=%-3d T=%s  section verts [%d,%d) n=%d'
              % (i, mi.lod_index, mi.transform_index, 'I' if ident else 'non-I',
                 lo, hi, hi - lo))
        print('     stored  min=%s max=%s radius=%.6f'
              % (np.round(stored[0:3], 6), np.round(stored[3:6], 6), stored[6]))
        print('     measured(local) min=%s max=%s radius=%.6f'
              % (np.round(mn, 6), np.round(mx, 6), rad))
        d = max(float(np.abs(mn - np.asarray(stored[0:3])).max()),
                float(np.abs(mx - np.asarray(stored[3:6])).max()),
                abs(rad - stored[6]))
        print('     max deviation %.6f  ->  %s'
              % (d, 'MATCH' if d < 1e-4 else 'MISMATCH'))
        if not ident:
            # what if the stored box were in character space instead?
            h = np.hstack([pos, np.ones((len(pos), 1))])
            w = (h @ T)[:, :3]
            print('     (char-space min=%s max=%s radius=%.6f)'
                  % (np.round(w.min(axis=0), 6), np.round(w.max(axis=0), 6),
                     float(np.linalg.norm(w, axis=1).max())))


def main():
    report(load_patch_unit(DONOR_PATCH, DONOR_UNIT),
           'DONOR 0x9841b16544c8fbae (mesh T = identity)')


if __name__ == '__main__':
    main()
