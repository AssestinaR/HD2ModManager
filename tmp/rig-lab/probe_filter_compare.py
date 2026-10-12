"""OLD edge filter vs NEW radius filter, on Zoey's retargeted mesh.

Two columns x three rows, zoomed so the difference is unmistakable:
  row 1  head + hair        (the ponytail)
  row 2  feet               (the platform-shoe heel)
  row 3  full body

usage: probe_filter_compare.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/filter-compare.png'


def old_edge_filter(pts, tris, factor=8.0, floor=0.06):
    P = np.asarray(pts, dtype=np.float64)
    lens = [max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                np.linalg.norm(P[c] - P[a])) for a, b, c, _m in tris]
    lim = max(float(np.median(lens)) * factor, floor)
    return [t for t in tris
            if max(np.linalg.norm(P[t[0]] - P[t[1]]),
                   np.linalg.norm(P[t[1]] - P[t[2]]),
                   np.linalg.norm(P[t[2]] - P[t[0]])) <= lim]


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060), verbose=False)
    pts = [tuple(x) for x in r['out']]
    old = old_edge_filter(r['src_pts'], r['tris'])
    new, (ndrop, cut) = H.drop_outlier_tris(r['src_pts'], r['tris'])
    print('OLD keeps %d/%d tris | NEW keeps %d/%d (hides %d, cut %.3f m)'
          % (len(old), len(r['tris']), len(new), len(r['tris']), ndrop, cut))

    P = np.asarray(pts)
    zmin, zmax = P[:, 2].min(), P[:, 2].max()
    # (label, z0, z1, horizontal axis, vertical axis, depth axis)
    # the feet row uses the SIDE projection, because the shoe heel is at the back
    BANDS = [('head + hair (front)', zmin + 0.78 * (zmax - zmin), zmax + 0.02,
              0, 2, 1),
             ('feet + shoe heel (side)', zmin - 0.02, zmin + 0.16 * (zmax - zmin),
              1, 2, 0),
             ('full body (front)', zmin - 0.02, zmax + 0.02, 0, 2, 1)]
    COLS = [('OLD edge filter (floor 0.06 m)', old, (255, 150, 150)),
            ('NEW radius filter (0.85 x height)', new, (150, 230, 160))]

    PW, PH, GAP, TOP, PAD = 320, 300, 16, 74, 12
    W = PAD + 2 * (PW + 10) + 4
    Hh = TOP + 3 * (PH + GAP) + PAD
    img = Image.new('RGB', (W, Hh), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((PAD, 8), 'Zoey retargeted mesh -- OLD edge filter vs NEW radius filter',
           fill=(232, 232, 242))
    d.text((PAD, 26), 'left: what the figure showed before (the edge rule deleted '
           'the ponytail + shoe heel)', fill=(255, 190, 190))
    d.text((PAD, 44), 'right: after (nothing hidden -- the geometry is back).  '
           'Same mesh, same pose; only the display filter changed.', fill=(180, 240, 190))

    for ri, (band, z0, z1, ah, av, ad) in enumerate(BANDS):
        cy = TOP + ri * (PH + GAP)
        for ci, (lab, mt, col) in enumerate(COLS):
            cx = PAD + ci * (PW + 10)
            d.text((cx + 2, cy - 16), '%s  |  %s' % (lab, band), fill=(205, 215, 232))
            d.rectangle([cx, cy, cx + PW - 1, cy + PH - 1], fill=(22, 24, 30),
                        outline=(90, 90, 100))
            sub = [p for p in pts if z0 <= p[2] <= z1]
            if not sub:
                continue
            hs = [p[ah] for p in sub]
            mn, mx = min(hs), max(hs)
            span = max(mx - mn, z1 - z0, 0.001)
            sc = min(PW, PH) * 0.9 / span
            cm, czm = (mn + mx) / 2, (z0 + z1) / 2

            def T(p):
                return (cx + PW / 2 - (p[ah] - cm) * sc,
                        cy + PH / 2 - (p[av] - czm) * sc)

            zb = [[None] * PW for _ in range(PH)]
            faces = sorted(((pts[a][ad] + pts[b][ad] + pts[c][ad], a, b, c)
                            for a, b, c, _m in mt), key=lambda f: f[0])
            for _dep, a, b, c in faces:
                pa, pb, pc = T(pts[a]), T(pts[b]), T(pts[c])
                xs2 = [pa[0], pb[0], pc[0]]
                ys2 = [pa[1], pb[1], pc[1]]
                x0 = max(cx, int(min(xs2))); x1 = min(cx + PW - 1, int(max(xs2)) + 1)
                y0 = max(cy, int(min(ys2))); y1 = min(cy + PH - 1, int(max(ys2)) + 1)
                if x0 > x1 or y0 > y1:
                    continue
                den = ((pb[1] - pc[1]) * (pa[0] - pc[0])
                       + (pc[0] - pb[0]) * (pa[1] - pc[1]))
                if abs(den) < 1e-9:
                    continue
                for py in range(y0, y1 + 1):
                    for px in range(x0, x1 + 1):
                        qx, qy = px + 0.5, py + 0.5
                        w1 = ((pb[1] - pc[1]) * (qx - pc[0])
                              + (pc[0] - pb[0]) * (qy - pc[1])) / den
                        w2 = ((pc[1] - pa[1]) * (qx - pc[0])
                              + (pa[0] - pc[0]) * (qy - pc[1])) / den
                        w3 = 1.0 - w1 - w2
                        if w1 < 0 or w2 < 0 or w3 < 0:
                            continue
                        dep = -(w1 * pts[a][ad] + w2 * pts[b][ad] + w3 * pts[c][ad])
                        if zb[py - cy][px - cx] is None or dep < zb[py - cy][px - cx]:
                            zb[py - cy][px - cx] = dep
                            img.putpixel((px, py), col)

    img.save(OUT)
    print('[written] %s  %dx%d' % (OUT, W, Hh))


if __name__ == '__main__':
    main()
