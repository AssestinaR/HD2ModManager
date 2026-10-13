# -*- coding: utf-8 -*-
"""READ-ONLY: seam size when the wrist roll is driven through l_hand_twist.

Round 70.  _probe_wrist_roll.py only counts weight on the l_hand subtree, which
misses l_hand_twist (it is a child of the elbow, not of the hand).  This probe
therefore evaluates three "what does the game actually drive?" hypotheses:

  A  only l_hand (+ fingers) rotates   -> l_hand_twist stays at rest
  B  both l_hand and l_hand_twist rotate by the same amount
  C  only l_hand_twist rotates

For each it reports the largest displacement JUMP across a mesh edge inside the
wrist band -- i.e. the seam that would actually be visible.

Usage: python _probe_twist_roll.py <patch_0> [<patch_0> ...] [--angle 180]
"""
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
UNIT = 0xcae35ee15168145f
SIDES = (('left', 'l_elbow', 'l_hand', 'l_hand_twist'),
         ('right', 'r_elbow', 'r_hand', 'r_hand_twist'))


def rodrigues(axis, a):
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(a) * K + (1.0 - np.cos(a)) * K @ K


def analyse(patch, angle_deg):
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

    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    nv = len(pos)

    idx = np.frombuffer(u.gpu_index[mi.stream_index], np.uint32).astype(np.int64)
    idx = idx.reshape(-1, 3)
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])

    print('=' * 78)
    print('patch %s   (roll %.0f deg)' % (patch, angle_deg))

    for side, el, hd, tw in SIDES:
        h_s = slot_of[hd]
        hand_ti = bi.real_indices[h_s]
        subtree = []
        for slot, ti_idx in enumerate(bi.real_indices):
            seen, cur = set(), ti_idx
            while 0 <= cur < ti.count and cur not in seen:
                if cur == hand_ti:
                    subtree.append(slot)
                    break
                seen.add(cur)
                cur = ti.entries[cur][1]

        def wmask(slots):
            m = np.zeros(nv)
            for j in range(4):
                hit = np.isin(bidx[:, j], list(slots))
                m[hit] += wgt[hit, j]
            return m

        wh = wmask(subtree)                      # l_hand + fingers
        wt = wmask([slot_of[tw]])                # l_hand_twist

        E, W = heads[slot_of[el]], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        tn = t / L

        band = (rad < 0.12) & (t > 0.75 * L) & (t < 1.15 * L)
        print('  [%s] L=%.1f mm   hand-w total %.1f   twist-w total %.1f'
              % (side, L * 1000, wh.sum(), wt.sum()))
        print('     t/L   n    hand-w  twist-w   (rad<120mm)')
        for k in range(24):
            lo, hi = k * 0.05, (k + 1) * 0.05
            sel = (rad < 0.12) & (tn >= lo) & (tn < hi)
            if sel.sum() == 0:
                continue
            print('     %4.2f  %5d  %6.3f  %7.3f'
                  % (0.5 * (lo + hi), int(sel.sum()), wh[sel].mean(), wt[sel].mean()))

        a = np.deg2rad(angle_deg)
        R = rodrigues(axis, a)
        delta = ((pos - W) @ R.T) - (pos - W)
        both = band[edges[:, 0]] & band[edges[:, 1]]
        eb = edges[both]
        for tag, m in (('A only l_hand      ', wh),
                       ('B both rotate      ', wh + wt),
                       ('C only l_hand_twist', wt)):
            d = m[:, None] * delta
            if not both.sum():
                print('     %s : no edges in band' % tag)
                continue
            jump = np.linalg.norm(d[eb[:, 0]] - d[eb[:, 1]], axis=1) * 1000
            order = np.argsort(jump)[::-1][:3]
            worst = ' | '.join('%.1f (w %.3f/%.3f)' % (jump[k], m[eb[k, 0]], m[eb[k, 1]])
                               for k in order)
            print('     %s : med %6.2f  p99 %7.2f  MAX %7.1f mm   worst %s'
                  % (tag, float(np.median(jump)), float(np.percentile(jump, 99)),
                     float(jump.max()), worst))
    return 0


def main():
    argv = sys.argv[1:]
    angle = 180.0
    if '--angle' in argv:
        k = argv.index('--angle')
        angle = float(argv[k + 1])
        argv = argv[:k] + argv[k + 2:]
    for patch in argv:
        analyse(patch, angle)
    return 0


if __name__ == '__main__':
    sys.exit(main())
