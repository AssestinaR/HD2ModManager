# -*- coding: utf-8 -*-
"""MourningVeil：58 个槽的 VMT 分别由哪个包提供（精确到槽名），
并列出每个包的 VMT 完整列表。只读。

用来决定 build_patch.py 要 --pack 哪些包，以及哪些槽必须靠某个包兜底。
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


def all_packs():
    fs = [f for f in os.listdir(LIL) if f.lower().endswith('.vpk')]
    sub = os.path.join(LIL, '材质开关')
    if os.path.isdir(sub):
        fs += ['材质开关/' + f for f in os.listdir(sub) if f.lower().endswith('.vpk')]
    return sorted(fs)


def main():
    vpk = os.path.join(LIL, MODEL_VPK)
    mdl_path, mdl, verts, tris, src, short = ST.load_source(vpk, 'survivor_teenangst')
    names, cds, skin = S.load_material_slots(mdl)
    used = sorted({int(t[3]) for t in tris})
    print('模型 %s : %d 槽 / 三角面引用 %d 槽' % (mdl_path, len(names), len(used)))
    print('')

    # 每个包提供的 vmt basename 集合
    prov = {}
    for f in all_packs():
        p = os.path.join(LIL, f)
        try:
            _v, do, ents = E.parse_vpk_tree(p)
        except Exception as ex:
            prov[f] = None
            print('  !! %s %s' % (f, ex))
            continue
        s = set()
        for fn, a, o, l in ents:
            b = os.path.basename(norm(fn))
            if b.endswith('.vmt'):
                s.add(b[:-4])
        prov[f] = s

    # 每个 used 槽由哪个包提供
    only_model = prov[MODEL_VPK] or set()
    print('%-56s %s' % ('槽名', '提供包'))
    miss = []
    for i in used:
        nm = names[i]
        base = os.path.basename(norm(nm))
        owners = [f for f, s in prov.items() if s and base in s]
        if not owners:
            miss.append(nm)
            print('  %-54s !! 无' % nm)
        else:
            print('  %-54s %s' % (nm, ' | '.join(owners)))
    print('')
    print('无包提供的槽: %d' % len(miss))
    for m in miss:
        print('   %s' % m)
    print('')
    print('各包 VMT 数量:')
    for f in all_packs():
        s = prov.get(f)
        print('  %-56s %s' % (f, 'ERR' if s is None else len(s)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
