"""Crop the four panels out of a comparison sheet and enlarge them, so the
silhouettes can actually be judged by eye (which is the only test that counts
here -- the axis guides were hiding the real deformation)."""
import sys

from PIL import Image

SRC = sys.argv[1]
TAG = sys.argv[2]
PW, PH = 340, 470
X0, Y0, GAP, COLS, ROWS = 12, 60, 14, 4, 2
ROW_GAP = 26

im = Image.open(SRC).convert('RGB')
for c in range(COLS):
    for r in range(ROWS):
        x = X0 + c * (PW + GAP)
        y = Y0 + r * (PH + ROW_GAP)
        box = (x, y, x + PW, y + PH)
        crop = im.crop(box).resize((PW * 2, PH * 2), Image.LANCZOS)
        out = 'out/_crop-%s-c%d-r%d.png' % (TAG, c + 1, r + 1)
        crop.save(out)
        print(out, crop.size)
