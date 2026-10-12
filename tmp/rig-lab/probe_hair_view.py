"""Back view, before vs after: `distance` (left) against `region` (right).

Round 2.5's damage is on the BACK -- the twintails and the back ribbon were being
swung down and inward by the arms during the A-pose bake.  Two rows: whole figure,
then head + shoulders.  The HD2 rest skeleton is drawn on top so it is obvious the
rig is identical in both columns and only the mesh moves.

usage: probe_hair_view.py [out.png]
"""
import json
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
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/hair-view.png'
PW, PH = 330, 560

KW = dict(align='yaw', spine_map='mesh', leg_tilt=5.7, leg_splay=4.0,
          skeleton='native', mesh_shift=(0.0, -0.100, -0.060),
          upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45, verbose=False)

# back view: horizontal = X (mirrored), vertical = Z, depth = Y
AH, AV, AD, FLIP = 0, 2, 1, True
ROWS = [('whole figure', 0.00, 2.05), ('head + shoulders', 1.30, 2.05)]
LINK = [('neck', 'head'), ('chest', 'l_shoulder'), ('l_shoulder', 'l_elbow'),
        ('l_elbow', 'l_hand'), ('chest', 'r_shoulder'), ('r_shoulder', 'r_elbow'),
        ('r_elbow', 'r_hand'), ('hips', 'spine2'), ('hips', 'l_thigh'),
        ('hips', 'r_thigh')]


def main():
    cols = []
    for fb in ('distance', 'region'):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, fallback=fb, **KW)
        tris, _ = H.drop_outlier_tris(r['src_pts'], r['tris'])
        rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
               for h in r['hd2'] if h['name']}
        cols.append((fb, [tuple(p) for p in r['out']], tris, rig))

    GAP, TOP, PAD = 18, 74, 14
    W = PAD * 2 + 2 * (PW + GAP)
    Hh = TOP + 2 * (PH + 26) + 30
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    d = ImageDraw.Draw(img)
    d.text((PAD, 8), 'BACK view -- the twintails / back ribbon the arms were '
           'dragging', fill=(232, 232, 242))
    d.text((PAD, 26), 'left = old fallback "distance"   right = new fallback '
           '"region"   amber = HD2 native rest (identical in both)', fill=(150, 160, 180))
    d.text((PAD, 44), 'same mesh, same rig -- only the source-bone->HD2-bone '
           'fallback differs', fill=(255, 214, 120))

    for ri, (label, z0, z1) in enumerate(ROWS):
        cy = TOP + ri * (PH + 26)
        for ci, (fb, pts, tris, rig) in enumerate(cols):
            cx = PAD + ci * (PW + GAP)
            sc = min(PW, PH) * 0.96 / max(z1 - z0, 0.001)
            box = [-1.0, -1.0, z0, 1.0, 1.0, z1]
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), box,
                         AH, AV, AD, FLIP, sc)
            p.mesh(pts, tris, (188, 200, 224), 1)
            for a, b in LINK:
                if a in rig and b in rig:
                    p.line(tuple(rig[a]), tuple(rig[b]), (255, 190, 90), 1)
            for nm, pos in rig.items():
                p.dot(tuple(pos), (255, 120, 60), 2)
            d.text((cx + 6, cy + 6), '%s  [%s]' % (label, fb),
                   fill=(255, 255, 255))

    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, img.size[0], img.size[1]))


if __name__ == '__main__':
    main()
