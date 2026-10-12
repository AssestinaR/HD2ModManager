"""Round 45d: overlay no-arm_rot (blue) and arm_rot (orange) in the SAME panel."""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
AR = (10.46, -18.55, 29.63)
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
            upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0, verbose=False)


def run(**kw):
    b = dict(BASE); b.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **b)


off = run(arm_rot=None)
on = run(arm_rot=AR)
P0 = np.asarray(off['out'], float)
P1 = np.asarray(on['out'], float)
tris = off['tris']
# only the left arm
sel = []
for t in tris:
    if P1[t[0], 0] < -0.12 or P0[t[0], 0] < -0.12:
        sel.append(t)

rig = E.read_hd2_rig(TOC, BH)
head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
        for h in rig if h['name']}
ed = []
for h in rig:
    p = h['parent']
    if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
        ed.append((head[rig[p]['name']], head[h['name']]))

rows = [('front (X-Z)', 0, 2, 1, [-0.80, -1.0, 1.05, -0.15, 1.0, 1.55]),
        ('top (X-Y)', 0, 1, 2, [-0.80, -0.22, -1.0, -0.15, 0.36, 1.0])]
pw, ph = 720, 640
W = 12 + len(rows) * (pw + 14)
Hh = 62 + ph + 26 + 16
img = Image.new('RGB', (W, Hh), (26, 28, 34))
dr = ImageDraw.Draw(img)
dr.text((12, 8), 'round 45d: LEFT arm -- BLUE = no arm_rot, ORANGE = arm_rot '
        '(seq).  The orange tube is the same tube, just rotated.',
        fill=(232, 232, 244))
dr.text((12, 26), 'cyan = the HD2 shoulder chain', fill=(150, 160, 180))
for ci, (tag, ah, av, ad, box) in enumerate(rows):
    cx = 12 + ci * (pw + 14)
    cy = 62
    dr.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
    p = PV.Panel(img, dr, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
    p.mesh([tuple(q) for q in P0], sel, (0.45, 0.62, 0.98), 1)
    p.mesh([tuple(q) for q in P1], sel, (0.98, 0.62, 0.28), 1)
    for x, y in ed:
        p.line(x, y, (92, 97, 112), 1)
    for nm, par in (('l_shoulder', 'l_clavicle'), ('l_elbow', 'l_shoulder'),
                    ('l_hand', 'l_elbow')):
        if nm in head and par in head:
            p.line(head[par], head[nm], (90, 220, 240), 3)
    for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
        if nm in head:
            p.dot(head[nm], (255, 210, 90), 4)
out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/arm-overlay.png')
img.save(out)
print('[written] %s %dx%d' % (out, W, Hh))
