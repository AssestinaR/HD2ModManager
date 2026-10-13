"""Dump every Unit in the reference patch: header offsets, sizes, meshes, streams.

This is the baseline our Python Unit writer must reproduce byte-for-byte when
we re-serialize an untouched unit.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU

REF = r'E:\mod\【4.28】Chiffon-LUNALICE替换-re-2310\9ba626afa44a3aa3.patch_0'
if not os.path.exists(REF):
    import glob
    cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
    REF = [c for c in cand if 'Chiffon' in c or '4.28' in c][0]

FIELDS = ['lod_group_list_data_offset', 'transform_info_offset', 'light_list_offset',
          'pre_light_list_offset', 'wwise_callback_offset', 'customization_info_offset',
          'unk_header_offset1', 'connecting_bone_hash_offset', 'bone_info_offset',
          'stream_info_offset', 'ending_offset', 'mesh_info_offset', 'materials_offset']


def main():
    entries, _ = HP.scan_entries(REF)
    units = [e for e in entries if e['type_id'] == HP.UNIT_TYPE_ID]
    print('patch %s' % os.path.basename(REF))
    print('units: %d' % len(units))
    for e in units:
        pay = HP.entry_payload(e, REF)
        toc = pay['toc']
        try:
            u = HU.Unit.read(toc, pay['gpu'])
        except Exception as ex:
            print('  0x%016x  READ FAIL %s' % (e['file_id'], ex))
            continue
        print('-' * 110)
        print('0x%016x  toc=%d gpu=%d  hdr1=%d ver=%d tf=%d lights=%d prelight=%d wwise=%d cust=%d unkhdr1=%d conn=%d'
              % (e['file_id'], len(toc), len(pay['gpu']), u.header_data1, u.version,
                 len(u.transform_info.matrices), u.light_count, len(u.pre_light_list_data),
                 len(u.wwise_callback_data), len(u.customization_data), len(u.unk_header_data1),
                 len(u.connecting_bone_data)))
        print('   lodgroup=%d nStream=%d nMesh=%d nBoneInfo=%d nMat=%d end=%d unrev2=%d endingBytes=%d'
              % (len(u.lod_group_list_data), len(u.stream_infos), len(u.mesh_infos),
                 len(u.bone_infos), len(u.material_ids), u.ending_offset,
                 len(u.unreversed_data2), u.ending_bytes))
        print('   refs unk1=0x%x bones=0x%x unk2=0x%x sm=0x%x headerUnk=0x%x'
              % (u.unk_ref1, u.bones_ref, u.unk_ref2, u.state_machine_ref, u.header_unk))
        for i, si in enumerate(u.stream_infos):
            print('     s%d vtx=%d strd=%d ibtype=%d idx=%d vbo=%d vbs=%d ibo=%d ibs=%d comps=%s'
                  % (i, si.num_vertices, si.vertex_stride, si.index_buffer_type, si.num_indices,
                     si.vertex_buffer_offset, si.vertex_buffer_size,
                     si.index_buffer_offset, si.index_buffer_size,
                     [(c.type, c.index) for c in si.components]))
        for mi, m in enumerate(u.mesh_infos):
            print('     m%d lod=%s stream=%d tf=%d meshid=0x%x nmat=%d nsec=%d secs=%s'
                  % (mi, m.lod_index, m.stream_index, m.transform_index, m.mesh_id,
                     m.num_materials, m.num_sections,
                     [(s.material_index, s.vertex_offset, s.num_vertices, s.index_offset, s.num_indices, s.group_index)
                      for s in m.sections]))
        for bi, b in enumerate(u.bone_infos):
            print('     b%d bones=%d remaps=%d counts=%s offs=%s real=%s remap0=%s'
                  % (bi, b.num_bones, b.num_remaps, b.remap_counts, b.remap_offsets,
                     b.real_indices, b.remaps[0] if b.remaps else None))
        print('   matids=%s' % [hex(x) for x in u.material_ids])
        print('   secids=%s' % [hex(x) for x in u.section_ids])
        print('   tf hashes=%s' % ['0x%x' % h for h in u.transform_info.hashes])
        print('   tf parents=%s' % u.transform_info.parents)


if __name__ == '__main__':
    main()
