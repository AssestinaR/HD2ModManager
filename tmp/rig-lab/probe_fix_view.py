"""probe_fix_view.py -- mesh-only render: native(deltas OFF) vs snap.

No skeleton lines, so the arm pose is unambiguous.  If native+deltas OFF really
lands the mesh on HD2's A-pose, the two columns must look the same.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
kw = dict(align='yaw', spine_map='mesh', fallback='region',
          leg_tilt=5.7, leg_splay=4.0, verbose=False)

a = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
               skeleton='native', axis_deltas=False, **kw)
b = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, skeleton='snap', **kw)

clean, _ = H.drop_outlier_tris(a['src_pts'], a['tris'])
A = [tuple(p) for p in a['out']]
B = [tuple(p) for p in b['out']]
allp = np.asarray(A + B)
lo, hi = allp[:, 2].min(), allp[:, 2].max()
cxm = 0.5 * (allp[:, 0].min() + allp[:, 0].max())
xh = 0.62 * (hi - lo)
gb = (cxm - xh, allp[:, 1].min(), lo, cxm + xh, allp[:, 1].max(), hi)
PW, PH = 300, 460
gsc = min(PW, PH) * 0.92 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)
ROWS = [('front (X-Z)', 0, 2, 1, False), ('side (Y-Z, +Y fwd)', 1, 2, 0, True)]
COLS = [('A native, symmetric deltas OFF', A, (0.94, 1.0, 0.96)),
        ('B snap (today)', B, (1.0, 0.90, 0.90))]
W = 12 + len(COLS) * (PW + 10)
Hh = 96 + len(ROWS) * (PH + 30) + 10
img = Image.new('RGB', (W, Hh), (28, 30, 36))
d = ImageDraw.Draw(img)
d.text((12, 10), 'mesh only, no skeleton -- native(deltas OFF) vs snap  (zoey)',
       fill=(232, 232, 242))
d.text((12, 30), 'if native+deltas OFF is right, the two columns must match; '
       'an arm that stays horizontal means the T->A bake did not land.',
       fill=(150, 160, 180))
for ci, (lab, pts, tint) in enumerate(COLS):
    cx = 12 + ci * (PW + 10)
    d.text((cx + 2, 74), lab, fill=(205, 215, 232))
    for ri, (tag, ah, av, ad, flip) in enumerate(ROWS):
        cy = 96 + ri * (PH + 30)
        d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
        p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
        p.mesh(pts, clean, tint, 1)
img.save('out/fix-mesh.png')
print('[written] out/fix-mesh.png')
