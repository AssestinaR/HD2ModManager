# -*- coding: utf-8 -*-
"""Pin down the exact byte layout of the VTF image block.

Candidates for the 128-byte 16x16 DXT1 low-res thumbnail:
   (a) at the very front, before the chain  -> layout = lowres + mipN..mip0
   (b) at the very back, after the chain    -> layout = mipN..mip0 + lowres
and, independently, confirm the chain runs smallest-first.

We render three slices as PNG and let the eye decide:
   front128 : data[0:128]     as 16x16 DXT1   (thumbnail if (a))
   back128  : data[-128:]     as 16x16 DXT1   (thumbnail if (b))
   head8    : data[0:8]       as 1x1   DXT1   (mip9 colour if chain is smallest-first)

usage: _probe_vtflayout.py [texture ...]
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
OUT = Path(__file__).resolve().parent / 'out' / 'miporder'
DEFAULT = ['white', 'lace_white01_catears', 'shinano_face']


def decode(payload, w, h, fmt, tmp, tag, scale=16):
    dxgi = {13: 71, 15: 77, 12: 70}.get(fmt, 71)
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(V.build_dds_dx10(w, h, dxgi, [payload]))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    return p if os.path.exists(p) else None


def main():
    from PIL import Image, ImageDraw
    want = sys.argv[1:] or DEFAULT
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='vtflay')
    for nm in want:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('MISSING', nm); continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        # full-res reference = smallest-first mip0 (lowres assumed last)
        sz0 = V.mip_size(v.width, v.height, v.hi_format)
        lo = V.mip_size(v.lo_width, v.lo_height, v.lo_format) if (v.lo_width and v.lo_height) else 0
        ref = decode(data[len(data) - lo - sz0:len(data) - lo],
                     v.width, v.height, v.hi_format, tmp, nm + '_ref')
        f = decode(data[:128], 16, 16, 13, tmp, nm + '_front')
        bk = decode(data[-128:], 16, 16, 13, tmp, nm + '_back')
        h8 = decode(data[:8], 1, 1, 13, tmp, nm + '_head8')
        tiles = [('ref 2048 full', ref), ('front128 as16x16', f),
                 ('back128 as16x16', bk), ('head8 as1x1', h8)]
        W = 300
        sheet = Image.new('RGB', (W * len(tiles) + 8 * (len(tiles) - 1), W + 30), (20, 20, 24))
        d = ImageDraw.Draw(sheet)
        for i, (t, p) in enumerate(tiles):
            x = i * (W + 8)
            d.text((x + 4, 8), '%s  %s' % (nm, t), fill=(255, 235, 120))
            if p:
                im = Image.open(p).convert('RGB').resize((W, W), Image.NEAREST)
            else:
                im = Image.new('RGB', (W, W), (60, 0, 0))
            sheet.paste(im, (x, 30))
        out = OUT / ('layout_%s.png' % nm)
        sheet.save(out)
        print('wrote', out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
