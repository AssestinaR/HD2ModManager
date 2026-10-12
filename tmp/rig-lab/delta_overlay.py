"""Axis-delta ON vs OFF, with HD2's bones drawn on BOTH of our columns.

`body_pose_profile.json` ships 20 `symmetric_axis_deltas_wxyz`.  Eighteen of them
are ~180 deg flips (everything on the right arm/hand).  They are baked into our
shipped rest, so the game's own idle frame -- which is `HD2_rest . inv(our rest)`
-- rotates the right arm mesh by 180 deg about every bone in the chain and
flattens it into a blade.  The BatchTool's LAST step is "back to the initial
state + original bone axes", so those deltas are not supposed to survive into the
final rest.

This renders three columns, each with the skeleton on top:

    A  ours, axis deltas ON   (what we ship today)
    B  ours, axis deltas OFF  (what the BatchTool's last step implies)
    C  the reference patch, with ITS own rest

row 1 = the bind pose (`out`, what we ship as the mesh)
row 2 = the GAME frame (`out_idle` = mesh re-posed by HD2's own rest)

and prints the head_slip numbers that say whether B is actually better.

usage: delta_overlay.py [out.png] [zmin,zmax frac] [WxH] [XHALF]
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

OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/delta-overlay.png'
BAND = ([float(x) for x in sys.argv[2].split(',')] if len(sys.argv) > 2
        else [None, None])
PW, PH = ([int(x) for x in sys.argv[3].split('x')] if len(sys.argv) > 3
          else [300, 460])
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

OURS = (90, 240, 230)       # cyan -- our rest
OURSD = (255, 90, 210)      # magenta dots
NATIVE = (255, 190, 60)     # amber -- HD2 native
REFCOL = (140, 220, 255)    # pale blue -- the reference's own rig


def run(axis_deltas):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=TILT,
                      leg_splay=SPLAY, axis_deltas=axis_deltas, verbose=False)


def ang(M):
    t = np.trace(M[:3, :3])
    return np.degrees(np.arccos(np.clip((t - 1) / 2.0, -1, 1)))


def tear(r):
    """head_slip per joint: how far the game drags the joint away from where the
    parent's transform would have put it -- i.e. the length over which the mesh
    is stretched at that joint in the idle frame."""
    hd2 = r['hd2']
    T = {i: r['rest_hd2'][i] @ np.linalg.inv(r['rest'][i]) for i in r['rest_hd2']}
    rows = []
    for h in hd2:
        i, p = h['index'], h.get('parent')
        if i not in T or p is None or p == i or p not in T:
            continue
        head = r['rest'][i][:3, 3]
        a = (T[i] @ np.append(head, 1.0))[:3]
        b = (T[p] @ np.append(head, 1.0))[:3]
        rows.append((float(np.linalg.norm(a - b)), h['name']))
    rows.sort(key=lambda x: -x[0])
    return rows


def stats(rows):
    hs = np.asarray([x[0] for x in rows])
    return (hs.mean(), float(np.median(hs)), float(np.percentile(hs, 90)),
            float(hs.max()), int((hs > 0.10).sum()))


def report(tag, rows):
    m, md, p90, mx, n10 = stats(rows)
    print('%-18s head_slip  mean %.4f  median %.4f  p90 %.4f  max %.4f  >0.10 m: %d'
          % (tag, m, md, p90, mx, n10))
    print('%-18s worst: %s' % ('', ', '.join('%s %.3f' % (n, v)
                                             for v, n in rows[:6])))
    return m, md, p90, mx, n10


def heads_of(r):
    by_name = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    out = {}
    for nm, i in by_name.items():
        if i in r['rest']:
            out[nm] = tuple((r['D'][i] @ r['rest'][i])[:3, 3])
    return out


def main():
    print('== axis deltas ON vs OFF (zoey, yaw / mesh-spine / distance-fallback,'
          ' tilt %.1f splay %.1f) ==' % (TILT, SPLAY))
    on, off = run(True), run(False)
    rep_on = report('axis deltas ON', tear(on))
    rep_off = report('axis deltas OFF', tear(off))

    d_out = np.linalg.norm(np.asarray(on['out']) - np.asarray(off['out']), axis=1)
    print('bind mesh `out` difference between ON and OFF: mean %.5f m  max %.5f m'
          % (d_out.mean(), d_out.max()))
    d_idle = np.linalg.norm(np.asarray(on['out_idle']) - np.asarray(off['out_idle']),
                            axis=1)
    print('idle mesh `out_idle` difference:               mean %.5f m  max %.5f m'
          % (d_idle.mean(), d_idle.max()))
    for tag, r in (('ON', on), ('OFF', off)):
        m = r['metrics']
        print('%-18s edge_mean %.4f  edge_max %6.2f  bad %5.2f%%  height %.3f'
              % ('metrics ' + tag, m['edge_mean'], m['edge_max'], 100 * m['bad'],
                 m['height_after']))

    # ---------------------------------------------------------------- render
    hd2 = PV.hd2_rig_heads(TOC, BH)
    names = PV.read_bone_names(BH)
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
    ref_heads = {k: v for k, v in PV.patch_rig_heads(meta, names).items()
                 if k in PARENT}

    clean, _good = H.drop_outlier_tris(on['src_pts'], on['tris'])
    tris = clean

    allp = [tuple(p) for p in on['out']] + rp
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

    COLS = [('ours: axis deltas ON  (shipped)', on, heads_of(on)),
            ('ours: axis deltas OFF', off, heads_of(off)),
            ('reference patch + ITS rest', None, ref_heads)]
    W = 12 + len(COLS) * (PW + 10)
    Hh = 92 + 2 * (PH + 30) + 10
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'axis-delta ON vs OFF -- skeleton drawn on every column '
           '(tilt %.1f, splay %.1f, yaw/mesh/distance)' % (TILT, SPLAY),
           fill=(232, 232, 242))
    d.text((12, 26), 'row 1 bind pose `out` (shipped mesh)   row 2 GAME frame '
           '`out_idle` = mesh re-posed by HD2\'s own rest -- that is what the '
           'player sees at idle.', fill=(150, 160, 180))
    d.text((12, 44), 'cyan/magenta = our shipped rest    amber = HD2 native rest'
           '    pale blue = reference rig', fill=(200, 212, 230))
    d.text((12, 62), 'head_slip (idle frame, how far the game drags each joint '
           'off its parent):  ON  mean %.4f  max %.4f  >0.10m %d      |      OFF  '
           'mean %.4f  max %.4f  >0.10m %d'
           % (rep_on[0], rep_on[3], rep_on[4], rep_off[0], rep_off[3], rep_off[4]),
           fill=(255, 214, 120))
    d.text((12, 76), 'worst joint ON: %s %.3f m        worst joint OFF: %s %.3f m'
           % (tear(on)[0][1], tear(on)[0][0], tear(off)[0][1], tear(off)[0][0]),
           fill=(255, 214, 120))

    for ci, (lab, r, hs) in enumerate(COLS):
        cx = 12 + ci * (PW + 10)
        d.text((cx + 2, 92 - 14), lab, fill=(205, 215, 232))
        for ri, (tag, ah, av, ad, flip) in enumerate(
                [('bind pose `out`', 0, 2, 1, False),
                 ('GAME frame `out_idle`', 1, 2, 0, True)]):
            cy = 92 + ri * (PH + 30)
            d.text((cx + 2, cy - 11), tag, fill=(150, 165, 185))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            if r is None:
                p.mesh(rp, rt, (0.96, 0.96, 1.0), 1)
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
                draw(hs, REFCOL, (200, 236, 255))
            elif ri == 0:
                draw(hs, OURS, OURSD)
                draw({k: v for k, v in hd2.items() if k in PARENT},
                     NATIVE, (255, 220, 120))
            else:
                draw({k: v for k, v in hd2.items() if k in PARENT},
                     NATIVE, (255, 220, 120))

    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
