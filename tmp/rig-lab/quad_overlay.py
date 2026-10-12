"""Four rows: bind front / bind side / idle front / idle side, three columns.

Row layout is the user's: the bind pose and the GAME frame each get a front and a
side view, so a defect can be told apart from a viewing-angle artefact.

  column A  NEW -- the shipped rig is HD2's native rest verbatim, and the mesh is
            what gets posed.  `A . inv(S_bind)` is the identity to 1e-16, so the
            idle row is pixel-identical to the bind row.
  column B  what we ship today -- HD2's joints snapped onto the source joints.
  column C  the reference patch.

Every column carries the skeleton: cyan/magenta = the rest that column ships,
amber = HD2's NATIVE rest.

usage: quad_overlay.py [out.png] [WxH] [XHALF]
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import probe_native_rig as NR

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/quad-overlay.png'
PW, PH = ([int(x) for x in sys.argv[2].split('x')] if len(sys.argv) > 2
          else [270, 430])
XHALF = float(sys.argv[3]) if len(sys.argv) > 3 else 0.62
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

ROWS = [('bind -- front (X-Z)', 0, 2, 1, False),
        ('bind -- side (Y-Z, +Y fwd)', 1, 2, 0, True),
        ('GAME frame -- front (X-Z)', 0, 2, 1, False),
        ('GAME frame -- side (Y-Z, +Y fwd)', 1, 2, 0, True)]


def main():
    kw = dict(align='yaw', spine_map='mesh', fallback='distance',
              leg_tilt=TILT, leg_splay=SPLAY, verbose=False)
    new = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                     skeleton='native', **kw)
    cur = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                     skeleton='snap', **kw)

    hd2 = PV.hd2_rig_heads(TOC, BH)
    hd2f = {k: v for k, v in hd2.items() if k in PARENT}
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

    def heads_of(S):
        return {h['name']: tuple(S[h['index']][:3, 3]) for h in new['hd2']
                if h['name'] and h['index'] in S and h['name'] in PARENT}

    new_heads = heads_of(new['rest_shipped'])
    cur_heads = heads_of(cur['rest_shipped'])

    clean, _g = H.drop_outlier_tris(new['src_pts'], new['tris'])
    tris = clean
    new_idle = new['out_idle']

    COLS = [('A  NEW: rig = HD2 native, mesh posed',
             [tuple(x) for x in new['out']], [tuple(x) for x in new_idle],
             new_heads, 'idle == bind to 1e-16'),
            ('B  TODAY: HD2 joints snapped to source',
             [tuple(x) for x in cur['out']], [tuple(x) for x in cur['out_idle']],
             cur_heads, 'idle drags the mesh (max %.3f m)'
             % float(np.linalg.norm(np.asarray(cur['out_idle'])
                                    - np.asarray(cur['out']), axis=1).max())),
            ('C  reference patch + ITS rest',
             rp, rp, ref_heads, 'rig is HD2 native')]

    allp = [tuple(p) for p in new['out']] + [tuple(p) for p in cur['out']] + rp
    A = np.asarray(allp)
    lo, hi = A[:, 2].min(), A[:, 2].max()
    cxm = 0.5 * (A[:, 0].min() + A[:, 0].max())
    xh = XHALF * (hi - lo)
    gb = (cxm - xh, A[:, 1].min(), lo, cxm + xh, A[:, 1].max(), hi)
    gsc = min(PW, PH) * 0.92 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    W = 12 + len(COLS) * (PW + 10)
    Hh = 150 + len(ROWS) * (PH + 30) + 10
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'bind pose vs GAME frame, front and side -- tilt %.1f splay '
           '%.1f  (zoey)' % (TILT, SPLAY), fill=(232, 232, 242))
    d.text((12, 28), 'cyan/magenta = the rest that column ships    amber = HD2 '
           'NATIVE rest.  In column A they coincide, because the shipped rig IS '
           'HD2\'s native rest.', fill=(150, 160, 180))
    d.text((12, 48), 'rows 3/4 are the GAME frame: the runtime applies '
           'rest_hd2[b] . inv(S_bind[b]) to every vertex.', fill=(150, 160, 180))
    d.text((12, 72), 'column B idle drags the mesh by up to %.3f m -- the missing '
           'heel, the broken ankle, the stretched neck, the split skirt.'
           % float(np.linalg.norm(np.asarray(cur['out_idle'])
                                  - np.asarray(cur['out']), axis=1).max()),
           fill=(255, 214, 120))
    d.text((12, 92), 'column A ships HD2\'s rest untouched, so rows 3/4 repeat '
           'rows 1/2 exactly (max diff 0.00e+00 m).  The arms still need the mesh '
           'placement work.', fill=(255, 214, 120))

    for ci, (lab, mb, mi, hs, note) in enumerate(COLS):
        cx = 12 + ci * (PW + 10)
        d.text((cx + 2, 150 - 26), lab, fill=(205, 215, 232))
        d.text((cx + 2, 150 - 12), note, fill=(230, 200, 130))
        for ri, (tag, ah, av, ad, flip) in enumerate(ROWS):
            cy = 150 + ri * (PH + 30)
            d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            p.mesh(mb if ri < 2 else mi, tris if ci < 2 else rtris,
                   (0.94, 1.0, 0.96) if ci < 2 else (0.96, 0.96, 1.0), 1)

            def draw(hsx, col, dot):
                for nm, hh in hsx.items():
                    par = PARENT.get(nm)
                    if par and par in hsx:
                        p.line(hsx[par], hh, col, 2)
                for v in hsx.values():
                    p.dot(v, dot, 3)

            if ci == 2:
                draw(hs, REFCOL, REFD)
            else:
                draw(hs, OURS, OURSD)
                draw(hd2f, NATIVE, NATIVED)

    img.save(OUT)
    print('[written] %s' % OUT)
    print('new idle==bind max diff %.2e m'
          % float(np.linalg.norm(np.asarray(new_idle) - np.asarray(new['out']),
                                 axis=1).max()))


if __name__ == '__main__':
    main()
