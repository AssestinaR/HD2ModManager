"""Zoom into the LEG region of the ref-study sheet.

The sheet is 4 columns x 2 rows; row 1 = front (X-Z), row 2 = side (Y-Z).
We only care about the lower body here, so crop the bottom part of the panel
and blow it up, for columns 2 and 4 (ours leaned 5.7 deg, and the reference
patch with its own skeleton drawn on it).
"""
import sys

from PIL import Image

SRC = sys.argv[1] if len(sys.argv) > 1 else 'out/ref-study.png'
TAG = sys.argv[2] if len(sys.argv) > 2 else 'legs'
ZOOM = int(sys.argv[3]) if len(sys.argv) > 3 else 4

# geometry of ref_study.py
X0, PW, GAPX = 12, 300, 12
Y0, PH, GAPY = 64, 460, 30

im = Image.open(SRC).convert('RGB')
print('sheet %s' % (im.size,))
for c in (1, 3):          # 0-based: col 2 and col 4
    for r in (0, 1):
        x = X0 + c * (PW + GAPX)
        y = Y0 + r * (PH + GAPY)
        # lower 55% of the panel = hips down
        top = y + int(PH * 0.45)
        box = (x, top, x + PW, y + PH)
        crop = im.crop(box)
        crop = crop.resize((crop.width * ZOOM, crop.height * ZOOM), Image.LANCZOS)
        out = 'out/_%s-c%d-r%d.png' % (TAG, c + 1, r + 1)
        crop.save(out)
        print(out, crop.size)
