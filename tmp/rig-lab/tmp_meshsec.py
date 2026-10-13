import hd2_patch, hd2_unit, struct
P = r"E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0"
ents,_ = hd2_patch.scan_entries(P)
for idx in (7, 0):
    e = ents[idx]
    pl = hd2_patch.entry_payload(e, P)
    u = hd2_unit.Unit.read(pl['toc'], pl['gpu'])
    print("=== entry %d fid=0x%016x toc=%d gpu=%d ===" % (idx, e['file_id'], len(pl['toc']), len(pl['gpu'])))
    print("  ver=%d hdr1=%d bones_ref=0x%x comp=0x%x sm=0x%x  nTf=%d" % (u.version,u.header_data1,u.bones_ref,u.composite_ref,u.state_machine_ref,u.transform_info.count))
    print("  lodGroup=%d preLight=%d unkHdr1=%d conn=%d cust=%d wwise=%d end=%d unreversed=%d ending=%d" %
          (len(u.lod_group_list_data), len(u.pre_light_list_data), len(u.unk_header_data1), len(u.connecting_bone_data),
           len(u.customization_data), len(u.wwise_callback_data), u.ending_offset, len(u.unreversed_data2), u.ending_bytes))
    print("  nStream=%d nMesh=%d nBoneInfo=%d nMat=%d" % (len(u.stream_infos), len(u.mesh_infos), len(u.bone_infos), len(u.section_ids)))
    print("  mats:", [hex(x) for x in u.material_ids])
    print("  sectids:", u.section_ids)
    for i,si in enumerate(u.stream_infos):
        print("   stream[%d] nv=%d stride=%d ni=%d ibtype=%d vboff=%d vbsz=%d iboff=%d ibsz=%d comps=%s" %
              (i, si.num_vertices, si.vertex_stride, si.num_indices, si.index_buffer_type,
               si.vertex_buffer_offset, si.vertex_buffer_size, si.index_buffer_offset, si.index_buffer_size,
               [(c.type,c.format,c.index) for c in si.components]))
    for i,mi in enumerate(u.mesh_infos):
        print("   mesh[%d] lod=%d stream=%d tf=%d meshid=0x%x unk3=%d unk4=%d nmat=%d matoff=%d nsec=%d secoff=%d matids=%s unk2tail=%s" %
              (i, mi.lod_index, mi.stream_index, mi.transform_index, mi.mesh_id, mi.unk3, mi.unk4,
               mi.num_materials, mi.material_offset, mi.num_sections, mi.sections_offset,
               [hex(x) for x in mi.material_ids], struct.unpack_from('<I', mi.unk2, 28)[0]))
        for s in mi.sections:
            print("      sec mat=%d vtx=%d+%d idx=%d+%d grp=%d" % (s.material_index,s.vertex_offset,s.num_vertices,s.index_offset,s.num_indices,s.group_index))
    for bi in u.bone_infos:
        print("   boneinfo nb=%d remaps=%d counts=%s offsets=%s real=%s remap0=%s" %
              (bi.num_bones, bi.num_remaps, bi.remap_counts, bi.remap_offsets, bi.real_indices, bi.remaps[0] if bi.remaps else None))
