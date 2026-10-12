"""Read HD2's native rig straight from the TOC (no retarget needed) and list the
bones, so the pick tool knows what to draw.
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
os.chdir(HERE)
sys.path.insert(0, HERE)

import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'

hd2 = E.read_hd2_rig(TOC, BH)
named = [h for h in hd2 if h['name']]
print('total bones %d   named %d' % (len(hd2), len(named)))
print()
print('%-4s %-28s %-6s   head (x, y, z)')
for h in named:
    t = np.asarray(h['world'], float).reshape(4, 4)[:3, 3]
    print('%-4d %-28s %-6s   %+.4f %+.4f %+.4f'
          % (h['index'], h['name'], h['parent'], t[0], t[1], t[2]))
