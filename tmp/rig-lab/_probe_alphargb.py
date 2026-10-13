# -*- coding: utf-8 -*-
"""Does the alpha-zero re-encode damage RGB?

Compares, for the DXT5 base textures:
  A = the source VTF mip0 decoded straight (fixed reader, no alpha-zero)
  B = the texture as it is shipped inside the candidate patch (alpha-zeroed)
and reports the RGB-only difference.  Alpha is expected to differ (A keeps the
source alpha, B is all zero); RGB should be essentially the same.

usage: _probe_alphargb.py <patch_0> [<patch_0> ...]
"""
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as HP
import hd2_source as S
import hd2_texpack as TP
import hd2_vtf as V

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
OUT = Path(__file__).resolve().parent / 'out' / 'texfix'
STEMS = {0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
         0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
         0xbbd058dd84a87e8c: 'headdress_white01_catears',
         0xbea2867f775bc39b: 'lace_white01_catears',
         0xbb106a56c4d48a2b: 'bra_white01_bra_inner',
         0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
         0xb8f74de60c5f8803: 'dress_white01_dress_corset',
         0xbc9d1471d87f8337: 'lower_white01_underwear',
         0xb65a1090f1511be3: 'shoes_white01_highheels',
         0xbf29404cde77bd86: 'shoes_02'}


def decode_dds(path, tmp, tag):
    from PIL import Image
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, path], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None, None
    im = Image.open(p).convert('RGBA')
    a = np.asarray(im, np.float64)
    return a[:, :, :3], a[:, :, 3]


def main():
    from PIL import Image, ImageDraw
    patches = sys.argv[1:] or ['E:/tmp/rig-lab/cand-m6/9ba626afa44a3aa3.patch_0']
    OUT.mkdir(parents=True, exist_ok=True)
    packs = S.Packs()
    tmp = tempfile.mkdtemp(prefix='alphargb')
    print('%-30s %-8s %-10s %-10s %s' % ('texture', 'fmt', 'meanRGBd', 'p99RGBd', 'alpha'))
    for patch in patches:
        entries, _ = HP.scan_entries(patch)
        for e in entries:
            stem = STEMS.get(e['file_id'])
            if e['type_id'] != A.TEXTURE_TYPE_ID or stem is None:
                continue
            pl = HP.entry_payload(e, patch)
            t = A.Texture.read(pl['toc'], pl['gpu'])
            if t.width < 2048:
                continue
            p_new = os.path.join(tmp, 'new_%s.dds' % stem)
            with open(p_new, 'wb') as f:
                f.write(bytes(t.dds_header) + bytes(t.pixels))
            rgb_new, al_new = decode_dds(p_new, tmp, 'new_' + stem)

            raw = packs.vtf_bytes(stem)
            v = V.VTF.read(raw)
            p_src = os.path.join(tmp, 'src_%s.dds' % stem)
            with open(p_src, 'wb') as f:
                f.write(V.build_dds_dx10(v.width, v.height,
                                         TP.VTF_TO_WRAPPER_DXGI[v.hi_format], [v.mips[0]]))
            rgb_src, al_src = decode_dds(p_src, tmp, 'src_' + stem)
            if rgb_new is None or rgb_src is None:
                print('%-30s decode failed' % stem); continue
            d = np.abs(rgb_new - rgb_src)
            print('%-30s %-8s %-10.2f %-10.2f  src mean %.1f -> shipped %.1f (0=all transparent)'
                  % (stem, v.format_name, d.mean(), np.percentile(d, 99),
                     al_src.mean(), al_new.mean()))
            if stem in ('headdress_white01_catears', 'lace_white01_catears'):
                T = 420
                sheet = Image.new('RGB', (T * 2 + 24, T + 26), (18, 18, 22))
                dr = ImageDraw.Draw(sheet)
                dr.text((6, 6), '%s   source mip0 (fixed reader)' % stem, fill=(255, 235, 120))
                dr.text((T + 18, 6), 'shipped in patch (alpha-zeroed)', fill=(150, 230, 255))
                sheet.paste(Image.open(os.path.join(tmp, 'src_%s.png' % stem)).convert('RGB').resize((T, T)), (6, 24))
                sheet.paste(Image.open(os.path.join(tmp, 'new_%s.png' % stem)).convert('RGB').resize((T, T)), (T + 18, 24))
                sheet.save(OUT / ('alphargb_%s.png' % stem))
                print('   wrote', OUT / ('alphargb_%s.png' % stem))
    return 0


if __name__ == '__main__':
    sys.exit(main())
