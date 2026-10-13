# -*- coding: utf-8 -*-
"""One texture at a time, big and unmistakable: OLD (largest-first) | NEW (fixed).

usage: _probe_texfix_one.py <texture> [<texture> ...]
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
    want = sys.argv[1:] or ['shinano_face', 'white']
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='texfix1')
    T = 640
    for nm in want:
        b = packs.vtf_bytes(nm)
        if b is None:
            print('MISSING', nm); continue
        v = V.VTF.read(b)
        hdr = struct.unpack_from('<I', b, 12)[0]
        data = b[hdr:]
        m0 = V.mip_size(v.width, v.height, v.hi_format) * v.frames * v.depth
        p_old = decode(data[:m0], v.width, v.height, v.hi_format, tmp, nm + '_o')
        p_new = decode(v.mips[0], v.width, v.height, v.hi_format, tmp, nm + '_n')
        # offset of the fixed mip0, for the record
        lo_sz = (V.mip_size(v.lo_width, v.lo_height, v.lo_format)
                 if (v.lo_width and v.lo_height) else 0)
        sizes = [V.mip_size(max(1, v.width >> m), max(1, v.height >> m), v.hi_format)
                 * v.frames * v.depth for m in range(v.mip_count)]
        off_new = v.header_size + lo_sz + sum(sizes[1:])
        sheet = Image.new('RGB', (T * 2 + 30, T + 60), (16, 16, 20))
        d = ImageDraw.Draw(sheet)
        d.text((8, 6), '%s  %dx%d  %s  mips=%d  block=%dB  lowres=%dB'
               % (nm, v.width, v.height, v.format_name, v.mip_count, len(data), lo_sz),
               fill=(255, 235, 120))
        d.text((8, 22), 'OLD: mip0 = data[0:%d]  (largest-first)' % m0, fill=(255, 130, 130))
        d.text((8, 38), 'NEW: mip0 = data[%d:%d]  (tail of block)' % (off_new - hdr, off_new - hdr + m0),
               fill=(130, 230, 255))
        for k, p in enumerate((p_old, p_new)):
            x = k * (T + 14) + 8
            im = Image.open(p).convert('RGB').resize((T, T)) if p else Image.new('RGB', (T, T), (60, 0, 0))
            sheet.paste(im, (x, 56))
            d.rectangle([x - 1, 55, x + T, 55 + T], outline=(255, 60, 60) if k == 0 else (60, 220, 255), width=3)
        out = OUT / ('one_%s.png' % nm)
        sheet.save(out)
        print('wrote', out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
