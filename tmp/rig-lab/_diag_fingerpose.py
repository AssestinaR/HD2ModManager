# -*- coding: utf-8 -*-
"""Round 55: did the finger residual pose actually curl the fingers onto HD2?

A 3 x 4 grid of the LEFT hand.  Every cell uses the SAME camera and the SAME
scale, so rows are directly comparable:

    row 1  OURS BEFORE the finger pass (the round-54 deliverable)
    row 2  OURS AFTER  the finger pass
    row 3  the REFERENCE patch
    orange = HD2's native finger chain (hand -> 3 phalanges); it cannot move
    red    = the wrist

    col 1  TOP   (X right, Y forward)
    col 2  FRONT (X, Z up)
    col 3  SIDE  (Y forward, Z up)
    col 4  PALM  (oblique, straight down the palm normal)

The hand region is cut along the FOREARM AXIS, not by a sphere: a sphere around
the wrist also swallows the forearm, because the source weights `L_Hand` well
past the wrist.

usage: python _diag_fingerpose.py
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
COMMON = dict(align='yaw', spine_map='mesh', fallback='region',
              leg_tilt=5.7, leg_splay=4.0, skeleton='native',
              mesh_shift=(0.0, -0.100, -0.090),
              upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
              arm_scratch_upper_gain=0.05, head_scale=8.0 / 7.0,
              arm_elbow_open=6.5, arm_hand_tilt=3.0, verbose=False)

FINGERS = [('thumb', 'thumb_finger'), ('index', 'index_finger'),
           ('middle', 'middle_finger'), ('ring', 'ring_finger'),
           ('pinky', 'pinky_finger')]
BG = (18, 20, 26)
C_BEFORE = (150, 156, 170)
C_AFTER = (96, 232, 138)
C_REF = (60, 190, 226)
C_BONE = (240, 168, 70)
C_JOINT = (255, 224, 120)
SIDE = 'l'

base = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **COMMON)
posed = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   arm_finger_pose=True, **COMMON)

bh = np.asarray([np.asarray(posed['rest_hd2'][h['index']], float)[:3, 3]
                 for h in posed['hd2']], float)
hidx = {h['name']: k for k, h in enumerate(posed['hd2']) if h['name']}
WRIST = bh[hidx['%s_hand' % SIDE]]
ELBOW = bh[hidx['%s_elbow' % SIDE]]
ZAP = (WRIST - ELBOW)
ZAP = ZAP / max(float(np.linalg.norm(ZAP)), 1e-12)     # points distally

# hand region: distal of the wrist plane, or on the hand/finger bones
verts = base['verts']
dom = np.full(len(verts), -1, dtype=int)
for k, v in enumerate(verts):
    best, bw = -1, -1.0
    for j in range(3):
        if v['w'][j] > bw:
            best, bw = int(v['bones'][j]), v['w'][j]
    dom[k] = base['resolved'].get(best, -1)
hand_ids = [hidx['%s_hand' % SIDE]]
for _l, stem in FINGERS:
    hand_ids += [hidx['%s_%s%d' % (SIDE, stem, k)] for k in (1, 2, 3)]
P0all = base['out']
on_hand_bone = np.isin(dom, hand_ids)
distal = ((P0all - WRIST) @ ZAP) > -0.030
near = np.linalg.norm(P0all - WRIST, axis=1) < 0.30
is_hand = on_hand_bone & distal & near
print('our hand vertices: %d (was %d before the forearm cut)'
      % (int(is_hand.sum()), int((on_hand_bone & near).sum())))

# ---------------------------------------------------------------- reference
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
    keep = (np.linalg.norm(ref_all - WRIST, axis=1) < 0.30) \
        & (((ref_all - WRIST) @ ZAP) > -0.030)
    R = ref_all[keep]
print('reference hand vertices: %d' % (0 if R is None else len(R)))

BEFORE = base['out'][is_hand]
AFTER = posed['out'][is_hand]

# oblique camera: down the palm normal of the POSED hand
cen = AFTER.mean(0)
U, S, Vt = np.linalg.svd(AFTER - cen)
nrm = Vt[2]
if nrm @ (WRIST - cen) < 0:
    nrm = -nrm
right = Vt[0]
up2 = np.cross(nrm, right)
up2 = up2 / max(float(np.linalg.norm(up2)), 1e-12)
OB = np.vstack([right, up2])

# ---------------------------------------------------------------- the picture
PW, PH, PAD, LEFTY, TOPY = 430, 430, 20, 96, 96
W = LEFTY + PAD + 4 * (PW + PAD)
HT = TOPY + 4 * (PH + PAD) + 60
img = Image.new('RGB', (W, HT), BG)
dr = ImageDraw.Draw(img)


def mkf(box, lo0, hi0, lo1, hi1):
    px0, py0, px1, py1 = box
    return lambda a, b: (px0 + (a - lo0) / (hi0 - lo0) * (px1 - px0),
                         py1 - (b - lo1) / (hi1 - lo1) * (py1 - py0))


def blob(f, p, col, rad):
    x, y = f(p[0], p[1])
    if 0 <= x < W and 0 <= y < HT:
        dr.rectangle([x - rad, y - rad, x + rad, y + rad], fill=col)


# ONE scale for every cell: the bounds of everything, in each projection
allpts = np.vstack([BEFORE, AFTER] + ([R] if R is not None and len(R) else []))
bone_pts = []
for _l, stem in FINGERS:
    bone_pts.append(WRIST)
    for k in (1, 2, 3):
        bone_pts.append(bh[hidx['%s_%s%d' % (SIDE, stem, k)]])
bone_pts = np.asarray(bone_pts)
allpts = np.vstack([allpts, bone_pts])

PROJ = [('TOP   (X right, Y fwd)', 0, 1), ('FRONT (X, Z up)', 0, 2),
        ('SIDE  (Y fwd, Z up)', 1, 2), ('PALM  (down the palm normal)', None, None)]


def span_of(P, ia, ib):
    q = P @ OB.T if ia is None else P[:, [ia, ib]]
    lo, hi = q.min(0), q.max(0)
    c = 0.5 * (lo + hi)
    s = max(float((hi - lo).max()), 1e-6)
    return c - 0.5 * s, c + 0.5 * s


FRAMES = []
for ci, (title, ia, ib) in enumerate(PROJ):
    lo, hi = span_of(allpts, ia, ib)
    for ri in range(4):
        px0 = LEFTY + PAD + ci * (PW + PAD)
        py0 = TOPY + ri * (PH + PAD)
        f = mkf((px0, py0, px0 + PW, py0 + PH), lo[0], hi[0], lo[1], hi[1])
        FRAMES.append((ri, ci, f, ia, ib))
    dr.text((LEFTY + PAD + ci * (PW + PAD), TOPY - 22), title,
            fill=(212, 218, 232))

ROW_LABEL = ['OURS\nbefore finger pass', 'OURS\nafter finger pass',
             'after  vs  REFERENCE', 'REFERENCE\npatch']
for ri, lab in enumerate(ROW_LABEL):
    dr.text((PAD - 8, TOPY + ri * (PH + PAD) + 6), lab,
            fill=(196, 204, 220))

for ri, ci, f, ia, ib in FRAMES:
    px0 = LEFTY + PAD + ci * (PW + PAD)
    py0 = TOPY + ri * (PH + PAD)
    dr.rectangle([px0, py0, px0 + PW, py0 + PH], outline=(44, 50, 62))

# ---- clouds (one row each) -------------------------------------------------
CLOUD = [(0, BEFORE, C_BEFORE, 1, 2), (1, AFTER, C_AFTER, 1, 2)]
if R is not None and len(R):
    CLOUD.append((3, R, C_REF, 1, 2))
    CLOUD.append((2, R, C_REF, 1, 2))
CLOUD.append((2, AFTER, C_AFTER, 1, 2))          # ours drawn over the reference
for ri, P, col, rad, step in CLOUD:
    for r, ci, f, ia, ib in FRAMES:
        if r != ri:
            continue
        q = P @ OB.T if ia is None else P[:, [ia, ib]]
        for p in q[::step]:
            blob(f, p, col, rad)

# ---- bones, drawn in EVERY cell so alignment is visible everywhere ---------
for ri, ci, f, ia, ib in FRAMES:
    for _l, stem in FINGERS:
        ch = [WRIST] + [bh[hidx['%s_%s%d' % (SIDE, stem, k)]] for k in (1, 2, 3)]
        # NB: project to 2D, then push through the panel's screen transform.
        # Drawing the raw projected coords instead puts the lines off-canvas and
        # leaves only the joint dots visible.
        q = [f(*(p @ OB.T if ia is None else p[[ia, ib]])) for p in ch]
        for k in range(3):
            dr.line([q[k], q[k + 1]], fill=C_BONE, width=2)
        for j in q:
            dr.ellipse([j[0] - 2, j[1] - 2, j[0] + 2, j[1] + 2], fill=C_JOINT)
    wq = WRIST @ OB.T if ia is None else WRIST[[ia, ib]]
    x, y = f(wq[0], wq[1])
    dr.ellipse([x - 5, y - 5, x + 5, y + 5], fill=(255, 96, 96))

dr.text((PAD, 16), 'LEFT hand -- the finger residual pose, before vs after, '
        'against the reference and HD2\'s own finger bones.',
        fill=(234, 234, 246))
dr.text((PAD, 34), 'rows: grey = ours BEFORE   |   green = ours AFTER   |   '
        'cyan = reference.   orange = HD2 native finger chain (cannot move);  '
        'red = wrist.', fill=(152, 162, 182))
dr.text((PAD, 52), 'Every cell shares one camera and one scale.  The palm '
        'normal is computed from the POSED hand, so column 4 is the only view '
        'that shows finger-pad facing.',
        fill=(112, 122, 140))
dr.text((PAD, 70), 'Nothing in the rig moved: this pass rotates the finger '
        'chains only, on the final mesh, so the palm / wrist / roots are '
        'bit-identical to round 54.', fill=(112, 122, 140))

out = os.path.abspath('out/fingerpose-compare.png')
img.save(out)
print('[written] %s' % out)
