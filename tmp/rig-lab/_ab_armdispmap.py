"""Round 45d: colour the LEFT arm by how far arm_rot moved it (vs no arm_rot).

grey < 5 mm, yellow 5-30 mm, red > 30 mm.  Also prints the radius of the arm
around its own centreline, so a bulge shows up as a number, not an impression.

usage: python _ab_armdispmap.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
AR = (10.46, -18.55, 29.63)
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
            upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0, verbose=False)


def run(**kw):
    b = dict(BASE); b.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **b)


GREY, YELL, RED = (0.62, 0.68, 0.74), (0.98, 0.82, 0.35), (0.95, 0.32, 0.28)


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                          'out/arm-disp-rot.png')
    off = run(arm_rot=None)
    on = run(arm_rot=AR)
    tris = off['tris']
    P0 = np.asarray(off['out'], float)
    P1 = np.asarray(on['out'], float)
    d = np.linalg.norm(P1 - P0, axis=1)
    print('arm_rot displacement: max %.4f mean %.4f  >3cm %d  >0.5cm %d'
          % (d.max(), d.mean(), int((d > 0.03).sum()), int((d > 0.005).sum())))

    idx = {h['name']: h['index'] for h in off['hd2'] if h['name']}
    rest = off['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)

    # ---- radius profile of the arm around its own centreline -------------
    def profile(P, tag):
        t = (P - a) @ u
        perp = np.linalg.norm((P - a) - np.outer(t, u), axis=1)
        sel = (P[:, 0] < -0.15) & (perp < 0.13) & (t > 0.0) & (t < 0.80)
        print('\n%s  (t: elbow 0.286, wrist 0.555)' % tag)
        for lo in np.arange(0.0, 0.75, 0.05):
            m = sel & (t >= lo) & (t < lo + 0.05)
            if m.sum() < 20:
                continue
            # centre of the ring in the plane perpendicular to u
            base = a + lo * u
            dd = P[m] - base
            tt = dd @ u
            perp2 = dd - np.outer(tt, u)
            c = perp2.mean(0)
            r = np.linalg.norm(perp2 - c, axis=1)
            print('   t %+.2f..%+.2f  n=%4d  mean r=%.4f  max r=%.4f'
                  % (lo, lo + 0.05, int(m.sum()), float(r.mean()), float(r.max())))

    profile(P0, 'NO arm_rot')
    profile(P1, 'WITH arm_rot (seq)')

    # ---- figure -----------------------------------------------------------
    band = []
    for k in range(0, len(tris), 1):
        t = tris[k]
        m = max(d[t[0]], d[t[1]], d[t[2]])
        band.append((t, GREY if m < 0.005 else (YELL if m < 0.03 else RED)))
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    ed = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            ed.append((head[rig[p]['name']], head[h['name']]))
    rows = [('front (X-Z)', 0, 2, 1, [-0.80, -1.0, 1.05, -0.15, 1.0, 1.55]),
            ('top (X-Y)', 0, 1, 2, [-0.80, -0.22, -1.0, -0.15, 0.36, 1.0])]
    pw, ph = 620, 560
    W = 12 + len(rows) * (pw + 14)
    Hh = 62 + ph + 26 + 16
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45d: how far arm_rot moves the LEFT arm -- grey '
            '<5 mm, yellow 5-30 mm, red >30 mm', fill=(232, 232, 244))
    dr.text((12, 26), 'cyan = the HD2 shoulder chain', fill=(150, 160, 180))
    for ci, (tag, ah, av, ad, box) in enumerate(rows):
        cx = 12 + ci * (pw + 14)
        cy = 62
        dr.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
        p = PV.Panel(img, dr, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
        for col in (GREY, YELL, RED):
            sub = [t for t, c in band if c == col]
            if sub:
                p.mesh([tuple(q) for q in P1], sub, col, 1)
        for x, y in ed:
            p.line(x, y, (92, 97, 112), 1)
        for nm, par in (('l_shoulder', 'l_clavicle'), ('l_elbow', 'l_shoulder'),
                        ('l_hand', 'l_elbow')):
            if nm in head and par in head:
                p.line(head[par], head[nm], (90, 220, 240), 3)
        for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
            if nm in head:
                p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('\n[written] %s %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
