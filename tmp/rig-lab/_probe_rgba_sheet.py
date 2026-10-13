# -*- coding: utf-8 -*-
"""Show a texture's RGB next to its alpha channel, so "is this a cutout?" is
answerable by eye.

usage: _probe_rgba_sheet.py <png> [<png> ...]
"""
import os
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent / 'out' / 'alpha'


def font(sz):
    for p in (r'C:\Windows\Fonts\msyh.ttc', r'C:\Windows\Fonts\segoeui.ttf'):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    T = 430
    for path in sys.argv[1:]:
        im = Image.open(path).convert('RGBA')
        a = np.asarray(im)
        al = a[:, :, 3]
        rgb = Image.fromarray(a[:, :, :3], 'RGB')
        # alpha as grey, and alpha hard-thresholded at 128 (what a cutout sees)
        ag = Image.fromarray(al, 'L')
        ab = Image.fromarray(np.where(al >= 128, 255, 0).astype(np.uint8), 'L')
        hist = np.bincount(al.ravel(), minlength=256)
        sheet = Image.new('RGB', (T * 3 + 40, T + 74), (16, 16, 20))
        d = ImageDraw.Draw(sheet)
        d.text((8, 6), '%s   %dx%d' % (os.path.basename(path), im.width, im.height),
               font=font(18), fill=(255, 235, 120))
        d.text((8, 28), 'alpha: ==0 %.1f%%   0<a<255 %.1f%%   ==255 %.1f%%   mean %.1f'
               % (100.0 * (al == 0).mean(), 100.0 * ((al > 0) & (al < 255)).mean(),
                  100.0 * (al == 255).mean(), al.mean()), font=font(15), fill=(200, 200, 210))
        d.text((8, 48), 'nonzero but <128: %.2f%% of image  (holes that a cutout at 0.5 would punch)'
               % (100.0 * ((al > 0) & (al < 128)).mean()), font=font(15), fill=(150, 230, 255))
        for i, (img, lab) in enumerate(((rgb, 'RGB'), (ag, 'alpha (grey)'),
                                        (ab, 'alpha >= 128 (cutout view)'))):
            x = 8 + i * (T + 12)
            d.text((x, T + 54), lab, font=font(15), fill=(255, 200, 120))
            sheet.paste(img.convert('RGB').resize((T, T)), (x, 70))
        out = OUT / ('rgba_' + os.path.basename(path))
        sheet.save(out)
        print('wrote', out, ' alpha histogram top bins:',
              sorted(range(256), key=lambda i: -hist[i])[:6])
    return 0


if __name__ == '__main__':
    sys.exit(main())
