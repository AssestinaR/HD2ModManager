# -*- coding: utf-8 -*-
"""Zoom the hair UV island: what does the sampled box actually contain?

Read-only.  `_probe_sourceuv.py` proved the shipped UVs equal the source UVs
byte for byte, so the hair's 0.200 x 0.175 island is authored, not damage.
That leaves two live hypotheses:

  H1  the box contains the right artwork, we simply magnify it  (expected, fine)
  H2  the box lands on the wrong corner because V was not flipped

So decode BOTH the shipped texture and the original source VTF, and crop the
island plus its V-flipped twin, at full resolution.  Whichever crop shows hair
strands is the correct orientation.

PIL gotcha: alpha=0 + thumbnail() premultiplies and the crop looks black.
Everything here is converted to RGB *before* any resize.
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
import hd2_source as S
import hd2_texpack as TP
import hd2_vtf as V

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4/9ba626afa44a3aa3.patch_0'

# our shipped hair texture (from fix_materials manifest chain)
OUR_HAIR_TEX = 0xB66B54BAFD3DD768
BOX = (0.7969, 0.9970, 0.0071, 0.1821)     # u0,u1,v0,v1 (source + shipped agree)


def dds_png(hdr, px, tmp, tag):
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(hdr) + bytes(px))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return Image.open(png).convert('RGB')      # RGB BEFORE resize


def crop(im, box, flip_v):
    W, H = im.size
    u0, u1, v0, v1 = box
    if flip_v:
        v0, v1 = 1.0 - v1, 1.0 - v0
    return im.crop((int(u0 * W), int(v0 * H), int(u1 * W), int(v1 * H)))


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='hairzoom')

    # --- ours (shipped) ------------------------------------------------------
    entries, _ = P.scan_entries(PATCH)
    e = next(x for x in entries
             if x['type_id'] == A.TEXTURE_TYPE_ID and x['file_id'] == OUR_HAIR_TEX)
    pl = P.entry_payload(e, PATCH)
    t = A.Texture.read(pl['toc'], pl['gpu'])
    ours = dds_png(t.dds_header, pl['gpu'], tmp, 'ours_hair')
    print('ours  hair texture: %s' % ('none' if ours is None else '%dx%d' % ours.size))

    # --- source VTF ----------------------------------------------------------
    packs = S.Packs()
    raw = packs.vtf_bytes('matchacheese/lunalice/shinano_hair_ear')
    src = None
    if raw:
        v = V.VTF.read(raw)
        print('src   vtf: fmt=%d %dx%d mips=%d' % (v.hi_format, v.width, v.height,
                                                   len(v.mips)))
        hdr, px = TP.vtf_to_dds(raw, TP.DXGI_BC7_SRGB, tag='src_hair')
        src = dds_png(hdr, px, tmp, 'src_hair')
    print('src   hair texture: %s' % ('none' if src is None else '%dx%d' % src.size))

    tiles = []
    for name, im in (('ours', ours), ('source', src)):
        if im is None:
            continue
        for flip in (False, True):
            c = crop(im, BOX, flip)
            c = c.resize((c.width * 2, c.height * 2), Image.NEAREST)
            tiles.append(('%s  box %s' % (name, 'V-flipped' if flip else 'raw'),
                          im, c))

    # --- sheet: full atlas + island crop -------------------------------------
    CELL, ZOOM = 420, 420
    sheet = Image.new('RGB', (2 * CELL + 2 * ZOOM, len(tiles) // 2 * (CELL + 26)),
                      (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (label, full, c) in enumerate(tiles):
        r, col = divmod(k, 2)
        cy = r * (CELL + 26)
        th = full.copy()
        th.thumbnail((CELL - 6, CELL - 6))
        sheet.paste(th, (col * (CELL + ZOOM) + 3, cy + 3))
        z = c.copy()
        z.thumbnail((ZOOM - 6, ZOOM - 6))
        sheet.paste(z, (col * (CELL + ZOOM) + CELL + 3, cy + 3))
        dr.text((col * (CELL + ZOOM) + 4, cy + CELL + 4), label,
                fill=(235, 240, 246))
        dr.text((col * (CELL + ZOOM) + 4, cy + CELL + 16),
                'UV island 0.200 x 0.175, magnified 2x', fill=(150, 205, 160))
    p = OUT / 'hair-island.png'
    sheet.save(p)
    print()
    print('hair island ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
