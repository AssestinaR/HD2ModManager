# -*- coding: utf-8 -*-
"""Read-only: compare the Chiffon structure donor with a real GameData carrier.

We are switching the single-unit package from a made-up id to a **native** id
(the DS-10 torso unit `0xa1a7563e6ac8b8f8`) so that (a) the game resolves the
unit without the Lua registry and (b) the manager CLI can label the mod as
equipment.  This probe only *reads*; it answers:

  * does the carrier carry the same 0x80-byte Unit header layout?
  * what exactly is in its 240-byte `customization_data` (vs the donor's)?
  * how do the TransformInfo / MeshInfo / Stream tables differ?

usage: _probe_nativecarrier.py
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

CARRIER = r'E:\tmp\hd2-mgroot\orig\unit_0xa1a7563e6ac8b8f8.toc'
DONOR_PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
DONOR_UNIT = 0x9841B16544C8FBAE


def read_unit_toc(path):
    return U.Unit.read(open(path, 'rb').read(), b'')


def read_patch_unit(path, key):
    entries, _ = P.scan_entries(path)
    for e in entries:
        if e['file_id'] == key and e['type_id'] == U.UNIT_ID:
            return U.Unit.read(P.entry_payload(e, path)['toc'], b'')
    raise SystemExit('unit 0x%x not found in %s' % (key, path))


def dump_customization(u, label):
    cd = u.customization_data or b''
    print('  %s customization_data: %d bytes' % (label, len(cd)))
    if not cd:
        return
    print('    head: %s' % cd[:16].hex(' '))
    # u32 0xE4 | u32 8 | u32 0 | u32 0xE0A22D09 | (u32 len, u32 3, u32 len, str) x4
    off = 16
    for i in range(4):
        if off + 12 > len(cd):
            break
        a, b, n = struct.unpack_from('<III', cd, off)
        s = cd[off + 12:off + 12 + n]
        print('    entry %d: a=%d b=%d len=%d text=%r' % (i, a, b, n, s))
        off += 12 + n
    print('    tail: %s' % cd[off:off + 16].hex(' '))


def dump_unit(u, label):
    print('=' * 78)
    print('%s  version=0x%08x (dec %d)' % (label, u.version, u.version))
    print('  header: lod=%#x tf=%#x light=%#x pre=%#x wwise=%#x cust=%#x unkh=%#x '
          'conn=%#x bi=%#x si=%#x mesh=%#x mat=%#x end=%#x'
          % (u.lod_group_list_data_offset, u.transform_info_offset, u.light_list_offset,
             u.pre_light_list_offset, u.wwise_callback_offset, u.customization_info_offset,
             u.unk_header_offset1, u.connecting_bone_hash_offset, u.bone_info_offset,
             u.stream_info_offset, u.mesh_info_offset, u.materials_offset, u.ending_offset))
    ti = u.transform_info
    ident = [i for i, m in enumerate(ti.matrices)
             if all(abs(m[j] - (1.0 if j in (0, 5, 10, 15) else 0.0)) < 1e-6 for j in range(16))]
    print('  TransformInfo: count=%d identity_slots=%s' % (ti.count, ident[:16]))
    print('  BoneInfo: %s' % [dict(bones=b.num_bones, remaps=b.num_remaps) for b in u.bone_infos])
    print('  Streams:')
    for i, s in enumerate(u.stream_infos):
        print('    s%d nv=%d ni=%d stride=%d ibtype=%d comps=%s'
              % (i, s.num_vertices, s.num_indices, s.vertex_stride, s.index_buffer_type,
                 [(c.type, c.format, c.index) for c in s.components]))
    print('  Meshes:')
    for mi in u.mesh_infos:
        print('    idx? mesh_id=0x%08x tf=%d unk3=%d lod=%d stream=%d nmat=%d nsec=%d '
              'unk2[0:7]=%s'
              % (mi.mesh_id, mi.transform_index, mi.unk3, mi.lod_index, mi.stream_index,
                 len(mi.material_ids), len(mi.sections),
                 [round(x, 4) for x in struct.unpack_from('<7f', mi.unk2, 0)]))
    print('  section_ids (%d): %s' % (len(u.section_ids), ['%x' % x for x in u.section_ids[:24]]))
    print('  material_ids(%d): %s' % (len(u.material_ids), ['%016x' % x for x in u.material_ids[:24]]))
    dump_customization(u, label)


def main():
    carrier = read_unit_toc(CARRIER)
    donor = read_patch_unit(DONOR_PATCH, DONOR_UNIT)
    dump_unit(carrier, 'CARRIER 0xa1a7563e6ac8b8f8 (DS-10 Torso/Armor/Any)')
    dump_unit(donor, 'DONOR   0x9841b16544c8fbae (Chiffon Torso/Stocky)')


if __name__ == '__main__':
    main()
