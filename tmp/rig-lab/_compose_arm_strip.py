# -*- coding: utf-8 -*-
"""Compose a labelled grid of arm renders (same camera, so they compare)."""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

CJK = ['C:/Windows/Fonts/msyh.ttc', 'C:/Windows/Fonts/simhei.ttf',
       'C:/Windows/Fonts/simsun.ttc']


def font(sz):
    for p in CJK:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    argv = sys.argv[1:]
    out = argv[0]
    argv = argv[1:]
    cols, title, W = 2, '', 1000
    rest = []
    for a in argv:
        if a.startswith('--cols='):
            cols = int(a.split('=', 1)[1])
        elif a.startswith('--width='):
            W = int(a.split('=', 1)[1])
        elif a.startswith('--title='):
            title = a.split('=', 1)[1]
        else:
            rest.append(a)
    items = []
    for c in rest:
        lab, path = c.split('=', 1)
        items.append((lab, Image.open(path).convert('RGB')))
    rows = (len(items) + cols - 1) // cols
    tiles = [(lab, im.resize((W, int(round(im.height * W / im.width))),
                             Image.LANCZOS)) for lab, im in items]
    th = max(t.height for _l, t in tiles)
    HEAD = 34
    PAD = 8
    TITLE = 40 if title else 0
    sheet = Image.new('RGB', (PAD + cols * (W + PAD),
                              PAD + TITLE + rows * (HEAD + th + PAD)),
                      (10, 11, 15))
    dr = ImageDraw.Draw(sheet)
    f = font(22)
    if title:
        dr.text((PAD + 6, PAD + 8), title, fill=(236, 240, 248), font=font(26))
    y0 = PAD + TITLE
    for i, (lab, im) in enumerate(tiles):
        r, c = divmod(i, cols)
        x = PAD + c * (W + PAD)
        y = y0 + r * (HEAD + th + PAD)
        dr.text((x + 6, y + 5), lab, fill=(255, 214, 120), font=f)
        sheet.paste(im, (x, y + HEAD))
    sheet.save(out)
    print('wrote %s (%dx%d)' % (out, sheet.width, sheet.height))


if __name__ == '__main__':
    main()
