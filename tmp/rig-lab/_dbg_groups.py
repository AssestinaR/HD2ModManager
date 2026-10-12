"""Round 45d: which bones own the LEFT-arm vertices, and how many get ZERO
rotation because `group()` does not classify their bone as arm?"""
import os
import sys
from collections import Counter

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
a = rest[idx['l_shoulder']][:3, 3].astype(float)
b = rest[idx['l_hand']][:3, 3].astype(float)
u = (b - a) / np.linalg.norm(b - a)
t = (P - a) @ u
perp = np.linalg.norm((P - a) - np.outer(t, u), axis=1)
region = (P[:, 0] < -0.15) & (perp < 0.16)
print('left-arm region: %d verts' % int(region.sum()))

names = np.array([by_idx.get(res.get(v['bones'][q], -1), '?')
                  for v in verts for q in range(1)], dtype=object)
print('\n-- ALL bones that appear in a skin slot of a left-arm vertex --')
cnt = Counter()
wsum = Counter()
zero_w = Counter()
for j in np.where(region)[0]:
    v = verts[j]
    tot_arm = 0.0
    for q in range(3):
        wq = v['w'][q]
        if wq <= 0:
            continue
        nm = by_idx.get(res.get(v['bones'][q], -1), '?')
        cnt[nm] += 1
        wsum[nm] += wq
        if group(nm, 'l') is not None:
            tot_arm += wq
    if tot_arm <= 1e-9:
        for q in range(3):
            wq = v['w'][q]
            if wq > 0.05:
                zero_w[by_idx.get(res.get(v['bones'][q], -1), '?')] += 1

tot = int(region.sum())
nz = int(sum(1 for j in np.where(region)[0]
             if sum(v['w'][q] for q in range(3)
                    if group(by_idx.get(res.get(verts[j]['bones'][q], -1), '?'),
                             'l') is not None) <= 1e-9))
print('\nvertices in the arm region with ZERO arm-rotation weight: %d / %d (%.1f%%)'
      % (nz, tot, 100.0 * nz / tot))
print('their biggest (>0.05) skin weights go to:')
for nm, c in zero_w.most_common(15):
    print('   %-28s %d' % (nm, c))

print('\n-- all skin bones in the region, by summed weight --')
for nm, w in wsum.most_common(25):
    g = group(nm, 'l')
    print('   %-28s w=%8.2f  n=%6d  group=%s' % (nm, w, cnt[nm], g))
