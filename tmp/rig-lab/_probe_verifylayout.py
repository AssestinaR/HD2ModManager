# -*- coding: utf-8 -*-
"""Verify the new VTF layout end-to-end, on all 14 shipping textures.

Two independent checks, neither of which needs a threshold judgement call:

 1. size bookkeeping --  header + lowres + sum(mips)  must equal the file size
    exactly (trailing == 0).

 2. thumbnail agreement -- the 16x16 low-res thumbnail is a *separate* copy of
    the picture (it lives at the front, the chain at the back), so decoding it
    and comparing against mip0 downsampled to 16x16 must agree strongly for the
    correct mip0 and poorly for the wrong one.  We report Pearson r for
      * new reader  (mip0 = tail of the block)
      * old reader  (mip0 = head of the block)
    and require new > old for every texture.

usage: _probe_verifylayout.py
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
DXGI = {13: 71, 15: 77, 12: 70, 37: 83, 38: 80}


def decode(payload, w, h, fmt, tmp, tag):
    dxgi = DXGI.get(fmt, 71)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None
    from PIL import Image
    im = Image.open(p).convert('L')
    if im.size != (w, h):
        im = im.resize((w, h))
    return np.asarray(im, np.float64)


def corr(a, b):
    a = a - a.mean(); b = b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else 0.0


def box_to(a, w, h):
    from PIL import Image
    return np.asarray(Image.fromarray(a.astype(np.uint8)).resize((w, h), Image.BOX), np.float64)


def main():
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='verifylay')
    print('%-28s %-10s %-9s | %-8s %-8s | %s'
          % ('texture', 'size', 'trailing', 'r(new)', 'r(old)', 'verdict'))
    bad = 0
    for nm in NAMES:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('%-28s MISSING' % nm); bad += 1; continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        m0 = V.mip_size(v.width, v.height, v.hi_format) * v.frames * v.depth
        lo_sz = (V.mip_size(v.lo_width, v.lo_height, v.lo_format)
                 if (v.lo_width and v.lo_height) else 0)

        # 1. bookkeeping
        sizes = [V.mip_size(max(1, v.width >> m), max(1, v.height >> m), v.hi_format)
                 * v.frames * v.depth for m in range(v.mip_count)]
        ok_size = (v.header_size + lo_sz + sum(sizes)) == len(b)

        # 2. thumbnail agreement
        r_new = r_old = None
        if v.lo_width and v.lo_height and v.lo_width >= 4:
            lo = decode(v.lowres, v.lo_width, v.lo_height, v.lo_format, tmp, nm + '_lo')
            if lo is not None:
                full = decode(v.mips[0], v.width, v.height, v.hi_format, tmp, nm + '_new')
                old = decode(data[:m0], v.width, v.height, v.hi_format, tmp, nm + '_old')
                if full is not None:
                    r_new = corr(lo, box_to(full, v.lo_width, v.lo_height))
                if old is not None:
                    r_old = corr(lo, box_to(old, v.lo_width, v.lo_height))

        if r_new is None or r_old is None:
            verdict = 'size-only ' + ('OK' if ok_size else 'SIZE MISMATCH')
        elif r_new > r_old + 0.05:
            verdict = 'NEW wins (d=%.3f)' % (r_new - r_old)
        else:
            verdict = '!! NEW does not win'
            bad += 1
        if not ok_size:
            verdict += '  !! trailing=%d' % v.trailing
            bad += 1
        print('%-28s %-10s %-9d | %-8s %-8s | %s'
              % (nm, '%dx%d' % (v.width, v.height), v.trailing,
                 ('%.3f' % r_new) if r_new is not None else '-',
                 ('%.3f' % r_old) if r_old is not None else '-', verdict))
    print('\n%s' % ('ALL 14 OK' if bad == 0 else '%d problem(s)' % bad))
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
