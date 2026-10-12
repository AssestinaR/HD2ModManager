"""Channel overlay: our mesh in GREEN, the reference in MAGENTA, overlap WHITE.

Both meshes are rendered alone into the same pixel frame (same box and scale),
then mixed channel-wise, so the residual offset is directly readable.  The HD2
native skeleton is drawn on top of the mix.

usage: probe_shift_view.py [out.png] [down] [back]
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

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/shiftview.png'
DOWN = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
BACK = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0

PW, PH = 420, 560
ROWS = [('front (X-Z)', 0, 2, 1, False),
        ('side (Y-Z, +Y fwd ->)', 1, 2, 0, True),
        ('top (X-Y, +Y fwd = up)', 0, 1, 2, False)]


def solo(pts, tris, box, ah, av, ad, flip, sc):
    img = Image.new('RGB', (PW, PH), (0, 0, 0))
    p = PV.Panel(img, ImageDraw.Draw(img), (0, 0, PW, PH), box, ah, av, ad,
                 flip, sc)
    p.mesh(pts, tris, (1.0, 1.0, 1.0), 1)
    return np.asarray(img.convert('L'), dtype=np.float64) / 255.0


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -BACK, -DOWN), verbose=False)
    ours = [tuple(x) for x in r['out']]
    tris, _ = H.drop_outlier_tris(r['src_pts'], r['tris'])

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

    GAP, TOP, PAD = 26, 74, 12
    W = PAD * 2 + len(ROWS) * (PW + GAP)
    Hh = TOP + PH + 40
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((PAD, 8), 'GREEN = ours (down %.3f back %.3f m)   MAGENTA = reference   '
           'WHITE = overlap' % (DOWN, BACK), fill=(232, 232, 242))
    d.text((PAD, 26), 'amber = HD2 native skeleton (identical in all three).  In the '
           'side view +Y (forward) points LEFT.', fill=(150, 160, 180))
    d.text((PAD, 46), 'side view: the two feet should sit at the same place; if GREEN '
           'is left of MAGENTA the mesh is too far FORWARD.', fill=(255, 214, 120))

    for ri, (tag, ah, av, ad, flip) in enumerate(ROWS):
        cx = PAD + ri * (PW + GAP)
        cy = TOP
        pts = np.asarray(ours + rp + list(skel.values()))
        mn_h, mx_h = float(pts[:, ah].min()), float(pts[:, ah].max())
        mn_v, mx_v = float(pts[:, av].min()), float(pts[:, av].max())
        span = max(mx_h - mn_h, mx_v - mn_v, 0.001)
        sc = min(PW, PH) * 0.92 / span
        box = [0.0] * 6
        box[ah], box[3 + ah] = mn_h, mx_h
        box[av], box[3 + av] = mn_v, mx_v

        L1 = solo(ours, tris, box, ah, av, ad, flip, sc)
        L2 = solo(rp, rtris, box, ah, av, ad, flip, sc)
        mix = np.stack([L2, L1, L2], -1)
        panel = Image.fromarray((mix * 255).astype(np.uint8))
        img.paste(panel, (cx, cy))
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
