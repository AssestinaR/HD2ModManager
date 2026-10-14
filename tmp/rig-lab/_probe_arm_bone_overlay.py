# -*- coding: utf-8 -*-
"""READ-ONLY: side view of the arm with the arm BONES drawn on top.

The cross-section overlay shows our forearm mesh sitting to one side of the
elbow->wrist axis while the vanilla arm skin is centred on it.  This renders the
whole upper arm + forearm from the side and draws the bone chain as bright lines,
so it is obvious at a glance whether the skeleton runs through the middle of the
arm or along its edge.

usage:
    _probe_arm_bone_overlay.py <out.png> <patch_0> [--unit 0x...] [--side left]
    _probe_arm_bone_overlay.py <out.png> --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_unit as U
import _probe_wrist_roll_render as R
import _probe_wrist_twist_render as T

TILE_W, TILE_H = 900, 520
R.TILE_W, R.TILE_H = TILE_W, TILE_H      # render_tile draws into its own tile size
CHAIN = ['l_shoulder', 'l_elbow', 'l_hand']


def load(args):
    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        u = U.Unit.read(toc, gpu)
        label = os.path.basename(args.local)
    else:
        import hd2_patch as P
        entries, _ = P.scan_entries(args.patch)
        e = next(x for x in entries if x['type_id'] == U.UNIT_ID
                 and x['file_id'] == int(args.unit, 16))
        pl = P.entry_payload(e, args.patch)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        label = os.path.basename(args.patch)
    return u, label


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--side', default='left')
    ap.add_argument('--tmin', type=float, default=-0.60)
    ap.add_argument('--tmax', type=float, default=0.16)
    ap.add_argument('--radius', type=float, default=0.17)
    args = ap.parse_args()

    u, label = load(args)
    pos, w_hand, w_tw, w_el, E, W, tri, mat = T.assemble(u, args.side)

    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: T.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

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
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    keep = (rad < args.radius) & (t > args.tmin) & (t < args.tmax)
    tris = tri[keep[tri].all(axis=1)]
    sel = np.nonzero(keep)[0]
    box = (float((pos[sel] @ axis).min()), float((pos[sel] @ V_).min()),
           float((pos[sel] @ axis).max()), float((pos[sel] @ V_).max()))
    print('%s  verts %d  tris %d' % (label, len(sel), len(tris)))

    img = R.render_tile(pos, tris, np.zeros(len(pos)), axis, V_, N_, box)
    im = Image.fromarray(img.astype(np.uint8))
    dr = ImageDraw.Draw(im)

    scale = min((TILE_W - 16) / (box[2] - box[0]), (TILE_H - 16) / (box[3] - box[1]))
    ox = (TILE_W - (box[2] - box[0]) * scale) / 2
    oy = (TILE_H - (box[3] - box[1]) * scale) / 2

    def to_screen(p):
        px, py = float(p @ axis), float(p @ V_)
        return ((px - box[0]) * scale + ox, TILE_H - ((py - box[1]) * scale + oy))

    pts = [heads[slot_of[nm]] for nm in CHAIN if nm in slot_of]
    pts.append(W + axis * 0.11)          # a proxy for the hand tip
    for a, b in zip(pts[:-1], pts[1:]):
        dr.line([to_screen(a), to_screen(b)], fill=(255, 96, 96), width=3)
    for nm in CHAIN:
        if nm not in slot_of:
            continue
        x, y = to_screen(heads[slot_of[nm]])
        dr.ellipse([x - 5, y - 5, x + 5, y + 5], outline=(255, 210, 90), width=2)
        dr.text((x + 7, y - 6), nm, fill=(255, 210, 90))

    dr.text((8, 6), '%s   %s side   red = arm bone chain (shoulder -> elbow -> wrist -> hand)'
            % (label, args.side), fill=(236, 240, 248))
    dr.text((8, 22), 'if the red line runs along the EDGE of the arm instead of through '
            'its middle, the mesh is off the bone', fill=(176, 184, 196))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    im.save(args.out)
    print('wrote %s' % args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
