# -*- coding: utf-8 -*-
"""Before/after preview for every material split patch.

For each patch folder: decode the textures it overrides, decode the same ids out
of the main patch, and lay them out side by side so the change can be eyeballed
without launching the game.  Also dumps the raw PNG of each new texture.

Read-only w.r.t. the patches; writes <patch>/preview/*.png
"""
import glob
import os
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from PIL import Image, ImageDraw, ImageFont

try:
    F = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 13)
except Exception:
    F = ImageFont.load_default()

import hd2_asset as A
import hd2_patch as HP
import fix_appearance as FA

M8 = r'E:\mod\Lunalice-RE2310-M8\9ba626afa44a3aa3.patch_0'
MOD = r'E:\mod'
S = 256


def label(d, xy, text, fill=(235, 235, 240)):
    d.text(xy, text, fill=fill, font=F)


def main():
    m8_entries, _ = HP.scan_entries(M8)
    m8 = {e['file_id']: e for e in m8_entries if e['type_id'] == A.TEXTURE_TYPE_ID}
    tmp = tempfile.mkdtemp(prefix='preview')
    for folder in sorted(glob.glob(os.path.join(MOD, 'Lunalice-RE2310-MAT-*'))):
        patch = os.path.join(folder, '9ba626afa44a3aa3.patch_0')
        if not os.path.exists(patch):
            continue
        tag = os.path.basename(folder).split('MAT-')[-1]
        ents, _r = HP.scan_entries(patch)
        tex = [e for e in ents if e['type_id'] == A.TEXTURE_TYPE_ID]
        if not tex:
            continue
        outdir = os.path.join(folder, 'preview')
        os.makedirs(outdir, exist_ok=True)
        tiles = []
        for e in tex:
            new, _t = FA.decode_patch_texture(patch, e, tmp, 'n_%s_%016x' % (tag, e['file_id']))
            ref = None
            if e['file_id'] in m8:
                ref, _t2 = FA.decode_patch_texture(M8, m8[e['file_id']], tmp,
                                                   'o_%s_%016x' % (tag, e['file_id']))
            # find which group this id belongs to
            name = None
            for base in ('tex/base/', 'tex/mask/', 'tex/albedo/'):
                pass
            Image.fromarray(new, 'RGBA').save(
                os.path.join(outdir, '%016x_new.png' % e['file_id']))
            tiles.append((e['file_id'], ref, new))
        n = len(tiles)
        cols = 2
        rows = (n + cols - 1) // cols
        sheet = Image.new('RGB', (cols * (S + 8) + 8, rows * (S + 30) + 8), (24, 24, 28))
        d = ImageDraw.Draw(sheet)
        for i, (fid, ref, new) in enumerate(tiles):
            cx = 8 + (i % cols) * (S + 8)
            cy = 8 + (i // cols) * (S + 30)
            if ref is not None:
                sheet.paste(Image.fromarray(ref[:, :, :3], 'RGB').resize((S, S), Image.LANCZOS),
                            (cx, cy + 18))
            sheet.paste(Image.fromarray(new[:, :, :3], 'RGB').resize((S, S), Image.LANCZOS),
                        (cx + S // 2, cy + 18))
            d.rectangle([cx + S // 2 - 1, cy + 18, cx + S // 2, cy + 18 + S], fill=(255, 90, 60))
            label(d, (cx, cy + 3), '%016x   left=主体 right=本包' % fid)
        p = os.path.join(outdir, 'compare.png')
        sheet.save(p)
        print('%-46s %2d textures -> %s' % (tag, n, p))


if __name__ == '__main__':
    main()
