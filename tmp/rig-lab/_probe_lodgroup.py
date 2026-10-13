# -*- coding: utf-8 -*-
"""Crack the `LODGroupList` blob.

Across every unit in the reference patch, print the blob as u32/f32 words next
to the unit's mesh structure (LOD indices, stream assignment, bboxes) so the
layout can be read off by comparison.
"""
import glob
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as U


def words(b):
    out = []
    for i in range(0, len(b) - 3, 4):
        u = struct.unpack_from('<I', b, i)[0]
        f = struct.unpack_from('<f', b, i)[0]
        if f == f and abs(f) < 1e6 and (u > 0x100000 or abs(f) >= 1e-6):
            s = '%.6g' % f
        else:
            s = '0x%x' % u
        out.append('%02x:%s' % (i, s))
    return ' '.join(out)


def main():
    tgt = sys.argv[1] if len(sys.argv) > 1 else \
        glob.glob(r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0')[0]
    entries, _raw = HP.scan_entries(tgt)
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        pl = HP.entry_payload(e, tgt)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        lg = u.lod_group_list_data
        head = struct.unpack_from('<7I', lg, 0) if len(lg) >= 28 else ()
        n = struct.unpack_from('<I', lg, 20)[0] if len(lg) >= 24 else 0
        offs = struct.unpack_from('<%dI' % (n + 3), lg, 28) if len(lg) >= 28 + 4 * (n + 3) else ()
        print('=' * 110)
        print('0x%x  lodgroup=%d  nMesh=%d nStream=%d  hdr7=%s  n@20=%d'
              % (e['file_id'], len(lg), len(u.mesh_infos), len(u.stream_infos),
                 ['0x%x' % v for v in head], n))
        print('  offsets(+8 abs): %s' % [hex(o + 8) for o in offs])
        for m in u.mesh_infos:
            print('    mesh lod=%-2d stream=%d tf=%d meshid=0x%x nsec=%d'
                  % (m.lod_index, m.stream_index, m.transform_index, m.mesh_id,
                     m.num_sections))
        print('  words: %s' % words(lg))


if __name__ == '__main__':
    main()
