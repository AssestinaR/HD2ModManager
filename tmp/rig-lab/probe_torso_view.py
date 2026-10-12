"""Zoomed torso silhouette: ours GREEN vs reference MAGENTA, overlap WHITE.

The frame is restricted to the torso z-band (hips -> a little above the neck), so
the torso outlines are large enough to read.  The HD2 native skeleton is drawn on
top -- it is the SAME rig in both, so any mismatch is purely the mesh.

usage: probe_torso_view.py [out.png]
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
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/torso-view.png'
UPSCALE = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
UPPIV = float(sys.argv[3]) if len(sys.argv) > 3 else 1.15
UPBLEND = float(sys.argv[4]) if len(sys.argv) > 4 else 0.45
PW, PH = 430, 560


def solo(pts, tris, box, ah, av, ad, flip, sc):
    img = Image.new('RGB', (PW, PH), (0, 0, 0))
    p = PV.Panel(img, ImageDraw.Draw(img), (0, 0, PW, PH), box, ah, av, ad,
                 flip, sc)
    p.mesh(pts, tris, (1.0, 1.0, 1.0), 1)
    return np.asarray(img.convert('L'), dtype=np.float64) / 255.0


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=UPSCALE, upper_pivot_z=UPPIV,
                   upper_blend=UPBLEND, verbose=False)
    ours = [tuple(x) for x in r['out']]
    tris, _ = H.drop_outlier_tris(r['src_pts'], r['tris'])
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp, rtris, base = [], [], 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in REF:
            continue
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        if t:
            rp.extend(p)
            rtris.extend([(x[0] + base, x[1] + base, x[2] + base, 0) for x in t])
            base += len(p)

    skel = {k: v for k, v in PV.hd2_rig_heads(TOC, BH).items()
            if k in PV.BONE_PARENT}

    # zoom band: from just under the hips to a little above the neck
    z0 = float(rig['hips'][2]) - 0.10
    z1 = float(rig['neck'][2]) + 0.12
    ROWS = [('front  (X-Z)', 0, 2, 1, False),
            ('side   (Y-Z, +Y forward ->)', 1, 2, 0, True)]

    GAP, TOP, PAD = 26, 84, 12
    W = PAD * 2 + len(ROWS) * (PW + GAP)
    Hh = TOP + PH + 40
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((PAD, 8), 'torso close-up   GREEN = ours   MAGENTA = reference   '
           'WHITE = overlap', fill=(232, 232, 242))
    d.text((PAD, 26), 'amber = HD2 native skeleton (the SAME rig in both, so a '
           'mismatch here is purely the mesh)', fill=(150, 160, 180))
    d.text((PAD, 46), 'front view: the horizontal axis is mirrored, same in both '
           'columns.  +X = the character\'s RIGHT.', fill=(255, 214, 120))
    d.text((PAD, 64), 'z band shown: %.3f .. %.3f m   |   upper scale x%.3f, '
           'pivot z %.3f, blend %.2f' % (z0, z1, UPSCALE, UPPIV, UPBLEND),
           fill=(150, 160, 180))

    for ri, (tag, ah, av, ad, flip) in enumerate(ROWS):
        cx = PAD + ri * (PW + GAP)
        cy = TOP
        allp = np.asarray(ours + rp)
        mn_h, mx_h = float(allp[:, ah].min()), float(allp[:, ah].max())
        span = max(mx_h - mn_h, z1 - z0, 0.001)
        sc = min(PW, PH) * 0.94 / span
        box = [0.0] * 6
        box[ah], box[3 + ah] = mn_h, mx_h
        box[av], box[3 + av] = z0, z1

        L1 = solo(ours, tris, box, ah, av, ad, flip, sc)
        L2 = solo(rp, rtris, box, ah, av, ad, flip, sc)
        mix = np.stack([L2, L1, L2], -1)
        img.paste(Image.fromarray((mix * 255).astype(np.uint8)), (cx, cy))
        dd = ImageDraw.Draw(img)
        dd.text((cx + 2, cy - 16), tag, fill=(205, 215, 232))
        p = PV.Panel(img, dd, (cx, cy, cx + PW, cy + PH), box, ah, av, ad,
                     flip, sc)
        for nm, hh in skel.items():
            par = PV.BONE_PARENT.get(nm)
            if par and par in skel:
                p.line(skel[par], hh, (255, 190, 60), 2)
        for v in skel.values():
            p.dot(v, (255, 226, 140), 3)

    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, W, Hh))


if __name__ == '__main__':
    main()
