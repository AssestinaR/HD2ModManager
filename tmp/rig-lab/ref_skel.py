"""Draw the reference patch's OWN skeleton on top of its mesh.

The point: the reference is a shipped, in-game-good sample, so its skeleton is
authoritative.  If its bones visibly do NOT sit inside the mesh, then the author
deliberately let the mesh deviate from HD2's proportions and fixed it with
weights -- which is the opposite of snapping every bone head onto the source.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
DUMP = 'out/patchdump'
UNITS = sys.argv[1].split(',') if len(sys.argv) > 1 else [
    'cae35ee15168145f', '17da843a4908511e', '91eb7be84016ceab', '7336c6b662522f0e']
OUT = sys.argv[2] if len(sys.argv) > 2 else 'out/ref-skeleton.png'
PW, PH = 380, 620

# every bone the reader can name, not just the 20 we map onto
PARENT = dict(PV.BONE_PARENT)
for extra, par in (('l_ball', 'l_foot'), ('r_ball', 'r_foot'),
                   ('l_toe', 'l_ball'), ('r_toe', 'r_ball'),
                   ('l_shoulder_twist', 'l_shoulder'), ('r_shoulder_twist', 'r_shoulder'),
                   ('l_hand_twist', 'l_hand'), ('r_hand_twist', 'r_hand'),
                   ('head_tip', 'head'), ('l_shoulderarmour', 'l_clavicle'),
                   ('r_shoulderarmour', 'r_clavicle')):
    PARENT.setdefault(extra, par)


def main():
    meta = json.load(open(os.path.join(DUMP, 'units.json'), encoding='utf-8'))
    names = PV.read_bone_names(BH)
    pts, tris, base, used = [], [], 0, []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in UNITS:
            continue
        p, t = PV.read_obj(os.path.join(DUMP, 'unit-%s.obj' % fid))
        if not t:
            continue
        used.append((u['tag'], fid, len(p), len(t)))
        pts.extend(p)
        tris.extend([(x[0] + base, x[1] + base, x[2] + base, 0) for x in t])
        base += len(p)
    for tag, fid, nv, nt in used:
        print('%-40s %s v=%6d t=%6d' % (tag, fid, nv, nt))

    heads = {k: v for k, v in PV.patch_rig_heads(meta, names).items()
             if k in PARENT and k in UNITS_BONES}
    print('bones drawn: %d -> %s' % (len(heads), ', '.join(sorted(heads))))

    A = np.asarray(pts)
    gb = (A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
          A[:, 0].max(), A[:, 1].max(), A[:, 2].max())
    gsc = min(PW, PH) * 0.86 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    views = [('front  (X-Z)', 0, 2, 1, False), ('side  (Y-Z, +Y = forward)', 1, 2, 0, True)]
    W, Hh = 12 + 2 * (PW + 14), 74 + 2 * (PH + 26)
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'reference patch: mesh | mesh + its OWN skeleton', fill=(232, 232, 242))
    d.text((12, 26), 'cyan = bones, magenta dots = joints.  Where a bone is outside '
           'the silhouette the mesh has been deliberately pulled off HD2\'s proportions.',
           fill=(150, 160, 180))

    for ri, (tag, ah, av, ad, flip) in enumerate(views):
        cy = 74 + ri * (PH + 26)
        for ci, with_skel in enumerate((False, True)):
            cx = 12 + ci * (PW + 14)
            d.text((cx + 4, cy - 13), tag + ('  + skeleton' if with_skel else '  (mesh only)'),
                   fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), gb, ah, av, ad, flip, gsc)
            p.mesh([tuple(x) for x in pts], tris, (0.92, 0.95, 1.0), 1)
            if with_skel:
                for a, b in lines(heads):
                    p.line(a, b, (90, 240, 230), 2)
                for v in heads.values():
                    p.dot(v, (255, 90, 210), 3)

    img.save(OUT)
    print('[written] %s' % OUT)


UNITS_BONES = set(PARENT)


def lines(heads):
    out = []
    for nm, h in heads.items():
        p = PARENT.get(nm)
        if p and p in heads:
            out.append((heads[p], h))
    return out


if __name__ == '__main__':
    main()
