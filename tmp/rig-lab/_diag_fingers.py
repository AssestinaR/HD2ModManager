# -*- coding: utf-8 -*-
"""Round 54c: our fingers vs the reference's, and vs HD2's native finger bones.

Round 53 concluded the PALM was fine because the palm-slab normal matched to
3.5 deg.  `_probe_fingers3.py` then said the fingers point 36-49 deg away from
the reference's, with a 3.8 cm median nearest-neighbour gap on a 10 cm finger.
Both can be true at once: a rotation ABOUT THE PALM NORMAL leaves the normal
untouched and swings the fingers sideways.  The palm-normal test was blind to
exactly the error we have.

This draws three orthographic views of the LEFT hand:
    grey  = our hand + fingers
    cyan  = the reference patch's hand
    orange = HD2's native finger bones (knuckle -> 3 phalanges), which cannot move
    yellow = the wrist joint

usage: python _diag_fingers.py
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

FINGERS = [('thumb', 'thumb_finger'), ('index', 'index_finger'),
           ('middle', 'middle_finger'), ('ring', 'ring_finger'),
           ('pinky', 'pinky_finger')]
BG = (18, 20, 26)
C_OURS = (128, 134, 148)
C_REF = (52, 200, 220)
C_BONE = (232, 156, 62)
C_JOINT = (255, 214, 92)

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

SIDE = 'l'
WRIST = bh[idx['%s_hand' % SIDE]]
hand_ids = [idx['%s_hand' % SIDE]]
for _l, stem in FINGERS:
    hand_ids += [idx['%s_%s%d' % (SIDE, stem, k)] for k in (1, 2, 3)]
is_hand = np.isin(dom, hand_ids)
H = ours[is_hand]
H = H[np.linalg.norm(H - WRIST, axis=1) < 0.22]
print('our hand vertices (dominant bone in the hand chain): %d' % len(H))

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
R = None
if ref_all is not None:
    # the arm unit also carries the sleeve and the cuff ruffles; keep only what
    # sits distal of the wrist along the forearm axis
    zap = WRIST - bh[idx['%s_elbow' % SIDE]]
    zap = zap / max(float(np.linalg.norm(zap)), 1e-12)
    keep = (np.linalg.norm(ref_all - WRIST, axis=1) < 0.22) \
        & (((ref_all - WRIST) @ zap) > -0.02)
    R = ref_all[keep]
print('reference hand vertices: %d' % (0 if R is None else len(R)))

# ---------------------------------------------------------------- the picture
PW, PH, PAD, TOPY = 640, 640, 28, 120
W = PAD * 4 + PW * 3
HT = TOPY + PH + 46
img = Image.new('RGB', (W, HT), BG)
dr = ImageDraw.Draw(img)


def mkf(box, lo0, hi0, lo1, hi1):
    px0, py0, px1, py1 = box
    return lambda a, b: (px0 + (a - lo0) / (hi0 - lo0) * (px1 - px0),
                         py1 - (b - lo1) / (hi1 - lo1) * (py1 - py0))


def blob(f, p, ia, ib, col, rad=1):
    # NB: pass the axis indices.  An earlier version read p[0], p[1] inside, so
    # every panel silently drew the XY projection -- the FRONT and SIDE panels
    # came out empty and it looked like the clouds were missing.
    x, y = f(p[ia], p[ib])
    if 0 <= x < W and 0 <= y < HT:
        dr.rectangle([x - rad, y - rad, x + rad, y + rad], fill=col)


pts = H if R is None or not len(R) else np.vstack([H, R])
for _l, stem in FINGERS:
    for k in (1, 2, 3):
        pts = np.vstack([pts, bh[idx['%s_%s%d' % (SIDE, stem, k)]]])
lo, hi = pts.min(0), pts.max(0)
pad = 0.10 * float((hi - lo).max())
lo, hi = lo - pad, hi + pad
mid = 0.5 * (lo + hi)
span = max(float((hi - lo).max()), 1e-6)
lo, hi = mid - 0.5 * span, mid + 0.5 * span

VIEWS = [('TOP   (X right, Y fwd)', 0, 1), ('FRONT (X, Z up)', 0, 2),
         ('SIDE  (Y fwd, Z up)', 1, 2)]
fns = []
for i, (title, ia, ib) in enumerate(VIEWS):
    px0 = PAD + i * (PW + PAD)
    box = (px0, TOPY, px0 + PW, TOPY + PH)
    f = mkf(box, lo[ia], hi[ia], lo[ib], hi[ib])
    fns.append((f, ia, ib))
    step = 0.02 if span < 0.40 else 0.05
    for a in np.arange(np.ceil(lo[ia] / step) * step, hi[ia] + 1e-9, step):
        px, _ = f(a, lo[ib])
        dr.line([(px, TOPY), (px, TOPY + PH)], fill=(30, 34, 42))
        dr.text((px - 15, TOPY + PH + 6), '%+.2f' % a, fill=(96, 106, 124))
    for b in np.arange(np.ceil(lo[ib] / step) * step, hi[ib] + 1e-9, step):
        _, py = f(lo[ia], b)
        dr.line([(px0, py), (px0 + PW, py)], fill=(38, 42, 52))
        dr.text((px0 - 42, py - 6), '%+.2f' % b, fill=(96, 106, 124))
    dr.rectangle([px0, TOPY, px0 + PW, TOPY + PH], outline=(48, 54, 66))
    dr.text((px0, TOPY - 22), title, fill=(206, 212, 226))
    dr.text((px0, TOPY - 38), 'span %.3f m   grid %.2f m' % (span, step),
            fill=(112, 122, 140))

for p in H[::8]:
    for f, ia, ib in fns:
        blob(f, p, ia, ib, C_OURS, 1)
if R is not None:
    for p in R[::3]:
        for f, ia, ib in fns:
            blob(f, p, ia, ib, C_REF, 2)

for _l, stem in FINGERS:
    ch = [bh[idx['%s_hand' % SIDE]]]
    for k in (1, 2, 3):
        ch.append(bh[idx['%s_%s%d' % (SIDE, stem, k)]])
    for f, ia, ib in fns:
        for k in range(3):
            dr.line([f(ch[k][ia], ch[k][ib]), f(ch[k + 1][ia], ch[k + 1][ib])],
                    fill=C_BONE, width=3)
        for j in ch:
            x, y = f(j[ia], j[ib])
            dr.ellipse([x - 3, y - 3, x + 3, y + 3], fill=C_JOINT)
for f, ia, ib in fns:
    x, y = f(WRIST[ia], WRIST[ib])
    dr.ellipse([x - 6, y - 6, x + 6, y + 6], fill=(255, 90, 90))

dr.text((PAD, 16), 'LEFT hand -- ours vs the reference patch, with HD2\'s native '
        'finger bones drawn in (they cannot move: the rig is HD2 verbatim).',
        fill=(234, 234, 246))
dr.text((PAD, 34), 'grey = OURS   |   cyan = REFERENCE patch   |   orange = HD2 '
        'native finger chain (hand -> 3 phalanges)   |   red dot = the wrist.',
        fill=(152, 162, 182))
dr.text((PAD, 52), 'If OUR fingers lie flat along one direction while the bones '
        'and the reference fan downwards, the hand is rotated about its own palm '
        'normal -- a rotation the round-53 palm-normal test cannot see.',
        fill=(112, 122, 140))
dr.text((PAD, 70), 'measured (_probe_fingers3.py): per-finger axis gap ours vs '
        'reference 36-49 deg, median nearest-neighbour gap 3.8 cm on a ~10 cm '
        'finger; the reference itself sits only 8-15 deg off the bones.',
        fill=(112, 122, 140))

out = os.path.abspath('out/fingers-compare.png')
img.save(out)
print('[written] %s' % out)
