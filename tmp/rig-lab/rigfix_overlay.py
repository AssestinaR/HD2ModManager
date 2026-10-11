"""The rig fix, drawn: LEGACY vs ROUND TRIP vs the reference -- all with bones.

Column A  ours as we ship today  (`S_A = D . rest_sym`, SYMMETRIC axes baked in)
Column B  ours after the BatchTool round trip  (axes converted back to ORIGINAL)
Column C  the reference patch with ITS own rest

Every column carries the skeleton so the comparison is like-for-like: cyan +
magenta is OUR shipped rest, amber is HD2's NATIVE rest.  The gap between the two
is literally how far the game carries each joint away from where the mesh was
bound -- zero gap means zero idle deformation, which is what the reference has.

row 1 = bind pose (`out`, the mesh we ship)
row 2 = the GAME frame (`out_idle` = mesh re-posed by HD2's own rest)

usage: rigfix_overlay.py [out.png] [zmin,zmax frac] [WxH] [XHALF]
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

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/rigfix-overlay.png'
BAND = ([float(x) for x in sys.argv[2].split(',')] if len(sys.argv) > 2
        else [None, None])
PW, PH = ([int(x) for x in sys.argv[3].split('x')] if len(sys.argv) > 3
          else [310, 470])
XHALF = float(sys.argv[4]) if len(sys.argv) > 4 else 0.55
TILT, SPLAY = 5.7, 4.0

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

OURS, OURSD = (90, 240, 230), (255, 90, 210)
NATIVE, NATIVED = (255, 190, 60), (255, 220, 120)
REFCOL, REFD = (140, 220, 255), (200, 236, 255)


def run(rt):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='distance', leg_tilt=TILT,
                      leg_splay=SPLAY, axis_roundtrip=rt, verbose=False)


def rot_ang(A, B):
    R = A[:3, :3].T @ B[:3, :3]
    return float(np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2.0, -1, 1))))


def rig_stats(r):
    S, rh = r['rest_shipped'], r['rest_hd2']
    dr, dh = [], []
    for i in S:
        dr.append(rot_ang(S[i], rh[i]))
        dh.append(float(np.linalg.norm(S[i][:3, 3] - rh[i][:3, 3])))
    dr, dh = np.asarray(dr), np.asarray(dh)
    d = np.linalg.norm(np.asarray(r['out_idle']) - np.asarray(r['out']), axis=1)
    return dr, dh, d


def main():
    legacy, rt = run(False), run(True)
    lr, lh, ld = rig_stats(legacy)
    rr, rh, rd = rig_stats(rt)
    print('legacy    rot vs native: mean %6.2f  max %6.2f deg   >5deg %d/%d   '
          'idle move mean %.4f max %.4f' % (lr.mean(), lr.max(), int((lr > 5).sum()),
                                            len(lr), ld.mean(), ld.max()))
    print('roundtrip rot vs native: mean %6.2f  max %6.2f deg   >5deg %d/%d   '
          'idle move mean %.4f max %.4f' % (rr.mean(), rr.max(), int((rr > 5).sum()),
                                            len(rr), rd.mean(), rd.max()))

    hd2 = PV.hd2_rig_heads(TOC, BH)
    names = PV.read_bone_names(BH)
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
    ref_heads = {k: v for k, v in PV.patch_rig_heads(meta, names).items()
                 if k in PARENT}
    hd2f = {k: v for k, v in hd2.items() if k in PARENT}

    clean, _g = H.drop_outlier_tris(rt['src_pts'], rt['tris'])
    tris = clean

    def heads(r):
        return {h['name']: tuple(r['rest_shipped'][h['index']][:3, 3])
                for h in r['hd2'] if h['name'] and h['index'] in r['rest_shipped']
                and h['name'] in PARENT}

    allp = [tuple(p) for p in rt['out']] + rp
    A = np.asarray(allp)
    lo, hi = A[:, 2].min(), A[:, 2].max()
    h = hi - lo
    if BAND[0] is not None:
        cxm = 0.5 * (A[:, 0].min() + A[:, 0].max())
        xh = XHALF * (BAND[1] - BAND[0]) * h
        gb = (cxm - xh, A[:, 1].min(), lo + BAND[0] * h,
              cxm + xh, A[:, 1].max(), lo + BAND[1] * h)
    else:
        gb = (A[:, 0].min(), A[:, 1].min(), lo, A[:, 0].max(), A[:, 1].max(), hi)
    gsc = min(PW, PH) * 0.90 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    COLS = [('A  LEGACY (shipped today)', legacy, heads(legacy),
             'rot vs native: mean %.1f  max %.0f deg   >5deg %d/%d'
             % (lr.mean(), lr.max(), int((lr > 5).sum()), len(lr))),
            ('B  ROUND TRIP (BatchTool order)', rt, heads(rt),
             'rot vs native: mean %.2f  max %.2f deg   >5deg %d/%d'
             % (rr.mean(), rr.max(), int((rr > 5).sum()), len(rr))),
            ('C  reference patch + ITS rest', None, ref_heads,
             'rig is bit-identical to HD2 native')]

    W = 12 + len(COLS) * (PW + 10)
    Hh = 128 + 2 * (PH + 34) + 10
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'the rig fix -- BatchTool round trip vs what we ship today '
           '(tilt %.1f, splay %.1f, yaw/mesh/distance)' % (TILT, SPLAY),
           fill=(232, 232, 242))
    d.text((12, 26), 'row 1 bind pose `out` (shipped mesh)    row 2 GAME frame '
           '`out_idle` = mesh re-posed by HD2\'s own rest, i.e. what the player sees.',
           fill=(150, 160, 180))
    d.text((12, 44), 'cyan/magenta = our shipped rest    amber = HD2 NATIVE rest '
           '   pale blue = reference rig', fill=(200, 212, 230))
    d.text((12, 62), 'A ships bone orientations up to 180 deg off HD2\'s, because '
           'basis_i and the symmetric axis deltas stay baked in.  B runs the '
           'BatchTool\'s real order:', fill=(255, 214, 120))
    d.text((12, 78), 'axes -> SYMMETRIC, bake tpose basis, bake initial basis, '
           'axes -> ORIGINAL.  The two bakes cancel on the rig and the shipped '
           'orientations become HD2\'s.', fill=(255, 214, 120))
    d.text((12, 94), 'idle frame displaces the mesh:  A mean %.4f max %.4f m      '
           'B mean %.4f max %.4f m      C 0.0000 m'
           % (ld.mean(), ld.max(), rd.mean(), rd.max()), fill=(255, 214, 120))

    for ci, (lab, r, hs, note) in enumerate(COLS):
        cx = 12 + ci * (PW + 10)
        d.text((cx + 2, 128 - 12), lab, fill=(205, 215, 232))
        for ri, (tag, ah, av, ad, flip) in enumerate(
                [('bind pose `out`', 0, 2, 1, False),
                 ('GAME frame `out_idle`', 1, 2, 0, True)]):
            cy = 128 + ri * (PH + 34)
            d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            if r is None:
                p.mesh(rp, rtris, (0.96, 0.96, 1.0), 1)
            elif ri == 0:
                p.mesh([tuple(x) for x in r['out']], tris, (0.94, 1.0, 0.96), 1)
            else:
                p.mesh([tuple(x) for x in r['out_idle']], tris, (0.94, 1.0, 0.96), 1)

            def draw(hsx, col, dot):
                for nm, hh in hsx.items():
                    par = PARENT.get(nm)
                    if par and par in hsx:
                        p.line(hsx[par], hh, col, 2)
                for v in hsx.values():
                    p.dot(v, dot, 3)

            if r is None:
                draw(hs, REFCOL, REFD)
            elif ri == 0:
                draw(hs, OURS, OURSD)
                draw(hd2f, NATIVE, NATIVED)
            else:
                draw(hd2f, NATIVE, NATIVED)
            if ri == 0:
                d.text((cx + 2, cy + PH + 8), note, fill=(230, 200, 130))

    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
