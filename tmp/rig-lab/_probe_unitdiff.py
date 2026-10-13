# -*- coding: utf-8 -*-
"""Diff the unit structures we inherited against the ones that actually ship.

Three artefacts matter:

  CHIFFON  E:\\mod\\【4.28】Chiffon-LUNALICE替换RE-2310\\9ba626afa44a3aa3.patch_0
           a *published, working* armour mod.  Its units are the ground truth
           for "a unit the game renders".
  MANAGER  E:\\mod\\Lunalice-Visible-Virtual-Model\\9ba626afa44a3aa3.patch_0
           the manager's re-homed copy of the same units -- never game tested.
  OURS     E:\\mod\\Lunalice-RigTest\\9ba626afa44a3aa3.patch_0

Question 1: is MANAGER's unit byte-identical to CHIFFON's (apart from the entry
            file id)?  If not, we built on an untested structure.
Question 2: which raw sections of OURS differ from the donor?
"""
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

CHIFFON = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
MANAGER = r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0'
OURS = r'E:\mod\Lunalice-RigTest\9ba626afa44a3aa3.patch_0'

PAIRS = [('0x9841b16544c8fbae', '0xd841b16544c8fbae'),   # Torso Stocky
         ('0xaf431352d9b95e83', '0xdf431352d9b95e83')]   # Head


def toc_of(path, fid):
    ents, _ = HP.scan_entries(path)
    for e in ents:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == fid:
            return HP.entry_payload(e, path), e
    return None, None


def dump(toc, tag):
    u = U.Unit.read(toc, b'')
    print('  %-8s version=%d header_data1=%d hdr2=%s' %
          (tag, u.version, u.header_data1, u.header_data2.hex()))
    print('           refs unk1=0x%x bones=0x%x comp=0x%x unk2=0x%x sm=0x%x hdrun=0x%x'
          % (u.unk_ref1, u.bones_ref, u.composite_ref, u.unk_ref2,
             u.state_machine_ref, u.header_unk))
    print('           offsets lod=0x%x tf=0x%x light=0x%x pre=0x%x wwise=0x%x '
          'cust=0x%x unkh=0x%x conn=0x%x bi=0x%x si=0x%x mesh=0x%x mat=0x%x end=0x%x'
          % (u.lod_group_list_data_offset, u.transform_info_offset,
             u.light_list_offset, u.pre_light_list_offset, u.wwise_callback_offset,
             u.customization_info_offset, u.unk_header_offset1,
             u.connecting_bone_hash_offset, u.bone_info_offset,
             u.stream_info_offset, u.mesh_info_offset, u.materials_offset,
             u.ending_offset))
    print('           blobs lod=%d cust=%d conn=%d unkhdr1=%d pre=%d wwise=%d '
          'unrev2=%d endbytes=%d lightcount=%d'
          % (len(u.lod_group_list_data), len(u.customization_data),
             len(u.connecting_bone_data), len(u.unk_header_data1),
             len(u.pre_light_list_data), len(u.wwise_callback_data),
             len(u.unreversed_data2), u.ending_bytes, u.light_count))
    print('           tf=%d bones(4x)=%s' %
          (u.transform_info.count, [b.num_bones for b in u.bone_infos]))
    print('           boneinfo0 real=%s remaps=%d offs=%s counts=%s'
          % (u.bone_infos[0].real_indices, u.bone_infos[0].num_remaps,
             u.bone_infos[0].remap_offsets, u.bone_infos[0].remap_counts))
    for i, s in enumerate(u.stream_infos):
        print('           s%d compid=0x%x ncomp=%d vbid=0x%x nv=%d stride=%d ibtype=%d '
              'ni=%d off=(%d,%d) size=(%d,%d)'
              % (i, s.component_info_id, len(s.components), s.vertex_buffer_id,
                 s.num_vertices, s.vertex_stride, s.index_buffer_type,
                 s.num_indices, s.vertex_buffer_offset, s.index_buffer_offset,
                 s.vertex_buffer_size, s.index_buffer_size))
    for i, m in enumerate(u.mesh_infos):
        print('           m%d lod=%d stream=%d tf=%d meshid=0x%x nmat=%d matids=%s '
              'nsec=%d secs=%s'
              % (i, m.lod_index, m.stream_index, m.transform_index, m.mesh_id,
                 len(m.material_ids), ['0x%x' % x for x in m.material_ids],
                 len(m.sections),
                 [(s.material_index, s.vertex_offset, s.num_vertices,
                   s.index_offset, s.num_indices, s.group_index) for s in m.sections][:3]))
    print('           unit secids=%s' % ['0x%x' % x for x in u.section_ids])
    print('           unit matids=%s' % ['0x%x' % x for x in u.material_ids])
    return u


print('=' * 78)
for a, b in PAIRS:
    pa, ea = toc_of(CHIFFON, int(a, 16))
    pb, eb = toc_of(MANAGER, int(b, 16))
    print('CHIFFON %s   vs   MANAGER %s' % (a, b))
    print('  toc len %d vs %d   identical=%s'
          % (len(pa['toc']), len(pb['toc']), pa['toc'] == pb['toc']))
    if pa['toc'] != pb['toc']:
        d = [i for i in range(min(len(pa['toc']), len(pb['toc'])))
             if pa['toc'][i] != pb['toc'][i]]
        print('  first differing bytes: %s ... total %d' % (d[:12], len(d)))
    print('  entry: chiffon unk=(%d,%d) gpu=%d | manager unk=(%d,%d) gpu=%d'
          % (ea['unknown1'], ea['unknown2'], ea['gpu_size'],
             eb['unknown1'], eb['unknown2'], eb['gpu_size']))
    dump(pa['toc'], 'CHIF')
    dump(pb['toc'], 'MGR')
    print('-' * 78)

print('=' * 78)
print('OURS vs the MANAGER donor')
po, eo = toc_of(OURS, 0xAF431352D9B95E83)
pm, em = toc_of(MANAGER, 0xDF431352D9B95E83)
dump(pm['toc'], 'MGR')
dump(po['toc'], 'OURS')
print('  gpu: manager %d  ours %d' % (len(pm['gpu']), len(po['gpu'])))
