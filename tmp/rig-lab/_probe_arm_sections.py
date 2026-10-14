# -*- coding: utf-8 -*-
"""READ-ONLY: plot the arm cross-sections with the bone axis marked.  (round 75)

The bones of our unit and the vanilla arm skin are byte-identical
(l_elbow head [-0.4723 -0.0539 1.3236], l_hand head [-0.6566 0.1401 1.2189]),
and after round 74 the twist weight profile matches vanilla too.  So the only
thing left that can make the wrist swing instead of spin is where the MESH sits
relative to the axis.

This draws, for a few stations along the forearm, the ring of vertices in the
plane perpendicular to the elbow->wrist axis.  The axis is the origin, marked
with a crosshair; the circle is a 40 mm radius reference.  If the ring is
centred on the crosshair the mesh spins in place; if it is pushed to one side
the mesh orbits when the wrist twists.

usage:
    _probe_arm_sections.py <out.png> --ours <patch_0> --unit 0x... \
        --vanilla <base_without_ext> [--side left]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL

TILE = 300
PAD = 10
HEAD = 18
STATIONS = [-0.20, -0.14, -0.08, -0.02, 0.04]
HALF = 0.085          # plot window, metres
WIN = 0.025           # slice half-thickness, metres


def sections(d, side):
    pos, E, W = d['pos'], d['E'], d['W']
    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(axis @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(axis, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, axis); V_ /= np.linalg.norm(V_)
    rel = pos - W
    t = rel @ axis
    perp = rel - np.outer(t, axis)
    rad = np.linalg.norm(perp, axis=1)
    out = []
    for a in STATIONS:
        m = (np.abs(t - a) < WIN) & (rad < 0.13)
        out.append((a, perp[m] @ N_, perp[m] @ V_, t[m]))
    return out, L


def tile(a, xs, ys, ts, title):
    img = Image.new('RGB', (TILE, TILE), (16, 18, 22))
    dr = ImageDraw.Draw(img)
    cx = cy = TILE // 2
    s = (TILE - 2 * 12) / (2 * HALF)
    # 40 mm reference circle
    r40 = 0.040 * s
    dr.ellipse([cx - r40, cy - r40, cx + r40, cy + r40], outline=(70, 78, 92))
    dr.line([cx - 8, cy, cx + 8, cy], fill=(240, 90, 90), width=2)
    dr.line([cx, cy - 8, cx, cy + 8], fill=(240, 90, 90), width=2)
    # colour by axial position: blue = proximal edge of the window, red = distal
    for x, y, tv in zip(xs, ys, ts):
        u = float(np.clip((tv - (a - WIN)) / (2 * WIN), 0.0, 1.0))
        c = (int(60 + 195 * u), int(150 - 90 * u), int(255 - 195 * u))
        px, py = cx + x * s, cy - y * s
        dr.ellipse([px - 2, py - 2, px + 2, py + 2], fill=c)
    if len(xs):
        dr.text((6, 4), '%s  n=%d  centroid %.1f mm'
                % (title, len(xs), float(np.hypot(xs.mean(), ys.mean())) * 1000),
                fill=(232, 236, 244))
    else:
        dr.text((6, 4), '%s  (empty)' % title, fill=(232, 236, 244))
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--ours', default=None)
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--vanilla', default=None)
    ap.add_argument('--side', default='left')
    ap.add_argument('--stations', default=None, help='comma list, metres')
    ap.add_argument('--win', type=float, default=None, help='slice half-thickness, m')
    ap.add_argument('--half', type=float, default=None, help='plot half-window, m')
    args = ap.parse_args()

    global STATIONS, WIN, HALF
    if args.stations:
        STATIONS = [float(x) for x in args.stations.split(',')]
    if args.win is not None:
        WIN = args.win
    if args.half is not None:
        HALF = args.half

    rows = []
    if args.ours:
        d = TL.load_patch(args.ours, int(args.unit, 16), args.side)
        rows.append(('OURS (shipped)', d))
    if args.vanilla:
        d = TL.load_local(args.vanilla, args.side)
        rows.append(('VANILLA arm skin', d))

    ncol = len(STATIONS)
    w = ncol * TILE + (ncol + 1) * PAD
    h = len(rows) * (TILE + HEAD) + (len(rows) + 1) * PAD
    sheet = Image.new('RGB', (w, h), (10, 11, 14))
    dr = ImageDraw.Draw(sheet)
    for r, (label, d) in enumerate(rows):
        secs, L = sections(d, args.side)
        y = PAD + r * (TILE + HEAD + PAD)
        for c, (a, xs, ys, ts) in enumerate(secs):
            x = PAD + c * (TILE + PAD)
            sheet.paste(tile(a, xs, ys, ts, 't=%+.0f mm' % (a * 1000)), (x, y + HEAD))
        dr.text((PAD, y + 3), '%s   (forearm L=%.1f mm, cross-section, bone axis at the red cross;'
                ' colour = axial position within the +/-25 mm slab)'
                % (label, L * 1000), fill=(236, 240, 248))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    sheet.save(args.out)
    print('wrote %s (%dx%d)' % (args.out, w, h))
    return 0


if __name__ == '__main__':
    sys.exit(main())
