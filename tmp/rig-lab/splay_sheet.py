"""Front-view leg-splay sweep, against the reference patch.

Row 1 front (X-Z), row 2 side (Y-Z).  Same camera and scale for every panel.
The last column is the reference patch with its OWN skeleton drawn on it, so the
"mesh feet sit inboard of the foot bones" thing is visible next to our results.

Each of our panels is annotated with the two numbers that decide the stance:
the mesh sole centre and the bone ankle span.
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
VPK = ('D:/SteamLibrary/steamapps/common/L4D2/left4dead2/addons/Lunalice_Zoey.vpk')
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']
SPLAY = [float(x) for x in (sys.argv[1].split(',') if len(sys.argv) > 1
                            else ['0', '2.5', '4', '5.57', '7.07'])]
OUT = sys.argv[2] if len(sys.argv) > 2 else 'out/splay-sheet.png'
TILT = 5.7
PW, PH = 260, 440

PARENT = dict(PV.BONE_PARENT)
for extra, par in (('l_ball', 'l_foot'), ('r_ball', 'r_foot'), ('l_toe', 'l_ball'),
                   ('r_toe', 'r_ball'), ('l_shoulder_twist', 'l_shoulder'),
                   ('r_shoulder_twist', 'r_shoulder'), ('l_hand_twist', 'l_hand'),
                   ('r_hand_twist', 'r_hand'), ('head_tip', 'head'),
                   ('l_shoulderarmour', 'l_clavicle'), ('r_shoulderarmour', 'r_clavicle')):
    PARENT.setdefault(extra, par)


def main():
    runs = []
    for sp in SPLAY:
        r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                       fallback='region', leg_tilt=TILT, leg_splay=sp, verbose=False)
        runs.append((sp, r))
        P = np.asarray(r['out'])
        lo = P[:, 2].min()
        sole = P[(P[:, 2] < lo + 0.02) & (P[:, 0] < 0)]
        print('splay %5.2f  sole x %.4f  ankle span %.4f  height %.3f'
              % (sp, abs(sole[:, 0].mean()),
                 abs(r['new_head']['r_foot'][0] - r['new_head']['l_foot'][0]),
                 P[:, 2].max() - lo))

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    names = PV.read_bone_names(BH)
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
    heads = {k: v for k, v in PV.patch_rig_heads(meta, names).items() if k in PARENT}

    allp = [tuple(p) for p in np.asarray(runs[0][1]['out'])] + rp
    A = np.asarray(allp)
    gb = (A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
          A[:, 0].max(), A[:, 1].max(), A[:, 2].max())
    gsc = min(PW, PH) * 0.90 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    labels = ['splay %.2f' % sp for sp, _ in runs] + ['REFERENCE (+ own skeleton)']
    W = 12 + len(labels) * (PW + 10)
    Hh = 60 + 2 * (PH + 46) + 8
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'front-view leg splay: our A-pose (leg-tilt %.1f) vs the '
           'reference patch' % TILT, fill=(232, 232, 242))
    d.text((12, 26), 'row 1 front (X-Z)   row 2 side (Y-Z).  The reference keeps HD2\'s '
           'own leg splay (7.07 deg) but holds its MESH feet 3.4 cm inboard of its '
           'foot bones.', fill=(150, 160, 180))

    for ci, lab in enumerate(labels):
        cx = 12 + ci * (PW + 10)
        d.text((cx + 2, 42), lab, fill=(205, 215, 232))
        for ri, (tag, ah, av, ad, flip) in enumerate(
                [('front (X-Z)', 0, 2, 1, False), ('side (Y-Z)', 1, 2, 0, True)]):
            cy = 60 + ri * (PH + 46)
            d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            if ci < len(runs):
                P = np.asarray(runs[ci][1]['out'])
                p.mesh([tuple(x) for x in P], runs[ci][1]['tris'], (0.94, 1.0, 0.96), 1)
            else:
                p.mesh(rp, rt, (0.96, 0.96, 1.0), 1)
                for nm, h in heads.items():
                    par = PARENT.get(nm)
                    if par and par in heads:
                        p.line(heads[par], h, (90, 240, 230), 2)
                for v in heads.values():
                    p.dot(v, (255, 90, 210), 3)
                P = np.asarray(rp)
            lo = P[:, 2].min()
            sole = P[(P[:, 2] < lo + 0.02) & (P[:, 0] < 0)]
            txt = 'sole x %.4f' % abs(sole[:, 0].mean()) if len(sole) else ''
            d.text((cx + 2, cy + PH - 24), txt, fill=(200, 212, 230))
            if ci < len(runs):
                nh = runs[ci][1]['new_head']
                d.text((cx + 2, cy + PH - 11),
                       'ankle span %.4f' % abs(nh['r_foot'][0] - nh['l_foot'][0]),
                       fill=(200, 212, 230))
            else:
                d.text((cx + 2, cy + PH - 11), 'bone ankle x %.4f' % abs(
                    heads['l_foot'][0]), fill=(200, 212, 230))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
