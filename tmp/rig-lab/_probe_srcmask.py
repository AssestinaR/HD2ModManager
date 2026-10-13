# -*- coding: utf-8 -*-
"""Dump the source (L4D2) VTF alpha channel of the outfit textures that are
candidates for镂空, as grayscale PNGs, so the *shape* of the transparency can be
looked at instead of guessed from statistics.

usage: _probe_srcmask.py
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_texpack as TP

TC = r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe'
OUT = Path(__file__).resolve().parent / 'out' / 'srcmask'

NAMES = ['headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'shoes_white01_highheels', 'shoes_02',
         'glove_white01_wristcuffs', 'dress_white01_dress_corset',
         'lower_white01_underwear', 'shinano_body', 'white',
         'shinano_face', 'shinano_hair_ear']


def main():
    from PIL import Image
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='srcmask')
    sheet_rgb, sheet_a = [], []
    for nm in NAMES:
        raw = packs.vtf_bytes(nm)
        if raw is None:
            print('%-30s NOT FOUND' % nm)
            continue
        import hd2_vtf as V
        v = V.VTF.read(raw)
        hdr, px = TP.vtf_to_dds(raw, TP.DXGI_BC7_UNORM, tmpdir=tmp, tag=nm)
        dds = os.path.join(tmp, nm + '.dds')
        subprocess.run([TC, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                        '-m', '1', '-o', tmp, dds], capture_output=True)
        png = os.path.join(tmp, nm + '.png')
        if not os.path.exists(png):
            print('%-30s decode fail' % nm)
            continue
        arr = np.asarray(Image.open(png).convert('RGBA'), np.uint8)
        a = arr[:, :, 3]
        Image.fromarray(a, 'L').save(OUT / (nm + '_alpha.png'))
        Image.fromarray(arr[:, :, :3], 'RGB').save(OUT / (nm + '_rgb.png'))
        print('%-30s %s %dx%d  alpha mean=%.1f 0:%.1f%% 255:%.1f%% mid:%.1f%%'
              % (nm, v.format_name, v.width, v.height, a.mean(),
                 100.0 * (a == 0).mean(), 100.0 * (a == 255).mean(),
                 100.0 * ((a > 0) & (a < 255)).mean()))
        s = 256
        sheet_rgb.append(np.asarray(Image.fromarray(arr[:, :, :3], 'RGB').resize((s, s), Image.LANCZOS)))
        sheet_a.append(np.asarray(Image.fromarray(a, 'L').resize((s, s), Image.LANCZOS)))
    if sheet_rgb:
        n = len(sheet_rgb)
        cols = 4
        rows = (n + cols - 1) // cols
        sh = np.zeros((rows * 256, cols * 256, 3), np.uint8)
        for i in range(n):
            r, c = divmod(i, cols)
            sh[r * 256:(r + 1) * 256, c * 256:(c + 1) * 256, 0] = sheet_rgb[i][:, :, 0]
            sh[r * 256:(r + 1) * 256, c * 256:(c + 1) * 256, 1] = sheet_a[i]
            sh[r * 256:(r + 1) * 256, c * 256:(c + 1) * 256, 2] = sheet_a[i]
        Image.fromarray(sh, 'RGB').save(OUT / '_sheet_rgb_alpha.png')
        print('sheet -> %s' % (OUT / '_sheet_rgb_alpha.png'))
    shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
