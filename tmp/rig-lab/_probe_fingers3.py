# -*- coding: utf-8 -*-
"""Round 54b: per-finger, ours vs the reference patch, with a proper axis.

`_probe_fingers2.py` showed something surprising: our finger directions and the
reference's agree to 2-5 deg, and BOTH sit ~160 deg off HD2's native finger
bones.  A 160 deg agreement between two unrelated meshes is not a coincidence,
it means the "axis" there was measuring the wrong thing (the vertex farthest
from the knuckle can sit on the WRIST side of the chain).

So this one does it properly, per finger:
  * our finger's vertex set comes from the SHIPPED WEIGHTS (dominant bone is in
    that finger's chain) -- no geometric guessing;
  * the reference's comes from the nearest HD2 finger chain segment;
  * the axis is the PCA principal direction, ORIENTED outward from the wrist;
  * and the honest number: the symmetric nearest-neighbour distance between the
    two clouds, per finger.

usage: python _probe_fingers3.py
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


def seg_dist(P, a, b):
    ab = b - a
    L2 = float(ab @ ab)
    t = np.clip(((P - a) @ ab) / max(L2, 1e-12), 0.0, 1.0)
    return np.linalg.norm(P - (a + t[:, None] * ab), axis=1)


def nn(A, B, chunk=512):
    out = np.empty(len(A))
    for i in range(0, len(A), chunk):
        out[i:i + chunk] = np.sqrt(
            ((A[i:i + chunk, None, :] - B[None, :, :]) ** 2).sum(-1)).min(1)
    return out


def pca_axis(Q, origin):
    c = Q.mean(0)
    _w, _v = np.linalg.eigh((Q - c).T @ (Q - c))
    ax = u(_v[:, -1])
    if float(ax @ (c - origin)) < 0:
        ax = -ax
    return ax, c


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

SIDES = [('l', 'LEFT'), ('r', 'RIGHT')]
for SIDE, SLABEL in SIDES:
    WRIST = bh[idx['%s_hand' % SIDE]]
    REF = None
    if ref_all is not None:
        # the arm unit holds the sleeve and the cuff ruffles too; keep only what
        # is inside the hand's own reach
        REF = ref_all[np.linalg.norm(ref_all - WRIST, axis=1) < 0.26]

    print('=' * 100)
    print('%s hand   |  our mesh %d verts / reference %d verts within 26 cm '
          'of the wrist' % (SLABEL, len(ours), 0 if REF is None else len(REF)))
    print('%-8s | %-28s %-9s | %-28s %-9s | %-8s %-8s %s'
          % ('finger', 'OURS axis (PCA)', 'len(m)', 'REF axis (PCA)', 'len(m)',
             'axis diff', 'ours->ref', 'ref->ours'))

    for label, stem in FINGERS:
        J = [bh[idx['%s_hand' % SIDE]]]
        for k in (1, 2, 3):
            J.append(bh[idx['%s_%s%d' % (SIDE, stem, k)]])
        bone_axis = u(J[3] - J[1])
        bone_len = float(np.linalg.norm(J[3] - J[1]))

        # --- ours: from the shipped weights ---------------------------------
        ids = [idx['%s_%s%d' % (SIDE, stem, k)] for k in (1, 2, 3)]
        O = ours[np.isin(dom, ids)]
        if len(O) < 10:
            print('%-8s | (our weights do not put any vertex on this finger)'
                  % label)
            continue
        oax, oc = pca_axis(O, WRIST)
        olen = float(np.max((O - J[1]) @ oax))

        # --- reference: nearest chain segment ------------------------------
        R, rax, rlen = None, None, np.nan
        if REF is not None and len(REF):
            segs = [(J[k], J[k + 1]) for k in range(3)]
            near = np.min(np.stack([seg_dist(REF, a, b) for a, b in segs]), 0)
            R = REF[near < 0.035]
            if len(R) > 10:
                rax, _rc = pca_axis(R, WRIST)
                rlen = float(np.max((R - J[1]) @ rax))
            else:
                R = None

        line = ('%-8s | %-28s %8.4f | %-28s %8.4f | %7.1f'
                % (label, np.array2string(oax, precision=3), olen,
                   'n/a' if R is None else np.array2string(rax, precision=3),
                   rlen, np.nan if R is None else ang(oax, rax)))
        if R is not None:
            a, b = nn(O, R), nn(R, O)
            line += ('  %5.1f mm %5.1f mm'
                     % (1000 * np.median(a), 1000 * np.median(b)))
        print(line + '   [HD2 bone: len %.3f m, axis %s]'
              % (bone_len, np.array2string(bone_axis, precision=3)))

    # the whole hand
    ids_all = []
    for _l, stem in FINGERS:
        ids_all += [idx['%s_%s%d' % (SIDE, stem, k)] for k in (1, 2, 3)]
    Oall = ours[np.isin(dom, ids_all)]
    if REF is not None and len(REF) and len(Oall):
        a, b = nn(Oall, REF), nn(REF, Oall)
        print('%-8s | whole finger set n=%d | ours->ref median %.1f mm mean '
              '%.1f mm | ref->ours median %.1f mm mean %.1f mm'
              % ('ALL', len(Oall), 1000 * np.median(a), 1000 * a.mean(),
                 1000 * np.median(b), 1000 * b.mean()))
    print()
