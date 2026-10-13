# -*- coding: utf-8 -*-
"""Decide where the real (full resolution) mip sits inside each source VTF.

The VTF high-res image block is a contiguous mip chain.  Reading it as if the
full-res level came first yields a "mip pyramid" picture: the small levels get
reinterpreted as rows, so the image shows the artwork several times, squashed and
duplicated, with hard seams at every level boundary.  Reading it the other way
round yields one clean picture.

Discriminator: row-to-row difference.  A clean texture has no rows that jump;
the reinterpretation has a handful of rows that jump by an order of magnitude.

usage: _probe_vtfmiporder.py
"""
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
NAMES = ['shinano_face', 'white', '_4', 'ornament_metal_underwear', 'shinano_body',
         'shinano_hair_ear', 'headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'glove_white01_wristcuffs',
         'dress_white01_dress_corset', 'lower_white01_underwear',
         'shoes_white01_highheels', 'shoes_02']
DXGI = {13: 71, 15: 77, 12: 70}          # DXT1->BC1, DXT5->BC3, DXT3->BC2


def decode(payload, w, h, dxgi, tmp, tag):
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None
    from PIL import Image
    return np.asarray(Image.open(p).convert('L'), np.float64)


def seams(img):
    """-> (max row jump, count of rows jumping >8x the median jump)."""
    d = np.abs(np.diff(img, axis=0)).mean(axis=1)
    med = np.median(d) + 1e-6
    return float(d.max()), int((d > 8 * med).sum())


def main():
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='vtfmip')
    print('%-30s %-6s %-9s | %-18s | %-18s | verdict'
          % ('texture', 'size', 'mip0len', 'first-as-mip0', 'last-as-mip0'))
    for nm in NAMES:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('%-30s MISSING' % nm)
            continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        m0 = V.mip_size(v.width, v.height, v.hi_format)
        dxgi = DXGI.get(v.hi_format, 71)
        a = decode(data[:m0], v.width, v.height, dxgi, tmp, nm + '_first')
        c = decode(data[-m0:], v.width, v.height, dxgi, tmp, nm + '_last')
        sa = seams(a) if a is not None else None
        sc = seams(c) if c is not None else None
        verdict = '?'
        if sa and sc:
            if sc[1] == 0 and sa[1] > 0:
                verdict = 'LAST is mip0  (smallest-first)'
            elif sa[1] == 0 and sc[1] > 0:
                verdict = 'FIRST is mip0 (largest-first)'
            elif sa[1] == 0 and sc[1] == 0:
                verdict = 'both clean (no chain?)'
            else:
                verdict = 'both seamy'
        print('%-30s %-6s %-9d | max %6.1f seams %4d | max %6.1f seams %4d | %s'
              % (nm, '%dx%d' % (v.width, v.height), m0,
                 sa[0], sa[1], sc[0], sc[1], verdict))
    return 0


if __name__ == '__main__':
    sys.exit(main())
