# -*- coding: utf-8 -*-
"""MourningVeil：列出每个槽引用的 matcap 贴图及其存在性（含 $MATCAP2/3）。
只读。"""
import os
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


def main():
    mp, mdl, verts, tris, src, short = ST.load_source(
        os.path.join(LIL, 'MourningVeil_Zoey.vpk'), 'survivor_teenangst')
    names, cds, skin = S.load_material_slots(mdl)
    packs = S.Packs(addons=LIL, packs=PACKS, model_vpk='MourningVeil_Zoey.vpk')
    KEYS = ('$matcaptexture', '$matcap2texture', '$matcap3texture')
    c = Counter()
    missing_slots = []
    for i, nm in enumerate(names):
        t = packs.vmt_text(nm)
        if t is None:
            continue
        v = S.parse_vmt(t)
        raw = v.get('_raw', {})
        for k in KEYS:
            val = raw.get(k) or v.get(k)
            if not val:
                continue
            val = val.strip().strip('"').strip()
            ok = packs.vtf_bytes(val) is not None
            c[(k, os.path.basename(val).lower(), ok)] += 1
            if not ok:
                missing_slots.append((nm, k, val))
    print('%-18s %-24s %-8s %s' % ('key', 'basename', 'exists', 'slots'))
    for (k, b, ok), n in sorted(c.items()):
        print('  %-16s %-24s %-8s %d' % (k, b, ok, n))
    print('')
    print('缺失 matcap 的槽 %d:' % len(missing_slots))
    for nm, k, val in missing_slots:
        print('   %-34s %-18s %s' % (nm, k, val))
    return 0


if __name__ == '__main__':
    sys.exit(main())
