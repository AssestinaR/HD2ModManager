# -*- coding: utf-8 -*-
"""Round 54d: the single rotation that would take our fingers onto the target.

`_probe_fingers3.py` gives five per-finger angles.  The brief needs the summary:
if ONE rotation of the hand about the wrist could fix most of it, say so and give
the axis and the angle; if the five fingers disagree, no single rotation works
and the fix has to be per finger.

Kabsch on the five (our axis -> target axis) pairs, weighted equally.

usage: python _probe_fingers4.py
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


def u(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def ang(a, b):
    return float(np.degrees(np.arccos(np.clip(u(a) @ u(b), -1, 1))))


def pca_axis(Q, origin):
    c = Q.mean(0)
    _w, _v = np.linalg.eigh((Q - c).T @ (Q - c))
    ax = u(_v[:, -1])
    if float(ax @ (c - origin)) < 0:
        ax = -ax
    return ax


def kabsch_pairs(A, B):
    """rotation taking the rows of A onto the rows of B (both 5x3)"""
    Hm = A.T @ B
    U, _s, Vt = np.linalg.svd(Hm)
    D = np.eye(3)
    D[2, 2] = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ D @ U.T
    return R


def axis_angle(R):
    c = (np.trace(R) - 1) / 2
    th = np.degrees(np.arccos(np.clip(c, -1, 1)))
    w, v = np.linalg.eig(R)
    i = int(np.argmin(np.abs(w - 1)))
    ax = u(np.real(v[:, i]))
    return ax, th


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

for SIDE, SL in (('l', 'LEFT'), ('r', 'RIGHT')):
    WRIST = bh[idx['%s_hand' % SIDE]]
    zap = u(WRIST - bh[idx['%s_elbow' % SIDE]])
    REF = None
    if ref_all is not None:
        keep = (np.linalg.norm(ref_all - WRIST, axis=1) < 0.26) \
            & (((ref_all - WRIST) @ zap) > -0.02)
        if keep.sum() > 50:
            REF = ref_all[keep]

    A, Bbone, Bref = [], [], []
    for label, stem in FINGERS:
        ids = [idx['%s_%s%d' % (SIDE, stem, k)] for k in (1, 2, 3)]
        O = ours[np.isin(dom, ids)]
        if len(O) < 10:
            continue
        J1 = bh[ids[0]]
        A.append(pca_axis(O, WRIST))
        Bbone.append(u(bh[ids[2]] - J1))
        if REF is not None:
            segs = [(bh[idx['%s_hand' % SIDE]], bh[ids[0]]),
                    (bh[ids[0]], bh[ids[1]]), (bh[ids[1]], bh[ids[2]])]
            near = np.full(len(REF), 1e9)
            for a, b in segs:
                ab = b - a
                t = np.clip(((REF - a) @ ab) / max(float(ab @ ab), 1e-12), 0, 1)
                near = np.minimum(near, np.linalg.norm(REF - (a + t[:, None] * ab),
                                                       axis=1))
            Q = REF[near < 0.035]
            if len(Q) > 10:
                Bref.append(pca_axis(Q, WRIST))
    A = np.asarray(A)
    Bbone = np.asarray(Bbone)
    print('=' * 78)
    print('%s hand -- one rotation for all fingers?' % SL)
    for tag, B in (('-> HD2 native finger bones', Bbone),
                   ('-> the reference patch   ', np.asarray(Bref) if Bref else None)):
        if B is None or len(B) != len(A):
            print('  %s : skipped (no reference cloud)' % tag)
            continue
        R = kabsch_pairs(A, B)
        ax, th = axis_angle(R)
        res = [ang(R @ a, b) for a, b in zip(A, B)]
        print('  %s : axis %s  angle %5.1f deg | after: per-finger residual '
              'min %.1f  mean %.1f  max %.1f deg'
              % (tag, np.array2string(ax, precision=3), th,
                 min(res), float(np.mean(res)), max(res)))
        print('       before: min %.1f  mean %.1f  max %.1f deg'
              % (min(ang(a, b) for a, b in zip(A, B)),
                 float(np.mean([ang(a, b) for a, b in zip(A, B)])),
                 max(ang(a, b) for a, b in zip(A, B))))
    print('  our finger axes      : %s'
          % ', '.join(np.array2string(a, precision=3) for a in A))
    print('  HD2 bone axes        : %s'
          % ', '.join(np.array2string(b, precision=3) for b in Bbone))
    if Bref and len(Bref) == len(A):
        print('  reference finger axes: %s'
              % ', '.join(np.array2string(b, precision=3) for b in Bref))
    print()
