# -*- coding: utf-8 -*-
"""核对：每个 MAT 变体包里被改的条目 id 是否与主体包 M8 完全一致（覆盖生效前提）。
只读，不改任何文件。"""
import os, sys, hashlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import hd2_patch as HP

MOD = r'E:/mod'
M8 = os.path.join(MOD, 'Lunalice-RE2310-M8')
PATCH = '9ba626afa44a3aa3.patch_0'
SIDECAR = PATCH + '.gpu_resources'

def load(p):
    ents, _ = HP.scan_entries(p)
    d = {}
    for e in ents:
        gpu = b''
        if e['gpu_size']:
            gpu = HP.read_range(p + '.gpu_resources', e['gpu_offset'], e['gpu_size'], True)
        toc = HP.read_range(p, e['toc_offset'], e['toc_size'])
        d[e['file_id']] = dict(type_id=e['type_id'], toc=toc, gpu=gpu)
    return d

m8 = load(os.path.join(M8, PATCH))
print('M8 entries:', len(m8))

mats = sorted(x for x in os.listdir(MOD) if x.startswith('Lunalice-RE2310-MAT-'))
allok = True
total_changed = 0
for d in mats:
    p = os.path.join(MOD, d, PATCH)
    if not os.path.exists(p):
        print('  [skip] no patch:', d); continue
    m = load(p)
    ids = list(m.keys())
    unknown = [i for i in ids if i not in m8]
    type_mismatch = [i for i in ids if i in m8 and m8[i]['type_id'] != m[i]['type_id']]
    print(d)
    print('   entries=%d  not_in_M8=%d  type_mismatch=%d' % (len(ids), len(unknown), len(type_mismatch)))
    for i in ids:
        e = m[i]
        if i not in m8:
            tag = 'NEW!!!'
        else:
            same = (m8[i]['toc'] == e['toc']) and (m8[i]['gpu'] == e['gpu'])
            tag = 'same-as-M8' if same else 'DIFF(overrides)'
            if not same: total_changed += 1
        print('     id=0x%016x type=0x%x toc=%dB gpu=%dB  %s'
              % (i, e['type_id'], len(e['toc']), len(e['gpu']), tag))
    if unknown or type_mismatch:
        allok = False
print()
print('ALL IDS PRESENT IN M8 & TYPE MATCH:', allok, '| total overriding entries:', total_changed)
