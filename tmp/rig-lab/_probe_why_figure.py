# -*- coding: utf-8 -*-
"""Build the 'why washed out' illustration."""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = r'E:/tmp/r71-color'
os.makedirs(OUT, exist_ok=True)
W, H = 620, 88
samples = [('皮肤/肤色', (196, 160, 150)), ('皮肤阴影', (128, 100, 92)),
           ('银灰头发', (176, 176, 180)), ('白色布', (232, 230, 228)),
           ('近黑布', (26, 26, 28))]
img = Image.new('RGB', (W, H * len(samples) + 34), (24, 24, 28))
d = ImageDraw.Draw(img)
try:
    f = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 15)
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 13)
except Exception:
    f = fs = ImageFont.load_default()
d.text((8, 8), '同一张贴图纹素 -> L4D2 NekoToon 渲染 | HD2 advanced PBR 渲染', fill=(235, 235, 240), font=f)
third = W // 3
for i, (nm, rgb) in enumerate(samples):
    y = 34 + i * H
    l4 = [min(255, c * 0.298 * 0.8 + 255 * 0.31 * 0.30) for c in rgb]
    hd = [min(255, c * 1.25) for c in rgb]
    d.rectangle([0, y + 20, third - 4, y + H - 6], fill=tuple(rgb))
    d.rectangle([third, y + 20, 2 * third - 4, y + H - 6], fill=tuple(int(x) for x in l4))
    d.rectangle([2 * third, y + 20, W - 4, y + H - 6], fill=tuple(int(x) for x in hd))
    d.text((6, y + 1), nm, fill=(205, 205, 210), font=fs)
    d.text((third + 6, y + 1), 'L4D2（乘 0.30 上限）', fill=(205, 205, 210), font=fs)
    d.text((2 * third + 6, y + 1), 'HD2（PBR，无上限）', fill=(205, 205, 210), font=fs)
img.save(os.path.join(OUT, 'why_washed.png'))
print('saved', os.path.join(OUT, 'why_washed.png'))
