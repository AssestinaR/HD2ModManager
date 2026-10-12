"""Print HD2's skeleton tree (the top levels) and where the body branches split.

`region` failed for the skirt because `subtree(hips)` turned out to hold only the
legs -- so the spine is NOT a child of hips.  Need to see the real shape.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'

hd2 = E.read_hd2_rig(TOC, BH)
by_idx = {h['index']: h for h in hd2}
nm = {h['index']: (h['name'] or 'b%d' % h['index']) for h in hd2}
kids = {}
roots = []
for h in hd2:
    p = h.get('parent')
    if p is None or p == h['index'] or p not in by_idx:
        roots.append(h['index'])
    else:
        kids.setdefault(p, []).append(h['index'])

print('bones %d | roots %s' % (len(hd2), [nm[r] for r in roots]))


def walk(i, depth, maxd=3):
    h = hd2[[x['index'] for x in hd2].index(i)]
    m = h['world']
    z = m[3][2] if isinstance(m[0], (list, tuple)) else m[2]
    ch = kids.get(i, [])
    print('%s%-14s idx %-4d children %-3d  %s'
          % ('  ' * depth, nm[i], i, len(ch),
             ', '.join(nm[c] for c in ch[:8]) + (' ...' if len(ch) > 8 else '')))
    if depth < maxd:
        for c in ch:
            walk(c, depth + 1, maxd)


for r in roots:
    walk(r, 0)

print('\n-- ancestors of key bones --')
for want in ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head',
             'l_clavicle', 'l_shoulder', 'l_thigh', 'l_knee'):
    i = next((h['index'] for h in hd2 if h['name'] == want), None)
    if i is None:
        print('  %-12s MISSING' % want)
        continue
    chain, cur, n = [], i, 0
    while cur is not None and n < 12:
        chain.append(nm[cur])
        p = by_idx[cur].get('parent')
        cur = None if (p is None or p == cur or p not in by_idx) else p
        n += 1
    print('  %-12s idx %-4d %s' % (want, i, ' <- '.join(chain)))
