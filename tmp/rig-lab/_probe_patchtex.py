# -*- coding: utf-8 -*-
"""Decode the base textures straight out of a candidate patch, for eyeballing.

usage: _probe_patchtex.py <patch_0> [out.png]
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as HP

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
OUT = Path(__file__).resolve().parent / 'out' / 'texfix'
ORDER = ['shinano_face', 'white', 'shinano_body', 'shinano_hair_ear',
         'headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'glove_white01_wristcuffs',
         'dress_white01_dress_corset', 'lower_white01_underwear',
         'shoes_white01_highheels', 'shoes_02']


def decode(t, tmp, tag):
    from PIL import Image
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(t.pixels))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    return p if os.path.exists(p) else None


def main():
    from PIL import Image, ImageDraw
    patch = sys.argv[1]
    name = sys.argv[2] if len(sys.argv) > 2 else 'sheet.png'
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='patchtex')
    entries, _ = HP.scan_entries(patch)
    tex = {}
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = HP.entry_payload(e, patch)
        t = A.Texture.read(pl['toc'], pl['gpu'])
        if t.width >= 2048:
            tex['%016x' % e['file_id']] = t
    print('%d large textures in %s' % (len(tex), patch))
    T = 250
    cols = 4
    rows = (len(tex) + cols - 1) // cols
    sheet = Image.new('RGB', (T * cols + 8 * (cols + 1), (T + 24) * rows + 8), (18, 18, 22))
    d = ImageDraw.Draw(sheet)
    for i, (fid, t) in enumerate(sorted(tex.items())):
        r, c = divmod(i, cols)
        x = 8 + c * (T + 8)
        y = 8 + r * (T + 24)
        d.text((x, y), '0x%s  %dx%d  dxgi=%d' % (fid[:16], t.width, t.height, t.dxgi_format),
               fill=(255, 235, 120))
        p = decode(t, tmp, fid)
        im = Image.open(p).convert('RGB').resize((T, T)) if p else Image.new('RGB', (T, T), (60, 0, 0))
        sheet.paste(im, (x, y + 16))
    out = OUT / name
    sheet.save(out)
    print('wrote', out, sheet.size)
    return 0


if __name__ == '__main__':
    sys.exit(main())
