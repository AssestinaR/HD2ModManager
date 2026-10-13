# -*- coding: utf-8 -*-
"""UV range per material, straight out of the shipped unit.

Read-only.  The user reports the hair looks like "a portion of the texture, as
if it was not scaled properly and too much area was applied".  That is a
statement about UV coverage, so measure it instead of arguing about it: for
every material section, collect the vertices its index window actually draws and
report the bounding box of their UV0.

A hair card atlas is used correctly when its pieces sit in *different* small
boxes spread over the atlas.  A whole-atlas stretch, or a single tiny box, both
show up immediately here.
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4/9ba626afa44a3aa3.patch_0'
UNIT = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0xCAE35EE15168145F

NAMES = {
    0xb9f84331454d8282: 'shinano_face',
    0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4',
    0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body',
    0xb66b54bafd3dd768: 'shinano_hair_ear <== HAIR',
    0xbbd058dd84a87e8c: 'headdress_white01_catears',
    0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner',
    0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset',
    0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels',
    0xbf29404cde77bd86: 'shoes_02',
}


def main():
    entries, _ = P.scan_entries(PATCH)
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    tex = {e['file_id'] for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}

    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('unit 0x%016x  %d streams / %d meshes'
          % (UNIT, len(u.stream_infos), len(u.mesh_infos)))

    # vertex uv0 per model stream (lod0 = stream 0)
    si = 0
    verts = u.decode_vertices(si)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in verts], np.float64)
    print('stream %d: %d vertices; UV0 overall min %s max %s'
          % (si, len(uv), np.round(uv.min(axis=0), 4), np.round(uv.max(axis=0), 4)))

    # index list for the same stream
    idx = np.asarray(u.index_list(si), np.int64)
    print('indices: %d' % len(idx))

    mi = next(m for m in u.mesh_infos if m.lod_index == 0)
    print('lod0 mesh: %d sections' % len(mi.sections))
    print()
    print('%-30s %8s %8s   UV bbox (min .. max)          span' % ('material', 'tris', 'verts'))
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)] if sid in u.section_ids else None
        name = NAMES.get(asset, '0x%016x' % (asset or 0))
        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices]
        used = np.unique(seg)
        b = uv[used]
        mn, mx = b.min(axis=0), b.max(axis=0)
        print('%-30s %8d %8d   u %.4f..%.4f  v %.4f..%.4f   %.3f x %.3f'
              % (name, sec.num_indices // 3, len(used),
                 mn[0], mx[0], mn[1], mx[1], mx[0] - mn[0], mx[1] - mn[1]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
