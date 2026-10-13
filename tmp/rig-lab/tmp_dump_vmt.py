# -*- coding: utf-8 -*-
import os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
packs = S.Packs()
targets = ['ACC_Metal_1','Ornament_metal_Underwear','Ornament_metal_HDBigRibbon',
           'Ornament_Pearl_HDBigRibbon','Ornament_jewel_HDBigRibbon','11','7','3',
           'Metal_2','Ornament_metal_Dress_Back_Ribbon']
print('vmt index size', len(packs.vmt))
for k in sorted(packs.vmt):
    pass
for t in targets:
    txt = packs.vmt_text(t)
    print('='*90)
    print('### %s -> %s' % (t, 'FOUND' if txt else 'MISSING'))
    if txt:
        print(txt)
