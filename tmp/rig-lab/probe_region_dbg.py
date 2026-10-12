"""Why did the skirt bones move to the thighs under `region`?

`probe_region.py` shows source_extra_91/92/93 going spine2 -> l/r_thigh.  That is
the opposite of what the rule should do (the subtree of the spine cannot contain a
thigh), so something about the ancestor lookup or the child lists is off.  This
prints the whole chain for the bones that changed hands.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')

WATCH = ['source_extra_91', 'source_extra_93', 'source_extra_100', 'source_extra_126',
         'source_extra_73', 'source_extra_85', 'source_extra_109', 'source_extra_124']

r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
               align='yaw', spine_map='mesh', fallback='region',
               leg_tilt=5.7, leg_splay=4.0, skeleton='native',
               mesh_shift=(0.0, -0.100, -0.060),
               upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
               verbose=False)

src = r['src']
direct = r['src_index_to_hd2']
by_idx = {h['index']: h for h in r['hd2']}
nm = {h['index']: (h['name'] or 'b%d' % h['index']) for h in r['hd2']}

# rebuild the child lists the way retarget does
kids = {}
for h in r['hd2']:
    p = h.get('parent')
    if p is not None and p != h['index'] and p in by_idx:
        kids.setdefault(p, []).append(h['index'])


def subtree(root):
    seen, stack = set(), [root]
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        stack.extend(kids.get(n, ()))
    return seen


print('hd2 %d bones | source %d bones' % (len(r['hd2']), len(src)))
print('hips index %s | its children: %s'
      % (nm.get(9), [nm.get(c) for c in kids.get(9, [])]))
print('subtree(hips) size %d' % len(subtree(9)))
for want in ('spine2', 'chest', 'l_thigh', 'r_thigh', 'head'):
    idx = next((h['index'] for h in r['hd2'] if h['name'] == want), None)
    print('  %-8s idx %-4s parent %-14s in subtree(hips) %s'
          % (want, idx, nm.get(by_idx[idx]['parent']) if idx is not None else '-',
             (idx in subtree(9)) if idx is not None else '-'))

print()
for w in WATCH:
    b = next((s for s in src if s['name'] == w), None)
    if b is None:
        print('%-20s NOT FOUND' % w)
        continue
    chain, cur, n = [], b['index'], 0
    while cur >= 0 and n < 12:
        chain.append('%s%s' % (src[cur]['name'],
                               '*' if cur in direct else ''))
        if cur in direct:
            break
        cur = src[cur]['parent']
        n += 1
    anc = cur if cur in direct else -1
    print('%-20s -> %s' % (w, ' <- '.join(chain)))
    if anc >= 0:
        h = direct[anc]
        pool = [c for c in set(direct.values()) if c in subtree(h)]
        print('     ancestor %s -> %s (idx %d), subtree size %d, pool %d'
              % (src[anc]['name'], nm.get(h), h, len(subtree(h)), len(pool)))
    print('     region picks %s | distance picks %s'
          % (nm.get(r['resolved'].get(b['index'])),
             nm.get(next((x for x in [None]), None))))
