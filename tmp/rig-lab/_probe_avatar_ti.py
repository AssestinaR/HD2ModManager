"""Compare the avatar toc's TransformInfo with a reference Unit's, and map our
HD2 bone names onto avatar TransformInfo indices.

The packager needs, for each bone our weights touch:
  * a TransformInfo slot (name hash + rest matrix)
  * an inverse-bind matrix for BoneInfo

Both come from the avatar toc, which is the authoritative rest pose.
"""
import glob
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU
import hd2_hash as HH

AVATAR = 'avatar/unit_0x4d1c334d294dfa97.toc'
cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
REF = [c for c in cand if 'Chiffon' in c or '4.28' in c][0]


def ti_of(path):
    """TransformInfo from a bare toc file (avatar) or from a patch entry."""
    toc = open(path, 'rb').read()
    b = HU.Buf(toc)
    off = struct.unpack_from('<I', toc, 0x34)[0]
    return HU.TransformInfo.read(b, off), off


def main():
    ati, aoff = ti_of(AVATAR)
    print('avatar TransformInfo @0x%x  count=%d  unk=%s' % (aoff, ati.count, ati.unk.hex()))
    print('  avatar hashes: %s' % ['0x%x' % h for h in ati.hashes])
    print('  avatar parents: %s' % [p for (_i, p) in ati.entries])
    print('  avatar incr: %s' % [i for (i, _p) in ati.entries])
    print('  locals[0] %s' % ati.locals[0])
    print('  matrices[0] %s' % ['%.6g' % x for x in ati.matrices[0]])

    entries, _ = HP.scan_entries(REF)
    e = [x for x in entries if x['type_id'] == HP.UNIT_TYPE_ID
         and x['file_id'] == 0xcae35ee15168145f][0]
    pay = HP.entry_payload(e, REF)
    u = HU.Unit.read(pay['toc'], pay['gpu'])
    uti = u.transform_info
    print()
    print('unit 0xcae35ee15168145f TransformInfo count=%d  unk=%s' % (uti.count, uti.unk.hex()))
    print('  unit parents: %s' % [p for (_i, p) in uti.entries])
    print('  unit incr: %s' % [i for (i, _p) in uti.entries])
    print('  locals == avatar locals? %s' % (uti.locals == ati.locals[:uti.count]))
    same = []
    for i, h in enumerate(uti.hashes):
        if h in ati.hashes:
            j = ati.hashes.index(h)
            same.append((i, j, all(abs(uti.matrices[i][k] - ati.matrices[j][k]) < 1e-12
                                   for k in range(16))))
    print('  unit hash -> avatar index (matrix identical?):')
    for i, j, ok in same:
        print('    unit[%2d] 0x%08x -> avatar[%2d]  matrix_same=%s' % (i, uti.hashes[i], j, ok))
    miss = [(i, uti.hashes[i]) for i in range(uti.count) if uti.hashes[i] not in ati.hashes]
    print('  unit hashes NOT in avatar: %s' % ['%d:0x%x' % m for m in miss])

    # our bones
    meta = json.load(open('out/blender/meta.json', encoding='utf-8'))
    names = meta['bones']
    print()
    print('our rig: %d bones' % len(names))
    un = [(i, n) for i, n in enumerate(names) if not n]
    print('  unnamed: %s' % un)
    tf_idx = {}
    notfound = []
    for i, n in enumerate(names):
        if not n:
            notfound.append((i, n, None))
            continue
        h = HH.name_hash(n)
        if h in ati.hashes:
            tf_idx[i] = ati.hashes.index(h)
        else:
            notfound.append((i, n, h))
    print('  mapped %d / %d to avatar TransformInfo' % (len(tf_idx), len(names)))
    print('  NOT found: %s' % notfound)


if __name__ == '__main__':
    main()
