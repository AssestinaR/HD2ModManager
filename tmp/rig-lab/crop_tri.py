"""Crop one row of a tri_overlay image at 2x.

usage: crop_tri.py <src> <dst> <row> [row ...]
"""
import sys
from PIL import Image, ImageDraw

src, dst = sys.argv[1], sys.argv[2]
rows = [int(x) for x in sys.argv[3:]] or [1]
PW, PH, GAP, TOP, PAD = 330, 470, 30, 96, 12

im = Image.open(src).convert('RGB')
S = 2
W = 2 * (PW * S + 10) + 20
H = len(rows) * (PH * S + 30) + 20
out = Image.new('RGB', (W, H), (28, 30, 36))
d = ImageDraw.Draw(out)
for i, ri in enumerate(rows):
    cy = TOP + ri * (PH + GAP)
    for ci in range(2):
        cx = PAD + ci * (PW + 10)
        crop = im.crop((cx, cy - 28, cx + PW, cy + PH))
        out.paste(crop.resize(((PW) * S, (PH + 28) * S), Image.NEAREST),
                  (10 + ci * (PW * S + 10), 10 + i * (PH * S + 30)))
    d.text((12, 12 + i * (PH * S + 30)), 'row %d  (left = ours, right = reference)'
           % ri, fill=(235, 235, 245))
out.save(dst)
print('[written] %s  %dx%d' % (dst, W, H))
