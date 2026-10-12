"""Our mesh with OUR skeleton drawn on it, next to the reference with ITS skeleton.

The reference column has always had its bones drawn; ours did not, which made the
comparison guesswork.  This puts both on the same footing, and adds a third
column showing HD2's NATIVE rig on our mesh -- that is the rig the game animates
toward, so the gap between our rest (cyan/magenta) and HD2's native (amber) is
exactly how far the game will carry each part of our mesh at idle.

Row 1 front (X-Z), row 2 side (Y-Z, +Y forward).  Same camera and scale.
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
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/stance-overlay.png'
BAND = ([float(x) for x in sys.argv[2].split(',')] if len(sys.argv) > 2
        else [None, None])            # zmin_frac, zmax_frac of body height
COLSEL = (sys.argv[3].split(',') if len(sys.argv) > 3
          else ['ours', 'rest', 'native', 'ref'])
SPLAY, TILT = 4.0, 5.7
PW, PH = ([int(x) for x in sys.argv[4].split('x')] if len(sys.argv) > 4
          else [270, 460])
XHALF = float(sys.argv[5]) if len(sys.argv) > 5 else 0.55

PARENT = dict(PV.BONE_PARENT)
for extra, par in (('l_ball', 'l_foot'), ('r_ball', 'r_foot'), ('l_toe', 'l_ball'),
                   ('r_toe', 'r_ball'), ('l_shoulder_twist', 'l_shoulder'),
                   ('r_shoulder_twist', 'r_shoulder'), ('l_hand_twist', 'l_hand'),
                   ('r_hand_twist', 'r_hand'), ('head_tip', 'head'),
                   ('l_shoulderarmour', 'l_clavicle'),
                   ('r_shoulderarmour', 'r_clavicle'),
                   ('l_middle_finger1', 'l_hand'), ('r_middle_finger1', 'r_hand'),
                   ('l_index_finger1', 'l_hand'), ('r_index_finger1', 'r_hand'),
                   ('l_pinky_finger1', 'l_hand'), ('r_pinky_finger1', 'r_hand'),
                   ('l_thumb_finger1', 'l_hand'), ('r_thumb_finger1', 'r_hand')):
    PARENT.setdefault(extra, par)

OURS = (90, 240, 230)       # cyan -- our shipped rest
OURSD = (255, 90, 210)      # magenta dots
NATIVE = (255, 190, 60)     # amber -- HD2's own rest


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=TILT,
                   leg_splay=SPLAY, verbose=False)
    ours_mesh = np.asarray(r['out'])
    hd2 = PV.hd2_rig_heads(TOC, BH)
    by_name = {h['name']: h['index'] for h in r['hd2'] if h['name']}

    # our shipped rest: the returned rest_sym is the T-posed one; the mesh is
    # bound to D*rest_sym, so that is what has to be drawn.
    ours_rest = {}
    for nm, i in by_name.items():
        if i in r['rest']:
            ours_rest[nm] = tuple((r['D'][i] @ r['rest'][i])[:3, 3])

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
    ref_heads = {k: v for k, v in PV.patch_rig_heads(meta, names).items()
                 if k in PARENT}

    allp = [tuple(p) for p in ours_mesh] + rp
    A = np.asarray(allp)
    gb = (A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
          A[:, 0].max(), A[:, 1].max(), A[:, 2].max())
    if BAND[0] is not None:
        lo, hi = A[:, 2].min(), A[:, 2].max()
        h = hi - lo
        cxm = 0.5 * (A[:, 0].min() + A[:, 0].max())
        xh = XHALF * (BAND[1] - BAND[0]) * h
        gb = (cxm - xh, A[:, 1].min(), lo + BAND[0] * h,
              cxm + xh, A[:, 1].max(), lo + BAND[1] * h)
    gsc = min(PW, PH) * 0.90 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    ALL = {'ours': ('ours: mesh only', 0),
           'rest': ('ours + OUR shipped rest', 1),
           'native': ('ours + HD2 NATIVE rest', 2),
           'ref': ('reference + ITS own rest', 3)}
    labels = [(ALL[c][0], ALL[c][1]) for c in COLSEL]
    W = 12 + len(labels) * (PW + 10)
    Hh = 74 + 2 * (PH + 30) + 8
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'stance check -- our A-pose (leg-tilt %.1f, leg-splay %.1f) '
           'against the shipped reference patch' % (TILT, SPLAY),
           fill=(232, 232, 242))
    d.text((12, 26), 'row 1 front (X-Z), row 2 side (Y-Z, +Y forward).  column 3 '
           'draws HD2\'s OWN rest on our mesh: every place it leaves the cyan '
           'skeleton is how far the game will carry that part of the mesh.',
           fill=(150, 160, 180))
    d.text((12, 44), 'cyan/magenta = our shipped rest     amber = HD2 native rest',
           fill=(200, 212, 230))

    for ci, (lab, kind) in enumerate(labels):
        cx = 12 + ci * (PW + 10)
        d.text((cx + 2, 58), lab, fill=(205, 215, 232))
        for ri, (tag, ah, av, ad, flip) in enumerate(
                [('front (X-Z)', 0, 2, 1, False), ('side (Y-Z)', 1, 2, 0, True)]):
            cy = 74 + ri * (PH + 30)
            d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            if kind < 3:
                p.mesh([tuple(x) for x in ours_mesh], r['tris'], (0.94, 1.0, 0.96), 1)
            else:
                p.mesh(rp, rt, (0.96, 0.96, 1.0), 1)

            def draw(hs, col, dot):
                for nm, h in hs.items():
                    par = PARENT.get(nm)
                    if par and par in hs:
                        p.line(hs[par], h, col, 2)
                for v in hs.values():
                    p.dot(v, dot, 3)

            if kind in (1, 2):
                draw({k: v for k, v in ours_rest.items() if k in PARENT},
                     OURS, OURSD)
            if kind == 2:
                draw(hd2, NATIVE, (255, 220, 120))
            elif kind == 3:
                draw(ref_heads, OURS, OURSD)

            if kind == 2:
                ds = [np.linalg.norm(np.asarray(ours_rest[k]) - np.asarray(hd2[k]))
                      for k in hd2 if k in ours_rest]
                ds = np.asarray(ds)
                d.text((cx + 2, cy + PH - 24),
                       'rest gap: mean %.3f m' % ds.mean(), fill=(240, 200, 120))
                d.text((cx + 2, cy + PH - 11),
                       'median %.3f  max %.3f' % (np.median(ds), ds.max()),
                       fill=(240, 200, 120))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
