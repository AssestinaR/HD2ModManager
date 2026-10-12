"""Round 45: the HAND close-up -- is the palm sitting on HD2's finger bones?

Tight camera on the left hand.  HD2's finger bones are drawn thick and labelled,
so "our palm is 5 cm inboard of the finger bones" is directly visible.
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
REF_UNIT = 'out/patchdump/unit-91eb7be84016ceab.obj'

PANEL = (470, 470)
STEP = 1
OUT = os.path.abspath('out/hand-zoom.png')
HAND = ['l_hand', 'l_hand_twist',
        'l_index_finger1', 'l_index_finger2', 'l_index_finger3',
        'l_middle_finger1', 'l_middle_finger2', 'l_middle_finger3',
        'l_ring_finger1', 'l_ring_finger2', 'l_ring_finger3',
        'l_pinky_finger1', 'l_pinky_finger2', 'l_pinky_finger3',
        'l_thumb_finger1', 'l_thumb_finger2', 'l_thumb_finger3']
PARENT = {}
for f in ('index', 'middle', 'ring', 'pinky', 'thumb'):
    PARENT['l_%s_finger1' % f] = 'l_hand'
    PARENT['l_%s_finger2' % f] = 'l_%s_finger1' % f
    PARENT['l_%s_finger3' % f] = 'l_%s_finger2' % f
PARENT['l_hand'] = 'l_elbow'


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
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
    rows = [('front (X-Z)', 0, 2, 1, False, [-0.80, -1, 1.04, -0.48, 1, 1.32]),
            ('top (X-Y)', 0, 1, 2, False, [-0.80, 0.02, -1, -0.48, 0.34, 1]),
            ('side (Y-Z)', 1, 2, 0, True, [-0.80, 0.02, 1.04, -0.48, 0.34, 1.32])]
    cols = [('OURS', 'ours'), ('REFERENCE sleeve', 'ref'), ('overlay', 'both')]

    W = 12 + len(cols) * (pw + 14)
    Hh = 62 + len(rows) * (ph + 26) + 18
    img = Image.new('RGB', (W, Hh), (26, 28, 34))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'round 45: LEFT HAND -- HD2 finger bones thick+labelled, '
           'joints yellow', fill=(232, 232, 244))
    d.text((12, 26), 'the palm should sit on the finger bones (that is what the '
           'first-person weapon grip needs)', fill=(150, 160, 180))

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
            for nm in HAND:
                par = PARENT.get(nm)
                if nm in head and par in head:
                    p.line(head[par], head[nm], (90, 220, 240), 3)
            for nm in HAND:
                if nm in head:
                    p.dot(head[nm], (255, 210, 90), 4)
            for nm in ('l_hand', 'l_index_finger3', 'l_thumb_finger3'):
                if nm in head:
                    p.label(head[nm], nm.replace('l_', ''), (255, 235, 120), dy=-14)
    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, W, Hh))


if __name__ == '__main__':
    main()
