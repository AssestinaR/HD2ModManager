# -*- coding: utf-8 -*-
"""The money shot: every large base texture exactly as shipped in M5 (left)
versus as shipped in M6 (right).

usage: _probe_m5m6tex.py <old.patch_0> <new.patch_0>
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
LABEL = {0xb9f84331454d8282: 'face', 0xbe569f23c33c3083: 'white(hair)',
         0xb6e760aee076359d: 'body', 0xb66b54bafd3dd768: 'hair_ear(cat)',
         0xbbd058dd84a87e8c: 'headdress', 0xbea2867f775bc39b: 'lace',
         0xbb106a56c4d48a2b: 'bra', 0xb3ba47e7968a0ab5: 'glove',
         0xb8f74de60c5f8803: 'dress', 0xbc9d1471d87f8337: 'underwear',
         0xb65a1090f1511be3: 'highheels', 0xbf29404cde77bd86: 'shoes_02'}


def textures(patch, tmp, tag):
    out = {}
    entries, _ = HP.scan_entries(patch)
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = HP.entry_payload(e, patch)
        t = A.Texture.read(pl['toc'], pl['gpu'])
        if t.width < 2048:
            continue
        p = os.path.join(tmp, '%s_%016x.dds' % (tag, e['file_id']))
        with open(p, 'wb') as f:
            f.write(bytes(t.dds_header) + bytes(t.pixels))
        subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                        '-m', '1', '-o', tmp, p], capture_output=True)
        q = os.path.splitext(p)[0] + '.png'
        if os.path.exists(q):
            out[e['file_id']] = q
    return out


def main():
    from PIL import Image, ImageDraw
    old_p, new_p = sys.argv[1], sys.argv[2]
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='m5m6')
    a = textures(old_p, tmp, 'old')
    b = textures(new_p, tmp, 'new')
    ids = [k for k in sorted(LABEL, key=lambda k: LABEL[k]) if k in a and k in b]
    T = 300
    per_row = 2
    rows = (len(ids) + per_row - 1) // per_row
    cellw = T * 2 + 20
    sheet = Image.new('RGB', (cellw * per_row + 12, (T + 34) * rows + 10), (18, 18, 22))
    d = ImageDraw.Draw(sheet)
    for i, fid in enumerate(ids):
        r, c = divmod(i, per_row)
        x0 = 6 + c * (cellw + 6)
        y = 6 + r * (T + 34)
        d.text((x0, y), '%s  0x%016x' % (LABEL[fid], fid), fill=(255, 235, 120))
        d.text((x0, y + 14), 'M5 shipped (mip pyramid)', fill=(255, 140, 140))
        d.text((x0 + T + 20, y + 14), 'M6 fixed reader', fill=(150, 230, 255))
        sheet.paste(Image.open(a[fid]).convert('RGB').resize((T, T)), (x0, y + 30))
        sheet.paste(Image.open(b[fid]).convert('RGB').resize((T, T)), (x0 + T + 20, y + 30))
    out = OUT / 'm5_vs_m6_textures.png'
    sheet.save(out)
    print('wrote', out, sheet.size)
    return 0


if __name__ == '__main__':
    sys.exit(main())
