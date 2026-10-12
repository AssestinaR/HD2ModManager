"""Two columns x three rows: ours | reference, front / side / top.

Column 0  our retargeted mesh, column 1 the reference patch mesh.
Every panel carries HD2's NATIVE skeleton (amber lines + dots) -- the same rig
in all six, because that is the fixed thing both meshes are being placed onto.

Each ROW gets its own scale so the top view (a ~0.7 m footprint) is not a speck
next to the 2.1 m front view, but the two COLUMNS of a row always share one
scale, so within a row the two meshes are directly comparable.

usage: tri_overlay.py [out.png] [WxH] [down_m] [back_m] [up_scale] [up_pivot_z] [up_blend]
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

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/tri.png'
PW, PH = ([int(x) for x in sys.argv[2].split('x')] if len(sys.argv) > 2
          else [330, 470])
DOWN = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
BACK = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0
UPSCALE = float(sys.argv[5]) if len(sys.argv) > 5 else 1.0
UPPIV = float(sys.argv[6]) if len(sys.argv) > 6 else 1.15
UPBLEND = float(sys.argv[7]) if len(sys.argv) > 7 else 0.45
TILT, SPLAY = 5.7, 4.0

# (label, horizontal axis, vertical axis, depth axis, mirror-horizontal)
ROWS = [('front  (X-Z)', 0, 2, 1, False),
        ('side   (Y-Z, +Y forward ->)', 1, 2, 0, True),
        ('top    (X-Y, +Y forward = up)', 0, 1, 2, False)]

SKEL = (255, 190, 60)
SKELD = (255, 226, 140)


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=TILT, leg_splay=SPLAY, skeleton='native',
                   mesh_shift=(0.0, -BACK, -DOWN),
                   upper_scale=UPSCALE, upper_pivot_z=UPPIV,
                   upper_blend=UPBLEND, verbose=False)
    ours = [tuple(x) for x in r['out']]
    tris, _lim = H.drop_outlier_tris(r['src_pts'], r['tris'])

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

    COLS = [('OURS  (mesh down %.3f / back %.3f m, upper x%.3f about z %.2f)'
             % (DOWN, BACK, UPSCALE, UPPIV), ours, tris),
            ('REFERENCE patch', rp, rtris)]

    GAP, TOP, PAD = 30, 96, 12
    W = PAD + len(COLS) * (PW + 10) + 4
    Hh = TOP + len(ROWS) * (PH + GAP) + PAD
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((PAD, 8), 'ours vs the reference patch -- same HD2 NATIVE skeleton in '
           'all six panels  (zoey)', fill=(232, 232, 242))
    d.text((PAD, 26), 'amber = HD2 native rest, i.e. the rig both meshes are being '
           'placed onto.  The rig never moves; only the mesh does.',
           fill=(150, 160, 180))
    d.text((PAD, 44), 'each ROW has its own scale (so the top view is legible), but '
           'the two columns of a row always share one scale.', fill=(150, 160, 180))
    d.text((PAD, 62), 'note: the horizontal axis is mirrored in every panel -- both '
           'columns the same way, so comparisons hold.  HD2 +X = the character\'s '
           'RIGHT.', fill=(255, 214, 120))

    for ri, (tag, ah, av, ad, flip) in enumerate(ROWS):
        cy = TOP + ri * (PH + GAP)
        pts = np.asarray(ours + rp + list(skel.values()))
        mn_h, mx_h = float(pts[:, ah].min()), float(pts[:, ah].max())
        mn_v, mx_v = float(pts[:, av].min()), float(pts[:, av].max())
        span = max(mx_h - mn_h, mx_v - mn_v, 0.001)
        sc = min(PW, PH) * 0.92 / span
        box = [0.0] * 6
        box[ah], box[3 + ah] = mn_h, mx_h
        box[av], box[3 + av] = mn_v, mx_v

        for ci, (lab, mesh, mt) in enumerate(COLS):
            cx = PAD + ci * (PW + 10)
            d.text((cx + 2, cy - 26), '%s   |   %s' % (lab, tag),
                   fill=(205, 215, 232))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), box, ah, av, ad,
                         flip, sc)
            p.mesh(mesh, mt, (0.96, 1.0, 0.96) if ci == 0 else (0.95, 0.95, 1.0),
                   1)
            for nm, hh in skel.items():
                par = PV.BONE_PARENT.get(nm)
                if par and par in skel:
                    p.line(skel[par], hh, SKEL, 2)
            for v in skel.values():
                p.dot(v, SKELD, 3)
            # a shared origin guide, so both columns can be read against the same
            # world line -- in the side/top views the horizontal axis is Y, so this
            # is the plane y = 0 (the skeleton's own centre line)
            p.guide(0.0, (96, 128, 168), 1)

    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, W, Hh))
    idle = float(np.linalg.norm(np.asarray(r['out_idle'])
                                - np.asarray(r['out']), axis=1).max())
    rigd = max(float(np.linalg.norm(r['rest_shipped'][i][:3, 3]
                                    - r['rest_hd2'][i][:3, 3]))
               for i in r['rest_shipped'])
    print('mesh down %.4f back %.4f | upper scale x%.3f pivot z %.3f blend %.2f | '
          'our zmin %.4f height %.4f | ref zmin %.4f height %.4f | '
          'idle vs bind %.2e m | shipped rig vs HD2 %.2e m'
          % (DOWN, BACK, UPSCALE, UPPIV, UPBLEND,
             np.asarray(ours)[:, 2].min(),
             float(np.asarray(ours)[:, 2].max() - np.asarray(ours)[:, 2].min()),
             min(p[2] for p in rp),
             max(p[2] for p in rp) - min(p[2] for p in rp), idle, rigd))


if __name__ == '__main__':
    main()
