"""Height-normalised front-view comparison of the leg stance.

The reference is 2.113 m and ours is 1.983 m, so the previous sheet -- one camera
for everything -- makes the reference look bigger and the stance hard to judge.
Here every column is scaled so the FIGURE IS THE SAME HEIGHT on screen, which is
the only way the splay can be compared by eye.
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
SPLAY = [float(x) for x in (sys.argv[1].split(',') if len(sys.argv) > 1
                            else ['0', '4', '5.57'])]
OUT = sys.argv[2] if len(sys.argv) > 2 else 'out/splay-norm.png'
TILT = 5.7
PW, PH = 260, 470
GRID = [0.0, 0.25, 0.5, 0.75, 1.0]


def main():
    runs = []
    for sp in SPLAY:
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='distance', leg_tilt=TILT,
                       leg_splay=sp, verbose=False)
        runs.append((sp, r))

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp, rt, base = [], [], 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in REF:
            continue
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        if t:
            rp.extend(p)
            rt.extend([(x[0] + base, x[1] + base, x[2] + base, 0) for x in t])
            base += len(p)
    ref = np.asarray(rp)

    cols = [('splay %.2f' % sp, np.asarray(r['out']), r['tris']) for sp, r in runs]
    cols.append(('REFERENCE patch', ref, rt))

    W = 12 + len(cols) * (PW + 10)
    Hh = 62 + PH + 40
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'front view, every figure scaled to the same on-screen height',
           fill=(232, 232, 242))
    d.text((12, 28), 'our A-pose (leg-tilt %.1f) at three splays, vs the reference '
           'patch.  Horizontal rules at 0/25/50/75/100%% of height.' % TILT,
           fill=(150, 160, 180))

    for ci, (lab, P, T) in enumerate(cols):
        cx = 12 + ci * (PW + 10)
        cy = 62
        lo = P[:, 2].min()
        hi = P[:, 2].max()
        cxm = 0.5 * (P[:, 0].min() + P[:, 0].max())
        gb = (cxm - (hi - lo) * 0.30, P[:, 1].min(), lo,
              cxm + (hi - lo) * 0.30, P[:, 1].max(), hi)
        gsc = min(PW, PH) * 0.94 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)
        p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, 0, 2, 1, False, gsc)
        p.mesh([tuple(x) for x in P], T, (0.94, 0.99, 0.96), 1)
        for f in GRID:
            z = lo + f * (hi - lo)
            p.line((gb[0], 0.0, z), (gb[3], 0.0, z), (70, 78, 96), 1)
        d.text((cx + 2, cy - 13), lab, fill=(205, 215, 232))
        sole = P[(P[:, 2] < lo + 0.02) & (P[:, 0] < 0)]
        calf = P[(P[:, 2] > lo + 0.22 * (hi - lo)) & (P[:, 2] < lo + 0.24 * (hi - lo))
                 & (P[:, 0] < 0)]
        d.text((cx + 2, cy + PH + 4), 'h %.3f  sole %.4f (%.4f h)'
               % (hi - lo, abs(sole[:, 0].mean()),
                  abs(sole[:, 0].mean()) / (hi - lo)), fill=(200, 212, 230))
        d.text((cx + 2, cy + PH + 20), 'calf@0.23h %.4f (%.4f h)'
               % (abs(calf[:, 0].mean()), abs(calf[:, 0].mean()) / (hi - lo)),
               fill=(200, 212, 230))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
