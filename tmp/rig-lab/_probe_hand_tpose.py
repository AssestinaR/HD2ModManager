# -*- coding: utf-8 -*-
"""Round 53: the T-POSE hand frame -- which world axis is the thumb?

The user's model, restated: in the T-pose the arm is a steel pipe whose AXIS is
z; the thumb, spread, always points along the pipe's surface normal, which is x;
and y is whatever is left.  So the correction is a rotation about **x = the
thumb**, tilting the hand's long axis (z) toward y.

Everything is measured on `src1` -- the source mesh aligned into HD2 space, i.e.
STILL IN T-POSE (the A-pose bake has not happened yet).  That is the frame the
user told us to work in ("你的方向应该从 tpose 的时候就开始取").

usage: python _probe_hand_tpose.py [side]
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
SIDE = sys.argv[1] if len(sys.argv) > 1 else 'l'
S = SIDE
SD = S.upper()


def u(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def ang(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(u(a) @ u(b)), -1, 1))))


def sang(a, b, n):
    a, b = u(a), u(b)
    return float(np.degrees(np.arctan2(float(np.cross(a, b) @ u(n)), float(a @ b))))


r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
               spine_map='mesh', fallback='region', leg_tilt=5.7, leg_splay=4.0,
               skeleton='native', mesh_shift=(0.0, -0.100, -0.090),
               upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
               head_scale=8.0 / 7.0, arm_scratch_upper_gain=0.05,
               arm_elbow_open=6.5, verbose=False)

nh = r['new_head']
src1 = np.asarray(r['src1'], float)

wrist = nh['%s_hand' % S]
elbow = nh['%s_elbow' % S]
sh = nh['%s_shoulder' % S]
arm_axis = u(wrist - elbow)                      # z, the pipe axis
print('=== %s hand, T-POSE frame ===' % SD)
print('shoulder %s' % np.array2string(sh, precision=4))
print('elbow    %s' % np.array2string(elbow, precision=4))
print('wrist    %s' % np.array2string(wrist, precision=4))
print('z  pipe axis  (elbow->wrist) %s   angle to %sX: %.2f deg'
      % (np.array2string(arm_axis, precision=4), '-' if arm_axis[0] < 0 else '+',
         ang(arm_axis, [-1 if arm_axis[0] < 0 else 1, 0, 0])))

# thumb direction from the SOURCE finger bones
t1 = nh['%s_thumb_finger1' % S]
t2 = nh['%s_thumb_finger2' % S]
t3 = nh['%s_thumb_finger3' % S]
thumb_raw = u(t3 - t1)
x_axis = u(thumb_raw - arm_axis * float(thumb_raw @ arm_axis))   # radial part
print('x  thumb (t1->t3, radial)  %s   raw %s'
      % (np.array2string(x_axis, precision=4), np.array2string(thumb_raw, precision=4)))
print('   thumb raw vs z: %.2f deg  (should be ~90 = it is a surface normal)'
      % ang(thumb_raw, arm_axis))
print('   x vs world +-Y: %.2f deg  |  x vs world +-Z: %.2f deg'
      % (ang(x_axis, [0, 1, 0]), ang(x_axis, [0, 0, 1])))

y_axis = u(np.cross(arm_axis, x_axis))           # the third orthogonal axis
print('y  = z x x                 %s   vs world +-Z: %.2f deg'
      % (np.array2string(y_axis, precision=4), ang(y_axis, [0, 0, 1])))

# ---- palm side: fit a slab of the source mesh right at the wrist -----------
m = np.linalg.norm(src1 - wrist, axis=1)
sel = m < 0.085
sel &= (src1[:, 0] < 0) if S == 'l' else (src1[:, 0] > 0)
Q = src1[sel]
print()
print('palm slab: %d vertices within 8.5 cm of the wrist' % len(Q))
if len(Q) > 50:
    c = Q.mean(0)
    _w, _v = np.linalg.eigh((Q - c).T @ (Q - c))
    pn = u(_v[:, 0])
    print('  smallest PCA axis (palm normal?) %s' % np.array2string(pn, precision=4))
    print('  vs z %.2f | vs x %.2f | vs y %.2f | vs +Z %.2f | vs -Z %.2f deg'
          % (ang(pn, arm_axis), ang(pn, x_axis), ang(pn, y_axis),
             ang(pn, [0, 0, 1]), ang(pn, [0, 0, -1])))
    if float(pn @ y_axis) < 0:
        pn = -pn
    print('  oriented to +y: %s   -> the palm FACES %s'
          % (np.array2string(pn, precision=4),
             'y' if float(pn @ y_axis) > 0 else '-y'))

# ---- what does +/-10 deg about x do to the hand's long axis? --------------
print()
print('rotating the hand about x (the thumb) -- the hand long axis is z:')
for sgn in (+1.0, -1.0):
    R = H.rot_axis_angle(x_axis, np.radians(sgn * 10.0))
    z2 = u(R @ arm_axis)
    print('  %+5.1f deg about x -> z becomes %s  |  d(z).y = %+.4f  (%s y)'
          % (sgn * 10.0, np.array2string(z2, precision=4),
             float(z2 @ y_axis), 'toward +' if float(z2 @ y_axis) > 0 else 'toward -'))
print()
print('  (+y is the palm-facing side if the line above says the palm FACES y;')
print('   "toward -y" is then wrist EXTENSION = toward the back of the hand.)')
