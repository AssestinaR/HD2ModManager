"""Round 45b: WHERE does the arm re-fit actually move the mesh?

Colour every triangle by how far arm_fit moved it: grey = untouched, yellow =
0.5-3 cm, red = more than 3 cm.  If the armpit is red, the re-fit is what opened
the seam there; if it is grey, the seam was already open.

usage: python probe_arm_disp.py [out.png]
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
PANEL = (520, 500)
STEP = 1

GREY = (0.62, 0.68, 0.74)
YELL = (0.98, 0.82, 0.35)
RED = (0.95, 0.32, 0.28)


def run(arm_fit):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      arm_fit=arm_fit, verbose=False)


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/arm-disp.png')
    a = run(0.0)
    b = run(1.0)
    tris = a['tris']
    P0 = np.asarray(a['out'], float)
    P1 = np.asarray(b['out'], float)
    d = np.linalg.norm(P1 - P0, axis=1)
    print('arm_fit displacement: max %.4f m, mean %.4f m, >3cm %d verts, '
          '>0.5cm %d verts'
          % (d.max(), d.mean(), int((d > 0.03).sum()), int((d > 0.005).sum())))

    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    edges = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            edges.append((head[rig[p]['name']], head[h['name']]))

    band = []
    for k in range(0, len(tris), STEP):
        t = tris[k]
        m = max(d[t[0]], d[t[1]], d[t[2]])
        if m < 0.005:
            band.append((t, GREY))
        elif m < 0.03:
            band.append((t, YELL))
        else:
            band.append((t, RED))

    rows = [('front (X-Z)', 0, 2, 1, [-0.80, -1.0, 1.05, 0.10, 1.0, 1.66]),
            ('top (X-Y)  -- +Y = forward', 0, 1, 2, [-0.80, -0.30, -1.0, 0.10, 0.34, 1.0])]
    pw, ph = PANEL
    W = 12 + len(rows) * (pw + 14)
    Hh = 62 + (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45b: how far arm_fit moved each triangle -- grey '
            'untouched, yellow 0.5-3 cm, red > 3 cm', fill=(232, 232, 244))
    dr.text((12, 26), 'cyan = the HD2 shoulder chain, yellow dots = its joints',
            fill=(150, 160, 180))

    for ci, (tag, ah, av, ad, box) in enumerate(rows):
        cx = 12 + ci * (pw + 14)
        cy = 62
        dr.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
        p = PV.Panel(img, dr, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
        for col in (GREY, YELL, RED):
            sub = [t for t, c in band if c == col]
            if sub:
                p.mesh([tuple(q) for q in P1], sub, col, 1)
        for x, y in edges:
            p.line(x, y, (92, 97, 112), 1)
        for nm, par in (('l_clavicle', None), ('l_shoulder', 'l_clavicle'),
                        ('l_elbow', 'l_shoulder'), ('l_hand', 'l_elbow')):
            if nm in head and par in head:
                p.line(head[par], head[nm], (90, 220, 240), 3)
        for nm in ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand'):
            if nm in head:
                p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('[written] %s  %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
