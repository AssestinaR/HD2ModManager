import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P, hd2_unit as U
from hd2_hash import load_bonehash
names = load_bonehash(os.path.join('..','..','docs','HD2SDK-CommunityEdition-3.8.0','hashlists','bonehash.txt'))
D='E:/mod/Lunalice-Visible-Virtual-Model'
tocs=[os.path.join(D,f) for f in os.listdir(D) if '.patch_' in f and not f.endswith(('.stream','.gpu_resources'))]
entries,_=P.scan_entries(tocs[0])
for e in entries:
    if e['type_id']!=U.UNIT_ID: continue
    pl=P.entry_payload(e,tocs[0]); u=U.Unit.read(pl['toc'],pl['gpu'])
    ti=u.transform_info
    bones=[names.get(h,'0x%08x'%h) for h in ti.hashes]
    print('unit 0x%016x  xform %2d  boneinfos %d  streams %d  meshes %d  mats %d'
          % (e['file_id'], ti.count, len(u.bone_infos), len(u.stream_infos), len(u.mesh_infos), len(u.material_ids)))
    print('   bones: %s' % bones)
    for i,bi in enumerate(u.bone_infos):
        print('   bi[%d] num_bones %d remap_counts %s  remap_lens %s' % (i, bi.num_bones, bi.remap_counts, [len(r) for r in bi.remaps]))
        for m,r in enumerate(bi.remaps):
            bad=[v for v in r if v>=bi.num_bones]
            print('      remap[%d] len %d  distinct %s  out_of_range %d' % (m, len(r), sorted(set(r))[:20], len(bad)))
    for i,mi in enumerate(u.mesh_infos):
        print('   mesh[%d] 0x%08x %-30s lod %2d stream %d nsec %d nmat %d mats %s'
              % (i, mi.mesh_id, names.get(mi.mesh_id,'?'), mi.lod_index, mi.stream_index, mi.num_sections, mi.num_materials, ['0x%x'%m for m in mi.material_ids]))
        for s in mi.sections:
            print('      sec mat_idx %d slot 0x%08x vtx %d nvtx %d idx %d nidx %d grp %d' % (s.material_index, mi.material_ids[s.material_index], s.vertex_offset, s.num_vertices, s.index_offset, s.num_indices, s.group_index))
    print()
