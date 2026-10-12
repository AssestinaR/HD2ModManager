# -*- coding: utf-8 -*-
"""Which HD2 bones does the reference actually skin each unit to?

`bone_index` is MESH-LOCAL; `bi[lod]['remaps'][mat]` routes it to a palette
slot, and `bi[lod]['real']` should be that palette as scene-graph nodes.  If it
is, the reference's own bone choices become readable -- which is the thing we
want to copy, not just its weight numbers.
"""
import importlib.util
import os
import sys

import numpy as np

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
sys.path.insert(0, KIT)
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa43aa3.patch_0'
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
UNITS = [('left arm', 0x91eb7be84016ceab), ('right arm', 0x7336c6b662522f0e),
         ('torso', 0xcae35ee15168145f), ('hips', 0x17da843a4908511e),
         ('helmet', 0xaf431352d9b95e83)]


def imp(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(KIT, name + '.py'))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


hm = imp('hd2_mesh')
for label, uid in UNITS:
    u = hm.UnitMesh(REF, uid, lod=0)
    bi = u.bi[u.lod]
    print('\n=== %s  (0x%016x) ===' % (label, uid))
    print('   scene graph nodes %d   real[] len %d   first real: %s'
          % (u.sg['n'], len(bi['real']), bi['real'][:6]))
    print('   node_name(real[0..5]): %s'
          % [u.node_name(x) for x in bi['real'][:6]])
    # palette slot -> name via real[]
    slot_name = {}
    for i, r_ in enumerate(bi['real']):
        slot_name[i] = u.node_name(r_)
    w = u.bone_weight
    sl = hm.shipped_slot(u)
    vals, cnt = np.unique(sl, return_counts=True)
    order = np.argsort(-cnt)
    print('   %d palette slots actually used:' % len(vals))
    for o in order[:14]:
        s = int(vals[o])
        print('     slot %3d  %-24s %7d verts (%.1f%%)'
              % (s, slot_name.get(s, '?'), cnt[o], 100.0 * cnt[o] / len(sl)))
    # distinct influences, not just dominant
    used = set()
    for t in range(4):
        m_ = w[:, t] > 0
        rm = np.array([bi['remaps'][u.sections[0]['mat']][int(x)] if int(x) < len(bi['remaps'][u.sections[0]['mat']]) else int(x) for x in u.bone_index[:, t]])
        used.update(rm[m_].tolist())
    print('   distinct bones with ANY weight: %d -> %s'
          % (len(used), sorted(slot_name.get(s, '?') for s in used)))
