# -*- coding: utf-8 -*-
"""Round 74: render the SHIPPED package under a realistic wrist twist.

The user reports that when the wrist turns about the forearm axis the mesh does
not spin in place -- the hand root lags (stem at 2 o'clock, tip at 3 o'clock),
and suspects the wrist bone is not at the centre of the wrist mesh.

Round 73 moved the mid-forearm onto l_hand_twist, and over 300 player clips the
game drives that bone at ~0.78x the hand's twist.  So the composite is

    d = w_hand  * (R(theta)      - I)(p - W)
      + w_twist * (R(0.78*theta) - I)(p - W)

with l_hand and l_hand_twist sharing the pivot W (the wrist) and the forearm
axis.  Circumferential stripes make a clean spin look like a smooth helix; if
the wrist ring is off-axis the stripes visibly orbit instead.

usage:
    _probe_wrist_twist_render.py <out.png> <patch_0> [--unit 0x...] [--angles 0,60,120]
    _probe_wrist_twist_render.py <out.png> --local <base> [...]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_unit as U
import hd2_patch as P
import _probe_wrist_roll_render as R

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
SIDES = {'left': ('l_elbow', 'l_hand', 'l_hand_twist'),
         'right': ('r_elbow', 'r_hand', 'r_hand_twist')}


def assemble(u, side):
    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], float)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    el, hd, tw = SIDES[side]

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    mat_of = np.full(n, -1, np.int32)
    idx = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    for s in mi.sections:
        mat_of[np.unique(idx[s.index_offset:s.index_offset + s.num_indices])] = s.material_index
    dec = bidx.copy()
    for m in np.unique(mat_of[mat_of >= 0]):
        w = mat_of == m
        rm = np.asarray(bi.remaps[m], np.int64)
        dec[w] = rm[np.minimum(bidx[w], len(rm) - 1)]
    live = mat_of >= 0
    new_of = np.full(n, -1, np.int64)
    new_of[live] = np.arange(int(live.sum()))
    tri = new_of[idx]
    tri = tri[(tri >= 0).all(axis=1)]
    pos, wgt, dec = pos[live], wgt[live], dec[live]

    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    pos = pos @ M[:3, :3] + M[3, :3]

    def wm(slots):
        if not slots:
            return np.zeros(len(pos))
        return np.where(np.isin(dec, list(slots)), wgt, 0.0).sum(axis=1)

    fingers = [s for s, nm in names.items() if nm.startswith(side[0] + '_') and 'finger' in nm]
    w_hand = wm([slot_of[hd]] + fingers)
    w_tw = wm([slot_of[tw]] if tw in slot_of else [])
    w_el = wm([slot_of[el]])
    return (pos, w_hand, w_tw, w_el, heads[slot_of[el]], heads[slot_of[hd]], tri,
            mat_of[live])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--side', default='left')
    ap.add_argument('--angles', default='0,60,120')
    ap.add_argument('--k', type=float, default=0.78, help='twist bone rate vs hand')
    ap.add_argument('--lo', type=float, default=-0.15)
    ap.add_argument('--hi', type=float, default=0.22)
    args = ap.parse_args()

    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        label = args.local
    else:
        entries, _ = P.scan_entries(args.patch)
        e = next(x for x in entries if x['type_id'] == U.UNIT_ID
                 and x['file_id'] == int(args.unit, 16))
        pl = P.entry_payload(e, args.patch)
        toc, gpu = pl['toc'], pl['gpu']
        label = os.path.basename(args.patch)
    pos, w_hand, w_tw, w_el, E, W, tri, _mat = assemble(U.Unit.read(toc, gpu), args.side)

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
    keep = (rad < 0.12) & (t > args.lo) & (t < args.hi) & (w_el < 0.999)
    sel = np.nonzero(keep)[0]
    tris = tri[keep[tri].all(axis=1)]
    print('%s  %s side  L=%.1f mm  verts kept %d  tris %d'
          % (label, args.side, L * 1000, len(sel), len(tris)))

    angles = [float(x) for x in args.angles.split(',')]
    box = (float((pos[sel] @ axis).min()), float((pos[sel] @ V_).min()),
           float((pos[sel] @ axis).max()), float((pos[sel] @ V_).max()))

    rows = []
    for mode in ('realistic (hand theta, twist 0.78 theta)', 'rigid (both theta)'):
        tiles = []
        for deg in angles:
            Rm = R.rodrigues(axis, np.deg2rad(deg))
            k = args.k if mode.startswith('realistic') else 1.0
            Rk = R.rodrigues(axis, np.deg2rad(deg * k))
            d = (w_hand[:, None] * (((pos - W) @ Rm.T) - (pos - W))
                 + w_tw[:, None] * (((pos - W) @ Rk.T) - (pos - W)))
            deformed = pos + d
            tiles.append(R.render_tile(deformed, tris,
                                       R.stripe_scalar(deformed, W, axis, V_, N_, 10.0),
                                       axis, V_, N_, box))
        rows.append((mode, tiles))

    pad, head = 8, 20
    w = len(angles) * R.TILE_W + (len(angles) + 1) * pad
    h = len(rows) * (R.TILE_H + head) + (len(rows) + 1) * pad
    sheet = Image.new('RGB', (w, h), (10, 11, 14))
    dr = ImageDraw.Draw(sheet)
    for r, (lab, tiles) in enumerate(rows):
        y = pad + r * (R.TILE_H + head + pad)
        dr.text((pad, y + 4), lab, fill=(236, 240, 248))
        for c, tile in enumerate(tiles):
            x = pad + c * (R.TILE_W + pad)
            sheet.paste(Image.fromarray(tile.astype(np.uint8)), (x, y + head))
            dr.text((x + 6, y + head + 5), 'wrist twist %d deg' % angles[c],
                    fill=(255, 214, 120))
    dr.text((pad, h - pad - 12),
            'circumferential stripes: a clean spin gives an even helix; '
            'stripes that orbit the axis mean the ring is off-axis',
            fill=(150, 160, 175))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    sheet.save(args.out)
    print('wrote %s (%dx%d)' % (args.out, w, h))
    return 0


if __name__ == '__main__':
    sys.exit(main())
