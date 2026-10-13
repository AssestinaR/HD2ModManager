# -*- coding: utf-8 -*-
"""Zoom on a texture's alpha: is the nonzero-alpha region a *shape* (a mask) or
just noise?  Shows RGB, alpha x8, and the alpha>0 silhouette.

usage: _probe_alphashape.py <png> [<png> ...]
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
    T = 500
    for path in sys.argv[1:]:
        im = Image.open(path).convert('RGBA')
        a = np.asarray(im)
        al = a[:, :, 3]
        rgb = Image.fromarray(a[:, :, :3], 'RGB')
        boosted = Image.fromarray(np.clip(al.astype(np.int32) * 8, 0, 255).astype(np.uint8), 'L')
        shape = Image.fromarray(np.where(al > 0, 255, 0).astype(np.uint8), 'L')
        sheet = Image.new('RGB', (T * 3 + 40, T + 60), (16, 16, 20))
        d = ImageDraw.Draw(sheet)
        d.text((8, 6), '%s  %dx%d   alpha: >0 covers %.2f%%   mid %.2f%%'
               % (os.path.basename(path), im.width, im.height,
                  100.0 * (al > 0).mean(), 100.0 * ((al > 0) & (al < 255)).mean()),
               font=font(17), fill=(255, 235, 120))
        for i, (img, lab) in enumerate(((rgb, 'RGB'), (boosted, 'alpha x8 (amplified)'),
                                        (shape, 'alpha > 0 silhouette'))):
            x = 8 + i * (T + 12)
            d.text((x, T + 34), lab, font=font(15), fill=(255, 200, 120))
            sheet.paste(img.convert('RGB').resize((T, T)), (x, 30))
        out = OUT / ('shape_' + os.path.basename(path))
        sheet.save(out)
        print('wrote', out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
