# -*- coding: utf-8 -*-
"""Round 54e: does HD2's finger chain end up INSIDE our finger mesh?

At runtime the game rotates the mesh about HD2's joints.  If a joint sits
outside the finger it is supposed to bend, the finger will hinge around thin air.
So for every HD2 finger joint, report the distance to the nearest of OUR
finger-dominant vertices, and the same for the reference patch.

usage: python _probe_fingers5.py
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

FINGERS = [('thumb', 'thumb_finger'), ('index', 'index_finger'),
           ('middle', 'middle_finger'), ('ring', 'ring_finger'),
           ('pinky', 'pinky_finger')]

m = json.load(open('out/blender/meta.json', encoding='utf-8'))
d = np.load('out/blender/model.npz')
names = m['bones']
bh = d['b_head'].astype(float)
idx = {n: i for i, n in enumerate(names) if n}
ours = np.asarray(d['ours_bind'], float)
wb = d['w_bone'].astype(np.int64)
wv = d['w_val'].astype(float)
ws = np.maximum(wv.sum(1), 1e-9)
dom = wb[np.arange(len(wb)), np.argmax(wv / ws[:, None], axis=1)]

ref_all = None
for fn in ('out/patchdump/unit-91eb7be84016ceab.obj',
           'out/patchdump/unit-7336c6b662522f0e.obj'):
    try:
        p, _ = PV.read_obj(fn)
        p = np.asarray(p, float)
    except Exception:
        continue
    if p[:, 0].mean() < 0:
        ref_all = p
        break

SIDE = 'l'
WRIST = bh[idx['%s_hand' % SIDE]]
zap = WRIST - bh[idx['%s_elbow' % SIDE]]
zap = zap / np.linalg.norm(zap)
REF = None
if ref_all is not None:
    keep = (np.linalg.norm(ref_all - WRIST, axis=1) < 0.26) \
        & (((ref_all - WRIST) @ zap) > -0.02)
    REF = ref_all[keep]


def nearest(P, Q):
    return float(np.sqrt(((P[None, :, :] - Q[:, None, :]) ** 2).sum(-1)).min())


print('%-8s %-10s | %-22s | %-22s' % ('finger', 'joint', 'OURS (dist to our mesh)',
                                      'REF (dist to the ref mesh)'))
for label, stem in FINGERS:
    for k in (1, 2, 3):
        bi = idx['%s_%s%d' % (SIDE, stem, k)]
        j = bh[bi]
        ids = [idx['%s_%s%d' % (SIDE, stem, q)] for q in (1, 2, 3)]
        O = ours[np.isin(dom, ids)]
        # subsample for speed; both clouds are dense
        do = nearest(O[::2], j[None, :]) if len(O) else float('nan')
        dr = nearest(REF[::3], j[None, :]) if REF is not None and len(REF) else float('nan')
        print('%-8s %-10s | %8.1f mm            | %8.1f mm' %
              (label, '%s_f%d' % (stem, k), 1000 * do, 1000 * dr))
