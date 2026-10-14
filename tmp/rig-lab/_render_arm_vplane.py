# -*- coding: utf-8 -*-
"""READ-ONLY: render the arm in its OWN bend plane, so the V is fully visible.

_probe_arm_bone_overlay.py can look along N_ or V_ of the forearm, but the arm
bends in the plane spanned by (shoulder->elbow) and (elbow->wrist); neither of
those views shows the V face on.  This renders along the bend-plane normal with
the bone chain drawn in red, and frames the picture from the BONES (not from the
mesh) so every package is drawn at exactly the same scale and offset and the
pictures can be compared side by side.

usage:
    _render_arm_vplane.py <out.png> <patch_0> [--unit 0x...] [--side left]
    _render_arm_vplane.py <out.png> --local <base_without_ext> [...]
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

TILE_W, TILE_H = 1040, 640
R.TILE_W, R.TILE_H = TILE_W, TILE_H


def load(args):
    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        u = U.Unit.read(toc, gpu)
        return u, args.label or os.path.basename(args.local)
    import hd2_patch as P
    entries, _ = P.scan_entries(args.patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID
             and x['file_id'] == int(args.unit, 16))
    pl = P.entry_payload(e, args.patch)
    return U.Unit.read(pl['toc'], pl['gpu']), args.label or os.path.basename(args.patch)


def seg_dist(p, a, b):
    ab = b - a
    t = np.clip(((p - a) @ ab) / float(ab @ ab), 0.0, 1.0)
    return np.linalg.norm(p - (a + t[:, None] * ab), axis=1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--side', default='left')
    ap.add_argument('--radius', type=float, default=0.16,
                    help='keep mesh within this distance of the arm bone chain')
    ap.add_argument('--frame', type=float, default=0.20,
                    help='picture half-margin around the bone chain, in metres')
    ap.add_argument('--center-on', default='chain', choices=('chain', 'elbow', 'wrist'),
                    help='chain: frame the whole bone chain.  elbow/wrist: square '
                         'window of half-size --half centred on that joint (same '
                         'centre for every package, so the crops compare)')
    ap.add_argument('--half', type=float, default=0.16,
                    help='half-size of the centred window, in metres')
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    u, label = load(args)
    pos, w_hand, w_tw, w_el, E, W, tri, mat = T.assemble(u, args.side)

    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: T.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    pre = 'l' if args.side == 'left' else 'r'
    S = heads[slot_of[pre + '_shoulder']]

    # bend plane
    vu, vf = E - S, W - E
    nrm = np.cross(vu, vf)
    nrm /= np.linalg.norm(nrm)
    SX = vu / np.linalg.norm(vu) + vf / np.linalg.norm(vf)
    SX /= np.linalg.norm(SX)
    SY = np.cross(nrm, SX)
    SY /= np.linalg.norm(SY)
    SD = np.cross(SX, SY)

    d = np.minimum(seg_dist(pos, S, E), seg_dist(pos, E, W))
    keep = d < args.radius
    tris = tri[keep[tri].all(axis=1)]
    print('%s  verts %d  tris %d' % (label, int(keep.sum()), len(tris)))

    # frame from the bones -> identical camera for every package
    bp = np.stack([S, E, W])
    px, py = bp @ SX, bp @ SY
    f = args.frame
    if args.center_on == 'chain':
        box = (float(px.min()) - f, float(py.min()) - f,
               float(px.max()) + f, float(py.max()) + f)
    else:
        j = E if args.center_on == 'elbow' else W
        cx, cy = float(j @ SX), float(j @ SY)
        h = args.half
        box = (cx - h, cy - h, cx + h, cy + h)

    img = R.render_tile(pos, tris, np.zeros(len(pos)), SX, SY, SD, box)
    im = Image.fromarray(img.astype(np.uint8))
    dr = ImageDraw.Draw(im)
    scale = min((TILE_W - 16) / (box[2] - box[0]), (TILE_H - 16) / (box[3] - box[1]))
    ox = (TILE_W - (box[2] - box[0]) * scale) / 2
    oy = (TILE_H - (box[3] - box[1]) * scale) / 2

    def to_screen(p):
        x, y = float(p @ SX), float(p @ SY)
        return ((x - box[0]) * scale + ox, TILE_H - ((y - box[1]) * scale + oy))

    pts = [S, E, W, W + (W - E) / np.linalg.norm(W - E) * 0.11]
    for a, b in zip(pts[:-1], pts[1:]):
        dr.line([to_screen(a), to_screen(b)], fill=(255, 96, 96), width=3)
    for nm, p in (('l_shoulder', S), (pre + '_elbow', E), (pre + '_hand', W)):
        x, y = to_screen(p)
        dr.ellipse([x - 5, y - 5, x + 5, y + 5], outline=(255, 210, 90), width=2)
        dr.text((x + 7, y - 6), nm, fill=(255, 210, 90))
    dr.text((8, 6), '%s   %s arm in its own bend plane   red = bone chain '
            '(shoulder -> elbow -> wrist)' % (label, args.side), fill=(236, 240, 248))
    dr.text((8, 22), 'camera framed from the BONES, so every package is drawn at the '
            'same scale', fill=(176, 184, 196))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    im.save(args.out)
    print('wrote %s' % args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
