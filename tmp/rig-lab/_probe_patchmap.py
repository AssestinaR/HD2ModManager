# -*- coding: utf-8 -*-
"""What every unit in the reference patch looks like, at the level the writer needs.

Answers, for all 23 units of the reference patch:
  * header_data1 / version / bones_ref / composite_ref / state_machine_ref
  * how many transforms, streams, meshes, materials
  * the first four transforms (entity root / axis convert / unnamed / game_mesh)
    and whether their matrices are identity -- i.e. whether a unit that carries the
    WHOLE character can be dropped in at the unit's own origin.
  * the raw sizes of the sections we intend to copy verbatim
    (lod group list, pre-light, light, customization, unk header, connecting bone)
  * which units carry 0 vertices (proof that an "empty" unit is legal)
usage: _probe_patchmap.py
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch
import hd2_unit

PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
GPU = PATCH + '.gpu_resources'

entries, _raw = hd2_patch.scan_entries(PATCH)
print('patch entries: %d' % len(entries))

types = {}
for e in entries:
    types.setdefault(e['type_id'], []).append(e)
for t, lst in types.items():
    print('  type 0x%016X : %d' % (t, len(lst)))

units = [e for e in entries if e['type_id'] == hd2_unit.UNIT_ID]
print('\nunits: %d' % len(units))

gpu = open(GPU, 'rb').read()
rows = []
for e in units:
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    u = hd2_unit.Unit.read(toc, gpu)
    ti = u.transform_info
    nverts = sum(si.num_vertices for si in u.stream_infos)
    ntris = sum(si.num_indices // 3 for si in u.stream_infos)
    rows.append(dict(fid=e['file_id'], toc=len(toc), hdr1=u.header_data1,
                     ver=u.version, bones_ref=u.bones_ref,
                     comp_ref=u.composite_ref, sm_ref=u.state_machine_ref,
                     ntf=ti.count, nstream=len(u.stream_infos), nmesh=len(u.mesh_infos),
                     nmat=len(u.material_ids), nverts=nverts, ntris=ntris,
                     nboneinfo=len(u.bone_infos),
                     lodg=len(u.lod_group_list_data),
                     prelight=len(u.pre_light_list_data),
                     light=len(u.lights), wwise=len(u.wwise_callback_data),
                     custom=len(u.customization_data),
                     unk1=len(u.unk_header_data1),
                     connect=len(u.connecting_bone_data),
                     end_unk=len(u.unreversed_data2), end_bytes=u.ending_bytes))

print('\n%-18s %6s %4s %3s %3s %3s %3s %7s %7s %5s %5s %5s %5s %5s %5s %5s %5s'
      % ('fileid', 'toc', 'hd1', 'ntf', 'str', 'msh', 'mat', 'verts', 'tris',
         'lodg', 'preL', 'lit', 'wwis', 'cust', 'unk1', 'conn', 'end'))
for r in sorted(rows, key=lambda r: -r['nverts']):
    print('%-18s %6d %4d %3d %3d %3d %3d %7d %7d %5d %5d %5d %5d %5d %5d %5d %5d'
          % ('0x%016x' % r['fid'], r['toc'], r['hdr1'], r['ntf'], r['nstream'],
             r['nmesh'], r['nmat'], r['nverts'], r['ntris'], r['lodg'], r['prelight'],
             r['light'], r['wwise'], r['custom'], r['unk1'], r['connect'], r['end_unk']))

print('\nheader bits that are not always 0:')
for key in ('bones_ref', 'comp_ref', 'sm_ref', 'end_bytes'):
    vals = sorted({r[key] for r in rows})
    print('  %-10s %s' % (key, ['0x%x' % v for v in vals]))
print('  hdr1       %s' % sorted({r['hdr1'] for r in rows}))
print('  version    %s' % sorted({r['ver'] for r in rows}))

print('\nzero-vertex units: %s'
      % [('0x%016x' % r['fid']) for r in rows if r['nverts'] == 0])

# ---- the transform chain of the biggest unit --------------------------------
big = max(rows, key=lambda r: r['nverts'])
e = next(x for x in units if x['file_id'] == big['fid'])
toc = hd2_patch.entry_payload(e, PATCH)['toc']
u = hd2_unit.Unit.read(toc, gpu)
ti = u.transform_info
print('\n=== the biggest unit 0x%016x : %d transforms ===' % (big['fid'], ti.count))
for i in range(min(ti.count, 30)):
    m = ti.matrices[i]
    ident = all(abs(m[j * 4 + k] - (1.0 if j == k else 0.0)) < 1e-9
                for j in range(4) for k in range(4))
    inc, par = ti.entries[i]
    print('  %2d hash %-11d parent %-3d inc %-2d %s  T=(%.4f,%.4f,%.4f)'
          % (i, ti.hashes[i], par, inc, 'IDENTITY' if ident else '        ',
             m[12], m[13], m[14]))

print('\n=== materials of that unit ===')
for sid, mid in zip(u.section_ids, u.material_ids):
    print('  section %-10d material 0x%016x' % (sid, mid))

print('\n=== streams / meshes ===')
for i, si in enumerate(u.stream_infos):
    print('  stream %d: %d verts stride %d  comps %s'
          % (i, si.num_vertices, si.vertex_stride,
             [(hd2_unit.TYPE_NAME.get(c.type, c.type), c.index,
               hd2_unit.format_name(c.format, u.version)) for c in si.components]))
    print('            vb off %d size %d   ib off %d size %d  ibtype %d'
          % (si.vertex_buffer_offset, si.vertex_buffer_size,
             si.index_buffer_offset, si.index_buffer_size, si.index_buffer_type))
for i, mi in enumerate(u.mesh_infos):
    print('  mesh %d: lod %d stream %d transform %d meshid %d sections %d mats %d'
          % (i, mi.lod_index, mi.stream_index, mi.transform_index, mi.mesh_id,
             mi.num_sections, mi.num_materials))
    for s in mi.sections:
        print('        sec mat %d vtx %d+%d idx %d+%d grp %d'
              % (s.material_index, s.vertex_offset, s.num_vertices,
                 s.index_offset, s.num_indices, s.group_index))

print('\n=== BoneInfo of that unit ===')
for bi in u.bone_infos:
    print('  num_bones %d remaps %d counts %s offsets %s'
          % (bi.num_bones, bi.num_remaps, bi.remap_counts, bi.remap_offsets))
    print('  real_indices %s' % bi.real_indices)
    for r in bi.remaps:
        print('  remap %s' % r)
