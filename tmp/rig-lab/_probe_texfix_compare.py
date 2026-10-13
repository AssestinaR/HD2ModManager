# -*- coding: utf-8 -*-
"""Before/after contact sheet: decode every shipping texture with the old
largest-first reader (left) and the fixed smallest-first reader (right).

usage: _probe_texfix_compare.py
"""
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
OUT = Path(__file__).resolve().parent / 'out' / 'texfix'
NAMES = ['shinano_face', 'white', 'shinano_body', 'shinano_hair_ear',
         'headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'glove_white01_wristcuffs',
         'dress_white01_dress_corset', 'lower_white01_underwear',
         'shoes_white01_highheels', 'shoes_02']
DXGI = {13: 71, 15: 77, 12: 70, 37: 83, 38: 80}


def decode(payload, w, h, fmt, tmp, tag):
    dxgi = DXGI.get(fmt, 71)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    return p if os.path.exists(p) else None


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='texfix')
    T = 260
    cols = 2
    rows = (len(NAMES) + 1) // 2
    sheet = Image.new('RGB', (T * 4 + 36, (T + 26) * rows + 8), (18, 18, 22))
    d = ImageDraw.Draw(sheet)
    for i, nm in enumerate(NAMES):
        b = packs.vtf_bytes(nm)
        r, c = divmod(i, 2)
        if b is None:
            continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        m0 = V.mip_size(v.width, v.height, v.hi_format) * v.frames * v.depth
        p_old = decode(data[:m0], v.width, v.height, v.hi_format, tmp, nm + '_o')
        p_new = decode(v.mips[0], v.width, v.height, v.hi_format, tmp, nm + '_n')
        y = r * (T + 26) + 8
        d.text((c * (T * 2 + 24) + 6, y), '%s  %dx%d %s' % (nm, v.width, v.height, v.format_name),
               fill=(255, 235, 120))
        for k, (p, lbl) in enumerate(((p_old, 'OLD largest-first'), (p_new, 'NEW smallest-first'))):
            x = c * (T * 2 + 24) + 6 + k * (T + 4)
            d.text((x + 2, y + 14), lbl, fill=(255, 140, 140) if k == 0 else (150, 230, 255))
            im = Image.open(p).convert('RGB').resize((T, T)) if p else Image.new('RGB', (T, T), (60, 0, 0))
            sheet.paste(im, (x, y + 26))
    out = OUT / 'texfix_compare.png'
    sheet.save(out)
    print('wrote', out, sheet.size)
    return 0


if __name__ == '__main__':
    sys.exit(main())
