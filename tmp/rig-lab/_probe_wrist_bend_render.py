# -*- coding: utf-8 -*-
"""Visual check: how far does the forearm skin get dragged by a WRIST BEND?

Why a bend and not a roll
-------------------------
Round 68 rejected the twist-bone split with a synthetic 180 deg ROLL of l_hand
while holding l_hand_twist still.  Two things were wrong with that:

  * the real player animation set shows the game *does* drive l_hand_twist -
    it tracks l_hand's twist with correlation 0.95..1.00 at ~0.78x the angle
    (see _probe_wrist_split.py over 300 clips), so a roll that leaves the twist
    bone frozen is not a state the game ever produces;
  * l_hand and l_hand_twist share their head (the wrist), so rotating both by
    the *same* matrix - which is what the old `--twist` switch did - gives a
    displacement that depends only on their weight SUM, i.e. it cannot tell the
    two splits apart.  That is why "all three twist_frac values came out
    identical": the negative control was vacuous.

The wrist motion that actually hurts is SWING (flexion / deviation), because
l_hand_twist is a pure 1-DOF twist bone: it carries no swing at all.  Under a
pure bend of psi about a transverse axis through the wrist,

    l_hand-weighted vertices      move by  w_hand * (R_bend - I)(p - W)
    l_hand_twist-weighted vertices  do not move

so the dragged weight is exactly w_hand, and the lever is |p - W| - up to 11 cm
at mid-forearm.  That is the rubber band.

Tiles are coloured by the displacement magnitude in mm on a shared scale
(blue = rigid, yellow = fully dragged).

usage:
    _probe_wrist_bend_render.py <out.png> <patch_0> [...] \
        [--label NAME] [--angles 0,30,60] [--side left]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_wrist_roll_render as R

TILE_W, TILE_H = R.TILE_W, R.TILE_H


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patches', nargs='+')
    ap.add_argument('--label', action='append', default=[])
    ap.add_argument('--angles', default='0,30,60')
    ap.add_argument('--side', default='left')
    args = ap.parse_args()

    angles = [float(x) for x in args.angles.split(',')]
    labels = args.label + [''] * (len(args.patches) - len(args.label))

    loaded = []
    peak = 1e-9
    for patch in args.patches:
        pos, tris, wh, E, W, axis, keep = R.load(patch, args.side, False)
        z = np.array([0.0, 0.0, 1.0])
        ra = np.cross(axis, z)
        if np.linalg.norm(ra) < 1e-8:
            ra = np.cross(axis, np.array([0.0, 1.0, 0.0]))
        ra = ra / np.linalg.norm(ra)
        disp = []
        for deg in angles:
            Rb = R.rodrigues(ra, np.deg2rad(deg))
            d = wh[:, None] * (((pos - W) @ Rb.T) - (pos - W))
            disp.append(d)
            peak = max(peak, float(np.linalg.norm(d, axis=1).max()))
        N_ = np.cross(axis, z)
        N_ /= np.linalg.norm(N_)
        V_ = np.cross(N_, axis)
        V_ /= np.linalg.norm(V_)
        loaded.append((pos, tris, disp, axis, V_, N_, keep))

    rows = []
    for (label, (pos, tris, disp, axis, V_, N_, keep)) in zip(labels, loaded):
        sel = np.nonzero(keep)[0]
        box = (float((pos[sel] @ axis).min()), float((pos[sel] @ V_).min()),
               float((pos[sel] @ axis).max()), float((pos[sel] @ V_).max()))
        tiles = []
        for d in disp:
            deformed = pos + d
            mag = np.linalg.norm(d, axis=1)
            tiles.append(R.render_tile(deformed, tris, np.clip(mag / peak, 0.0, 1.0),
                                       axis, V_, N_, box))
        rows.append((label, tiles))

    ncol = len(angles)
    pad, head = 8, 20
    width = ncol * TILE_W + (ncol + 1) * pad
    height = len(rows) * (TILE_H + head) + (len(rows) + 1) * pad
    sheet = Image.new('RGB', (width, height), (10, 11, 14))
    draw = ImageDraw.Draw(sheet)
    for r, (label, tiles) in enumerate(rows):
        y = pad + r * (TILE_H + head + pad)
        draw.text((pad, y + 4), label, fill=(236, 240, 248))
        for c, tile in enumerate(tiles):
            x = pad + c * (TILE_W + pad)
            sheet.paste(Image.fromarray(tile.astype(np.uint8)), (x, y + head))
            draw.text((x + 6, y + head + 5), 'wrist bend %d deg' % angles[c],
                      fill=(255, 214, 120))
    draw.text((pad, height - pad - 12),
              'colour = skin displacement (blue rigid -> yellow dragged), shared scale, max %.0f mm'
              % (peak * 1000.0), fill=(150, 160, 175))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    sheet.save(args.out)
    print('wrote %s  (%dx%d)  peak %.1f mm' % (args.out, width, height, peak * 1000.0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
