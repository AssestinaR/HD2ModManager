# -*- coding: utf-8 -*-
"""Where exactly do our units diverge from the donor, and is each divergence legal?

Checks, in order of how likely each is to explain "transparent + garbage shadow":

  1. the LOD group list -- we rewrote records 3/4 of it
  2. TransformInfo `locals` for the 107 appended bones (the SDK builds its bone
     hierarchy from `locals`, so zeros there = every bone at the origin)
  3. `mesh_infos[i].transform_index` vs the LOD's mesh transform
  4. section vertex/index offsets (the donor packs 3 LODs into stream 1)
  5. the material slot table lengths
"""
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

MANAGER = r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0'
OURS = r'E:\mod\Lunalice-RigTest\9ba626afa44a3aa3.patch_0'
AVATAR = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')


def unit_of(path, fid):
    ents, _ = HP.scan_entries(path)
    e = next(x for x in ents if x['type_id'] == U.UNIT_ID and x['file_id'] == fid)
    pl = HP.entry_payload(e, path)
    return U.Unit.read(pl['toc'], pl['gpu']), pl


ref, rpl = unit_of(MANAGER, 0xDF431352D9B95E83)
our, opl = unit_of(OURS, 0xAF431352D9B95E83)
raw = open(AVATAR, 'rb').read()
av = U.Unit.read(raw, b'')


def words(b, n=None):
    n = n if n is not None else len(b) // 4
    return ['%08x' % v for v in struct.unpack_from('<%dI' % n, b, 0)]


print('=' * 78)
print('1. LOD GROUP LIST  (ref %d B, ours %d B)'
      % (len(ref.lod_group_list_data), len(our.lod_group_list_data)))
a, b = ref.lod_group_list_data, our.lod_group_list_data
diff = [i for i in range(min(len(a), len(b))) if a[i] != b[i]]
print('   differing bytes: %d  %s' % (len(diff), diff[:40]))
print('   ref  :', ' '.join(words(a)))
print('   ours :', ' '.join(words(b)))

print()
print('   as floats (ref):')
print('        ', np.round(np.frombuffer(a, np.float32), 6))
print('   as floats (ours):')
print('        ', np.round(np.frombuffer(b, np.float32), 6))

print()
print('=' * 78)
print('2. TransformInfo locals for the appended 107 bones')
bad_loc = 0
for k in range(107):
    lo_o = our.transform_info.locals[23 + k]
    lo_a = av.transform_info.locals[k]
    same = all(abs(x - y) < 1e-12 for x, y in
               zip(lo_o['rot'] + lo_o['pos'] + lo_o['scale'] + lo_o['dummy'],
                   lo_a['rot'] + lo_a['pos'] + lo_a['scale'] + lo_a['dummy']))
    if not same:
        bad_loc += 1
print('   appended bones whose locals != avatar locals: %d / 107' % bad_loc)
print('   our locals[23] (avatar bone 0) = %s' % our.transform_info.locals[23])
print('   av  locals[0]                  = %s' % av.transform_info.locals[0])
print('   our locals[30] (avatar bone 7) = %s' % our.transform_info.locals[30])
print('   av  locals[7]                  = %s' % av.transform_info.locals[7])
print('   our entries[23..25] = %s' % our.transform_info.entries[23:26])
print('   av  entries[0..2]   = %s' % av.transform_info.entries[0:3])
print('   our hashes[23..25]  = %s' % ['0x%08x' % h for h in our.transform_info.hashes[23:26]])
print('   av  hashes[0..2]    = %s' % ['0x%08x' % h for h in av.transform_info.hashes[0:3]])

print()
print('=' * 78)
print('3. mesh transform indices / LOD streams')
for tag, u in (('REF', ref), ('OURS', our)):
    print('   %s' % tag)
    for i, m in enumerate(u.mesh_infos):
        print('      m%d lod=%d stream=%d tf=%d nmat=%d nsec=%d' %
              (i, m.lod_index, m.stream_index, m.transform_index,
               len(m.material_ids), len(m.sections)))
    for i, s in enumerate(u.stream_infos):
        print('      s%d nv=%d ni=%d ibtype=%d vb=(%d,%d) ib=(%d,%d)' %
              (i, s.num_vertices, s.num_indices, s.index_buffer_type,
               s.vertex_buffer_offset, s.vertex_buffer_size,
               s.index_buffer_offset, s.index_buffer_size))

print()
print('=' * 78)
print('4. section offsets')
for tag, u in (('REF', ref), ('OURS', our)):
    print('   %s' % tag)
    for i, m in enumerate(u.mesh_infos[:4]):
        print('      m%d(lod%d) %s' % (i, m.lod_index,
              [(s.material_index, s.vertex_offset, s.num_vertices,
                s.index_offset, s.num_indices) for s in m.sections]))

print()
print('=' * 78)
print('5. material slot tables')
print('   REF  unit: %d secids / %d matids' % (len(ref.section_ids), len(ref.material_ids)))
print('   OURS unit: %d secids / %d matids' % (len(our.section_ids), len(our.material_ids)))
print('   OURS m0 matids = %s' % ['0x%x' % x for x in our.mesh_infos[0].material_ids])
print('   OURS m0 nsec   = %d' % len(our.mesh_infos[0].sections))
print('   OURS unit secids head = %s' % ['0x%x' % x for x in our.section_ids[:20]])
print('   OURS unit matids head = %s' % ['0x%x' % x for x in our.material_ids[:20]])
