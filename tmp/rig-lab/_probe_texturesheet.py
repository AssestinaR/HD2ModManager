# -*- coding: utf-8 -*-
"""Decode every base colour texture in a package into one contact sheet.

Read-only.  Answers the round-60 question "is the hair showing the wrong image,
or the right image at the wrong scale?" by letting the operator *look* at what
we actually shipped.

PIL gotcha (cost us a wrong conclusion once): `thumbnail()` on an RGBA image
with alpha == 0 premultiplies and the tile comes out black.  Every tile is
converted to RGB **before** resizing here.
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

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\our-textures')

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4/9ba626afa44a3aa3.patch_0'

NAMES = {
    0xb4948ba8ac86844a: 'shared_pbr -> metallic',
    0xba1a40900756a7f3: 'shared_flat_normal',
    0xb9f84331454d8282: 'shinano_face',
    0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4 (1x1)',
    0xb8a1ba9aed375bed: 'ornament_metal (1x1)',
    0xb6e760aee076359d: 'shinano_body',
    0xb66b54bafd3dd768: 'shinano_hair_ear  <== HAIR',
    0xbbd058dd84a87e8c: 'headdress_white01_catears',
    0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner',
    0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset',
    0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels',
    0xbf29404cde77bd86: 'shoes_02',
}


def decode(toc_bytes, gpu_bytes, tag, tmpdir):
    try:
        t = A.Texture.read(toc_bytes, gpu_bytes)
    except Exception as e:  # noqa: BLE001
        return None, 'parse: %s' % e
    dds = os.path.join(tmpdir, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(gpu_bytes))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmpdir, dds],
                   capture_output=True)
    png = os.path.join(tmpdir, tag + '.png')
    if not os.path.exists(png):
        return None, '%s %dx%d decode-failed' % (t.format_name, t.width, t.height)
    from PIL import Image
    im = Image.open(png).convert('RGBA')
    arr = np.asarray(im, np.float64).reshape(-1, 4)
    mean = arr.mean(axis=0)
    rgb = im.convert('RGB')                       # BEFORE any resize
    return (rgb, t, mean), ('%s %dx%d mean RGB=(%5.1f,%5.1f,%5.1f) A=%5.1f'
                            % (t.format_name, t.width, t.height,
                               mean[0], mean[1], mean[2], mean[3]))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    entries, _ = P.scan_entries(PATCH)
    tmp = tempfile.mkdtemp(prefix='texsheet')
    tiles = []
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = P.entry_payload(e, PATCH)
        got, st = decode(pl['toc'], pl['gpu'], '%016x' % e['file_id'], tmp)
        label = NAMES.get(e['file_id'], '0x%016x' % e['file_id'])
        print('%-30s %s' % (label, st))
        tiles.append((label, got[0] if got else None, st))

    from PIL import Image, ImageDraw
    CELL, COLS = 300, 4
    rows = (len(tiles) + COLS - 1) // COLS
    sheet = Image.new('RGB', (COLS * CELL, rows * (CELL + 44)), (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (label, im, st) in enumerate(tiles):
        cx, cy = (k % COLS) * CELL, (k // COLS) * (CELL + 44)
        if im is not None:
            th = im.copy()
            th.thumbnail((CELL - 8, CELL - 8))
            sheet.paste(th, (cx + 4, cy + 4))
        else:
            dr.rectangle([cx + 4, cy + 4, cx + CELL - 4, cy + CELL - 4],
                         outline=(150, 80, 80))
            dr.text((cx + 10, cy + CELL // 2), 'DECODE FAILED', fill=(240, 140, 140))
        dr.text((cx + 4, cy + CELL + 2), label, fill=(235, 240, 246))
        dr.text((cx + 4, cy + CELL + 20), st[:46], fill=(150, 205, 160))
    p = OUT / 'our-textures.png'
    sheet.save(p)
    print()
    print('contact sheet ->', p)
    # also save the hair alone, at full size, so it can be inspected closely
    for k, (label, im, _st) in enumerate(tiles):
        if 'HAIR' in label and im is not None:
            hp = OUT / 'hair-full.png'
            im.copy().resize((768, 768)).save(hp)
            print('hair alone     ->', hp)
    return 0


if __name__ == '__main__':
    sys.exit(main())
