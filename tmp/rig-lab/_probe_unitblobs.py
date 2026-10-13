# -*- coding: utf-8 -*-
"""Dump the *opaque* Unit sections (lodgroup / customization / prelight /
connecting-bone / unreversed2) for the reference units, so the packager can
copy them verbatim instead of inventing them."""
import glob
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as U


def hexdump(b, per=16, limit=512):
    out = []
    for i in range(0, min(len(b), limit), per):
        chunk = b[i:i + per]
        hx = ' '.join('%02x' % c for c in chunk)
        asc = ''.join(chr(c) if 32 <= c < 127 else '.' for c in chunk)
        out.append('      %04x  %-*s  %s' % (i, per * 3 - 1, hx, asc))
    if len(b) > limit:
        out.append('      ... (%d bytes total)' % len(b))
    return '\n'.join(out)


def u32s(b, n=None):
    n = len(b) // 4 if n is None else n
    return list(struct.unpack_from('<%dI' % n, b, 0))


def main():
    tgt = sys.argv[1] if len(sys.argv) > 1 else \
        glob.glob(r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0')[0]
    want = sys.argv[2].lower() if len(sys.argv) > 2 else None
    entries, _raw = HP.scan_entries(tgt)
    print('patch %s  (%d entries)' % (tgt, len(entries)))
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        if want and want not in ('0x%x' % e['file_id']):
            continue
        pl = HP.entry_payload(e, tgt)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        print('=' * 100)
        print('0x%x  toc=%d gpu=%d' % (e['file_id'], len(pl['toc']), len(pl['gpu'])))
        print('  header_data1=%d header_data2=%s header_unk=0x%x'
              % (u.header_data1, u.header_data2.hex(), u.header_unk))
        print('  refs unk1=0x%x bones=0x%x unk2=0x%x sm=0x%x'
              % (u.unk_ref1, u.bones_ref, u.unk_ref2, u.state_machine_ref))
        for nm, blob in (('lodgroup', u.lod_group_list_data),
                         ('prelight', u.pre_light_list_data),
                         ('wwise', u.wwise_callback_data),
                         ('customization', u.customization_data),
                         ('unkhdr1', u.unk_header_data1),
                         ('connectingbone', u.connecting_bone_data),
                         ('unrev2', u.unreversed_data2)):
            print('  %-14s %4d B  u32[:8]=%s'
                  % (nm, len(blob), ['0x%x' % v for v in u32s(blob, min(8, len(blob) // 4))]))
        print('  -- lodgroup raw --')
        print(hexdump(u.lod_group_list_data))
        print('  -- prelight raw --')
        print(hexdump(u.pre_light_list_data))
        print('  -- customization raw --')
        print(hexdump(u.customization_data, limit=272))
        print('  -- connecting bone raw --')
        print(hexdump(u.connecting_bone_data))
        print('  -- unrev2 raw --')
        print(hexdump(u.unreversed_data2))
        print('  lights=%d light_count_field=%d unk0=%s'
              % (len(u.lights), u.light_count, u.light_unk0))
        print('  ending_bytes=%d  num_meshes=%d  num_streams=%d'
              % (u.ending_bytes, len(u.mesh_infos), len(u.stream_infos)))
        ti = u.transform_info
        print('  tf count=%d unk=%s' % (ti.count, ti.unk.hex()))
        print('  tf hashes=%s' % ['0x%x' % h for h in ti.hashes])
        print('  tf entries=%s' % (ti.entries,))
        print('  tf matrix[0]=%s' % (['%.6g' % v for v in ti.matrices[0]],))
        print('  tf matrix[4]=%s' % (['%.6g' % v for v in ti.matrices[4]],))
        print('  tf matrix[5]=%s' % (['%.6g' % v for v in ti.matrices[5]],))
        print('  tf matrix[8]=%s' % (['%.6g' % v for v in ti.matrices[8]],))
        print('  tf local[0]=%s' % (ti.locals[0],))
        print('  si0 comp_id=0x%x vb_id=0x%x vb_u1=0x%x vb_u2=0x%x vb_u3=0x%x'
              % (u.stream_infos[0].component_info_id, u.stream_infos[0].vertex_buffer_id,
                 u.stream_infos[0].vertex_buffer_unk1, u.stream_infos[0].vertex_buffer_unk2,
                 u.stream_infos[0].vertex_buffer_unk3))
        print('  si0 ib_id=0x%x ib_u1=0x%x ib_u2=0x%x ib_u3=0x%x unk2=0x%x'
              % (u.stream_infos[0].index_buffer_id, u.stream_infos[0].index_buffer_unk1,
                 u.stream_infos[0].index_buffer_unk2, u.stream_infos[0].index_buffer_unk3,
                 u.stream_info_unk2))
        print('  si0 unk_ending=%s' % u.stream_infos[0].unk_ending.hex())
        print('  si unks=%s  mesh unks=%s'
              % (['0x%x' % v for v in u.stream_info_unks],
                 ['0x%x' % v for v in u.mesh_info_unks]))
        m = u.mesh_infos[0]
        print('  mi0 unk1=0x%x unk2=%s meshid=0x%x unk3=%d tf=%d unk4=%d lod=%d stream=%d'
              % (m.unk1, m.unk2.hex(), m.mesh_id, m.unk3, m.transform_index, m.unk4,
                 m.lod_index, m.stream_index))
        print('  mi0 unk6=%s matoff=%d unk8=0x%x nsec=%d secoff=%d'
              % (m.unk6.hex(), m.material_offset, m.unk8, m.num_sections, m.sections_offset))


if __name__ == '__main__':
    main()
