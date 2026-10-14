# -*- coding: utf-8 -*-
"""READ-ONLY: which distal bones actually carry weight on the forearm surface?

Round 72 finding: under the wrist sit 17 bones (hand + 14 finger joints +
attachments), while under the ankle sit 3.  If any of those 14 finger joints
carry weight on the FOREARM surface, then every finger animation (trigger pull,
reload, fist) will drag the forearm -- which no amount of elbow/hand split can
fix.

Usage: python _probe_forearm_bones.py <patch_0> [...]
"""
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
UNIT = 0xcae35ee15168145f
FINGER_TOKENS = ('finger', 'thumb', 'index', 'middle', 'ring', 'pinky', 'little')


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
    return ti, bi, names, heads, slot_of, pos, bidx, wgt


def main():
    for patch in sys.argv[1:]:
        ti, bi, names, heads, slot_of, pos, bidx, wgt = load(patch)
        Pp, Dd = heads[slot_of['l_elbow']], heads[slot_of['l_hand']]
        axis = Dd - Pp
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - Pp
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        # the forearm tube: several windows, so we can see where fingers show up
        nv = len(pos)
        print()
        print('=' * 108)
        print('patch %s' % patch)
        print('=' * 108)
        windows = [
            ('proximal  0.05L-0.60L', 0.05, 0.60),
            ('distal    0.60L-0.90L', 0.60, 0.90),
            ('wrist ring 0.90L-1.00L', 0.90, 1.00),
        ]
        for wname, w0, w1 in windows:
            tube = (rad < 0.095) & (t > w0 * L) & (t < w1 * L)
            if tube.sum() == 0:
                print('  %-22s (empty)' % wname)
                continue
            contrib = {}
            for slot, ti_idx in enumerate(bi.real_indices):
                nm = names[slot]
                w = np.zeros(nv)
                for j in range(4):
                    m = bidx[:, j] == slot
                    w[m] += wgt[m, j]
                s = float(w[tube].sum())
                if s > 1e-6:
                    contrib[nm] = (s / max(1, int(tube.sum())),
                                   int((w[tube] > 1e-4).sum()))
            tot = sum(v[0] for v in contrib.values())
            finger_mass = sum(v[0] for k, v in contrib.items()
                              if any(tok in k for tok in FINGER_TOKENS))
            print()
            print('  %s   ->  %d verts' % (wname, int(tube.sum())))
            print('    %-30s %-10s %-8s %s' % ('bone', 'mean w', 'verts', 'share'))
            for nm, (mw, nvt) in sorted(contrib.items(), key=lambda kv: -kv[1][0])[:8]:
                tag = '  <<< FINGER' if any(tok in nm for tok in FINGER_TOKENS) else ''
                print('    %-30s %-10.4f %-8d %.2f%%%s'
                      % (nm, mw, nvt, 100.0 * mw / tot if tot else 0.0, tag))
            print('    => FINGER-JOINT weight mass here: %.2f%%'
                  % (100.0 * finger_mass / tot if tot else 0.0))
    return 0



if __name__ == '__main__':
    sys.exit(main())
