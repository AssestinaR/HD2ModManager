# -*- coding: utf-8 -*-
"""计算 7 个材质变体包之间被改条目的 id 重叠 —— 决定哪些可以同时启用。"""
import os, sys, itertools
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import hd2_patch as HP
MOD = r'E:/mod'; PATCH = '9ba626afa44a3aa3.patch_0'
M8 = os.path.join(MOD, 'Lunalice-RE2310-M8')

def ids_of(p):
    ents, _ = HP.scan_entries(p)
    return {e['file_id'] for e in ents}

m8 = ids_of(os.path.join(M8, PATCH))
mats = sorted(x for x in os.listdir(MOD) if x.startswith('Lunalice-RE2310-MAT-'))
sets = {d: ids_of(os.path.join(MOD, d, PATCH)) for d in mats}
for d in mats:
    print('%-32s %2d ids' % (d, len(sets[d])))
print()
print('=== pairwise overlaps (共享 id 数) ===')
for a, b in itertools.combinations(mats, 2):
    n = len(sets[a] & sets[b])
    if n: print('  %-30s <-> %-30s  %d' % (a.replace('Lunalice-RE2310-MAT-',''), b.replace('Lunalice-RE2310-MAT-',''), n))
print()
print('=== B2 的 16 个 id 明细 ===')
b2 = [d for d in mats if 'B2' in d][0]
for i in sorted(sets[b2]):
    print('   0x%016x' % i)
