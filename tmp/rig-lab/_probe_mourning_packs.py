# -*- coding: utf-8 -*-
"""Lilium：每个材质槽的 VMT/VTF 到底由哪个 VPK 提供？

决定 `S.Packs(packs=...)` 该装哪些包。只读。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import export_obj as E
import hd2_source as S
import hd2_style as ST

LIL = 'E:/tmp/mourning'
MODEL_VPK = 'MourningVeil_Zoey.vpk'


def norm(p):
    return p.replace('/', '\\').lower()


def main():
    vpk = os.path.join(LIL, MODEL_VPK)
    mdl_path, mdl, verts, tris, src, short = ST.load_source(vpk, 'survivor_teenangst')
    names, cds, skin = S.load_material_slots(mdl)
    used = sorted({int(t[3]) for t in tris})
    print('模型 %s : %d 槽 / 三角面引用 %d 槽' % (mdl_path, len(names), len(used)))

    # 每个槽引用到的 basetexture
    packs = S.Packs(addons=LIL, packs=[MODEL_VPK], model_vpk=MODEL_VPK)
    print('仅模型 VPK：vmt=%d vtf=%d' % (len(packs.vmt), len(packs.vtf)))
    miss_vmt, hit_vmt = [], []
    bases = {}
    for i in used:
        nm = names[i]
        txt = packs.vmt_text(nm)
        if txt is None:
            miss_vmt.append(nm)
        else:
            hit_vmt.append(nm)
            v = S.parse_vmt(txt)
            bases[i] = v.get('$basetexture')
    print('  槽 VMT 命中 %d / 缺失 %d' % (len(hit_vmt), len(miss_vmt)))
    if miss_vmt:
        print('  缺失槽: %s' % miss_vmt)

    # 缺的 VMT 与引用的贴图，去哪个包找
    allv = [f for f in os.listdir(LIL) if f.lower().endswith('.vpk')]
    sub = os.path.join(LIL, '材质开关')
    if os.path.isdir(sub):
        allv += ['材质开关/' + f for f in os.listdir(sub) if f.lower().endswith('.vpk')]

    # VPK 里存的是完整路径，槽名只是 basename -> 一律按 basename 比
    want_vmt = set((m.split('\\')[-1].split('/')[-1] + '.vmt').lower()
                   for m in names)
    want_vtf = set()
    for i, b in bases.items():
        if b:
            want_vtf.add((b if b.endswith('.vtf') else b + '.vtf').lower())
    print()
    print('需要的 VMT basename: %d 个' % len(want_vmt))
    print('引用的贴图 basename: %d 个' % len(want_vtf))
    print()
    print('%-52s %s' % ('VPK', '提供(vmt / vtf)'))
    for f in allv:
        p = os.path.join(LIL, f)
        try:
            _v, do, ents = E.parse_vpk_tree(p)
        except Exception as ex:
            print('  !! %s %s' % (f, ex))
            continue
        got_vmt = sum(1 for fn, a, o, l in ents
                      if os.path.basename(norm(fn)) in want_vmt)
        got_vtf = sum(1 for fn, a, o, l in ents
                      if os.path.basename(norm(fn)) in want_vtf)
        if got_vmt or got_vtf:
            print('  %-50s vmt=%-3d vtf=%-3d' % (f, got_vmt, got_vtf))
    return 0


if __name__ == '__main__':
    sys.exit(main())
