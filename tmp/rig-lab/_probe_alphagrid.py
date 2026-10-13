# -*- coding: utf-8 -*-
"""Contact sheet of the source alpha channels, in a fixed readable order."""
import sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

SRC = Path(__file__).resolve().parent / 'out' / 'srcmask'
ORDER = ['headdress_white01_catears', 'lace_white01_catears',
         'bra_white01_bra_inner', 'lower_white01_underwear',
         'dress_white01_dress_corset', 'glove_white01_wristcuffs',
         'shoes_white01_highheels', 'shoes_02']
S = 400
cols = 2
rows = (len(ORDER) + cols - 1) // cols
sheet = Image.new('RGB', (cols * S, rows * (S + 26)), (24, 24, 24))
d = ImageDraw.Draw(sheet)
for i, nm in enumerate(ORDER):
    p = SRC / (nm + '_alpha.png')
    if not p.exists():
        continue
    a = Image.open(p).convert('L').resize((S, S), Image.LANCZOS)
    r, c = divmod(i, cols)
    y = r * (S + 26)
    sheet.paste(Image.merge('RGB', (a, a, a)), (c * S, y + 26))
    d.text((c * S + 6, y + 6), '%d  %s' % (i, nm), fill=(255, 230, 120))
out = SRC / '_alpha_grid.png'
sheet.save(out)
print('->', out)
