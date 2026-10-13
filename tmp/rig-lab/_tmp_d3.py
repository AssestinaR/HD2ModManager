# -*- coding: utf-8 -*-
import os, sys, difflib
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E, hd2_source as S, hd2_style as ST, numpy as np
import fix_appearance as FA
vpk = S.ADDONS + S.MODEL_VPK
_md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
names, cds, _row = S.load_material_slots(mdl)
packs = S.Packs()
groups = FA.analyse(np.asarray(tris4, np.int64), names, packs)
targets = ['dress_white01_dress_corset','shoes_02','glove_white01_glove_arm','bra_white01_armcuff']
for base, gg in groups.items():
    b = os.path.basename(base).lower()
    if b not in targets: continue
    first = gg['slots'][0]
    nm = names[first]
    cur = packs.vmt_text(nm) or ''
    print('='*70); print('GROUP', b, 'first slot', first, repr(nm))
    print('  active keys:', {k:v for k,v in FA.active_keys(cur).items() if k in ('$translucent','$alphatest','$basetexture','$matcaptint')})
    # find same-named vmt inside B1
    p = r'E:/tmp/pkg8/Lunalice基础材质B1服装色白.vpk'
    _v, do, ents = E.parse_vpk_tree(p)
    for f,a,o,l in ents:
        fb = os.path.basename(f.replace('/','\\')).lower()
        if fb == b + '.vmt':
            new = E.read_range(p, do, a, o, l).decode('latin-1','replace')
            a1 = FA.active_keys(cur); a2 = FA.active_keys(new)
            for k in sorted(set(a1)|set(a2)):
                if a1.get(k)!=a2.get(k):
                    print('   DIFF %-24s model=%r  B1=%r' % (k, a1.get(k), a2.get(k)))
            if not [k for k in set(a1)|set(a2) if a1.get(k)!=a2.get(k)]:
                print('   (active keys identical; raw text differs)')
                d = list(difflib.unified_diff(cur.splitlines(), new.splitlines(), lineterm='', n=1))
                print('\n'.join(d[:24]))
