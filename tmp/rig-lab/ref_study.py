"""The reference study sheet.

Row 1 = front (X-Z), row 2 = side (Y-Z, +Y forward).  One shared camera.

col 1  our A-pose, legs upright (leg-tilt 0)
col 2  our A-pose, legs leaned forward about the ankle (leg-tilt 4)
col 3  the reference patch mod, complete (its own head + hair unit included)
col 4  the same reference, with the patch's OWN skeleton drawn on top

col 4 is the whole point: the patch's bones are bit-identical to HD2's native rig,
so every place a bone leaves the silhouette is the author's deliberate choice to
let the mesh deviate from the skeleton instead of moving the bones.
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
MODEL = 'survivor_teenangst'
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/ref-study.png'
PW, PH = 300, 460

PARENT = dict(PV.BONE_PARENT)
for extra, par in (('l_ball', 'l_foot'), ('r_ball', 'r_foot'), ('l_toe', 'l_ball'),
                   ('r_toe', 'r_ball'), ('l_shoulder_twist', 'l_shoulder'),
                   ('r_shoulder_twist', 'r_shoulder'), ('l_hand_twist', 'l_hand'),
                   ('r_hand_twist', 'r_hand'), ('head_tip', 'head'),
                   ('l_shoulderarmour', 'l_clavicle'), ('r_shoulderarmour', 'r_clavicle')):
    PARENT.setdefault(extra, par)


def main():
    runs = {}
    for tl in (0.0, 5.7):
        runs[tl] = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                              fallback='distance', leg_tilt=tl, verbose=False)

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
    print('reference bones drawn: %d' % len(heads))

    ours0 = np.asarray(runs[0.0]['out'])
    ours4 = np.asarray(runs[5.7]['out'])
    tris = runs[0.0]['tris']

    allp = [tuple(p) for p in ours0] + [tuple(p) for p in ours4] + rp
    A = np.asarray(allp)
    gb = (A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
          A[:, 0].max(), A[:, 1].max(), A[:, 2].max())
    gsc = min(PW, PH) * 0.88 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    labels = ['1  ours: A-pose, legs upright', '2  ours: A-pose, legs leaned fwd 5.7 deg',
              '3  reference patch (head included)', '4  reference patch + ITS OWN skeleton']
    views = [('front  (X-Z)', 0, 2, 1, False), ('side  (Y-Z, +Y = forward)', 1, 2, 0, True)]

    W, Hh = 12 + len(labels) * (PW + 12), 64 + 2 * (PH + 30) + 8
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'reference study: our A-pose vs the shipped reference patch',
           fill=(232, 232, 242))
    d.text((12, 26), 'same camera and scale for every panel.  The patch bones are '
           'identical to HD2 native; the mesh is what the author moved.',
           fill=(150, 160, 180))

    for ci, lab in enumerate(labels):
        cx = 12 + ci * (PW + 12)
        d.text((cx + 2, 44), lab, fill=(205, 215, 232))
        for ri, (tag, ah, av, ad, flip) in enumerate(views):
            cy = 64 + ri * (PH + 30)
            d.text((cx + 2, cy - 12), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            if ci == 0:
                p.mesh([tuple(x) for x in ours0], tris, (0.94, 1.0, 0.96), 1)
            elif ci == 1:
                p.mesh([tuple(x) for x in ours4], tris, (0.94, 1.0, 0.96), 1)
            else:
                p.mesh(rp, rt, (0.96, 0.96, 1.0), 1)
                if ci == 3:
                    for nm, h in heads.items():
                        par = PARENT.get(nm)
                        if par and par in heads:
                            p.line(heads[par], h, (90, 240, 230), 2)
                    for v in heads.values():
                        p.dot(v, (255, 90, 210), 3)
            A2 = np.asarray(ours0 if ci == 0 else ours4 if ci == 1 else rp)
            d.text((cx + 2, cy + PH - 26),
                   'height %.3f m  (top %.3f)' % (A2[:, 2].max() - A2[:, 2].min(),
                                                  A2[:, 2].max()), fill=(200, 212, 230))
            if tag.startswith('side'):
                lo = A2[:, 2].min()
                m = A2[(A2[:, 2] < lo + 0.01) & (A2[:, 0] < -0.05)]
                if len(m):
                    d.text((cx + 2, cy + PH - 12),
                           'sole y %+.3f..%+.3f (len %.3f)'
                           % (m[:, 1].min(), m[:, 1].max(),
                              m[:, 1].max() - m[:, 1].min()), fill=(200, 212, 230))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
