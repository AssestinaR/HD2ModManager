# -*- coding: utf-8 -*-
"""UV bbox per material, measured on the SOURCE model (L4D2 VVD/VTX).

Read-only.  This is the ground truth we need before blaming our writer.

If the source hair material's UVs really do occupy only a small corner of its
atlas, then our shipped unit is faithful and the fault is in the *pairing*
(texture vs mesh) -- not in the UV bytes.  If the source spans the full atlas
and our shipped unit spans 0.2 x 0.175, we broke it somewhere in the pipeline.

Prints, per source material index: name, $basetexture, vertex count, and the
UV0 bbox raw + V-flipped.
"""
import os
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_obj as E
import hd2_source as S
import hd2_style as ST

VPK = S.ADDONS + S.MODEL_VPK
MODEL = sys.argv[1] if len(sys.argv) > 1 else 'survivor_teenangst'


def main():
    packs = S.Packs()
    _p, mdl, verts, tris, _s, _sh = ST.load_source(VPK, MODEL)
    names, cds, resolved = E.read_mdl_materials(mdl)
    print('source textures: %d   cdtectures: %s' % (len(names), cds))
    print()

    uv = np.asarray([v['uv'] for v in verts], np.float64)
    print('source verts %d  tris %d   UV overall u %.4f..%.4f  v %.4f..%.4f'
          % (len(uv), len(tris), uv[:, 0].min(), uv[:, 0].max(),
             uv[:, 1].min(), uv[:, 1].max()))
    print()

    used = {}
    for a, b, c, mi in tris:
        used.setdefault(mi, set()).update((a, b, c))

    print('%-4s %-46s %7s   u0..u1        v0..v1        span        flipU span' %
          ('idx', 'material (vmt path)', 'verts'))
    for mi in sorted(used):
        idxs = np.fromiter(used[mi], np.int64)
        b = uv[idxs]
        mn, mx = b.min(axis=0), b.max(axis=0)
        nm = resolved[0][mi] if mi < len(resolved[0]) else None
        text = packs.vmt_text(nm) if nm else None
        vmt = S.parse_vmt(text) if text else {}
        base = vmt.get('$basetexture')
        short = (base or nm or '?').rsplit('\\', 1)[-1]
        tiled = '  TILED' if (mx[0] - mn[0] > 1.5 or mx[1] - mn[1] > 1.5) else ''
        print('%-4d %-46s %7d   %.4f..%.4f  %.4f..%.4f  %.3f x %.3f%s'
              % (mi, short[:46], len(idxs), mn[0], mx[0], mn[1], mx[1],
                 mx[0] - mn[0], mx[1] - mn[1], tiled))
    return 0


if __name__ == '__main__':
    sys.exit(main())
