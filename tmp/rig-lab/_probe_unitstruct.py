# -*- coding: utf-8 -*-
"""Dump a real Unit from a working patch so the writer knows every field.

usage: _probe_unitstruct.py [patchdir] [unit_hex]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P
import hd2_unit as U

PATCHDIR = sys.argv[1] if len(sys.argv) > 1 else 'E:/mod/Lunalice-Visible-Virtual-Model'
WANT = sys.argv[2] if len(sys.argv) > 2 else 'dae35ee15168145f'

tocs = [os.path.join(PATCHDIR, f) for f in os.listdir(PATCHDIR)
        if '.patch_' in f and not f.endswith(('.stream', '.gpu_resources'))]
toc_path = tocs[0]
entries, _raw = P.scan_entries(toc_path)
units = [e for e in entries if e['type_id'] == U.UNIT_ID]
print('patch %s : %d entries, %d units' % (os.path.basename(toc_path), len(entries), len(units)))

tgt = None
for e in units:
    if '%016x' % e['file_id'] == WANT:
        tgt = e
if tgt is None:
    tgt = units[0]
    print('unit %s not found, using first' % WANT)
print('unit 0x%016x  toc %d B  gpu %d B  stream %d B'
      % (tgt['file_id'], tgt['toc_size'], tgt['gpu_size'], tgt['stream_size']))

pl = P.entry_payload(tgt, toc_path)
u = U.Unit.read(pl['toc'], pl['gpu'])

print('\n== header ==')
print('  version %d   header_data1 %d   header_unk 0x%x' % (u.version, u.header_data1, u.header_unk))
print('  bones_ref 0x%016x   composite_ref 0x%016x   state_machine_ref 0x%016x'
      % (u.bones_ref, u.composite_ref, u.state_machine_ref))
print('  unk_ref1 0x%016x  unk_ref2 0x%016x' % (u.unk_ref1, u.unk_ref2))
print('  header_data2 %s' % u.header_data2.hex())
for nm in ('lod_group_list_data_offset', 'transform_info_offset', 'light_list_offset',
           'pre_light_list_offset', 'wwise_callback_offset', 'customization_info_offset',
           'unk_header_offset1', 'connecting_bone_hash_offset', 'bone_info_offset',
           'stream_info_offset', 'ending_offset', 'mesh_info_offset', 'materials_offset'):
    print('  %-28s 0x%06x (%d)' % (nm, getattr(u, nm), getattr(u, nm)))

print('\n== raw blobs ==')
print('  lod_group_list_data   %d B  %s' % (len(u.lod_group_list_data), u.lod_group_list_data[:48].hex()))
print('  customization_data    %d B  %s' % (len(u.customization_data), u.customization_data[:48].hex()))
print('  unk_header_data1      %d B  %s' % (len(u.unk_header_data1), u.unk_header_data1[:48].hex()))
print('  connecting_bone_data  %d B  %s' % (len(u.connecting_bone_data), u.connecting_bone_data[:64].hex()))
print('  pre_light_list_data   %d B' % len(u.pre_light_list_data))
print('  wwise_callback_data   %d B' % len(u.wwise_callback_data))
print('  unreversed_data2      %d B  %s' % (len(u.unreversed_data2), u.unreversed_data2[:48].hex()))
print('  ending_bytes          %d' % u.ending_bytes)
print('  lights                %d   light_unk0 %s' % (u.light_count, u.light_unk0))

ti = u.transform_info
print('\n== transform info ==  count %d  unk %s' % (ti.count, ti.unk.hex()))
for i in range(min(ti.count, 6)):
    m = ti.matrices[i]
    print('  [%2d] hash 0x%08x parent %3d  pos (%+.4f %+.4f %+.4f)  incr %d'
          % (i, ti.hashes[i], ti.entries[i][1], m[3], m[7], m[11], ti.entries[i][0]))
print('  ... last: hash 0x%08x parent %d pos (%+.4f %+.4f %+.4f)'
      % (ti.hashes[-1], ti.entries[-1][1], ti.matrices[-1][3], ti.matrices[-1][7], ti.matrices[-1][11]))

print('\n== bone infos (%d) ==' % len(u.bone_infos))
for i, bi in enumerate(u.bone_infos):
    print('  [%d] num_bones %d  num_remaps %d  remap_counts %s'
          % (i, bi.num_bones, bi.num_remaps, bi.remap_counts[:8]))
    print('      real_indices[:12] %s' % bi.real_indices[:12])
    if bi.remaps:
        print('      remaps[0][:12] %s' % bi.remaps[0][:12])
    print('      bones[0] %s' % [round(v, 4) for v in bi.bones[0][:8]])
    print('      bones[1] %s' % [round(v, 4) for v in bi.bones[1][:8]])

print('\n== stream infos (%d) ==' % len(u.stream_infos))
for i, si in enumerate(u.stream_infos):
    print('  [%d] comp_info_id 0x%x  ncomp %d  stride %d  nverts %d  nindices %d  ib_type %d'
          % (i, si.component_info_id, len(si.components), si.vertex_stride,
             si.num_vertices, si.num_indices, si.index_buffer_type))
    print('      vb_id 0x%x unk1 0x%x unk2 0x%x unk3 0x%x' % (si.vertex_buffer_id, si.vertex_buffer_unk1,
                                                            si.vertex_buffer_unk2, si.vertex_buffer_unk3))
    print('      ib_id 0x%x unk1 0x%x unk2 0x%x unk3 0x%x' % (si.index_buffer_id, si.index_buffer_unk1,
                                                            si.index_buffer_unk2, si.index_buffer_unk3))
    print('      vb off/size %d/%d   ib off/size %d/%d   unk_ending %s'
          % (si.vertex_buffer_offset, si.vertex_buffer_size,
             si.index_buffer_offset, si.index_buffer_size, si.unk_ending.hex()))
    for c in si.components:
        print('        comp type %d (%-10s) fmt %d (%-14s) index %d unknown 0x%x size %d'
              % (c.type, U.TYPE_NAME.get(c.type, '?'), c.format,
                 U.format_name(c.format, u.version), c.index, c.unknown, c.size(u.version)))

print('\n== mesh infos (%d) ==' % len(u.mesh_infos))
for i, mi in enumerate(u.mesh_infos):
    print('  [%d] mesh_id 0x%x  transform_index %d  lod_index %d  stream_index %d'
          % (i, mi.mesh_id, mi.transform_index, mi.lod_index, mi.stream_index))
    print('      unk1 %d unk2 %s' % (mi.unk1, mi.unk2.hex()))
    print('      unk3 %d unk4 %d unk8 %d  nmat %d nsec %d  mat_off %d sec_off %d'
          % (mi.unk3, mi.unk4, mi.unk8, mi.num_materials, mi.num_sections,
             mi.material_offset, mi.sections_offset))
    print('      unk6 %s' % mi.unk6.hex())
    print('      material_ids %s' % ['0x%x' % m for m in mi.material_ids])
    for s in mi.sections[:4]:
        print('        sec mat_idx %d vtx_off %d nvtx %d idx_off %d nidx %d grp %d'
              % (s.material_index, s.vertex_offset, s.num_vertices, s.index_offset,
                 s.num_indices, s.group_index))

print('\n== materials ==')
print('  section_ids %s' % u.section_ids)
print('  material_ids %s' % ['0x%x' % m for m in u.material_ids])

if pl['gpu']:
    print('\n== gpu payload ==')
    for i, si in enumerate(u.stream_infos):
        print('  stream %d  vb %d..%d  ib %d..%d'
              % (i, si.vertex_buffer_offset, si.vertex_buffer_offset + si.vertex_buffer_size,
                 si.index_buffer_offset, si.index_buffer_offset + si.index_buffer_size))
    v0 = u.decode_vertices(0)
    print('  first 3 decoded vertices:')
    for r in v0[:3]:
        print('    %s' % {k: (v if not isinstance(v, list) else [round(x, 4) for x in v])
                          for k, v in r.items()})
    idx = u.index_list(0)
    print('  first 9 indices: %s   max index %d' % (idx[:9], max(idx) if idx else -1))

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out', 'unitstruct.json')
os.makedirs(os.path.dirname(out), exist_ok=True)
json.dump(dict(
    unit='%016x' % tgt['file_id'], version=u.version,
    bones_ref=u.bones_ref, state_machine_ref=u.state_machine_ref, composite_ref=u.composite_ref,
    header_data1=u.header_data1, header_unk=u.header_unk, header_data2=u.header_data2.hex(),
    offsets={nm: getattr(u, nm) for nm in (
        'lod_group_list_data_offset', 'transform_info_offset', 'light_list_offset',
        'pre_light_list_offset', 'wwise_callback_offset', 'customization_info_offset',
        'unk_header_offset1', 'connecting_bone_hash_offset', 'bone_info_offset',
        'stream_info_offset', 'ending_offset', 'mesh_info_offset', 'materials_offset')},
    blob_sizes={nm: len(getattr(u, nm)) for nm in (
        'lod_group_list_data', 'customization_data', 'unk_header_data1',
        'connecting_bone_data', 'pre_light_list_data', 'wwise_callback_data', 'unreversed_data2')},
    transform_count=ti.count,
    stream_infos=[dict(ncomp=len(s.components), stride=s.vertex_stride, nverts=s.num_vertices,
                       nindices=s.num_indices, ib_type=s.index_buffer_type,
                       components=[(c.type, c.format, c.index) for c in s.components])
                  for s in u.stream_infos],
    mesh_infos=[dict(mesh_id=mi.mesh_id, lod=mi.lod_index, stream=mi.stream_index,
                     nsec=mi.num_sections, mats=['0x%x' % m for m in mi.material_ids])
                for mi in u.mesh_infos],
    section_ids=u.section_ids, material_ids=['0x%x' % m for m in u.material_ids],
), open(out, 'w'), indent=1)
print('\n[written] %s' % out)
