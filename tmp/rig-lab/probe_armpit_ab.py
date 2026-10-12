"""Round 45b: the armpit seam, OLD arm re-fit vs NEW, next to the reference.

OLD = the round-45 build the user inspected (arm_fit 1, arm_up 0, no taper).
NEW = the same but with the position taper and the vertical term.

If the open seam is what the re-fit's chest displacement opened, the OLD column
shows it and the NEW column does not.

usage: python probe_armpit_ab.py [out.png]
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
REF_UNITS = [('out/patchdump/unit-91eb7be84016ceab.obj', 'LeftArm'),
             ('out/patchdump/unit-cae35ee15168145f.obj', 'Torso')]

PANEL = (430, 430)
ARM = ['l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand']
PARENT = {'l_shoulder': 'l_clavicle', 'l_elbow': 'l_shoulder', 'l_hand': 'l_elbow'}


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/armpit-ab.png')
    old = run(arm_fit=1.0, arm_up=0.0, arm_perp=0.0, arm_taper=None)
    new = run(arm_fit=1.0)
    off = run(arm_fit=0.0)
    tris = old['tris']

    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    edges = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            edges.append((head[rig[p]['name']], head[h['name']]))
    ref = [(nm, ) + PV.read_obj(p) for p, nm in REF_UNITS]

    # centred on the left armpit (-0.20, 0.02, 1.34)
    rows = [('front (X-Z)', 0, 2, 1, [-0.56, -1.0, 1.10, 0.10, 1.0, 1.62]),
            ('top (X-Y)  +Y = forward', 0, 1, 2, [-0.56, -0.28, -1.0, 0.10, 0.34, 1.0]),
            ('side (Y-Z)  +Y = forward', 1, 2, 0, [-1.0, -0.28, 1.10, 1.0, 0.34, 1.62])]
    cols = [('OURS  arm_fit OFF', np.asarray(off['out'])),
            ('OURS  round-45 (OLD)', np.asarray(old['out'])),
            ('OURS  round-45b (NEW)', np.asarray(new['out'])),
            ('REFERENCE', None)]

    pw, ph = PANEL
    W = 12 + len(cols) * (pw + 14)
    Hh = 62 + len(rows) * (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'round 45b: LEFT armpit -- OFF / round-45 / round-45b / '
           'reference', fill=(232, 232, 244))
    d.text((12, 26), 'cyan = the HD2 shoulder chain, yellow dots = its joints',
           fill=(150, 160, 180))

    for ci, (clabel, pts) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        d.text((cx + 4, 44), clabel, fill=(205, 214, 228))
        for ri, (tag, ah, av, ad, box) in enumerate(rows):
            cy = 62 + ri * (ph + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, False)
            if pts is None:
                for _, rp, rt in ref:
                    p.mesh(rp, rt, (1.0, 0.86, 0.96), 1)
            else:
                p.mesh([tuple(q) for q in pts], tris, (0.78, 0.97, 0.84), 1)
            for x, y in edges:
                p.line(x, y, (92, 97, 112), 1)
            for nm in ARM:
                par = PARENT.get(nm)
                if nm in head and par in head:
                    p.line(head[par], head[nm], (90, 220, 240), 3)
            for nm in ARM:
                if nm in head:
                    p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('[written] %s  %dx%d' % (out, W, Hh))


if __name__ == '__main__':
    main()
