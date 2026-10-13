# -*- coding: utf-8 -*-
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
def show(p, want=None, maxb=2600):
    _v, do, ents = E.parse_vpk_tree(p)
    print('='*100); print(os.path.basename(p))
    for f,a,o,l in ents:
        b = os.path.basename(f.replace('/','\\'))
        print('   %-52s %8d' % (b, l))
    if want:
        for f,a,o,l in ents:
            b = os.path.basename(f.replace('/','\\')).lower()
            if b == want.lower():
                print('-'*80); print(E.read_range(p, do, a, o, l).decode('latin-1','replace')[:maxb])
show(r'E:/tmp/pkg8/材质开关/Lunalice手套Glove关.vpk', 'glove_white01_lace_glove.vmt')
show(r'E:/tmp/pkg8/材质开关/Lunalice猫耳CatEars关.vpk', 'shinano_hair_ear.vmt')
show(r'E:/tmp/pkg8/Lunalice基础材质F1身体描边.vpk')
show(r'E:/tmp/pkg8/Lunalice基础材质F2身体描边无.vpk')
show(r'E:/tmp/pkg8/材质开关/Lunalice静态造型表情切换PoseExpressionChange.vpk')
