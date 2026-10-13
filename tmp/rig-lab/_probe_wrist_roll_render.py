# -*- coding: utf-8 -*-
"""Visual check: does the forearm skin twist EVENLY under a wrist roll?

Round 68.  The numeric probe (`_probe_wrist_roll.py`) says round-67's narrow
hand-weight band tore the wrist by 83 mm at a 180 deg roll, while round-68's
wide ramp leaves only the 37.8 mm geometric floor.  This renders that
difference so a human can confirm it by eye.

How to read it
--------------
Only the forearm tube is drawn (radius < 90 mm, elbow..wrist), painted with
circumferential stripes.  A healthy roll turns the stripes into a smooth helix:
each stripe advances by the same amount as its neighbour.  If the hand weight
jumps across one ring of edges, the stripes bunch up there and the surface
visibly folds -- which is exactly the "连接不上" the user reported.

usage:
    _probe_wrist_roll_render.py <out.png> <patch_0> [...] \
        [--label NAME] [--angles 0,90,180] [--side left] [--stripes 8]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
UNIT = 0xcae35ee15168145f
SIDES = {'left': ('l_elbow', 'l_hand'), 'right': ('r_elbow', 'r_hand')}

TILE_W, TILE_H = 460, 150
BG = (18, 20, 24)


def rodrigues(axis, a):
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K


def load(patch, side, with_twist=False):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64).reshape(-1, 3)

    el, hd = SIDES[side]
    root_ti = bi.real_indices[slot_of[hd]]
    hand_slots = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                hand_slots.append(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    wh = np.zeros(len(pos))
    for j in range(4):
        hit = np.isin(bidx[:, j], np.asarray(hand_slots))
        wh[hit] += wgt[hit, j]

    if with_twist:
        # l_hand_twist is a child of the elbow, so it is not in the hand subtree.
        # Under the "the game drives both" hypothesis it rolls by the same amount,
        # so the moving weight is simply wh + w(l_hand_twist).
        tw_slot = slot_of.get(el.replace('elbow', 'hand_twist'))
        if tw_slot is not None:
            for j in range(4):
                hit = bidx[:, j] == tw_slot
                wh[hit] += wgt[hit, j]

    E, W = heads[slot_of[el]], heads[slot_of[hd]]
    axis = W - E
    axis /= np.linalg.norm(axis)
    rel = pos - W
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    keep = (rad < 0.090) & (t > -0.20) & (t < 0.02)
    tris = idx[keep[idx].all(axis=1)]
    return pos, tris, wh, E, W, axis, keep


def stripe_scalar(pos, W, U_, V_, N_, stripes):
    rel = pos - W
    az = np.arctan2(rel @ N_, rel @ V_)
    return 0.5 + 0.5 * np.sin(stripes * az)


def render_tile(pos, tris, scalar, U_, V_, N_, box, bg=BG):
    px, py, pz = pos @ U_, pos @ V_, pos @ N_
    lo = np.array(box[:2])
    hi = np.array(box[2:])
    scale = min((TILE_W - 16) / (hi[0] - lo[0]), (TILE_H - 16) / (hi[1] - lo[1]))
    ox = (TILE_W - (hi[0] - lo[0]) * scale) / 2
    oy = (TILE_H - (hi[1] - lo[1]) * scale) / 2
    sx = (px - lo[0]) * scale + ox
    sy = TILE_H - ((py - lo[1]) * scale + oy)

    img = np.zeros((TILE_H, TILE_W, 3), np.float64)
    img[:, :] = bg
    zbuf = np.full((TILE_H, TILE_W), -1e18)

    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-30)
    L = np.array([0.35, 0.5, 0.79])
    L /= np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.55 + 0.45

    for i in range(len(tris)):
        tri = tris[i]
        xs, ys = sx[tri], sy[tri]
        x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
        y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
        x0, x1 = max(x0, 0), min(x1, TILE_W - 1)
        y0, y1 = max(y0, 0), min(y1, TILE_H - 1)
        if x0 > x1 or y0 > y1:
            continue
        d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
        if abs(d) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / d
        w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / d
        w2 = 1.0 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        depth = w0 * pz[tri[0]] + w1 * pz[tri[1]] + w2 * pz[tri[2]]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        m = inside & (depth > sub)
        if not m.any():
            continue
        sub[m] = depth[m]
        g = w0 * scalar[tri[0]] + w1 * scalar[tri[1]] + w2 * scalar[tri[2]]
        base = np.stack([np.full_like(g, 62.0), np.full_like(g, 148.0), np.full_like(g, 196.0)], -1)
        warm = np.stack([np.full_like(g, 236.0), np.full_like(g, 196.0), np.full_like(g, 96.0)], -1)
        col = (base * (1.0 - g[..., None]) + warm * g[..., None]) * (lam[i] * 0.75 + 0.25)
        img[y0:y1 + 1, x0:x1 + 1][m] = col[m]
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patches', nargs='+')
    ap.add_argument('--label', action='append', default=[])
    ap.add_argument('--angles', default='0,90,180')
    ap.add_argument('--side', default='left')
    ap.add_argument('--stripes', type=float, default=8.0)
    ap.add_argument('--twist', action='store_true',
                    help='count l_hand_twist as rolling too (hypothesis B)')
    args = ap.parse_args()

    angles = [float(x) for x in args.angles.split(',')]
    labels = args.label + [''] * (len(args.patches) - len(args.label))

    rows = []
    for patch, label in zip(args.patches, labels):
        pos, tris, wh, E, W, axis, keep = load(patch, args.side, args.twist)
        N_ = np.cross(axis, [0.0, 0.0, 1.0])
        N_ /= np.linalg.norm(N_)
        V_ = np.cross(N_, axis)
        V_ /= np.linalg.norm(V_)

        sel = np.nonzero(keep)[0]
        box = (0.0, 0.0, 0.0, 0.0)
        proj_x, proj_y = pos[sel] @ axis, pos[sel] @ V_
        box = (proj_x.min(), proj_y.min(), proj_x.max(), proj_y.max())

        tiles = []
        for deg in angles:
            R = rodrigues(axis, np.deg2rad(deg))
            d = wh[:, None] * (((pos - W) @ R.T) - (pos - W))
            deformed = pos + d
            tiles.append(render_tile(deformed, tris,
                                     stripe_scalar(deformed, W, axis, V_, N_, args.stripes),
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
            draw.text((x + 6, y + head + 5), 'roll %d deg' % angles[c], fill=(255, 214, 120))
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    sheet.save(args.out)
    print('wrote %s  (%dx%d)' % (args.out, width, height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
