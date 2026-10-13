# -*- coding: utf-8 -*-
"""Save the hair UV island crops as individual full-size PNGs.

Read-only.  Companion to `_probe_hairzoom.py`: the contact sheet was too small
to judge, so write one file per case and read them one at a time.

Outputs (out/uv-crops/):
  hair-atlas-ours.png    full shipped hair texture
  hair-atlas-src.png     full source VTF
  hair-crop-ours-raw.png     island at v as authored
  hair-crop-ours-flip.png    island at 1-v   (catches a missing V flip)
  hair-crop-src-raw.png      same, from the source VTF
  hair-crop-src-flip.png
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_source as S
import hd2_texpack as TP
import hd2_vtf as V

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')

PATCH = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/tmp/rig-lab/cand-m4/9ba626afa44a3aa3.patch_0'
OUR_HAIR_TEX = 0xB66B54BAFD3DD768
BOX = (0.7969, 0.9970, 0.0071, 0.1821)


def dds_png(hdr, px, tmp, tag):
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(hdr) + bytes(px))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                   capture_output=True)
    png = os.path.join(tmp, tag + '.png')
    if not os.path.exists(png):
        return None
    from PIL import Image
    return Image.open(png).convert('RGB')


def main():
    from PIL import Image, ImageDraw
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='hairpng')

    entries, _ = P.scan_entries(PATCH)
    e = next(x for x in entries
             if x['type_id'] == A.TEXTURE_TYPE_ID and x['file_id'] == OUR_HAIR_TEX)
    pl = P.entry_payload(e, PATCH)
    t = A.Texture.read(pl['toc'], pl['gpu'])
    ours = dds_png(t.dds_header, pl['gpu'], tmp, 'ours_hair')

    packs = S.Packs()
    raw = packs.vtf_bytes('matchacheese/lunalice/shinano_hair_ear')
    hdr, px = TP.vtf_to_dds(raw, TP.DXGI_BC7_SRGB, tag='src_hair')
    src = dds_png(hdr, px, tmp, 'src_hair')

    for name, im in (('ours', ours), ('src', src)):
        if im is None:
            print('%s: no image' % name)
            continue
        im.save(OUT / ('hair-atlas-%s.png' % name))
        W, H = im.size
        u0, u1, v0, v1 = BOX
        for flip in (False, True):
            a, b = (1.0 - v1, 1.0 - v0) if flip else (v0, v1)
            c = im.crop((int(u0 * W), int(a * H), int(u1 * W), int(b * H)))
            c = c.resize((c.width * 2, c.height * 2), Image.LANCZOS)
            tag = 'flip' if flip else 'raw'
            c.save(OUT / ('hair-crop-%s-%s.png' % (name, tag)))
            print('%s %-4s  v %.4f..%.4f  crop %dx%d -> %s'
                  % (name, tag, a, b, c.width, c.height,
                     OUT / ('hair-crop-%s-%s.png' % (name, tag))))

    # label the atlas so the box is unambiguous
    if ours is not None:
        W, H = ours.size
        marked = ours.copy()
        dr = ImageDraw.Draw(marked)
        dr.rectangle([int(BOX[0] * W), int(BOX[2] * H),
                      int(BOX[1] * W), int(BOX[3] * H)],
                     outline=(255, 40, 40), width=8)
        marked.thumbnail((900, 900))
        marked.save(OUT / 'hair-atlas-ours-marked.png')
        print('marked atlas ->', OUT / 'hair-atlas-ours-marked.png')
    return 0


if __name__ == '__main__':
    sys.exit(main())
