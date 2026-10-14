# -*- coding: utf-8 -*-
"""MourningVeil：把行内 // 注释剥离后，统计 matcap 引用与存在性。只读。"""
import os
import re
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hd2_source as S
import hd2_style as ST

LIL = 'E:/tmp/mourning'
PACKS = ['MourningVeil_Zoey.vpk', 'MourningVeil基础材质A1主体.vpk',
         'MourningVeil基础材质B1服装色黑.vpk', 'MourningVeil基础材质B3服装色白紫.vpk',
         'MourningVeil基础材质C1发色浅棕.vpk', 'MourningVeil基础材质D1瞳色酒红.vpk',
         'MourningVeil基础材质E1光泽感肌肤.vpk']


def strip_comment(val):
    # drop an inline `// ...` (VMT uses it heavily) and any quotes
    val = val.split('//')[0]
    return val.strip().strip('"').strip()


def main():
    mp, mdl, verts, tris, src, short = ST.load_source(
        os.path.join(LIL, 'MourningVeil_Zoey.vpk'), 'survivor_teenangst')
    names, cds, skin = S.load_material_slots(mdl)
    packs = S.Packs(addons=LIL, packs=PACKS, model_vpk='MourningVeil_Zoey.vpk')
    c = Counter()
    missing = {}
    for i, nm in enumerate(names):
        t = packs.vmt_text(nm)
        if t is None:
            continue
        v = S.parse_vmt(t)
        raw = v.get('_raw', {})
        val = raw.get('$matcaptexture')
        if not val:
            continue
        p = strip_comment(val)
        ok = packs.vtf_bytes(p) is not None
        c[(os.path.basename(p).lower(), ok)] += 1
        if not ok:
            missing.setdefault(os.path.basename(p).lower(), []).append(nm)
    print('%-24s %-8s %s' % ('matcap basename', 'exists', 'slots'))
    for (b, ok), n in sorted(c.items()):
        print('  %-22s %-8s %d' % (b, ok, n))
    print('')
    print('缺失的 matcap:')
    for b, slots in sorted(missing.items()):
        print('  %-24s 槽: %s' % (b, ', '.join(slots)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
