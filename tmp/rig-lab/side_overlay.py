"""Overlay the side-view profiles of source / ours(kabsch) / ours(yaw) / patch.

One panel, one scale, one axis.  For every 2 cm band of height we draw the
front-most and back-most Y of the mesh, so the four silhouettes can be compared
without the eye having to flip between panels.  Vertical guides mark the hip and
the mid-foot of each dataset.

Reading it: **+Y (forward) points LEFT**, +Z is up, both in metres.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = 'tmp_bh.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
MODEL = 'survivor_teenangst'
PATCH = ['out/patchdump/unit-17da843a4908511e.obj',
         'out/patchdump/unit-cae35ee15168145f.obj',
         'out/patchdump/unit-91eb7be84016ceab.obj',
         'out/patchdump/unit-7336c6b662522f0e.obj']

W, HGT = 900, 980
Y0, Y1 = -0.45, 0.45
Z0, Z1 = -0.05, 2.05
BG = (24, 26, 32)


def load_obj(path):
    P = []
    for line in open(path, encoding='utf-8', errors='replace'):
        if line.startswith('v '):
            _, x, y, z = line.split()[:4]
            P.append((float(x), float(y), float(z)))
    return np.array(P)


def profiles(P, step=0.02):
    zmin = P[:, 2].min()
    out = []
    z = zmin + step / 2
    while z < P[:, 2].max():
        m = P[np.abs(P[:, 2] - z) < step]
        if len(m) > 20:
            out.append((z, m[:, 1].min(), m[:, 1].max()))
        z += step
    return out


def main():
    r_k = H.retarget(VPK, MODEL, TOC, BH, PP, align='kabsch', verbose=False)
    r_y = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', verbose=False)
    patch = np.vstack([load_obj(p) for p in PATCH])

    sets = [
        ('source aligned (kabsch, T-pose)', np.asarray(r_k['src1']),
         r_k['new_head']['l_thigh'], (255, 165, 80)),
        ('OURS  kabsch (A-pose)', np.asarray(r_k['out']),
         r_k['new_head']['l_thigh'], (255, 80, 80)),
        ('OURS  yaw    (A-pose)', np.asarray(r_y['out']),
         r_y['new_head']['l_thigh'], (90, 240, 140)),
        ('reference patch mod', patch, np.array([0.0, -0.0190, 0.9417]), (110, 175, 255)),
    ]

    img = Image.new('RGB', (W, HGT), BG)
    d = ImageDraw.Draw(img)

    def X(y):
        return 90 + (Y1 - y) / (Y1 - Y0) * (W - 140)   # +Y -> left

    def Z(z):
        return HGT - 50 - (z - Z0) / (Z1 - Z0) * (HGT - 110)

    d.text((12, 8), 'SIDE-VIEW PROFILE OVERLAY  (forward = LEFT, +Z up)   Zoey vs the '
           'reference patch', fill=(235, 235, 245))
    d.text((12, 24), 'each band shows the front-most and back-most Y of the mesh; '
           'guides = hip (bright) and mid-foot (dim) of the same colour',
           fill=(150, 160, 180))

    # grid
    for z in np.arange(0.0, 2.01, 0.25):
        d.line([(X(Y0), Z(z)), (X(Y1), Z(z))], fill=(46, 50, 60))
        d.text((60, Z(z) - 6), '%.2f' % z, fill=(120, 130, 150))
    for y in np.arange(-0.4, 0.41, 0.1):
        d.line([(X(y), Z(Z0)), (X(y), Z(Z1))], fill=(46, 50, 60))
        d.text((X(y) - 12, HGT - 40), '%+.1f' % y, fill=(120, 130, 150))

    # the four profiles
    for label, P, hip, col in sets:
        pr = profiles(P)
        for z, ylo, yhi in pr:
            d.line([(X(ylo), Z(z)), (X(yhi), Z(z))], fill=col)
        zmin = P[:, 2].min()
        band = P[(P[:, 2] < zmin + 0.07) & (P[:, 0] < -0.05)]
        if not len(band):
            band = P[P[:, 2] < zmin + 0.07]
        ymid = (band[:, 1].min() + band[:, 1].max()) / 2.0
        d.line([(X(hip[1]), Z(Z0)), (X(hip[1]), Z(Z1))], fill=col, width=2)
        d.line([(X(ymid), Z(Z0)), (X(ymid), Z(Z0) + 0.55)], fill=col, width=1)
        d.text((X(hip[1]) + 3, Z(1.98)), label, fill=col)
        print('%-36s hip y %+.4f  mid-foot y %+.4f  ->  mid-foot is %+.4f vs the hip'
              % (label, hip[1], ymid, ymid - hip[1]))

    img.save('out/side-overlay.png')
    print('[written] out/side-overlay.png')


if __name__ == '__main__':
    main()
