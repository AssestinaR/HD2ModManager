# -*- coding: utf-8 -*-
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E, hd2_source as S, hd2_style as ST, numpy as np
vpk = S.ADDONS + S.MODEL_VPK
_md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
names, cds, _row = S.load_material_slots(mdl)
packs = S.Packs()
import fix_appearance as FA
groups = FA.analyse(np.asarray(tris4, np.int64), names, packs)
g = groups['matchacheese\\lunalice\\opt\\dress_white01_dress_corset'] if 'matchacheese\\lunalice\\opt\\dress_white01_dress_corset' in groups else None
for base, gg in groups.items():
    if os.path.basename(base)=='dress_white01_dress_corset':
        print('base=',repr(base)); print('slots=',gg['slots']); print('first slot name=',repr(names[gg['slots'][0]]))
        cur = packs.vmt_text(names[gg['slots'][0]])
        print('--- model-resolved VMT (via Packs) ---'); print(cur)
p = r'E:/tmp/pkg8/Lunalice基础材质B1服装色白.vpk'
_v, do, ents = E.parse_vpk_tree(p)
for f,a,o,l in ents:
    if os.path.basename(f.replace('/','\\')).lower()=='dress_white01_dress_corset.vmt':
        new = E.read_range(p, do, a, o, l).decode('latin-1','replace')
        print('--- B1 vpk copy ---'); print(new)
