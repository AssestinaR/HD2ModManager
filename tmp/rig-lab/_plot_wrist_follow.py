# -*- coding: utf-8 -*-
"""READ-ONLY: plot how much of each arm vertex follows the wrist rotation.

Round 70.  The wrist render is noisy (sleeve fragments fill the frame), so this
draws the one quantity that actually differs between rounds: the fraction of a
vertex that follows a wrist rotation, as a function of position along
elbow -> hand.  t/L = 1.0 is the wrist; > 1 is the hand and fingers.

  round 66 : band 0.22L .. 0.82L   (soft; the forearm middle follows a lot)
  round 69 : band 0.22L .. 1.00L   (same start, ramp reaches the wrist)
  round 70 : band 0.22L .. 1.00L, split 50/50 between l_hand and l_hand_twist

usage: python _plot_wrist_follow.py <out.png> <patch_0> [<patch_0> ...] \
           [--label NAME] [--twist 0,0,1]
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
W, H_ = 900, 420
BG = (16, 18, 22)
COL = [(236, 196, 96), (108, 176, 232), (232, 112, 112), (128, 216, 160)]


def profile(patch, side, with_twist):
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

    el, hd = side[0] + '_elbow', side[0] + '_hand'
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
    m = np.zeros(len(pos))
    for j in range(4):
        hit = np.isin(bidx[:, j], np.asarray(hand_slots))
        m[hit] += wgt[hit, j]
    if with_twist:
        tw = slot_of.get(side[0] + '_hand_twist')
        if tw is not None:
            for j in range(4):
                hit = bidx[:, j] == tw
                m[hit] += wgt[hit, j]

    E, Wp = heads[slot_of[el]], heads[slot_of[hd]]
    axis = Wp - E
    L = float(np.linalg.norm(axis))
    axis /= L
    rel = pos - E
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    tn = t / L
    xs, ys = [], []
    for k in range(26):
        lo, hi = k * 0.05, (k + 1) * 0.05
        sel = (rad < 0.12) & (tn >= lo) & (tn < hi)
        if sel.sum() < 4:
            continue
        xs.append(0.5 * (lo + hi))
        ys.append(float(m[sel].mean()))
    return xs, ys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('patches', nargs='+')
    ap.add_argument('--label', action='append', default=[])
    ap.add_argument('--twist', default='', help='comma list of 0/1, one per patch')
    ap.add_argument('--side', default='left')
    args = ap.parse_args()

    tw = [bool(int(x)) for x in args.twist.split(',')] if args.twist else [False] * len(args.patches)
    tw += [False] * (len(args.patches) - len(tw))
    labels = args.label + [''] * (len(args.patches) - len(args.label))

    img = Image.new('RGB', (W, H_), BG)
    d = ImageDraw.Draw(img)
    ml, mr, mt, mb = 62, 20, 44, 46
    x0, x1 = ml, W - mr
    y0, y1 = mt, H_ - mb

    def px(v):
        return x0 + (v - 0.0) / 1.3 * (x1 - x0)

    def py(v):
        return y1 - v * (y1 - y0)

    d.text((ml, 14), 'how much of each arm vertex follows a wrist rotation', fill=(232, 238, 248))
    d.text((ml, 28), 'x = position along elbow -> hand   (1.0 = the wrist; > 1 = hand / fingers)',
           fill=(150, 160, 176))
    for v in np.arange(0.0, 1.01, 0.25):
        d.line([(px(0), py(v)), (px(1.3), py(v))], fill=(44, 50, 60))
        d.text((12, py(v) - 6), '%.2f' % v, fill=(150, 160, 176))
    for v in np.arange(0.0, 1.31, 0.2):
        d.line([(px(v), py(0)), (px(v), py(1))], fill=(38, 44, 52))
        d.text((px(v) - 12, y1 + 8), '%.1f' % v, fill=(150, 160, 176))
    d.line([(px(1.0), py(0)), (px(1.0), py(1))], fill=(96, 104, 118))
    d.text((px(1.0) + 4, y0 + 4), 'wrist', fill=(180, 188, 200))

    for i, patch in enumerate(args.patches):
        xs, ys = profile(patch, args.side, tw[i])
        pts = [(px(a), py(b)) for a, b in zip(xs, ys)]
        if len(pts) > 1:
            d.line(pts, fill=COL[i % len(COL)], width=3)
        for p in pts:
            d.ellipse([p[0] - 2.5, p[1] - 2.5, p[0] + 2.5, p[1] + 2.5], fill=COL[i % len(COL)])
        ly = y0 + 12 + i * 18
        d.line([(x1 - 250, ly), (x1 - 220, ly)], fill=COL[i % len(COL)], width=3)
        d.text((x1 - 212, ly - 7), labels[i] if i < len(labels) else '', fill=(220, 226, 236))

    img.save(args.out)
    print('wrote %s (%dx%d)' % (args.out, W, H_))
    return 0


if __name__ == '__main__':
    sys.exit(main())
