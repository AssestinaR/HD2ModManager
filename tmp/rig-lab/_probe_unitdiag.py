# -*- coding: utf-8 -*-
"""Full diagnostic dump for the "unit is invisible in game" investigation.

Answers, with numbers:

  A. The reference unit's own 23-node TransformInfo: is it a partial *skeleton*
     whose node hashes also exist in the avatar rig?  For each overlapping hash,
     do the reference's locals/matrices equal the avatar's?
  B. How many of the avatar's 107 bones are missing from the reference unit and
     therefore have to be appended?
  C. What locals/entries does the reference use for its bones (local offset vs
     world matrix)?
  D. Reference vs ours: per-mesh transform_index / lod / stream, section
     offsets, material slot counts.
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

REF_TORSO = 0xD841B16544C8FBAE          # manager-reissued id of the Torso/Stocky donor
OUR_HEAD = 0xAF431352D9B95E83           # ours, Head/Any/slot0


def unit_of(path, fid):
    ents, _ = HP.scan_entries(path)
    e = next(x for x in ents if x['type_id'] == U.UNIT_ID and x['file_id'] == fid)
    pl = HP.entry_payload(e, path)
    return U.Unit.read(pl['toc'], pl['gpu'])


def is_ident(m):
    return bool(np.allclose(np.asarray(m, float).reshape(4, 4), np.eye(4), atol=1e-6))


def t_of(m):
    return np.asarray(m, float).reshape(4, 4)[3, :3]


def loc_tuple(lo):
    return tuple(lo['rot'] + lo['pos'] + lo['scale'] + lo['dummy'])


print('=' * 96)
print('A. REFERENCE UNIT TransformInfo  (0x%x, donor for structure)' % REF_TORSO)
ref = unit_of(MANAGER, REF_TORSO)
t = ref.transform_info
print('   count=%d' % t.count)
for i in range(t.count):
    print('   [%2d] hash=0x%08x entry=(inc=%d par=%d) ident=%-5s T=%s  localpos=%s'
          % (i, t.hashes[i], t.entries[i][0], t.entries[i][1], is_ident(t.matrices[i]),
             np.round(t_of(t.matrices[i]), 5), np.round(t.locals[i]['pos'], 5)))

print()
print('=' * 96)
print('B. AVATAR RIG TransformInfo')
raw = open(AVATAR, 'rb').read()
ti = U.TransformInfo.read(U.Buf(raw), struct.unpack_from('<I', raw, 0x34)[0])
print('   count=%d  unk=%s' % (ti.count, ti.unk.hex()))
av_by_hash = {h: i for i, h in enumerate(ti.hashes)}
ref_by_hash = {h: i for i, h in enumerate(t.hashes)}
print('   identity matrices at indices: %s'
      % [i for i in range(ti.count) if is_ident(ti.matrices[i])])
print('   roots (entry par == self): %s'
      % [i for i in range(ti.count) if ti.entries[i][1] == i])
print('   zero-local-pos bones: %s'
      % [i for i in range(ti.count) if max(abs(v) for v in ti.locals[i]['pos']) < 1e-9])

overlap = [h for h in t.hashes if h in av_by_hash]
print()
print('   overlap: %d of the reference unit\'s %d node hashes also exist in the avatar'
      % (len(overlap), t.count))
print('   --- for each overlapping hash, do ref locals/matrices equal avatar\'s? ---')
n_loc_diff = n_mat_diff = n_entry_diff = 0
for h in overlap:
    ri, ai = ref_by_hash[h], av_by_hash[h]
    same_loc = max(abs(a - b) for a, b in zip(loc_tuple(t.locals[ri]), loc_tuple(ti.locals[ai]))) < 1e-9
    same_mat = np.allclose(np.asarray(t.matrices[ri], float), np.asarray(ti.matrices[ai], float),
                           atol=1e-6)
    same_par = t.entries[ri][1] == ti.entries[ai][1]
    n_loc_diff += (not same_loc)
    n_mat_diff += (not same_mat)
    n_entry_diff += (not same_par)
    print('   hash=0x%08x ref[%2d] av[%3d]  locals %-5s matrices %-5s parent %-5s | '
          'ref par=%-3d av par=%-3d'
          % (h, ri, ai, 'same' if same_loc else 'DIFF', 'same' if same_mat else 'DIFF',
             'same' if same_par else 'DIFF', t.entries[ri][1], ti.entries[ai][1]))
print('   totals: locals differ %d, matrices differ %d, parent differs %d'
      % (n_loc_diff, n_mat_diff, n_entry_diff))

missing = [i for i in range(ti.count) if ti.hashes[i] not in ref_by_hash]
print()
print('   avatar bones MISSING from the reference unit: %d  (indices %s%s)'
      % (len(missing), missing[:12], ' ...' if len(missing) > 12 else ''))

print()
print('=' * 96)
print('C. REFERENCE bone nodes: local offset vs world matrix (the "locals are local" proof)')
for i in range(t.count):
    if is_ident(t.matrices[i]):
        continue
    print('   [%2d] hash=0x%08x  locals.pos=%s  matrix.T=%s'
          % (i, t.hashes[i], np.round(t.locals[i]['pos'], 6), np.round(t_of(t.matrices[i]), 6)))
print('   avatar[11] hash=0x%08x locals.pos=%s matrix.T=%s'
      % (ti.hashes[11], np.round(ti.locals[11]['pos'], 6), np.round(t_of(ti.matrices[11]), 6)))

print()
print('=' * 96)
print('D. mesh / stream / section layout')
our = unit_of(OURS, OUR_HEAD)
for tag, u in (('REF', ref), ('OURS', our)):
    print('   --- %s ---' % tag)
    print('      bone_infos=%d  streams=%d  meshes=%d  secids=%d  matids=%d'
          % (len(u.bone_infos), len(u.stream_infos), len(u.mesh_infos),
             len(u.section_ids), len(u.material_ids)))
    for i, s in enumerate(u.stream_infos):
        print('      s%d nv=%d ni=%d stride=%d ibtype=%d vb=(%d,%d) ib=(%d,%d)'
              % (i, s.num_vertices, s.num_indices, s.vertex_stride, s.index_buffer_type,
                 s.vertex_buffer_offset, s.vertex_buffer_size,
                 s.index_buffer_offset, s.index_buffer_size))
    for i, m in enumerate(u.mesh_infos):
        print('      m%d lod=%-2d stream=%d tf=%2d unk3=%2d nmat=%d nsec=%d'
              % (i, m.lod_index, m.stream_index, m.transform_index, m.unk3,
                 len(m.material_ids), len(m.sections)))
        if m.lod_index >= 0:
            print('           secs %s' % [(s.material_index, s.vertex_offset, s.num_vertices,
                                           s.index_offset, s.num_indices, s.group_index)
                                          for s in m.sections][:6])
    for bi, b in enumerate(u.bone_infos[:1]):
        print('      boneinfo[%d] num_bones=%d remaps=%d real=%s' % (bi, b.num_bones, b.num_remaps,
                                                                     list(b.real_indices)[:14]))
    print('      tf count=%d  identity indices=%s'
          % (u.transform_info.count,
             [i for i in range(u.transform_info.count) if is_ident(u.transform_info.matrices[i])]))
