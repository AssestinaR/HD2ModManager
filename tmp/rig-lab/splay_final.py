"""Final front-view leg strip: our A-pose at three splays vs the reference.

Every figure is scaled to the same on-screen height and only the legs are shown,
so the stance is the only thing on the page.  The last panel is the reference
with its own skeleton, which is where the "bones are more splayed than the mesh"
story is visible.
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
                            else ['0', '3', '4', '5.57'])]
OUT = sys.argv[2] if len(sys.argv) > 2 else 'out/splay-final.png'
TILT = 5.7
PW, PH = 240, 560
TOP, BOT = 0.00, 0.62          # fraction of body height shown (sole = 0)

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
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='region', leg_tilt=TILT,
                       leg_splay=sp, verbose=False)
        runs.append((sp, r))

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
    ref = np.asarray(rp)

    cols = [('splay %.2f' % sp, np.asarray(r['out']), r['tris'], r) for sp, r in runs]
    cols.append(('REFERENCE (+ its own skeleton)', ref, rt, None))

    W = 12 + len(cols) * (PW + 10)
    Hh = 78 + PH + 44
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'front view, legs only -- every figure scaled to the same '
           'on-screen height', fill=(232, 232, 242))
    d.text((12, 26), 'our A-pose with leg-tilt %.1f, at four splays, vs the reference '
           'patch.  Bottom row of numbers is the mesh profile normalised by height: '
           'the fit lands on 4.0 deg.' % TILT, fill=(150, 160, 180))

    for ci, (lab, P, T, r) in enumerate(cols):
        cx = 12 + ci * (PW + 10)
        cy = 78
        lo, hi = P[:, 2].min(), P[:, 2].max()
        h = hi - lo
        cxm = 0.5 * (P[:, 0].min() + P[:, 0].max())
        half = 0.155 * h
        gb = (cxm - half, P[:, 1].min(), lo + TOP * h,
              cxm + half, P[:, 1].max(), lo + BOT * h)
        gsc = min(PW, PH) * 0.96 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)
        p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, 0, 2, 1, False, gsc)
        p.mesh([tuple(x) for x in P], T, (0.94, 0.99, 0.96), 1)
        for f in (0.25, 0.5):
            z = lo + f * h
            p.line((gb[0], 0.0, z), (gb[3], 0.0, z), (72, 80, 98), 1)
        if r is not None:
            for sd in ('l', 'r'):
                for j in ('thigh', 'knee', 'foot'):
                    nm = '%s_%s' % (sd, j)
                    v = r['new_head'].get(nm)
                    if v is not None:
                        p.dot(tuple(v), (255, 120, 210), 2)
                for a, b in (('thigh', 'knee'), ('knee', 'foot')):
                    va, vb = r['new_head'].get('%s_%s' % (sd, a)), r['new_head'].get('%s_%s' % (sd, b))
                    if va is not None and vb is not None:
                        p.line(tuple(va), tuple(vb), (255, 120, 210), 2)
        else:
            for nm, v in heads.items():
                par = PARENT.get(nm)
                if par and par in heads:
                    p.line(heads[par], v, (90, 240, 230), 2)
            for v in heads.values():
                p.dot(v, (255, 90, 210), 3)
        d.text((cx + 2, cy - 14), lab, fill=(205, 215, 232))
        sole = P[(P[:, 2] < lo + 0.02) & (P[:, 0] < 0)]
        d.text((cx + 2, cy + PH + 4), 'sole %.4f h' % (abs(sole[:, 0].mean()) / h),
               fill=(200, 212, 230))
        d.text((cx + 2, cy + PH + 20), 'calf@0.23h %.4f h'
               % (abs(P[(P[:, 2] > lo + 0.22 * h) & (P[:, 2] < lo + 0.24 * h)
                        & (P[:, 0] < 0)][:, 0].mean()) / h), fill=(200, 212, 230))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
