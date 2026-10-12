# -*- coding: utf-8 -*-
"""Can we read the REFERENCE patch's own HD2-native skin weights?

If yes, the plan "take the weights from the reference project" has a real
foundation instead of an assumption.  Uses the reference kit's own reader
(HD2PhysBone-ReferenceSource-main/kit/tools/hd2_mesh.py) so we are reading the
file the same way its author does.
"""
import importlib.util
import os
import sys

import numpy as np

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
sys.path.insert(0, KIT)

REF = ('E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0')
UNITS = [('left arm', 0x91eb7be84016ceab), ('right arm', 0x7336c6b662522f0e),
         ('torso', 0xcae35ee15168145f), ('hips', 0x17da843a4908511e),
         ('helmet', 0xaf431352d9b95e83)]


def imp(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(KIT, name + '.py'))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


hm = imp('hd2_mesh')
print('hd2_mesh imported OK')
for label, uid in UNITS:
    try:
        u = hm.UnitMesh(REF, uid, lod=0)
    except Exception as e:
        print('%-10s FAILED: %s: %s' % (label, type(e).__name__, e))
        continue
    w = u.bone_weight
    bi = u.bone_index
    nz = (w > 0).sum(1)
    print('%-10s %7d verts %7d tris  sections %2d  stream stride %s'
          % (label, len(u.positions), len(u.tris), len(u.sections),
             u.st[0]['comps'].get('bone_weight')))
    print('           influences/vertex: %s   sum(weights) min %.6f max %.6f'
          % (np.bincount(nz, minlength=5)[:5],
             float(w.sum(1).min()), float(w.sum(1).max())))
    print('           bone_index range %d..%d   remaps per section: %s'
          % (int(bi.min()), int(bi.max()),
             [len(u.bi[u.lod]['remaps'][s['mat']]) for s in u.sections][:8]))
    sl = hm.shipped_slot(u)
    names = u.names
    # palette slot -> name needs the unit's palette; print the raw slot census
    vals, cnt = np.unique(sl, return_counts=True)
    order = np.argsort(-cnt)[:8]
    print('           dominant palette slots (top 8 of %d): %s'
          % (len(vals), ', '.join('%d:%d' % (vals[i], cnt[i]) for i in order)))
