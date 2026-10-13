# -*- coding: utf-8 -*-
"""Draw a unit's triangles in UV space on top of the texture they sample.

Read-only, and the least ambiguous view available: it shows exactly which piece
of artwork every triangle reads, with no projection, lighting or occlusion to
muddy the judgement.

Both V conventions are drawn, because the face atlas packs several face
variants -- a V flip there just selects a different variant, which a render
cannot distinguish from a correct flip.  In UV space the difference is obvious.

usage: _probe_uvplot.py <patch_0> <unit_hex> <out_tag> [max_sections]
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

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4w/9ba626afa44a3aa3.patch_0'
UNIT = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0xCAE35EE15168145F
TAG = sys.argv[3] if len(sys.argv) > 3 else 'uvplot'
MAXSEC = int(sys.argv[4]) if len(sys.argv) > 4 else 99
ONLY = None
if len(sys.argv) > 5 and sys.argv[5] != '-':
    ONLY = set(int(x) for x in sys.argv[5].split(','))

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


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='uvplot')
    entries, _ = P.scan_entries(PATCH)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    mats = {e['file_id']: e for e in entries if e['type_id'] == A.MATERIAL_TYPE_ID}

    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    vs = u.decode_vertices(0)
    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in vs], np.float64)
    idx = np.asarray(u.index_list(0), np.int64)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    tiles = []
    for si, sec in enumerate(mi.sections):
        if sec.num_indices == 0 or len(tiles) >= MAXSEC:
            continue
        if ONLY is not None and si not in ONLY:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        me = mats.get(asset)
        if me is None:
            continue
        m = A.Material.read(P.entry_payload(me, PATCH)['toc'])
        tid = m.tex_ids[BASE_SLOT]
        te = tex.get(tid)
        if te is None:
            continue
        tp = P.entry_payload(te, PATCH)
        im = decode(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
        if im is None or im.width < 64:
            continue
        print('sec %2d  %6d tris  tex 0x%016x  %s'
              % (si, sec.num_indices // 3, tid, NAMES.get(tid, '?')))
        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices]
        for flip in (False, True):
            base = im.copy()
            dr = ImageDraw.Draw(base)
            W, H = base.size
            for k in range(0, len(seg), 3):
                a, b, c = seg[k], seg[k + 1], seg[k + 2]
                pts = []
                for vi in (a, b, c):
                    uu = uv[vi, 0] % 1.0
                    vv = uv[vi, 1] % 1.0
                    if flip:
                        vv = 1.0 - vv
                    pts.append((uu * W, vv * H))
                dr.polygon(pts, outline=(255, 40, 40))
            base.thumbnail((620, 620))
            tiles.append(('sec%d %s  %s'
                          % (si, NAMES.get(tid, '?'),
                             'V-FLIPPED' if flip else 'raw'),
                          base))

    CELL = 630
    cols = 2
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * CELL, rows * (CELL + 20)), (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (lab, im) in enumerate(tiles):
        r, c = divmod(k, cols)
        sheet.paste(im, (c * CELL + 4, r * (CELL + 20) + 4))
        dr.text((c * CELL + 4, r * (CELL + 20) + CELL + 2), lab,
                fill=(235, 240, 246))
    p = OUT / ('%s.png' % TAG)
    sheet.save(p)
    print('uv plot ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
