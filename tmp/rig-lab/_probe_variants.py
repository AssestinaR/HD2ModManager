# -*- coding: utf-8 -*-
"""Slim or Stocky?  The reference ships BOTH body types of every garment.

The pipeline currently mixes them (Slim torso/arms, Stocky hips).  If our mesh
is one body type, comparing against the other adds a systematic offset to every
alignment number we have taken -- including the weight comparison.
"""
import importlib.util
import json
import os
import sys

import numpy as np
from scipy.spatial import cKDTree

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
sys.path.insert(0, KIT)
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'


def imp(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(KIT, name + '.py'))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


hm = imp('hd2_mesh')
d = np.load('out/blender/model.npz')
ours = np.asarray(d['ours_bind'], np.float64)

meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
rows = []
for u in meta:
    fid = int(u['unitFileId'], 16)
    tag = u.get('tag') or ''
    if u.get('vertices', 0) < 1000:
        continue
    try:
        um = hm.UnitMesh(REF, fid, lod=0)
    except Exception as e:
        rows.append((tag, fid, -1, -1, str(e)[:30]))
        continue
    P = np.asarray(um.positions, np.float64)
    if len(P) < 1000:
        continue
    t = cKDTree(P)
    dd, _ = t.query(ours)
    mm = dd * 1000.0
    rows.append((tag, fid, np.median(mm), np.percentile(mm, 90),
                 '%d verts' % len(P)))

rows.sort(key=lambda r: (r[0], r[2]))
print('%-34s %-18s %8s %8s  %s' % ('tag', 'fileId', 'p50 mm', 'p90 mm', 'note'))
for tag, fid, p50, p90, note in rows:
    print('%-34s %016x %8.1f %8.1f  %s' % (tag, fid, p50, p90, note))
