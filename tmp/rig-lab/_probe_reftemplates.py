# -*- coding: utf-8 -*-
"""Decode the reference (working) armour mod's `advanced` material textures.

Read-only.  Builds a labelled contact sheet so the slots can be identified by
eye -- the SDK's TextureTypeLookup for `advanced` is mostly blank ("", "", ...)
so the only reliable source of truth is a material that actually ships and
renders: 【4.28】Chiffon-LUNALICE替换RE-2310.

Also decodes the vanilla RE-2310 armour material for contrast.
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
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\template-slots')

REF = Path(r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0')
REF_MAT = 0x0d04d273fa5a4646          # parent = advanced
VAN_MAT = Path(r'E:\tmp\hd2-mgroot\re2310-full\material\material_0x0b20ee2c1407bb72.toc')
VAN_TEX = Path(r'E:\tmp\hd2-mgroot\re2310-full\texture')

ADV_SLOTS = ('', '', 'Normal/AO/Roughness', 'Metallic', '',
             'Color/Emission Mask', '', '', '', '', '')


def decode(toc_bytes, gpu_bytes, tag):
    tmp = tempfile.mkdtemp(prefix='refslot')
    dds = os.path.join(tmp, tag + '.dds')
    try:
        t = A.Texture.read(toc_bytes, gpu_bytes)
    except Exception as e:  # noqa: BLE001
        return None, 'parse: %s' % e
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + gpu_bytes)
    p = subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                        '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                       capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None, '%s %dx%d decode-failed' % (t.format_name, t.width, t.height)
    from PIL import Image
    im = Image.open(png).convert('RGBA')
    arr = np.asarray(im, np.float64).reshape(-1, 4).mean(axis=0)
    return im, ('%s %dx%d mean=(%5.1f,%5.1f,%5.1f,%5.1f)'
                % (t.format_name, t.width, t.height, arr[0], arr[1], arr[2], arr[3]))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    from PIL import Image, ImageDraw

    entries, _ = P.scan_entries(str(REF))
    tex = {}
    for e in entries:
        if e['type_id'] == A.TEXTURE_TYPE_ID:
            tex[e['file_id']] = P.entry_payload(e, str(REF))

    mat = next(e for e in entries if e['file_id'] == REF_MAT)
    m = A.Material.read(P.entry_payload(mat, str(REF))['toc'])

    print('reference mod texture entries (%d):' % len(tex))
    for fid in sorted(tex):
        print('    0x%016x' % fid)
    print()

    van_tex = {}
    for f in VAN_TEX.glob('*.toc'):
        fid = int(f.stem.split('_')[1], 16)
        g = f.with_suffix('.gpu_resources')
        van_tex[fid] = (f.read_bytes(), g.read_bytes() if g.exists() else b'')
    vm = A.Material.read(VAN_MAT.read_bytes())

    tiles = []          # (label, PIL image or None, stat string)
    for i, tid in enumerate(m.tex_ids):
        slot = ADV_SLOTS[i] if i < len(ADV_SLOTS) else ''
        lab = 'advanced[%d] %s\nkey=%08x\nid=%016x' % (i, slot or '-', m.tex_unks[i], tid)
        if tid in tex:
            im, st = decode(tex[tid]['toc'], tex[tid]['gpu'], 'ref%02d' % i)
        else:
            im, st = None, 'NOT IN PACKAGE'
        print('%-28s %s' % (lab.replace('\n', ' | '), st))
        tiles.append((lab, im, st))

    print()
    for i, tid in enumerate(vm.tex_ids):
        lab = 'VANILLA[%d]\nkey=%08x\nid=%016x' % (i, vm.tex_unks[i], tid)
        if tid in van_tex:
            im, st = decode(*van_tex[tid], 'van%02d' % i)
        else:
            im, st = None, 'NOT EXTRACTED'
        print('%-28s %s' % (lab.replace('\n', ' | '), st))

    # ---- contact sheet -----------------------------------------------------
    CELL, COLS = 200, 4
    rows = (len(tiles) + COLS - 1) // COLS
    sheet = Image.new('RGBA', (COLS * CELL, rows * (CELL + 46)), (24, 28, 34, 255))
    dr = ImageDraw.Draw(sheet)
    for k, (lab, im, st) in enumerate(tiles):
        cx, cy = (k % COLS) * CELL, (k // COLS) * (CELL + 46)
        if im is not None:
            th = im.copy()
            th.thumbnail((CELL - 8, CELL - 8))
            sheet.paste(th, (cx + 4, cy + 4), th if th.mode == 'RGBA' else None)
        else:
            dr.rectangle([cx + 4, cy + 4, cx + CELL - 4, cy + CELL - 4], outline=(120, 60, 60))
            dr.text((cx + 10, cy + CELL // 2), 'MISSING', fill=(230, 120, 120))
        dr.text((cx + 4, cy + CELL + 2), lab, fill=(230, 235, 240))
        dr.text((cx + 4, cy + CELL + 32), st[:34], fill=(150, 200, 150))
    p = OUT / 'ref-advanced-slots.png'
    sheet.save(p)
    print()
    print('contact sheet ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
