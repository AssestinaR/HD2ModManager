# -*- coding: utf-8 -*-
"""READ-ONLY: why is the forearm special?  Structural comparison across limbs.

Hypotheses to test against the shipped data:
  H-A  segment length: the forearm is the shortest limb segment, so any fixed
       smoothing window covers a larger fraction of it.
  H-B  cross-section radius: a roll of a thin tube displaces fewer mm per
       degree, but a roll of a WIDE band does more damage.
  H-C  the distal partner is a HAND subtree (many bones), not a single bone:
       the hand rolls, the forearm does not, so every forearm vertex bound to
       the hand subtree inherits the full roll.
  H-D  bone count / branch topology under the distal joint.

Usage: python _probe_forearm_why.py <patch_0> [...]
"""
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
UNIT = 0xcae35ee15168145f

SEGMENTS = [
    ('upperarm', 'l_shoulder', 'l_elbow'),
    ('forearm ', 'l_elbow', 'l_hand'),
    ('shin    ', 'l_knee', 'l_foot'),
]
SIDES = (('l', 'L'), ('r', 'R'))


def load(patch):
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
    si = [i for i, s in enumerate(u.stream_infos)
          if s.num_vertices > 1000
          and any(c.type == 7 for c in s.components)
          and any(c.type == 6 for c in s.components)][0]
    verts = u.decode_vertices(si)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    return u, ti, bi, names, heads, slot_of, pos, bidx, wgt


def subtree_slots(ti, bi, root_slot):
    root_ti = bi.real_indices[root_slot]
    out = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                out.append(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    return out


def main():
    for patch in sys.argv[1:]:
        u, ti, bi, names, heads, slot_of, pos, bidx, wgt = load(patch)
        nv = len(pos)
        print()
        print('=' * 100)
        print('patch %s' % patch)
        print('=' * 100)
        print('  %-10s %-8s %-8s %-9s %-9s %-10s %-9s %s'
              % ('segment', 'len mm', 'verts', 'radius mm', 'len/rad', 'subtree n',
                 'girth mm', 'note'))
        for seg, prox, dist in SEGMENTS:
            # both sides, average the geometry, take the left for the topology
            lens, rads, counts, subs = [], [], [], []
            for side, _ in SIDES:
                pn, dn = side + '_' + prox[2:], side + '_' + dist[2:]
                if pn not in slot_of or dn not in slot_of:
                    continue
                Pp, Dd = heads[slot_of[pn]], heads[slot_of[dn]]
                axis = Dd - Pp
                L = float(np.linalg.norm(axis))
                axis /= L
                rel = pos - Pp
                t = rel @ axis
                rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
                near = (rad < 0.095) & (t > 0.0) & (t < L)
                lens.append(L)
                if near.sum():
                    rads.append(float(rad[near].mean()))
                    counts.append(int(near.sum()))
                if side == 'l':
                    subs.append(subtree_slots(ti, bi, slot_of[dn]))
            if not lens:
                continue
            L = float(np.mean(lens))
            r = float(np.mean(rads)) if rads else float('nan')
            # bone count strictly under the distal joint
            nsub = len(subs[0]) if subs else 0
            # bones sharing the distal joint's parent chain, i.e. how many
            # siblings hang off the same proximal bone
            siblings = 0
            note = ''
            if seg.strip() == 'forearm':
                tw = 'l_hand_twist' in slot_of
                note = 'hand_twist bone present: %s' % tw
            if seg.strip() == 'shin':
                note = 'ankle == single bone'
            print('  %-10s %-8.1f %-8d %-9.1f %-9.2f %-10d %-9.1f %s'
                  % (seg, L * 1000, int(np.mean(counts)) if counts else 0,
                     r * 1000, L / r if r == r else float('nan'), nsub,
                     2 * np.pi * r * 1000 if r == r else float('nan'), note))

        # --- how many bones hang under each distal joint, and their spread
        print()
        print('  bones under each distal joint (left side):')
        for seg, prox, dist in SEGMENTS:
            pn = 'l_' + dist[2:]
            if pn not in slot_of:
                continue
            sub = subtree_slots(ti, bi, slot_of[pn])
            nm = sorted(names[s] for s in sub)
            print('    %-9s -> %2d bones: %s%s'
                  % (seg, len(sub), ', '.join(nm[:8]), ' ...' if len(nm) > 8 else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
