"""Is the triangle winding of the source model consistent?

A 168 deg median "hardness" (angle between a vertex's incident face normals)
would mean half the model is inside out -- which matters a lot, because HD2
back-face culls.  Find out whether that number is real or an artefact.
"""
import os
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import hd2_style as H

MODEL = 'survivor_teenangst'


def main():
    d = np.load('out/blender/model.npz')
    mdl_path, mdl, verts, tris, src, short = H.load_source(
        S.ADDONS + S.MODEL_VPK, MODEL)
    n_src = np.asarray([v['nrm'] for v in verts], np.float64)
    n_src /= np.linalg.norm(n_src, axis=1, keepdims=True)
    fin = d['ours_bind'].astype(np.float64)
    T = d['tris'].astype(np.int64)

    v0, v1, v2 = fin[T[:, 0]], fin[T[:, 1]], fin[T[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-30)

    # 1) does each face agree with the mean SOURCE normal of its 3 corners?
    mn = (n_src[T[:, 0]] + n_src[T[:, 1]] + n_src[T[:, 2]]) / 3.0
    dot = (fn * mn).sum(1)
    print('face normal vs mean source normal of its corners:')
    print('   agree (dot>0)  : %d (%.1f%%)' % ((dot > 0).sum(), 100 * (dot > 0).mean()))
    print('   disagree       : %d (%.1f%%)' % ((dot <= 0).sum(), 100 * (dot <= 0).mean()))
    print('   dot percentiles 5/25/50/75/95: %s'
          % np.round(np.percentile(dot, [5, 25, 50, 75, 95]), 4))

    # 2) a vertex whose incident faces really do span 180 deg would have a
    #    "front" face and a "back" face -- is that limited to thin shells?
    nv = len(fin)
    mx = np.full(nv, -2.0)
    for k in range(3):
        np.maximum.at(mx, T[:, k], (fn * n_src[T[:, k]]).sum(1))
    ang = np.degrees(np.arccos(np.clip(mx, -1, 1)))
    hist = Counter((ang // 20).astype(int))
    print('per-vertex WORST disagreement with its own source normal:')
    for b in sorted(hist):
        print('   %3d-%3d deg : %7d' % (b * 20, b * 20 + 20, hist[b]))

    # 3) degenerate / duplicate triangles
    ar = np.linalg.norm(np.cross(v1 - v0, v2 - v0), axis=1) * 0.5
    print('degenerate tris (area < 1e-12): %d ; zero-area frac %.5f%%'
          % ((ar < 1e-12).sum(), 100 * (ar < 1e-12).mean()))
    print('total surface area %.4f m^2' % ar.sum())
    print('area percentiles 50/90/99/max: %s'
          % np.round(np.percentile(ar, [50, 90, 99, 100]), 8))

    # 4) material index spread (material index lives in column 3 of the source)
    tr = np.asarray(tris)
    print('material column distinct: %d' % len(set(tr[:, 3].tolist())))

    # 5) is the material's own VMT $nocull set?  Those are the double sided ones
    names, cds, skin = S.load_material_slots(mdl)
    p = S.Packs()
    nocl = 0
    for mi in set(tr[:, 3].tolist()):
        txt = p.vmt_text(names[mi])
        dd = S.parse_vmt(txt) if txt else {}
        if str(dd.get('$nocull', '0')).strip() not in ('0', 'None', ''):
            nocl += 1
    print('materials with $nocull 1: %d / %d' % (nocl, len(set(tr[:, 3].tolist()))))


if __name__ == '__main__':
    main()
