# -*- coding: utf-8 -*-
"""Two checks on _probe_uvplot.py's geometry, so the red overlay can be trusted.

1. Synthetic self-test: draw one triangle with a known uv into a blank image and
   report the pixel bbox it lands in.  Verifies the uv -> pixel mapping.
2. Per section, count triangles whose uv extent exceeds one unit, or that cross
   the 0/1 seam.  Those are the ones the plot's `uv % 1` wrap draws as long
   stretched slivers -- a genuine drawing artefact, not a data problem.

usage: _probe_uvplot_check.py <patch_0> <unit_hex>
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

NAMES = {
    0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
    0xbbd058dd84a87e8c: 'headdress_white01_catears', 0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner', 0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset', 0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels', 0xbf29404cde77bd86: 'shoes_02',
}


def selftest():
    from PIL import Image, ImageDraw
    W = H = 512
    im = Image.new('RGB', (W, H), (0, 0, 0))
    dr = ImageDraw.Draw(im)
    # uv (0.25,0.25) (0.75,0.25) (0.75,0.75)  -> expect x 128..384, y 128..384
    tri = [(0.25, 0.25), (0.75, 0.25), (0.75, 0.75)]
    pts = [(u * W, v * H) for u, v in tri]
    dr.polygon(pts, outline=(255, 40, 40))
    a = np.asarray(im)[:, :, 0]
    ys, xs = np.nonzero(a > 100)
    print('SELFTEST  expected x 128..384  y 128..384')
    print('SELFTEST  drawn    x %d..%d  y %d..%d  %s'
          % (xs.min(), xs.max(), ys.min(), ys.max(),
             'OK' if (xs.min() == 128 and xs.max() == 384 and ys.min() == 128 and ys.max() == 384)
             else 'MISMATCH'))
    print('SELFTEST  origin: uv(0,0) -> pixel(0,0) = %s ; uv(1,1) -> pixel(512,512) = %s'
          % ('top-left', 'bottom-right'))


def main():
    selftest()
    patch = sys.argv[1]
    unit = int(sys.argv[2], 16)
    entries, _ = P.scan_entries(patch)
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    vs = u.decode_vertices(0)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    print()
    print('%-26s %7s %7s %7s %7s' % ('section', 'tris', 'span>1u', 'seamX', 'worstspan'))
    for si, sec in enumerate(mi.sections):
        if sec.num_indices == 0:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        me = mats.get(asset)
        nm = '?'
        if me is not None:
            m = A.Material.read(P.entry_payload(me, patch)['toc'])
            tid = m.tex_ids[5] if len(m.tex_ids) > 5 else 0
            nm = NAMES.get(tid, 'tex0x%016x' % tid)
        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices].reshape(-1, 3)
        if len(seg) == 0:
            continue
        U3 = uv[seg, 0]
        V3 = uv[seg, 1]
        su = U3.max(axis=1) - U3.min(axis=1)
        sv = V3.max(axis=1) - V3.min(axis=1)
        big = ((su > 1.0) | (sv > 1.0)).sum()
        # seam crossing: floor() of the three corners is not all equal
        fl = np.floor(U3)
        seam = ((fl.max(axis=1) - fl.min(axis=1)) > 0).sum()
        print('%-4s %-21s %7d %7d %7d %7.3f'
              % ('sec%d' % si, nm, len(seg), big, seam, max(su.max(), sv.max())))
    return 0


if __name__ == '__main__':
    sys.exit(main())
