# -*- coding: utf-8 -*-
"""M5 vs M6, one texture per image, big and readable.

usage: _probe_m5m6one.py [label ...]
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
OLD = 'E:/tmp/rig-lab/cand-m5/9ba626afa44a3aa3.patch_0'
NEW = 'E:/tmp/rig-lab/cand-m6/9ba626afa44a3aa3.patch_0'
LABEL = {0xb9f84331454d8282: 'face', 0xbe569f23c33c3083: 'white_hair',
         0xb6e760aee076359d: 'body', 0xb66b54bafd3dd768: 'hair_ear_cat',
         0xbbd058dd84a87e8c: 'headdress', 0xbea2867f775bc39b: 'lace',
         0xbb106a56c4d48a2b: 'bra', 0xb3ba47e7968a0ab5: 'glove',
         0xb8f74de60c5f8803: 'dress', 0xbc9d1471d87f8337: 'underwear',
         0xb65a1090f1511be3: 'highheels', 0xbf29404cde77bd86: 'shoes_02'}


def dump(patch, tmp, tag):
    out = {}
    entries, _ = HP.scan_entries(patch)
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID or e['file_id'] not in LABEL:
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
    want = sys.argv[1:]
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='m5m6one')
    a, b = dump(OLD, tmp, 'old'), dump(NEW, tmp, 'new')
    T = 620
    for fid, lab in LABEL.items():
        if want and lab not in want:
            continue
        if fid not in a or fid not in b:
            print('skip', lab); continue
        sheet = Image.new('RGB', (T * 2 + 30, T + 54), (16, 16, 20))
        d = ImageDraw.Draw(sheet)
        d.text((8, 8), '%s   0x%016x    2048x2048' % (lab, fid), fill=(255, 235, 120))
        d.text((8, 26), 'M5 SHIPPED  (mip-pyramid bytes)', fill=(255, 120, 120))
        d.text((T + 22, 26), 'M6  fixed reader', fill=(120, 220, 255))
        sheet.paste(Image.open(a[fid]).convert('RGB').resize((T, T)), (8, 46))
        sheet.paste(Image.open(b[fid]).convert('RGB').resize((T, T)), (T + 22, 46))
        d.rectangle([7, 45, 7 + T, 45 + T], outline=(255, 60, 60), width=3)
        d.rectangle([T + 21, 45, T + 21 + T, 45 + T], outline=(60, 220, 255), width=3)
        out = OUT / ('m5m6_%s.png' % lab)
        sheet.save(out)
        print('wrote', out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
