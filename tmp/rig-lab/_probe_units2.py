"""Compact per-unit table for the reference patch: streams, meshes and a
decode sanity check on the first vertex of each stream.

usage: _probe_units2.py
"""
import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU

cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
REF = [c for c in cand if 'Chiffon' in c or '4.28' in c][0]


def main():
    entries, _ = HP.scan_entries(REF)
    units = [e for e in entries if e['type_id'] == HP.UNIT_TYPE_ID]
    for e in units:
        pay = HP.entry_payload(e, REF)
        u = HU.Unit.read(pay['toc'], pay['gpu'])
        print('=' * 100)
        print('0x%016x  toc=%d gpu=%d  hdr1=%d ver=%d tf=%d nStream=%d nMesh=%d nBoneInfo=%d nMat=%d'
              % (e['file_id'], len(pay['toc']), len(pay['gpu']), u.header_data1, u.version,
                 len(u.transform_info.matrices), len(u.stream_infos), len(u.mesh_infos),
                 len(u.bone_infos), len(u.material_ids)))
        print('   lodgroup=%d prelight=%d cust=%d conn=%d unkhdr1=%d unrev2=%d endingBytes=%d'
              % (len(u.lod_group_list_data), len(u.pre_light_list_data),
                 len(u.customization_data), len(u.connecting_bone_data),
                 len(u.unk_header_data1), len(u.unreversed_data2), u.ending_bytes))
        for i, si in enumerate(u.stream_infos):
            v0 = None
            try:
                v0 = u.decode_vertices(i)[0]
            except Exception as ex:
                v0 = 'DECODE FAIL %s' % ex
            print('   s%d nv=%-7d strd=%-3d ibtype=%d ni=%-7d vbo=%-9d vbs=%-9d ibo=%-9d ibs=%d'
                  % (i, si.num_vertices, si.vertex_stride, si.index_buffer_type, si.num_indices,
                     si.vertex_buffer_offset, si.vertex_buffer_size,
                     si.index_buffer_offset, si.index_buffer_size))
            print('       comps=%s' % [(c.type, c.format, c.index) for c in si.components])
            print('       v0=%s' % (v0,))
        for mi, m in enumerate(u.mesh_infos):
            print('   m%d lod=%-3d stream=%d tf=%-3d meshid=0x%-9x nmat=%d nsec=%d matids=%s secs=%s'
                  % (mi, m.lod_index, m.stream_index, m.transform_index, m.mesh_id,
                     m.num_materials, m.num_sections,
                     ['0x%x' % x for x in m.material_ids],
                     [(s.material_index, s.vertex_offset, s.num_vertices,
                       s.index_offset, s.num_indices, s.group_index) for s in m.sections]))
        print('   unit matids=%s' % ['0x%x' % x for x in u.material_ids])
        print('   unit secids=%s' % ['0x%x' % x for x in u.section_ids])
        print('   tf hashes=%s' % ['0x%x' % h for h in u.transform_info.hashes])
        for bi, b in enumerate(u.bone_infos):
            print('   b%d bones=%d remaps=%d counts=%s offs=%s real=%s remap0=%s'
                  % (bi, b.num_bones, b.num_remaps, b.remap_counts, b.remap_offsets,
                     b.real_indices, b.remaps[0] if b.remaps else None))


if __name__ == '__main__':
    main()
