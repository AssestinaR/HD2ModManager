"""Round 45: zoom on the LEFT arm -- front and top, ours vs the reference.

Same conventions as probe_arm_view.py, but the camera box is tight on the left
arm so the elbow/hand can actually be judged, and only the reference's LEFT arm
unit is loaded (no torso noise).
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
REF_UNIT = 'out/patchdump/unit-91eb7be84016ceab.obj'      # LeftArm_Undergarment_Slim

PANEL = (470, 500)
STEP = 1
ARM = ['l_clavicle', 'l_shoulder', 'l_shoulder_twist', 'l_shoulderarmour',
       'l_elbow', 'l_hand', 'l_hand_twist']


def main():
    arm_fit = float(sys.argv[1]) if len(sys.argv) > 1 else 1.0
    out = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else 'out/arm-zoom.png')
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_fit=arm_fit, verbose=False)
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    edges = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            edges.append((head[rig[p]['name']], head[h['name']]))

    our_pts = [tuple(p) for p in np.asarray(r['out'])]
    tris = r['tris']
    ref_pts, ref_tris = PV.read_obj(REF_UNIT)

    pw, ph = PANEL
    rows = [('front (X-Z)  -- left arm', 0, 2, 1, False,
             [-0.90, -1, 1.06, -0.06, 1, 1.64]),
            ('top (X-Y)  -- left arm, +Y = forward', 0, 1, 2, False,
             [-0.90, -0.28, -1, -0.06, 0.36, 1])]
    cols = [('OURS (green)', 'ours'), ('REFERENCE sleeve (pink)', 'ref'),
            ('overlay', 'both')]

    W = 12 + len(cols) * (pw + 14)
    Hh = 62 + len(rows) * (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'round 45: LEFT arm zoom -- HD2 native rig in grey, the '
           'arm chain in cyan, joints as yellow dots', fill=(232, 232, 244))
    d.text((12, 26), 'bone order: clavicle -> shoulder -> [twist/armour] -> elbow '
           '-> hand -> hand_twist', fill=(150, 160, 180))

    for ci, (clabel, kind) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        d.text((cx + 4, 44), clabel, fill=(205, 214, 228))
        for ri, (tag, ah, av, ad, flip, box) in enumerate(rows):
            cy = 62 + ri * (ph + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, flip)
            if kind in ('ref', 'both'):
                p.mesh(ref_pts, ref_tris, (1.0, 0.86, 0.96), STEP)
            if kind in ('ours', 'both'):
                p.mesh(our_pts, tris, (0.78, 0.97, 0.84), STEP)
            for a, b in edges:
                p.line(a, b, (92, 97, 112), 1)
            for nm in ARM:
                if nm in head:
                    par = {'l_shoulder': 'l_clavicle', 'l_shoulder_twist': 'l_shoulder',
                           'l_shoulderarmour': 'l_clavicle', 'l_elbow': 'l_shoulder',
                           'l_hand': 'l_elbow', 'l_hand_twist': 'l_hand'}.get(nm)
                    if par and par in head:
                        p.line(head[par], head[nm], (90, 220, 240), 3)
            for nm in ARM:
                if nm in head:
                    p.dot(head[nm], (255, 210, 90), 4)
    img.save(out)
    print('[written] %s  %dx%d  (arm_fit %.2f)' % (out, W, Hh, arm_fit))


if __name__ == '__main__':
    main()
