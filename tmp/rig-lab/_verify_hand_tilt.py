# -*- coding: utf-8 -*-
"""Verify --arm-hand-tilt: run the pipeline at 0 and at DEG, then check
(a) WHERE the mesh moved, (b) that the hand turned as a rigid body, and
(c) by how much, about which axis.

usage: python _verify_hand_tilt.py [deg]
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
DEG = float(sys.argv[1]) if len(sys.argv) > 1 else 10.0


def run(t):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, skeleton='native',
                      mesh_shift=(0.0, -0.100, -0.090),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      head_scale=8.0 / 7.0, arm_scratch_upper_gain=0.05,
                      arm_elbow_open=6.5, arm_hand_tilt=t, verbose=False)


def u(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


r0, r1 = run(0.0), run(DEG)
names = [h['name'] for h in r0['hd2']]
pos = {h['name']: h['index'] for h in r0['hd2']}
par = r0['hd2']
A = np.asarray(r0['out'], float)
B = np.asarray(r1['out'], float)
d = np.linalg.norm(B - A, axis=1)

# --- subtree helpers on the HD2 tree ---------------------------------------
parent_of = {}
children = {}
for h in r0['hd2']:
    parent_of[h['index']] = h['parent']
for h in r0['hd2']:
    children.setdefault(h['parent'], []).append(h['index'])


def subtree(root):
    out, st = set(), [root]
    while st:
        c = st.pop()
        if c in out:
            continue
        out.add(c)
        st.extend(children.get(c, []))
    return out


hand_sub = subtree(pos['l_hand'])
res = r0['resolved']
dom = np.array([res.get(v['bones'][int(np.argmax(v['w']))], -1) for v in r0['verts']])
is_hand = np.isin(dom, list(hand_sub))
is_larm = np.isin(dom, [h['index'] for h in r0['hd2']
                        if h['name'] and h['name'].startswith('l_')
                        and H.is_arm_bone(h['name'])])
wrist = np.array(r0['rest'][pos['l_hand']][:3, 3], float)
elbow = np.array(r0['rest'][pos['l_elbow']][:3, 3], float)
sh = np.array(r0['rest'][pos['l_shoulder']][:3, 3], float)
zax = u(wrist - elbow)

print('left hand subtree: %d bones | %d hand-dominant vertices'
      % (len(hand_sub), int(is_hand.sum())))
print()
print('LEFT-side mesh displacement (m):')
for nm, m in (('body / torso / legs', ~is_larm),
              ('hand + fingers', is_hand),
              ('rest of the arm', is_larm & ~is_hand)):
    if m.sum() == 0:
        continue
    print('  %-22s n=%6d  max %9.6f  mean %9.6f'
          % (nm, int(m.sum()), d[m].max(), d[m].mean()))

# --- rigid check on the hand ------------------------------------------------
sub0 = A[is_hand]
sub1 = B[is_hand]
k = min(len(sub0), 4000)
sel = np.linspace(0, len(sub0) - 1, k).astype(int)
P0, P1 = sub0[sel], sub1[sel]
R, ca, cb = H.kabsch(P0, P1)
ang, ax = H.rot_angle_axis(R)
resid = np.linalg.norm((P0 - ca) @ R.T + cb - P1, axis=1)
print()
print('Kabsch rigid fit of the hand shell, 0 -> %+.1f:' % DEG)
print('  rotation %.4f deg about %s' % (ang, np.array2string(ax, precision=4)))
print('  residual mean %.3e  max %.3e m   (0 = perfectly rigid)'
      % (resid.mean(), resid.max()))
print('  axis vs world +Y: %.2f deg   (the thumb axis of the T-pose)'
      % np.degrees(np.arccos(np.clip(abs(float(ax @ [0, 1, 0])), -1, 1))))

# --- what happened to the hand's long axis, in the T-pose frame -------------
# the hand's world long axis = D[hand] . (source hand long axis)
# measure it instead off the mesh: wrist -> hand-region centroid is not rigid,
# so use the Kabsch rotation on the SOURCE long axis carried by the chain.
print()
print('hand long axis (elbow->wrist, z) after the A-pose bake:')
for tag, r in (('tilt 0', r0), ('tilt %+.1f' % DEG, r1)):
    D = r['D']
    Dh = D[pos['l_hand']][:3, :3]
    print('  %-12s D[l_hand].z = %s'
          % (tag, np.array2string(u(Dh @ zax), precision=4)))

# --- every other region must be bit-identical ------------------------------
moved_else = d[~is_larm].max()
print()
print('max displacement ANYWHERE outside the left arm: %.3e m' % moved_else)
print('  (the right arm mirrors it, so it is expected to move too)')
