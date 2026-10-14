# -*- coding: utf-8 -*-
"""Which arm axis does our shipped unit actually store?

Round 77c, read-only.  The user asks which version faithfully implements
"pivot on the elbow bone, align (elbow -> wrist) onto (elbow -> HD2 wrist)".
To answer that we must know what the unit's `l_elbow` / `l_hand` heads are:

  * if  |l_hand - l_elbow| == HD2 rest length (288.5 mm)  -> the unit stores
    the HD2 standard skeleton
  * if  |l_hand - l_elbow| == source length (~316.6 mm)   -> the unit stores the
    SOURCE skeleton (the pipeline pins every HD2 head onto its source joint)

usage:
    _probe_unit_arm_heads.py <patch_0> [<patch_0> ...] [--unit 0x...]
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
ARM_SKIN = 0x4a213e0ae8acf74b          # vanilla left-arm skin unit (round 73)
NAMES = ('l_shoulder', 'l_elbow', 'l_hand', 'l_hand_twist',
         'r_shoulder', 'r_elbow', 'r_hand')


def heads_of(u):
    ti, bi = u.transform_info, u.bone_infos[0]
    out = {}
    for slot, ti_idx in enumerate(bi.real_indices):
        nm = BONES.get(ti.hashes[ti_idx])
        if nm in NAMES:
            m = ti.matrices[ti_idx]
            out[nm] = np.array([m[12], m[13], m[14]], np.float64)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('srcs', nargs='+')
    ap.add_argument('--unit', default=None)
    a = ap.parse_args()

    want = int(a.unit, 16) if a.unit else None
    for src in a.srcs:
        entries, _raw = P.scan_entries(src)
        seen = set()
        for e in entries:
            if e['type_id'] != U.UNIT_ID or e['file_id'] in seen:
                continue
            if want is not None and e['file_id'] != want:
                continue
            seen.add(e['file_id'])
            pl = P.entry_payload(e, src)
            try:
                u = U.Unit.read(pl['toc'], pl['gpu'])
            except Exception as ex:
                print('  unit 0x%016x: read failed (%s)' % (e['file_id'], ex))
                continue
            hd = heads_of(u)
            print('--- %s' % os.path.basename(src))
            print('    unit 0x%016x   (%d arm bones found)' % (e['file_id'], len(hd)))
            for side in ('l', 'r'):
                el, ha, tw = side + '_elbow', side + '_hand', side + '_hand_twist'
                if el not in hd:
                    continue
                if ha in hd:
                    d = float(np.linalg.norm(hd[ha] - hd[el]))
                    print('    %s_elbow %s  ->  %s_hand %s   |d| = %7.2f mm'
                          % (side, np.array2string(hd[el], precision=4),
                             side, np.array2string(hd[ha], precision=4), d * 1000))
                if tw in hd:
                    d = float(np.linalg.norm(hd[tw] - hd[el]))
                    print('    %s_elbow %s  ->  %s_hand_twist %s   |d| = %7.2f mm'
                          % (side, np.array2string(hd[el], precision=4),
                             side, np.array2string(hd[tw], precision=4), d * 1000))
            print()


if __name__ == '__main__':
    main()
