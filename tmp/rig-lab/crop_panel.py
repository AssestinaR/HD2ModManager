"""usage: crop_panel.py <src> <dst> <x> <y> <w> <h> [scale]"""
import sys
from PIL import Image

src, dst = sys.argv[1], sys.argv[2]
x, y, w, h = (int(v) for v in sys.argv[3:7])
s = int(sys.argv[7]) if len(sys.argv) > 7 else 2
im = Image.open(src).convert('RGB')
c = im.crop((x, y, x + w, y + h))
if s != 1:
    c = c.resize((w * s, h * s), Image.NEAREST)
c.save(dst)
print('[written] %s %dx%d' % (dst, c.width, c.height))
