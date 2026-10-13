# -*- coding: utf-8 -*-
"""Raw floats of the torso unit's bone transforms vs the avatar rig's, same bone.

The two readers disagree about where the translation lives, so print the FILE
order floats (no reshaping) next to the StingrayLocalTransform block that
precedes them, plus the BoneInfo inverse-bind matrix for the same bone.
usage: _probe_tfraww.py
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch

PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
AVATAR = 'avatar/unit_0x4d1c334d294dfa97.toc'
names = hd2_patch.bone_hash_file(BH)

entries, _raw = hd2_patch.scan_entries(PATCH)
e = next(x for x in entries if x['file_id'] == 0x9841b16544c8fbae)
toc = hd2_patch.entry_payload(e, PATCH)['toc']


def dump(toc, label, want):
    tio, = struct.unpack_from('<I', toc, 0x34)
    n, = struct.unpack_from('<I', toc, tio)
    lo = tio + 16
    mo = lo + n * 64
    eo = mo + n * 64
    ho = eo + n * 4
    print('--- %s : %d transforms, locals@%d matrices@%d entries@%d hashes@%d'
          % (label, n, lo, mo, eo, ho))
    for i in want:
        if i >= n:
            continue
        h, = struct.unpack_from('<I', toc, ho + i * 4)
        loc = list(struct.unpack_from('<16f', toc, lo + i * 64))
        mat = list(struct.unpack_from('<16f', toc, mo + i * 64))
        inc, par = struct.unpack_from('<2H', toc, eo + i * 4)
        print('  tf %2d %-16s hash %-11d inc %d parent %d' % (i, names.get(h, ''), h, inc, par))
        print('     local rot %s' % ['%+.4f' % v for v in loc[0:9]])
        print('     local pos %s  scale %s  dummy %+.4f'
              % (['%+.4f' % v for v in loc[9:12]], ['%+.4f' % v for v in loc[12:15]], loc[15]))
        print('     raw16  %s' % ' '.join('%+.4f' % v for v in mat))
        print('     rows   %s | %s | %s | %s'
              % (' '.join('%+.4f' % v for v in mat[0:4]),
                 ' '.join('%+.4f' % v for v in mat[4:8]),
                 ' '.join('%+.4f' % v for v in mat[8:12]),
                 ' '.join('%+.4f' % v for v in mat[12:16])))
        print('     cols   %s | %s | %s | %s'
              % (' '.join('%+.4f' % v for v in mat[0::4]),
                 ' '.join('%+.4f' % v for v in mat[1::4]),
                 ' '.join('%+.4f' % v for v in mat[2::4]),
                 ' '.join('%+.4f' % v for v in mat[3::4])))


dump(toc, 'PATCH unit 0x9841b16544c8fbae', [3, 9, 10, 11, 12, 13, 14, 21])

# BoneInfo inverse bind for the same bones
bio, = struct.unpack_from('<I', toc, 0x58)
nb, = struct.unpack_from('<I', toc, bio)
offs = [struct.unpack_from('<I', toc, bio + 4 + 4 * i)[0] for i in range(nb)]
start = bio + offs[0]
num, mo_, rio, fio = struct.unpack_from('<4I', toc, start)
print('\n--- BoneInfo 0 num_bones %d matrixOffset %d realOffset %d fakeOffset %d'
      % (num, mo_, rio, fio))
ri = [struct.unpack_from('<I', toc, start + rio + 4 * i)[0] for i in range(num)]
print('    real_indices -> transform %s' % ri)
for slot, tf in enumerate(ri):
    m = list(struct.unpack_from('<16f', toc, start + mo_ + slot * 64))
    h, = struct.unpack_from('<I', toc, tio + 16 + n * 64 + n * 4 + tf * 4)
    print('    slot %2d tf %2d %-14s inv-bind rows %s | %s | %s | %s'
          % (slot, tf, names.get(h, ''), ' '.join('%+.4f' % v for v in m[0:4]),
             ' '.join('%+.4f' % v for v in m[4:8]),
             ' '.join('%+.4f' % v for v in m[8:12]),
             ' '.join('%+.4f' % v for v in m[12:16])))

av = open(AVATAR, 'rb').read()
dump(av, 'AVATAR unit_0x4d1c334d294dfa97', [4, 7, 21, 26, 28, 89, 90])
