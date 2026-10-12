# -*- coding: utf-8 -*-
"""Do OUR weights agree with the REFERENCE's, vertex by vertex?

This is the number that decides whether "copy the reference's weights" is worth
doing.  For every vertex of our mesh, take the nearest reference vertex (the
surfaces agree to p50 7.8 mm) and compare the DOMINANT HD2 bone.  Agreement
means our name/region mapping already lands on the same bone the reference's
author chose; disagreement is exactly the list a transfer would fix.

usage: _probe_weightcompare.py
"""
import collections
import importlib.util
import json
import os
import sys

import numpy as np
from scipy.spatial import cKDTree

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
sys.path.insert(0, KIT)
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
UNITS = [('left arm', 0x91eb7be84016ceab), ('right arm', 0x7336c6b662522f0e),
         ('torso', 0xcae35ee15168145f), ('hips', 0x17da843a4908511e),
         ('torso2', 0xaf431352d9b95e83)]


def imp(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(KIT, name + '.py'))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


hm = imp('hd2_mesh')
d = np.load('out/blender/model.npz')
m = json.load(open('out/blender/meta.json', encoding='utf-8'))
names = m['bones']
P = np.asarray(d['ours_bind'], np.float64)
wb = d['w_bone'].astype(np.int64)
wv = np.asarray(d['w_val'], np.float64)
live = wb >= 0
our_dom = np.array([names[b] for b in
                    wb[np.arange(len(P)), np.argmax(np.where(live, wv, -1), axis=1)]])

RP, Rname = [], []
for label, uid in UNITS:
    u = hm.UnitMesh(REF, uid, lod=0)
    bi = u.bi[u.lod]
    slot_name = {i: u.node_name(r_) for i, r_ in enumerate(bi['real'])}
    sec = hm.section_of_vertex(u)
    dom = np.zeros(len(u.positions), np.int64)
    for si, s in enumerate(u.sections):
        rows = np.flatnonzero(sec == si)
        if not len(rows):
            continue
        rm = bi['remaps'][s['mat']]
        picked = u.bone_index[rows][np.arange(len(rows)),
                                    np.argmax(u.bone_weight[rows], axis=1)]
        dom[rows] = [rm[int(x)] if int(x) < len(rm) else int(x) for x in picked]
    RP.append(np.asarray(u.positions, np.float64))
    Rname.append(np.array([slot_name.get(int(x), '?') for x in dom]))
    print('%-10s %7d verts  bones used: %s'
          % (label, len(u.positions), len(set(Rname[-1].tolist()))))
RP = np.concatenate(RP)
Rname = np.concatenate(Rname)

tree = cKDTree(RP)
dist, idx = tree.query(P)
rn = Rname[idx]
mm = dist * 1000.0
print('\nnearest reference vertex: p50 %.1f  p90 %.1f mm' %
      (np.median(mm), np.percentile(mm, 90)))

agree = our_dom == rn
print('\n== dominant-bone agreement over all %d vertices ==========' % len(P))
print('   agree  %6d  (%.1f%%)' % (int(agree.sum()), 100.0 * agree.mean()))
print('   differ %6d  (%.1f%%)' % (int((~agree).sum()), 100.0 * (~agree).mean()))

# restrict to vertices where the surfaces actually coincide
close = mm < 5.0
print('   among the %d vertices within 5 mm of the reference: agree %.1f%%'
      % (int(close.sum()), 100.0 * agree[close].mean()))

print('\n== where they differ =====================================')
pairs = collections.Counter(zip(our_dom[~agree].tolist(), rn[~agree].tolist()))
print('   top 18 (ours -> reference), with the median surface distance:')
for (a_, b_), n in pairs.most_common(18):
    sel = (~agree) & (our_dom == a_) & (rn == b_)
    print('     %-22s -> %-22s %6d  (%.1f%%)  d50 %5.1f mm'
          % (a_, b_, n, 100.0 * n / len(P), np.median(mm[sel])))

print('\n== bones the reference uses that we never do ==============')
ourset = set(our_dom.tolist())
refset = set(rn.tolist())
print('   only in the reference: %s' % sorted(refset - ourset))
print('   only in ours:          %s' % sorted(ourset - refset))
