# -*- coding: utf-8 -*-
"""Corrected uv overlay: draws a unit's triangles on the texture they sample,
with a coordinate ruler, and -- unlike the first version -- wraps each TRIANGLE
as a rigid whole instead of wrapping each VERTEX.

Why that matters: the first version applied `uv % 1` per vertex.  For a triangle
that straddles the 0/1 seam the three corners land on opposite sides and PIL
draws one long sliver across the whole texture.  Those slivers are pure drawing
artefacts and look exactly like "the overlay is in the wrong place".  Here every
triangle is translated by a single integer offset chosen so its bbox sits inside
0..1, so the shape is preserved.

usage: _probe_uvplot2.py <patch_0> <unit_hex> <out_tag> [max_sections] [only_csv]
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

NAMES = {
    0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
    0xbbd058dd84a87e8c: 'headdress_white01_catears', 0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner', 0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset', 0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels', 0xbf29404cde77bd86: 'shoes_02',
}
BASE_SLOT = 5


def decode(toc, gpu, tmp, tag):
    t = A.Texture.read(toc, gpu)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(gpu))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return Image.open(png).convert('RGB')


def ruler(dr, W, H, step=0.1):
    for k in range(1, 10):
        f = k / 10.0
        x = f * W
        y = f * H
        dr.line([x, 0, x, H], fill=(255, 210, 60), width=1)
        dr.line([0, y, W, y], fill=(255, 210, 60), width=1)
        dr.text((x + 2, 2), 'u=%.1f' % f, fill=(255, 210, 60))
        dr.text((2, y + 2), 'v=%.1f' % f, fill=(255, 210, 60))


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    patch = sys.argv[1]
    unit = int(sys.argv[2], 16)
    tag = sys.argv[3] if len(sys.argv) > 3 else 'uvplot2'
    maxsec = int(sys.argv[4]) if len(sys.argv) > 4 else 99
    only = None
    if len(sys.argv) > 5 and sys.argv[5] != '-':
        only = set(int(x) for x in sys.argv[5].split(','))

    tmp = tempfile.mkdtemp(prefix='uvplot2')
    entries, _ = P.scan_entries(patch)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    vs = u.decode_vertices(0)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    tiles = []
    for si, sec in enumerate(mi.sections):
        if sec.num_indices == 0 or len(tiles) >= maxsec:
            continue
        if only is not None and si not in only:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        me = mats.get(asset)
        if me is None:
            continue
        m = A.Material.read(P.entry_payload(me, patch)['toc'])
        tid = m.tex_ids[BASE_SLOT]
        te = tex.get(tid)
        if te is None:
            continue
        tp = P.entry_payload(te, patch)
        im = decode(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
        if im is None or im.width < 64:
            continue
        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices]
        uu = uv[seg, 0]
        vv = uv[seg, 1]
        tiled = (uu.min() < -1e-4 or uu.max() > 1 + 1e-4 or
                 vv.min() < -1e-4 or vv.max() > 1 + 1e-4)
        span = 'u %+.2f..%+.2f  v %+.2f..%+.2f' % (uu.min(), uu.max(), vv.min(), vv.max())
        print('sec %2d  %6d tris  %-30s %s  %s'
              % (si, sec.num_indices // 3, NAMES.get(tid, '?'), span,
                 'TILED' if tiled else ''))

        base = im.copy()
        dr = ImageDraw.Draw(base)
        W, H = base.size
        tri = seg.reshape(-1, 3)
        for a, b, c in tri:
            pu = uv[[a, b, c], 0]
            pv = uv[[a, b, c], 1]
            if (pu.max() - pu.min()) > 1.0 or (pv.max() - pv.min()) > 1.0:
                continue                      # spans a whole tile: no single home
            du = -np.floor(pu.min())
            dv = -np.floor(pv.min())
            pts = [((pu[k] + du) * W, (pv[k] + dv) * H) for k in range(3)]
            dr.polygon(pts, outline=(255, 40, 40))
        ruler(dr, W, H)
        base.thumbnail((620, 620))
        tiles.append(('sec%d %s  %s%s'
                      % (si, NAMES.get(tid, '?'),
                         'TILED ' if tiled else '', span), base))

    if not tiles:
        print('no tiles')
        return 0
    CELL = 630
    cols = 2
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * CELL, rows * (CELL + 20)), (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (lab, im) in enumerate(tiles):
        r, c = divmod(k, cols)
        sheet.paste(im, (c * CELL + 4, r * (CELL + 20) + 4))
        dr.text((c * CELL + 4, r * (CELL + 20) + CELL + 2), lab, fill=(235, 240, 246))
    p = OUT / ('%s.png' % tag)
    sheet.save(p)
    print('->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
