# -*- coding: utf-8 -*-
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E, hd2_source as S, hd2_style as ST, numpy as np
vpk = S.ADDONS + S.MODEL_VPK
_md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
names, cds, _row = S.load_material_slots(mdl)
print('cdtextures', cds)
print('--- first 12 slot names ---')
for i in range(12):
    print('  %2d %r' % (i, names[i]))
print('--- names containing .vmt/.vtf ? ---')
print(sum(1 for n in names if n.lower().endswith(('.vmt', '.vtf'))), 'of', len(names))
print('--- sample A1 raw entries ---')
for f,a,o,l in E.parse_vpk_tree(r'E:/tmp/pkg8/Lunalice基础材质A1主体.vpk')[2]:
    print('   ', repr(f), l)
