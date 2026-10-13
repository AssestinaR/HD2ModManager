# -*- coding: utf-8 -*-
"""Render, for a few textures, the two candidate mip0 layouts side by side so a
human eye can settle it.  Left = largest-first (what VTF.read does today),
right = smallest-first.  A wrong layout shows the "mip pyramid" artefact.

usage: _probe_mipviz.py [texture ...]
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
OUT = Path(__file__).resolve().parent / 'out' / 'miporder'
DXGI = {13: 71, 15: 77, 12: 70, 37: 83, 38: 80}
DEFAULT = ['shinano_face', 'white', 'shinano_body', 'lace_white01_catears',
           'glove_white01_wristcuffs', 'shoes_white01_highheels']


def decode(payload, w, h, fmt, tmp, tag):
    dxgi = DXGI.get(fmt, 71)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    return p if os.path.exists(p) else None


def mip_offsets(v):
    sz = []
    for m in range(v.mip_count):
        w = max(1, v.width >> m)
        h = max(1, v.height >> m)
        sz.append(V.mip_size(w, h, v.hi_format) * v.frames * v.depth)
    offL, acc = [], 0
    for m in range(v.mip_count):
        offL.append(acc)
        acc += sz[m]
    offS = [0] * v.mip_count
    acc = 0
    for m in range(v.mip_count - 1, -1, -1):
        offS[m] = acc
        acc += sz[m]
    return sz, offL, offS


def main():
    from PIL import Image, ImageDraw
    want = sys.argv[1:] or DEFAULT
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='mipviz')
    for nm in want:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('MISSING', nm)
            continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        sz, offL, offS = mip_offsets(v)
        m0 = sz[0]
        pA = decode(data[offL[0]:offL[0] + m0], v.width, v.height, v.hi_format, tmp, nm + '_L')
        pB = decode(data[offS[0]:offS[0] + m0], v.width, v.height, v.hi_format, tmp, nm + '_S')
        pC = decode(data[-m0:], v.width, v.height, v.hi_format, tmp, nm + '_T')
        if not (pA and pB):
            print('decode fail', nm)
            continue
        imA = Image.open(pA).convert('RGB').resize((512, 512))
        imB = Image.open(pB).convert('RGB').resize((512, 512))
        imC = Image.open(pC).convert('RGB').resize((512, 512)) if pC else Image.new('RGB', (512, 512))
        sheet = Image.new('RGB', (512 * 3 + 24, 512 + 30), (20, 20, 24))
        sheet.paste(imA, (0, 30)); sheet.paste(imB, (512 + 12, 30)); sheet.paste(imC, (1024 + 24, 30))
        d = ImageDraw.Draw(sheet)
        for i, t in enumerate(['largest-first (off=%d)' % offL[0],
                               'smallest-first (off=%d)' % offS[0],
                               'tail m0 (off=%d)' % (len(data) - m0)]):
            d.text((i * (512 + 12) + 6, 8), '%s  %s' % (nm, t), fill=(255, 235, 120))
        out = OUT / ('%s.png' % nm)
        sheet.save(out)
        print('wrote', out, ' lowres=%d' % (V.mip_size(v.lo_width, v.lo_height, v.lo_format)
                                            if v.lo_width else 0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
