# -*- coding: utf-8 -*-
"""MourningVeil：C/D/E 包（0 个 VMT）到底替换了哪些贴图？
以及 8 个角色 VPK 各自提供什么（模型/贴图？）。只读。
"""
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import export_obj as E

LIL = 'E:/tmp/mourning'


def norm(p):
    return p.replace('/', '\\').lower()


def packs():
    fs = [f for f in os.listdir(LIL) if f.lower().endswith('.vpk')]
    sub = os.path.join(LIL, '材质开关')
    if os.path.isdir(sub):
        fs += ['材质开关/' + f for f in os.listdir(sub) if f.lower().endswith('.vpk')]
    for f in os.listdir(os.path.join(LIL, '材质开关', '身体描边关闭时手套关')):
        if f.lower().endswith('.vpk'):
            fs.append('材质开关/身体描边关闭时手套关/' + f)
    for f in os.listdir(os.path.join(LIL, '材质开关', '身体描边开启时手套关')):
        if f.lower().endswith('.vpk'):
            fs.append('材质开关/身体描边开启时手套关/' + f)
    return sorted(fs)


def main():
    table = {}
    for f in packs():
        p = os.path.join(LIL, f)
        try:
            _v, do, ents = E.parse_vpk_tree(p)
        except Exception as ex:
            print('!! %s %s' % (f, ex))
            continue
        vmt, vtf, other = set(), set(), set()
        for fn, a, o, l in ents:
            b = os.path.basename(norm(fn))
            if b.endswith('.vmt'):
                vmt.add(b[:-4])
            elif b.endswith('.vtf'):
                vtf.add(b[:-4])
            else:
                other.add(b)
        table[f] = (vmt, vtf, other)

    print('%-58s %4s %4s %s' % ('包', 'vmt', 'vtf', '其它'))
    for f in sorted(table):
        vmt, vtf, other = table[f]
        print('  %-56s %4d %4d %s' % (f, len(vmt), len(vtf),
                                      ','.join(sorted(other))[:40]))

    print('')
    print('=== C/D/E 包替换的贴图 ===')
    for f in sorted(table):
        if ('发色' in f or '瞳色' in f or '光泽' in f):
            print('  %s' % f)
            print('     vtf: %s' % sorted(table[f][1]))

    print('')
    print('=== 被 >=2 个包共享的贴图（潜在冲突点）===')
    owner = defaultdict(set)
    for f, (vmt, vtf, _o) in table.items():
        for t in vtf:
            owner[t].add(f)
    shared = {t: s for t, s in owner.items() if len(s) > 1}
    for t in sorted(shared):
        print('  %-40s %s' % (t, ' | '.join(sorted(shared[t]))))
    print('  共享贴图数: %d / 总数 %d' % (len(shared), len(owner)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
