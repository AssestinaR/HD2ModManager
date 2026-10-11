"""Overlay the SIDE-VIEW SKELETONS of source / ours(kabsch) / ours(yaw) / HD2 / patch.

The question is about relative positions of the legs, the torso and the head in
the side view, so the mesh is noise (arms and skirt dominate its silhouette).
Bones only, one panel, one scale.

Reading it: **+Y (forward) points LEFT**, +Z is up, both in metres.
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H
import patch_view as PV

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = 'tmp_bh.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
MODEL = 'survivor_teenangst'
PATCH_DIR = 'out/patchdump'

W, HGT = 980, 1000
Y0, Y1 = -0.42, 0.42
Z0, Z1 = -0.08, 2.02
BG = (24, 26, 32)

SPINE = ['hips', 'spine1', 'spine2', 'chest', 'neck', 'head']
LEG = ['hips', 'l_thigh', 'l_knee', 'l_foot']
ARM = ['chest', 'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand']


def main():
    names = PV.read_bone_names(BH)
    r_k = H.retarget(VPK, MODEL, TOC, BH, PP, align='kabsch', verbose=False)
    r_y = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', verbose=False)
    sc, R, t = PV.similarity(np.asarray(r_k['src_pts']), np.asarray(r_k['src1']))
    raw = PV.src_rig_heads(VPK, MODEL)
    src_al = {PV.SRC_TO_HD2[k]: tuple(sc * (R @ np.asarray(v)) + t)
              for k, v in raw.items() if k in PV.SRC_TO_HD2}
    hd2 = {k: v for k, v in PV.hd2_rig_heads(TOC, BH).items() if k in PV.BONE_PARENT}
    import json
    meta = json.load(open(os.path.join(PATCH_DIR, 'units.json'), encoding='utf-8'))
    patch = {k: v for k, v in PV.patch_rig_heads(meta, names).items() if k in PV.BONE_PARENT}

    sets = [
        ('source aligned (kabsch)', src_al, (255, 165, 80)),
        ('OURS kabsch', {k: tuple(v) for k, v in r_k['new_head'].items()}, (255, 90, 90)),
        ('OURS yaw', {k: tuple(v) for k, v in r_y['new_head'].items()}, (90, 240, 140)),
        ('HD2 native', hd2, (110, 175, 255)),
        ('reference patch', patch, (255, 120, 220)),
    ]

    img = Image.new('RGB', (W, HGT), BG)
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'SIDE-VIEW SKELETON OVERLAY   (forward = LEFT, +Z up)   '
           'Zoey vs HD2 native vs the reference patch', fill=(235, 235, 245))
    d.text((12, 24), 'thick line = hip vertical;  thin line = ankle vertical',
           fill=(150, 160, 180))

    def X(y):
        return 90 + (Y1 - y) / (Y1 - Y0) * (W - 150)

    def Z(z):
        return HGT - 50 - (z - Z0) / (Z1 - Z0) * (HGT - 110)

    for z in np.arange(0.0, 2.01, 0.25):
        d.line([(X(Y0), Z(z)), (X(Y1), Z(z))], fill=(44, 48, 58))
        d.text((58, Z(z) - 6), '%.2f' % z, fill=(120, 130, 150))
    for y in np.arange(-0.4, 0.41, 0.1):
        d.line([(X(y), Z(Z0)), (X(y), Z(Z1))], fill=(44, 48, 58))
        d.text((X(y) - 13, HGT - 40), '%+.1f' % y, fill=(120, 130, 150))

    print('%-24s %9s %9s %9s %9s %9s' % ('dataset', 'hip y', 'ankle d', 'knee d',
                                         'neck d', 'head d'))
    for label, h, col in sets:
        hip = h.get('l_thigh') or h.get('hips')
        if hip is None:
            continue
        for chain in (SPINE, LEG, ARM):
            pts = [(k, h[k]) for k in chain if k in h]
            for i in range(len(pts) - 1):
                a, b = pts[i][1], pts[i + 1][1]
                d.line([(X(a[1]), Z(a[2])), (X(b[1]), Z(b[2]))], fill=col, width=3)
        for k, v in h.items():
            d.ellipse([X(v[1]) - 4, Z(v[2]) - 4, X(v[1]) + 4, Z(v[2]) + 4], fill=col)
        # hip / ankle verticals
        d.line([(X(hip[1]), Z(Z0)), (X(hip[1]), Z(Z1))], fill=col, width=2)
        ank = h.get('l_foot')
        if ank:
            d.line([(X(ank[1]), Z(Z0)), (X(ank[1]), Z(Z0) + 0.7)], fill=col, width=1)
        d.text((X(hip[1]) + 4, Z(1.99)), label, fill=col)
        row = (label, hip[1],
               (h['l_foot'][1] - hip[1]) if 'l_foot' in h else float('nan'),
               (h['l_knee'][1] - hip[1]) if 'l_knee' in h else float('nan'),
               (h['neck'][1] - hip[1]) if 'neck' in h else float('nan'),
               (h['head'][1] - hip[1]) if 'head' in h else float('nan'))
        print('%-24s %+9.4f %+9.4f %+9.4f %+9.4f %+9.4f' % row)

    img.save('out/side-skeleton-overlay.png')
    print('[written] out/side-skeleton-overlay.png')


if __name__ == '__main__':
    main()
