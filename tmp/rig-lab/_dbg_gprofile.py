"""Round 45d: profile g0/g1/g2 (the arm-rotation weight fields) along the arm."""
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


def group(nm, side):
    if not H.is_arm_bone(nm) or not nm.startswith(side + '_'):
        return None
    tail = nm[len(side) + 1:]
    if 'finger' in tail or tail.startswith('hand'):
        return 2
    if tail.startswith('elbow'):
        return 1
    return 0


r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
               spine_map='mesh', fallback='region', leg_tilt=5.7, leg_splay=4.0,
               mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
               upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0,
               arm_rot=(9.90, -17.25, 21.50), verbose=False)
P = np.asarray(r['out'], float)
verts = r['verts']
res = r['resolved']
by_idx = {h['index']: h['name'] for h in r['hd2'] if h['name']}
idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
rest = r['rest_shipped']
a0 = rest[idx['l_shoulder']][:3, 3].astype(float)
b0 = rest[idx['l_hand']][:3, 3].astype(float)
u = (b0 - a0) / np.linalg.norm(b0 - a0)
t = (P - a0) @ u
perp = np.linalg.norm((P - a0) - np.outer(t, u), axis=1)
region = (P[:, 0] < -0.15) & (perp < 0.10) & (t > -0.05) & (t < 0.85)

A = np.zeros((len(P), 3))
for j, v in enumerate(verts):
    for q in range(3):
        wq = v['w'][q]
        if wq <= 0:
            continue
        gg = group(by_idx.get(res.get(v['bones'][q], -1), '?'), 'l')
        if gg is not None:
            A[j, gg] += wq

print('elbow t=0.286, wrist t=0.555')
print('%8s %6s | %6s %6s %6s | %6s %6s %6s' %
      ('t-bin', 'n', 'a0', 'a1', 'a2', 'g0', 'g1', 'g2'))
for lo in np.arange(0.0, 0.80, 0.05):
    sel = region & (t >= lo) & (t < lo + 0.05)
    if sel.sum() < 20:
        continue
    a = A[sel].mean(0)
    g = np.array([a.sum(), a[1] + a[2], a[2]])
    print('%+.2f..%+.2f %6d | %6.3f %6.3f %6.3f | %6.3f %6.3f %6.3f'
          % (lo, lo + 0.05, int(sel.sum()), a[0], a[1], a[2], g[0], g[1], g[2]))
