"""Round 45b: zoom on the LEFT shoulder / armpit.

The user reports that our model's armpit seam OPENS (a visible gap, worst on the
left) and that the upper arm looks "broken", while the reference -- which has the
same seam line -- stays closed.  This probe puts three things side by side at the
same tight camera box:

    ours  (arm_fit = 1)   ours (arm_fit = 0)   reference sleeve + torso

so we can tell whether the gap is something the arm re-fit OPENED or something the
source mesh always had.

usage: python probe_armpit.py [out.png]
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
REF_UNITS = [('out/patchdump/unit-91eb7be84016ceab.obj', 'LeftArm_Undergarment_Slim'),
             ('out/patchdump/unit-cae35ee15168145f.obj', 'Torso_Undergarment_Slim')]

PANEL = (480, 470)
STEP = 1
ARM = ['l_clavicle', 'l_shoulder', 'l_shoulder_twist', 'l_shoulderarmour',
       'l_elbow', 'l_hand']
PARENT = {'l_shoulder': 'l_clavicle', 'l_shoulder_twist': 'l_shoulder',
          'l_shoulderarmour': 'l_clavicle', 'l_elbow': 'l_shoulder',
          'l_hand': 'l_elbow'}


def run(arm_fit):
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_fit=arm_fit, verbose=False)
    return r


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else 'out/armpit.png')
    r1 = run(1.0)
    pts1, tris = np.asarray(r1['out']), r1['tris']
    pts0 = np.asarray(run(0.0)['out'])
    src = np.asarray(r1['src1'])          # source, rigidly fitted, STILL T-POSE
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: (h['world'][3], h['world'][7], h['world'][11])
            for h in rig if h['name']}
    edges = []
    for h in rig:
        p = h['parent']
        if 0 <= p < len(rig) and h['name'] and rig[p]['name']:
            edges.append((head[rig[p]['name']], head[h['name']]))
    ref = [(nm, ) + PV.read_obj(p) for p, nm in REF_UNITS]

    # the armpit: x about -0.28, z about +1.32.  In T-POSE the arm is horizontal,
    # so the same box still contains the shoulder -- that is the point.
    rows = [('front (X-Z)', 0, 2, 1, False,
             [-0.70, -1.0, 1.10, 0.10, 1.0, 1.70]),
            ('top (X-Y)  -- +Y = forward', 0, 1, 2, False,
             [-0.70, -0.26, -1.0, 0.10, 0.30, 1.0])]
    cols = [('SOURCE (T-pose, pre-bake)', src),
            ('OURS  arm_fit=0', pts0),
            ('OURS  arm_fit=1', pts1),
            ('REFERENCE', None)]

    pw, ph = PANEL
    W = 12 + len(cols) * (pw + 14)
    Hh = 62 + len(rows) * (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'round 45b: LEFT armpit -- is the open seam caused by the arm '
           're-fit, or was it always there?', fill=(232, 232, 244))
    d.text((12, 26), 'cyan = the HD2 shoulder chain, yellow dots = its joints '
           '(clavicle, shoulder, elbow, hand)', fill=(150, 160, 180))

    for ci, (clabel, pts) in enumerate(cols):
        cx = 12 + ci * (pw + 14)
        d.text((cx + 4, 44), clabel, fill=(205, 214, 228))
        for ri, (tag, ah, av, ad, flip, box) in enumerate(rows):
            cy = 62 + ri * (ph + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + pw, cy + ph), box, ah, av, ad, flip)
            if pts is None:
                for _, rp, rt in ref:
                    p.mesh(rp, rt, (1.0, 0.86, 0.96), STEP)
            else:
                p.mesh(pts, tris, (0.78, 0.97, 0.84), STEP)
            for a, b in edges:
                p.line(a, b, (92, 97, 112), 1)
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
