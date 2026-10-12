# -*- coding: utf-8 -*-
"""How well does OUR mesh line up with the reference patch's units?

Weight transfer is only as good as this number.  If our surface and the
reference's coincide to a few mm, a nearest-point transfer is trustworthy; if
they are centimetres apart, the transfer would silently pick the wrong surface
and the gates in weight_transfer.py would have to reject most of it.

usage: _probe_refalign.py
"""
import importlib.util
import os
import sys

import numpy as np
from scipy.spatial import cKDTree

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
sys.path.insert(0, KIT)
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
d = np.load('out/blender/model.npz')
ours = np.asarray(d['ours_bind'], np.float64)
print('OURS  %d verts' % len(ours))
tree_ours = cKDTree(ours)

print('%-10s %8s %8s | %-28s | %-28s' % ('unit', 'verts', 'weld', 'REF -> our mesh (mm)',
                                         'our mesh -> REF (mm)'))
print('%-10s %8s %8s | %7s %7s %7s %7s | %7s %7s %7s %7s'
      % ('', '', '', 'p50', 'p90', 'max', '<5mm%', 'p50', 'p90', 'max', '<5mm%'))
allref = []
for label, uid in UNITS:
    u = hm.UnitMesh(REF, uid, lod=0)
    P = np.asarray(u.positions, np.float64)
    q = np.round(P / 1e-6).astype(np.int64)
    _, inv = np.unique(q, axis=0, return_inverse=True)
    wel = P[np.unique(inv)]
    allref.append(P)
    dd, _ = tree_ours.query(P)
    mm = dd * 1000.0
    print('%-10s %8d %8d | %7.1f %7.1f %7.1f %6.1f%%'
          % (label, len(P), len(wel), np.percentile(mm, 50),
             np.percentile(mm, 90), mm.max(), 100.0 * (mm < 5).mean()))
R = np.vstack(allref)
print('\nreference total %d verts (all five units)' % len(R))
tree_ref = cKDTree(R)
dd, _ = tree_ref.query(ours)
mm = dd * 1000.0
print('our mesh -> reference:  p50 %.1f  p90 %.1f  p99 %.1f  max %.1f mm   '
      'within 5mm %.1f%%  within 20mm %.1f%%'
      % (np.percentile(mm, 50), np.percentile(mm, 90), np.percentile(mm, 99),
         mm.max(), 100.0 * (mm < 5).mean(), 100.0 * (mm < 20).mean()))

# where are our worst vertices?
bad = np.argsort(-mm)[:12]
print('\nour 12 worst vertices (mm from the nearest reference vertex):')
for i in bad:
    print('   %6d  %6.1f mm  at (%+.3f, %+.3f, %+.3f)'
          % (i, mm[i], ours[i, 0], ours[i, 1], ours[i, 2]))
# z-histogram of the mismatch
print('\nour mesh, by height band (z): share of verts further than 20 mm')
for z0 in np.arange(0.0, 2.1, 0.25):
    sel = (ours[:, 2] >= z0) & (ours[:, 2] < z0 + 0.25)
    if sel.sum() < 50:
        continue
    print('   z %4.2f..%4.2f  n %6d   >20mm %5.1f%%   median %5.1f mm'
          % (z0, z0 + 0.25, int(sel.sum()), 100.0 * (mm[sel] > 20).mean(),
             np.median(mm[sel])))
