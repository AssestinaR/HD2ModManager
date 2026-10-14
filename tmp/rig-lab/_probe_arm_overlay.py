# -*- coding: utf-8 -*-
"""READ-ONLY: overlay our forearm cross-section and vanilla's in the SAME frame.

Both units share byte-identical arm bones, so the elbow->wrist axis is the same
line in both.  Plotting both cross-sections in the plane perpendicular to that
axis therefore shows, directly, whether our mesh sits off the bone while
vanilla's sits on it.

usage:
    _probe_arm_overlay.py <out.png> --ours <patch_0> --unit 0x... \
        --vanilla <base_without_ext> [--side left] [--station -0.15] [--win 0.11]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL

TILE = 560


def cross_section(d, side, station, win, half):
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
    m = (np.abs(t - station) < win) & (rad < half)
    return perp[m] @ N_, perp[m] @ V_, L


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--ours', required=True)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--vanilla', required=True)
    ap.add_argument('--side', default='left')
    ap.add_argument('--station', type=float, default=-0.15)
    ap.add_argument('--win', type=float, default=0.11)
    ap.add_argument('--half', type=float, default=0.13)
    args = ap.parse_args()

    A = TL.load_patch(args.ours, int(args.unit, 16), args.side)
    B = TL.load_local(args.vanilla, args.side)

    img = Image.new('RGB', (TILE, TILE), (16, 18, 22))
    dr = ImageDraw.Draw(img)
    cx = cy = TILE // 2
    s = (TILE - 2 * 14) / (2 * args.half)

    for r_mm in (20, 40, 60, 80, 100):
        r = r_mm / 1000.0 * s
        dr.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(58, 66, 80))
        dr.text((cx + r + 3, cy - 6), '%d mm' % r_mm, fill=(92, 100, 114))
    dr.line([cx - 12, cy, cx + 12, cy], fill=(240, 90, 90), width=2)
    dr.line([cx, cy - 12, cx, cy + 12], fill=(240, 90, 90), width=2)

    info = []
    for label, d, col in (('OURS', A, (255, 170, 70)), ('VANILLA', B, (96, 168, 255))):
        x, y, L = cross_section(d, args.side, args.station, args.win, args.half)
        for xi, yi in zip(x, y):
            px, py = cx + xi * s, cy - yi * s
            dr.ellipse([px - 3, py - 3, px + 3, py + 3], fill=col)
        c = float(np.hypot(x.mean(), y.mean())) * 1000 if len(x) else 0.0
        info.append('%s: n=%d  centroid %.1f mm off the bone axis' % (label, len(x), c))

    dr.text((10, 8), 'forearm cross-section at t = %+.0f mm   (t=0 is the wrist bone head)'
            % (args.station * 1000), fill=(236, 240, 248))
    dr.text((10, 25), 'red cross = BONE axis    orange = OURS    blue = VANILLA arm skin',
            fill=(182, 190, 202))
    dr.text((10, 42), '  |  '.join(info), fill=(204, 212, 224))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    img.save(args.out)
    print('wrote %s' % args.out)
    for line in info:
        print('  ' + line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
