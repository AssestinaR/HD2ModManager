"""Prove / disprove: is the reference patch's SKELETON bit-identical to HD2's
native rig, and what direction do its arm bones point?

The user's instruction: "the blue reference line must come from the reference
model's SKELETON, not from its mesh."  So this prints the reference's own arm
joints next to HD2's native ones, plus the three view angles of each.
"""
import io
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
os.chdir(HERE)
sys.path.insert(0, HERE)

import export_obj as E
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
DUMP = 'out/patchdump'

meta = json.load(io.open(os.path.join(DUMP, 'units.json'), encoding='utf-8'))
names = PV.read_bone_names(BH)
patch = PV.patch_rig_heads(meta, names)

hd2 = E.read_hd2_rig(TOC, BH)
nat = {h['name']: np.asarray(h['world'], float).reshape(4, 4)[:3, 3]
       for h in hd2 if h['name']}

ARM = ('l_clavicle', 'l_shoulder', 'l_shoulder_twist', 'l_elbow', 'l_hand')

print('=== 1. patch skeleton arm joints vs HD2 native (metres) ===')
print('%-20s %-26s %-26s %s' % ('bone', 'patch', 'HD2 native', 'delta'))
for k in ARM:
    if k in patch and k in nat:
        p = np.asarray(patch[k], float)
        n = nat[k]
        d = p - n
        print('%-20s (%+.4f %+.4f %+.4f)   (%+.4f %+.4f %+.4f)   |d|=%.7f'
              % (k, p[0], p[1], p[2], n[0], n[1], n[2], float(np.linalg.norm(d))))
    else:
        print('%-20s %s' % (k, 'missing (patch)' if k not in patch else 'missing (hd2)'))

print()
print('=== 2. how many patch bones differ from HD2 at all ===')
common = [k for k in patch if k in nat]
worst = sorted(((float(np.linalg.norm(np.asarray(patch[k], float) - nat[k])), k)
                for k in common), reverse=True)
print('patch names read: %d   common with HD2: %d' % (len(patch), len(common)))
print('worst 8 deltas:')
for d, k in worst[:8]:
    print('   %-26s %.7f m' % (k, d))


def angles(u):
    """(front X-Z deg below horizontal, top X-Y deg forward, side Y-Z deg)."""
    u = np.asarray(u, float)
    u = u / max(float(np.linalg.norm(u)), 1e-12)
    return (math.degrees(math.atan2(-u[2], abs(u[0]))),
            math.degrees(math.atan2(abs(u[1]), abs(u[0]))),
            math.degrees(math.atan2(-u[2], abs(u[1]))))


print()
print('=== 3. arm bone DIRECTIONS (the "blue line" source) ===')
print('%-34s %9s %9s %9s' % ('segment', 'front', 'top', 'side'))
for tag, P in (('patch skeleton', patch), ('HD2 native', nat)):
    for lab, a, b in (('l_sh->l_el (upper arm)', 'l_shoulder', 'l_elbow'),
                      ('l_el->l_ha (forearm)', 'l_elbow', 'l_hand'),
                      ('l_sh->l_ha (whole arm)', 'l_shoulder', 'l_hand')):
        if a in P and b in P:
            g = angles(np.asarray(P[b], float) - np.asarray(P[a], float))
            print('%-34s %9.2f %9.2f %9.2f' % ('%s [%s]' % (lab, tag), g[0], g[1], g[2]))
