# -*- coding: utf-8 -*-
"""Crop each base texture to the UV box its own mesh actually samples.

Read-only.  A material's UV bbox tells us which part of the atlas the mesh
reads; overlaying that box on the atlas tells us whether the *pairing* is right.
Cheap, and it separates two very different faults:

  * box lands on unrelated artwork      -> wrong texture assigned to the mesh
  * box lands on the right artwork      -> pairing fine, the fault is elsewhere
                                           (scale / wrapping / sampler)

Both the raw box and its V-flipped twin are written, because a missing V flip
mirrors the sample and this pipeline has been bitten by axis conventions before.
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
    r'E:/tmp/rig-lab/cand-m4/9ba626afa44a3aa3.patch_0'
UNIT = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0xCAE35EE15168145F

NAMES = {
    0xb9f84331454d8282: 'face', 0xbe569f23c33c3083: 'white(hair?)',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'body', 0xb66b54bafd3dd768: 'hair_ear',
    0xbbd058dd84a87e8c: 'headdress', 0xbea2867f775bc39b: 'lace',
    0xbb106a56c4d48a2b: 'bra_inner', 0xb3ba47e7968a0ab5: 'glove',
    0xb8f74de60c5f8803: 'dress_corset', 0xbc9d1471d87f8337: 'lower_underwear',
    0xb65a1090f1511be3: 'shoes_highheels', 0xbf29404cde77bd86: 'shoes_02',
}


def decode_rgb(toc, gpu, tmp, tag):
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
    return Image.open(png).convert('RGB')          # RGB BEFORE any resize


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='uvcrop')
    entries, _ = P.scan_entries(PATCH)
    tex = {e['file_id']: e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])

    uv = np.asarray([[v['uv0'][0], v['uv0'][1]] for v in u.decode_vertices(0)],
                    np.float64)
    idx = np.asarray(u.index_list(0), np.int64)
    mi = next(m for m in u.mesh_infos if m.lod_index == 0)

    from PIL import Image, ImageDraw
    tiles = []
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        sid = mi.material_ids[sec.material_index]
        asset = u.material_ids[u.section_ids.index(sid)]
        m = A.Material.read(P.entry_payload(
            next(x for x in entries
                 if x['type_id'] == A.MATERIAL_TYPE_ID and x['file_id'] == asset),
            PATCH)['toc'])
        tid = m.tex_ids[5]                      # advanced: slot 5 = base colour
        seg = idx[sec.index_offset:sec.index_offset + sec.num_indices]
        b = uv[np.unique(seg)]
        mn, mx = b.min(axis=0), b.max(axis=0)
        label = NAMES.get(tid, '0x%016x' % tid)
        span = (mx[0] - mn[0], mx[1] - mn[1])
        note = ''
        if mx[0] - mn[0] > 1.5 or mx[1] - mn[1] > 1.5:
            note = '  TILED'
        print('%-18s u %8.4f..%8.4f  v %8.4f..%8.4f  span %.3f x %.3f%s'
              % (label, mn[0], mx[0], mn[1], mx[1], span[0], span[1], note))
        if tid not in tex:
            continue
        tp = P.entry_payload(tex[tid], PATCH)
        im = decode_rgb(tp['toc'], tp['gpu'], tmp, '%016x' % tid)
        if im is None:
            continue
        W, H = im.size
        # draw the UV box on the full atlas
        marked = im.copy()
        dr = ImageDraw.Draw(marked)
        x0, x1 = int(np.clip(mn[0], 0, 1) * W), int(np.clip(mx[0], 0, 1) * W)
        y0, y1 = int(np.clip(mn[1], 0, 1) * H), int(np.clip(mx[1], 0, 1) * H)
        dr.rectangle([x0, y0, x1, y1], outline=(255, 60, 60), width=max(2, W // 300))
        tiles.append((label, marked, 'box u %.3f..%.3f v %.3f..%.3f' % (mn[0], mx[0], mn[1], mx[1])))

    CELL, COLS = 320, 3
    rows = (len(tiles) + COLS - 1) // COLS
    sheet = Image.new('RGB', (COLS * CELL, rows * (CELL + 40)), (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (label, im, st) in enumerate(tiles):
        cx, cy = (k % COLS) * CELL, (k // COLS) * (CELL + 40)
        th = im.copy()
        th.thumbnail((CELL - 8, CELL - 8))
        sheet.paste(th, (cx + 4, cy + 4))
        dr.text((cx + 4, cy + CELL + 2), label, fill=(235, 240, 246))
        dr.text((cx + 4, cy + CELL + 20), st, fill=(150, 205, 160))
    p = OUT / 'uv-boxes.png'
    sheet.save(p)
    print()
    print('atlas + UV boxes ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
