# -*- coding: utf-8 -*-
"""Compare the reference units' TransformInfo with the avatar rig's.

Two questions:
  1. which entries of a unit's TransformInfo are identity (candidate mesh
     transforms)?
  2. does a bone's matrix in a unit equal the same bone's matrix in the avatar
     toc?  (If yes, a from-scratch unit can simply copy the avatar's 107-entry
     TransformInfo and set real_indices = 0..106.)
"""
import glob
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as U

AVATAR = 'avatar/unit_0x4d1c334d294dfa97.toc'


def ident(m):
    return np.allclose(np.asarray(m, float).reshape(4, 4), np.eye(4), atol=1e-6)


def main():
    av_bytes = open(AVATAR, 'rb').read()
    ti = U.TransformInfo.read(U.Buf(av_bytes), struct.unpack_from('<I', av_bytes, 0x34)[0])
    print('avatar tf count=%d unk=%s' % (ti.count, ti.unk.hex()))
    print('  identity entries: %s'
          % [i for i, m in enumerate(ti.matrices) if ident(m)][:20])
    print('  first 6 hashes  : %s' % ['0x%x' % h for h in ti.hashes[:6]])
    print('  first 6 parents : %s' % ti.entries[:6])
    print('  matrix[0]  =%s' % (np.asarray(ti.matrices[0]).reshape(4, 4),))
    print('  matrix[4]  =%s' % (np.asarray(ti.matrices[4]).reshape(4, 4),))
    print('  matrix[11] (boss? 0x%x) =%s'
          % (ti.hashes[11], np.asarray(ti.matrices[11]).reshape(4, 4)))
    print('  matrix[20] (0x%x) =%s'
          % (ti.hashes[20], np.asarray(ti.matrices[20]).reshape(4, 4)))
    print('  locals[0]=%s' % (ti.locals[0],))

    tgt = sys.argv[1] if len(sys.argv) > 1 else \
        glob.glob(r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0')[0]
    want = (sys.argv[2].lower() if len(sys.argv) > 2 else '0x9841b16544c8fbae')
    entries, _raw = HP.scan_entries(tgt)
    for e in entries:
        if e['type_id'] != U.UNIT_ID or ('0x%x' % e['file_id']) != want:
            continue
        pl = HP.entry_payload(e, tgt)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        t = u.transform_info
        print('=' * 100)
        print('%s tf count=%d' % (want, t.count))
        print('  identity entries: %s'
              % [i for i, m in enumerate(t.matrices) if ident(m)])
        for i in range(t.count):
            print('   [%2d] 0x%08x par=%-3d %s'
                  % (i, t.hashes[i], t.entries[i][1],
                     'IDENTITY' if ident(t.matrices[i]) else
                     'T=%s' % np.round(np.asarray(t.matrices[i], float).reshape(4, 4)[3, :3], 5)))
        # cross check a few bones against the avatar
        av = {h: i for i, h in enumerate(ti.hashes)}
        print('  -- cross check vs avatar --')
        for bi in u.bone_infos[:1]:
            for k, ri in enumerate(bi.real_indices):
                h = t.hashes[ri]
                if h in av:
                    a = np.asarray(ti.matrices[av[h]], float).reshape(4, 4)
                    b = np.asarray(t.matrices[ri], float).reshape(4, 4)
                    d = float(np.abs(a - b).max())
                    print('     real[%2d]=%2d hash=0x%08x avatar=%2d maxdiff=%.3g'
                          % (k, ri, h, av[h], d))
                else:
                    print('     real[%2d]=%2d hash=0x%08x NOT IN AVATAR' % (k, ri, h))
            break


if __name__ == '__main__':
    main()
