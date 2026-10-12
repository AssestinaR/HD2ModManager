"""Round 45: the ARM, ours vs the reference, in the three views the user used.

rows = front (X-Z) / top (X-Y) / side (Y-Z), all zoomed onto the arms.
cols = ours / reference sleeve / both overlaid.

HD2's own skeleton is drawn on top of every panel (thin grey), and the left arm
chain is drawn thick, so "is our mesh wrapped around the bone" is readable.
"""
import json
import math
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
REF_UNITS = [('91eb7be84016ceab', 'LeftArm'), ('7336c6b662522f0e', 'RightArm'),
             ('cae35ee15168145f', 'Torso'), ('17da843a4908511e', 'Hip')]

PANEL = (400, 420)
STEP = 2
OUT = os.path.abspath('out/arm-view.png')


def rig_edges(rig):
    """[(p0, p1)] over the whole HD2 rig, from the rig's own parent field."""
    head = [(h['world'][3], h['world'][7], h['world'][11]) for h in rig]
    out = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            out.append((head[p], head[h['index']]))
    return out


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    rig = E.read_hd2_rig(TOC, BH)
    edges = rig_edges(rig)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    arm_bones = ['l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
                 'r_clavicle', 'r_shoulder', 'r_elbow', 'r_hand']

    our_pts = [tuple(p) for p in np.asarray(r['out'])]
    tris = r['tris']

    ref_pts, ref_tris, base = [], [], 0
    for fid, _tag in REF_UNITS:
        path = 'out/patchdump/unit-%s.obj' % fid
        if not os.path.exists(path):
            continue
        p, t = PV.read_obj(path)
        ref_pts.extend(p)
        ref_tris.extend([(a + base, b + base, c + base, 0) for a, b, c, _ in t])
        base += len(p)
    print('ours %d v / ref %d v' % (len(our_pts), len(ref_pts)))

    pw, ph = PANEL
    rows = [('front (X-Z)', 0, 2, 1, False, [-0.86, -1, 1.03, 0.86, 1, 1.70]),
            ('top (X-Y, +Y = forward)', 0, 1, 2, False,
             [-0.86, -0.40, -1, 0.86, 0.50, 1]),
            ('side (Y-Z, +Y = forward)', 1, 2, 0, True,
             [-0.86, -0.40, 1.03, 0.86, 0.50, 1.70])]
    cols = [('OURS', 'ours'), ('reference sleeve', 'ref'), ('overlay', 'both')]

    W = 12 + len(cols) * (pw + 14)
    Hh = 62 + len(rows) * (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'round 45: the arm -- ours vs the reference sleeve, HD2 skeleton '
           'overlaid', fill=(232, 232, 244))
    d.text((12, 26), 'HD2 native rig = grey lines; left arm chain = cyan thick; '
           '+Y = forward, +X = the character\'s right (so the LEFT arm is at -X)',
           fill=(150, 160, 180))

    for ci, (clabel, kind) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        d.text((cx + 4, 44), clabel, fill=(205, 214, 228))
        for ri, (tag, ah, av, ad, flip, box) in enumerate(rows):
            cy = 62 + ri * (ph + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, flip)
            if kind == 'ours':
                p.mesh(our_pts, tris, (0.95, 1.0, 0.96), STEP)
            elif kind == 'ref':
                p.mesh(ref_pts, ref_tris, (1.0, 0.93, 0.98), STEP)
            else:
                p.mesh(ref_pts, ref_tris, (1.0, 0.85, 0.95), STEP)
                p.mesh(our_pts, tris, (0.80, 0.97, 0.85), STEP)
            for a, b in edges:
                p.line(a, b, (95, 100, 115), 1)
            for a, b in PV.bone_lines({k: v for k, v in head.items()
                                       if k in arm_bones or k in
                                       ('chest', 'neck', 'head')}):
                p.line(a, b, (90, 220, 240), 2)
            for k, v in head.items():
                if k in arm_bones:
                    p.dot(v, (255, 210, 90), 3)
    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, W, Hh))


if __name__ == '__main__':
    main()
