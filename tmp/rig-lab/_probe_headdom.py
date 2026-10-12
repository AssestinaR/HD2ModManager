# -*- coding: utf-8 -*-
"""Who are the 25327 vertices dominated by `head`, and do they really belong there?

25327 spans z 0.759..2.030 -- if those low ones are the twin-tail hair hanging
to hip height, weighting them to `head` is coarse but sane.  If they are body,
it is a runtime "flung across the screen" bug that only shows once the head
bone animates (the bind pose hides it, which is exactly why it survived).
"""
import collections
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
COMMON = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
              leg_splay=4.0, skeleton='native', mesh_shift=(0.0, -0.100, -0.090),
              upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
              arm_scratch_upper_gain=0.05, head_scale=8.0 / 7.0,
              arm_elbow_open=6.5, arm_hand_tilt=3.0, arm_finger_pose=True,
              verbose=False)
r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **COMMON)
verts, resolved = r['verts'], r['resolved']
hd2 = r['hd2']
name_of = {h['index']: (h['name'] or 'bone_%d' % h['index']) for h in hd2}
P = np.asarray(r['out'], float)

src = r['src']
srcname = {b['index']: b['name'] for b in src}
head_idx = next(h['index'] for h in hd2 if h['name'] == 'head')

# which SOURCE bone sends vertices to HD2 `head`?
per_src = collections.Counter()
per_src_pos = collections.defaultdict(list)
for k, v in enumerate(verts):
    best, bw = -1, -1.0
    for j in range(3):
        if v['w'][j] > bw:
            best, bw = int(v['bones'][j]), v['w'][j]
    if resolved.get(best) == head_idx:
        per_src[srcname.get(best, '?')] += 1
        per_src_pos[srcname.get(best, '?')].append(k)

print('vertices dominated by HD2 `head`: %d, from %d source bones'
      % (sum(per_src.values()), len(per_src)))
print('%-26s %7s  %-28s %s' % ('source bone', 'verts', 'z range', '|P-head_joint| max'))
hj = np.asarray(r['rest_hd2'][head_idx], float)[:3, 3]
for nm, n in per_src.most_common(20):
    kk = np.asarray(per_src_pos[nm], int)
    dd = np.linalg.norm(P[kk] - hj, axis=1)
    print('%-26s %7d  %6.3f..%6.3f  %-12s %.3f m'
          % (nm, n, P[kk, 2].min(), P[kk, 2].max(),
             '', dd.max()))

# connectivity: is the low-z part the same shell as the skull?
tris = np.asarray([(int(t[0]), int(t[1]), int(t[2])) for t in r['tris']], np.int64)
low = np.zeros(len(P), bool)
kk = np.concatenate([np.asarray(per_src_pos[nm], int) for nm in per_src])
low[kk] = True
low &= P[:, 2] < 1.40                      # below the neck
print('\nhead-dominated vertices below z=1.40: %d' % int(low.sum()))
sel = low[tris].any(1)
print('triangles touching them: %d' % int(sel.sum()))

# what else is this vertex set adjacent to?
adj = collections.Counter()
nb = {}
for t in tris[sel]:
    for a_ in t:
        for b_ in t:
            if a_ != b_:
                nb.setdefault(a_, set()).add(b_)
for k in np.nonzero(low)[0]:
    for o in nb.get(int(k), ()):
        if not low[o]:
            dom = resolved.get(int(verts[o]['bones'][int(np.argmax(verts[o]['w']))]))
            adj[name_of.get(dom, '?')] += 1
print('their neighbours are dominated by: %s' % adj.most_common(10))
