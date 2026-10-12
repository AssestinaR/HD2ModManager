"""Crop selected columns of a quad_overlay image and lay them out side by side.

usage: crop_cols.py <src> <dst> <colA> [colB ...]
"""
import sys
from PIL import Image, ImageDraw

src, dst = sys.argv[1], sys.argv[2]
cols = [int(x) for x in sys.argv[3:]] or [0, 2]
PW, PH, GAP, TOP, LEFTPAD = 270, 430, 30, 150, 12

im = Image.open(src).convert('RGB')
S = 1
PAD = 8
W = len(cols) * (PW + PAD) + PAD
H = 28 + 4 * (PH + PAD) + PAD
out = Image.new('RGB', (W, H), (28, 30, 36))
d = ImageDraw.Draw(out)
for ci, c in enumerate(cols):
    x = PAD + ci * (PW + PAD)
    for ri in range(4):
        crop = im.crop((LEFTPAD + c * (PW + 10), TOP + ri * (PH + GAP),
                        LEFTPAD + c * (PW + 10) + PW,
                        TOP + ri * (PH + GAP) + PH))
        out.paste(crop, (x, 28 + ri * (PH + PAD)))
    d.text((x, 10), 'column %d' % c, fill=(230, 230, 240))
out.save(dst)
print('[written] %s  %dx%d' % (dst, W, H))
