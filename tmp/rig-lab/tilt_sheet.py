"""Render the same model at several leg-tilt angles next to the reference patch.

One shared scale for every panel, so the stances can be compared by eye rather
than by axis guides -- the guides hid the real deformation last time.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'
PATCH_UNITS = ('af431352d9b95e83',   # head + hair (the piece that was missing)
               'cae35ee15168145f', '17da843a4908511e',
               '91eb7be84016ceab', '7336c6b662522f0e')
TILTS = [float(x) for x in (sys.argv[1].split(',') if len(sys.argv) > 1
                            else ['0', '4', '6.5'])]
OUT = sys.argv[2] if len(sys.argv) > 2 else 'out/tilt-sheet.png'

PW, PH = 340, 470


def main():
    ref_pts, ref_tris, base = [], [], 0
    for fid in PATCH_UNITS:
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        ref_pts.extend(p)
        ref_tris.extend([(x[0] + base, x[1] + base, x[2] + base, 0) for x in t])
        base += len(p)

    runs = []
    for tl in TILTS:
        r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                       fallback='region', leg_tilt=tl, verbose=False)
        runs.append((tl, r))
        print('tilt %.1f -> bind z[%.4f,%.4f]' % (tl, np.asarray(r['out'])[:, 2].min(),
                                                  np.asarray(r['out'])[:, 2].max()))

    allp = list(ref_pts)
    for _tl, r in runs:
        allp.extend([tuple(p) for p in np.asarray(r['out'])])
    gb = list(PV.bbox(allp)) if hasattr(PV, 'bbox') else None
    if gb is None:
        A = np.asarray(allp)
        gb = [A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
              A[:, 0].max(), A[:, 1].max(), A[:, 2].max()]
    gsc = min(PW, PH) * 0.88 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    labels = ['tilt %.1f deg' % tl for tl in TILTS] + ['reference patch']
    W, Hh = 12 + len(labels) * (PW + 14), 74 + 2 * (PH + 26)
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'leg tilt about the ankle -- bind pose (what we would publish) '
           'vs the reference patch', fill=(232, 232, 242))
    d.text((12, 26), 'torso is carried forward with the hip and stays upright; '
           'the foot stays planted; one shared scale', fill=(150, 160, 180))

    views = [('front  (X-Z)', 0, 2, 1, False), ('side  (Y-Z, +Y = forward)', 1, 2, 0, True)]
    cols = [(lab, np.asarray(r['out']), r['tris']) for lab, (_t, r) in zip(labels, runs)]
    cols.append((labels[-1], np.asarray(ref_pts), ref_tris))

    for ci, (lab, pts, tris) in enumerate(cols):
        cx = 12 + ci * (PW + 14)
        d.text((cx + 4, 50), lab, fill=(210, 220, 235))
        for ri, (tag, ah, av, ad, flip) in enumerate(views):
            cy = 74 + ri * (PH + 26)
            d.text((cx + 4, cy - 13), tag, fill=(160, 170, 190))
            p = PV.Panel(img, d, (cx, cy, cx + PW, cy + PH), tuple(gb), ah, av, ad,
                         flip, gsc)
            p.mesh([tuple(x) for x in pts], tris, (0.96, 1.0, 0.97), 1)
            if tag.startswith('side'):
                A = np.asarray(pts)
                zmin = A[:, 2].min()
                m = A[(A[:, 2] < zmin + 0.08) & (A[:, 0] < -0.05)]
                if len(m):
                    ylo, yhi = float(m[:, 1].min()), float(m[:, 1].max())
                    d.text((cx + 4, cy + PH - 14),
                           'foot [%+.3f,%+.3f]  len %.3f' % (ylo, yhi, yhi - ylo),
                           fill=(205, 215, 230))
                d.text((cx + 4, cy + PH - 28),
                       'total height %.3f m  (top %.3f)' % (A[:, 2].max() - zmin,
                                                            A[:, 2].max()),
                       fill=(205, 215, 230))
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
