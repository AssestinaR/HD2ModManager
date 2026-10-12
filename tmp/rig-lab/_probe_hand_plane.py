# -*- coding: utf-8 -*-
"""Round 53: the PALM-PLANE normal, ours vs the reference's.

A centroid cannot tell a 10 deg rotation from a 1 cm translation, and the hand
is only 10 cm long -- so measure the thing a rotation actually changes: the
normal of the palm slab.  A plane fit is translation-invariant, which is exactly
what we want here.

No retarget run: ours comes from out/blender/model.npz (which is tilt 0), the
reference straight from out/patchdump/*.obj, and the frame from the rig in the
npz.  So this is a 5-second check.

usage: python _probe_hand_plane.py
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV


def u(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def ang(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(u(a) @ u(b)), -1, 1))))


m = json.load(open('out/blender/meta.json', encoding='utf-8'))
d = np.load('out/blender/model.npz')
names = m['bones']
bh = d['b_head'].astype(float)
idx = {n: i for i, n in enumerate(names) if n}

S = 'l'
wrist = bh[idx['%s_hand' % S]].copy()
zap = u(wrist - bh[idx['%s_elbow' % S]])                    # A-pose forearm axis
xap = u(bh[idx['%s_thumb_finger1' % S]] - wrist)            # toward the thumb
nap = u(np.cross(zap, xap))                                 # 3rd orthogonal axis

print('wrist %s' % np.array2string(wrist, precision=4))
print('zap (forearm)  %s' % np.array2string(zap, precision=4))
print('xap (to thumb) %s' % np.array2string(xap, precision=4))
print('nap = zap x xap %s' % np.array2string(nap, precision=4))

# the palm slab: distal of the wrist, but NOT out along the thumb
def palm_slab(P):
    rel = P - wrist
    t = rel @ zap
    ang_thumb = np.degrees(np.arccos(np.clip((rel @ xap) /
                                             np.maximum(np.linalg.norm(rel, axis=1), 1e-9),
                                             -1, 1)))
    sel = (np.linalg.norm(rel, axis=1) < 0.105) & (t > 0.012) & (t < 0.098) & (ang_thumb > 45)
    return P[sel], int(sel.sum())


def plane_normal(Q):
    c = Q.mean(0)
    _w, _v = np.linalg.eigh((Q - c).T @ (Q - c))
    n = u(_v[:, 0])
    rms = float(np.sqrt(((Q - c) @ n) ** 2).mean())
    return n, rms


ours = np.asarray(d['ours_bind'], float)
O, no = palm_slab(ours)
n_o, rms_o = plane_normal(O)
print()
print('OURS      palm slab n=%d  plane RMS %.4f m  normal %s'
      % (no, rms_o, np.array2string(n_o, precision=4)))
print('   vs zap %.1f | vs xap %.1f | vs nap %.1f deg'
      % (ang(n_o, zap), ang(n_o, xap), ang(n_o, nap)))

ref = None
for fn in ('out/patchdump/unit-91eb7be84016ceab.obj',
           'out/patchdump/unit-7336c6b662522f0e.obj'):
    try:
        p, _ = PV.read_obj(fn)
        p = np.asarray(p, float)
    except Exception:
        continue
    if p[:, 0].mean() < 0:
        ref = p
        break
R, nr = palm_slab(ref)
n_r, rms_r = plane_normal(R)
print()
print('REFERENCE palm slab n=%d  plane RMS %.4f m  normal %s'
      % (nr, rms_r, np.array2string(n_r, precision=4)))
print('   vs zap %.1f | vs xap %.1f | vs nap %.1f deg'
      % (ang(n_r, zap), ang(n_r, xap), ang(n_r, nap)))

# orient both to the same half-space, then measure
if float(n_o @ n_r) < 0:
    n_r = -n_r
print()
print('angle between the two palm normals: %.2f deg' % ang(n_o, n_r))
ax = np.cross(n_o, n_r)
if np.linalg.norm(ax) > 1e-9:
    ax = u(ax)
    print('rotation carrying ours onto the reference: axis %s' % np.array2string(ax, precision=4))
    print('   vs xap (thumb) %.1f | vs zap %.1f | vs nap %.1f deg'
          % (ang(ax, xap), ang(ax, zap), ang(ax, nap)))
# decompose the needed rotation into the three frame axes
for nm, v in (('xap (thumb)', xap), ('zap (forearm)', zap), ('nap', nap)):
    R = np.eye(3)
    c, s = float(n_o @ n_r), float(np.linalg.norm(np.cross(n_o, n_r)))
    th = np.arctan2(s, c)
    # signed rotation about v that best explains it
    print('   signed rotation about %-14s = %+7.2f deg'
          % (nm, np.degrees(np.arctan2(float(np.cross(n_o, n_r) @ v),
                                       float(n_o @ n_r) - (1 - float(n_o @ n_r)) * 0.5))))
