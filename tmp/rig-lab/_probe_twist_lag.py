# -*- coding: utf-8 -*-
"""READ-ONLY: the "clock hand" lag -- how far does each part of the arm
actually turn when the game twists the wrist?   (round 75)

Round 74 measured the WEIGHT profile and found it close to vanilla, yet the
shipped package "looks no better" in game.  This probe measures the thing the
eye actually sees: the effective rotation angle of every vertex.

For a pure wrist twist the game rotates

    l_hand        by  theta
    l_hand_twist  by  0.78 * theta      (median over 300 player clips)
    elbow / shoulder / everything else by 0

so a vertex turns by

    a(v) = w_hand(v) * theta + w_twist(v) * 0.78 * theta

Define the effective factor   f(v) = a(v) / theta = w_hand + 0.78 * w_twist.
A clean twist has f == 1 everywhere distal to the handover; f < 1 is lag.
The user's "stem at 2 o'clock, tip at 3 o'clock" is exactly a band where f
drops from 1 to ~0.78 and stays there.

Reports, per unit and per side:
  * f binned along t/L (t measured from the wrist bone head, + = towards hand)
  * a per-material (shell) breakdown inside the wrist band |t| <= 45 mm --
    that is where a rigid cuff or bracelet visibly fails to follow the skin.

Nothing is written.

usage:
    _probe_twist_lag.py <patch_0> [--unit 0x...] [--side left]
    _probe_twist_lag.py --local <base_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
SLIM = 0xcae35ee15168145f
K = 0.78


def _live_lod0(u, mi):
    """pos (unit space), raw bone_index, weights, material index per vertex.

    bone_index is material-local and must be run through bi.remaps[material];
    only vertices a LOD0 triangle references are actually drawn.
    """
    ti, bi = u.transform_info, u.bone_infos[0]
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    mat_of = np.full(n, -1, np.int32)
    flat = np.asarray(u.index_list(mi.stream_index), np.int64)
    for s in mi.sections:
        sv = np.unique(flat[s.index_offset:s.index_offset + s.num_indices])
        mat_of[sv] = s.material_index
    decoded = bidx.copy()
    for material in np.unique(mat_of[mat_of >= 0]):
        which = mat_of == material
        remap = np.asarray(bi.remaps[material], np.int64)
        decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]

    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    pos = pos @ M[:3, :3] + M[3, :3]          # row-major, translation last row

    live = mat_of >= 0
    return pos[live], wgt[live], decoded[live], mat_of[live]


def assemble(u, side):
    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    pre = 'l' if side == 'left' else 'r'
    el, hd = pre + '_elbow', pre + '_hand'
    tw = pre + '_hand_twist'
    stw = pre + '_shoulder_twist'

    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    pos, wgt, dec, mat = _live_lod0(u, mi)

    def wm(slots):
        slots = [s for s in slots if s is not None]
        if not slots:
            return np.zeros(len(pos))
        return np.where(np.isin(dec, list(slots)), wgt, 0.0).sum(axis=1)

    # l_hand is the root of the whole hand subtree (fingers rotate with it)
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

    w = {
        'hand': wm(hand_slots),
        'twist': wm([slot_of.get(tw)]),
        'elbow': wm([slot_of.get(el)]),
        'shoulder_twist': wm([slot_of.get(stw)]),
    }
    return dict(pos=pos, w=w, E=heads[slot_of[el]], W=heads[slot_of[hd]],
                mat=mat, names=names, mi=mi, unit=u)


def load_patch(patch, unit, side):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    return assemble(U.Unit.read(pl['toc'], pl['gpu']), side)


def load_local(base, side):
    toc = open(base + '.toc', 'rb').read()
    gpu = open(base + '.gpu_resources', 'rb').read()
    return assemble(U.Unit.read(toc, gpu), side)


def report(label, d, side, theta):
    pos, w, E, W = d['pos'], d['w'], d['E'], d['W']
    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    rel = pos - W
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    f = w['hand'] + K * w['twist']

    print()
    print('=' * 100)
    print('%s   [%s]   forearm L = %.1f mm   (theta = %.0f deg)' % (label, side, L * 1000, theta))
    print('=' * 100)
    print('  total weight mass over LOD0:  hand %.1f   twist %.1f   elbow %.1f   shoulder_twist %.1f'
          % (w['hand'].sum(), w['twist'].sum(), w['elbow'].sum(), w['shoulder_twist'].sum()))
    print('  f = w_hand + %.2f*w_twist  (1.00 = turns with the hand, %.2f = full lag)'
          % (K, K))
    print()
    print('   t(mm)   t/L     n    f_mean   f_min   deg@%.0f   w_twist  w_hand' % (theta,))
    band = rad < 0.10
    for a in np.arange(-0.20, 0.16 - 1e-9, 0.02):
        m = band & (t >= a) & (t < a + 0.02)
        if int(m.sum()) < 5:
            continue
        print('  %6.0f  %5.2f  %5d   %6.3f  %6.3f   %6.1f   %7.3f  %6.3f'
              % (a * 1000, a / L, int(m.sum()), f[m].mean(), f[m].min(),
                 f[m].mean() * theta, w['twist'][m].mean(), w['hand'][m].mean()))

    # per-shell breakdown inside the wrist band
    wb = band & (np.abs(t) <= 0.045)
    if int(wb.sum()) >= 5:
        print()
        print('  --- wrist band |t| <= 45 mm, per material shell ---')
        print('  %-10s %6s %8s %8s %8s %8s' % ('mat', 'n', 'w_twist', 'w_hand', 'f', 'deg@%.0f' % theta))
        for mi_ in np.unique(d['mat'][wb]):
            m = wb & (d['mat'] == mi_)
            if int(m.sum()) < 3:
                continue
            print('  %-10s %6d %8.3f %8.3f %8.3f %8.1f'
                  % ('mat %d' % mi_, int(m.sum()), w['twist'][m].mean(),
                     w['hand'][m].mean(), f[m].mean(), f[m].mean() * theta))

    # the number the user sees: spread of f inside the wrist band
    if int(wb.sum()) >= 5:
        print()
        print('  wrist-band f: min %.3f  max %.3f  spread %.3f  (= %.1f deg at theta=%.0f)'
              % (f[wb].min(), f[wb].max(), f[wb].max() - f[wb].min(),
                 (f[wb].max() - f[wb].min()) * theta, theta))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--theta', type=float, default=90.0)
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        d = load_local(args.local, args.side)
        label = args.label or args.local
    else:
        d = load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or ('%s unit %s' % (os.path.basename(args.patch), args.unit))
    report(label, d, args.side, args.theta)
    return 0


if __name__ == '__main__':
    sys.exit(main())
