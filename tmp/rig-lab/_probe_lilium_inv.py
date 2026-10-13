# -*- coding: utf-8 -*-
"""Lilium：逐 VPK 清点 .vmt / .vtf basename，并报告 45 个槽的解析结果。只读。"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import export_obj as E
import hd2_source as S
import hd2_style as ST

LIL = 'E:/tmp/lilium'
MODEL = 'Lilium_Sancta_Zoey.vpk'


def inventory(path):
    _v, do, ents = E.parse_vpk_tree(path)
    vmt, vtf = set(), set()
    for fn, a, o, l in ents:
        b = os.path.basename(fn.replace('/', '\\')).lower()
        if b.endswith('.vmt'):
            vmt.add(b)
        elif b.endswith('.vtf'):
            vtf.add(b)
    return vmt, vtf


def main():
    files = [f for f in os.listdir(LIL) if f.lower().endswith('.vpk')]
    sub = os.path.join(LIL, '材质开关')
    if os.path.isdir(sub):
        files += ['材质开关\\' + f for f in os.listdir(sub) if f.lower().endswith('.vpk')]
    files.sort()

    inv = {}
    print('=' * 100)
    print('%-58s %6s %6s' % ('VPK', '#vmt', '#vtf'))
    print('=' * 100)
    for f in files:
        vmt, vtf = inventory(os.path.join(LIL, f))
        inv[f] = (vmt, vtf)
        print('%-58s %6d %6d' % (f, len(vmt), len(vtf)))

    # 45 个槽需要哪些 vmt
    mdl_path, mdl, _v, tris, _s, _sh = ST.load_source(
        os.path.join(LIL, MODEL), 'survivor_teenangst')
    names, _cds, _skin = S.load_material_slots(mdl)
    want = set((n.split('\\')[-1].split('/')[-1] + '.vmt').lower() for n in names)

    print()
    print('槽 VMT 覆盖情况（按 basename）:')
    cover = {}
    for f in files:
        vmt, _ = inv[f]
        got = want & vmt
        for g in got:
            cover.setdefault(g, []).append(f)
        if got:
            print('  %-58s %d/%d' % (f, len(got), len(want)))
    miss = sorted(want - set(cover))
    print()
    print('没有任何 VPK 提供的槽 VMT: %s' % (miss or '无'))

    # 用「模型 + A1 + B1」解析，看每个槽的 basetexture 落在哪个 VPK
    print()
    for combo, tag in (([MODEL, 'Lilium_Sancta基础材质A1主体.vpk'], 'A1'),
                       ([MODEL, 'Lilium_Sancta基础材质A1主体.vpk',
                         'Lilium_Sancta基础材质B1服装色白.vpk'], 'A1+B1')):
        p = S.Packs(addons=LIL, packs=combo, model_vpk=MODEL)
        hit = 0
        vtfs = set()
        for n in names:
            t = p.vmt_text(n)
            if t:
                hit += 1
                v = S.parse_vmt(t)
                b = v.get('$basetexture')
                if b:
                    vtfs.add(os.path.basename(b).lower() + ('' if b.endswith('.vtf') else '.vtf'))
        have = set()
        for f in files:
            have |= {os.path.basename(x) for x in inv[f][1]}
        print('  组合 %-8s -> VMT 解析 %d/45   引用贴图 %d 个，其中已随包提供 %d 个'
              % (tag, hit, len(vtfs), len(vtfs & have)))
        if tag == 'A1+B1':
            lack = sorted(vtfs - have)
            print('     缺贴图: %s' % (lack or '无'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
