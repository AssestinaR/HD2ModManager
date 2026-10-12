"""Crop column A (x 12..282) of quad-fix2.png into its 4 rows, side by side.

Rows start at y = 150 + ri*(430+30), each 430 tall.
"""
import sys
from PIL import Image

src = sys.argv[1] if len(sys.argv) > 1 else 'out/quad-fix2.png'
dst = sys.argv[2] if len(sys.argv) > 2 else 'out/quad-fix2-colA.png'
PW, PH, GAP, TOP = 270, 430, 30, 150

im = Image.open(src).convert('RGB')
crops = [im.crop((12, TOP + ri * (PH + GAP), 12 + PW, TOP + ri * (PH + GAP) + PH))
         for ri in range(4)]
S = 2
W = 4 * (PW * S + 8) + 8
H = PH * S + 40
out = Image.new('RGB', (W, H), (28, 30, 36))
from PIL import ImageDraw
d = ImageDraw.Draw(out)
for i, c in enumerate(crops):
    c = c.resize((PW * S, PH * S), Image.NEAREST)
    x = 8 + i * (PW * S + 8)
    out.paste(c, (x, 32))
    d.text((x, 10), ['bind front', 'bind side', 'GAME front', 'GAME side'][i],
           fill=(230, 230, 240))
out.save(dst)
print('[written] %s  %dx%d' % (dst, W, H))
