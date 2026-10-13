import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P, hd2_unit as U
from hd2_hash import load_bonehash
names = load_bonehash(os.path.join('..','..','docs','HD2SDK-CommunityEdition-3.8.0','hashlists','bonehash.txt'))
tocs=[os.path.join('E:/mod/Lunalice-Visible-Virtual-Model',f) for f in os.listdir('E:/mod/Lunalice-Visible-Virtual-Model') if '.patch_' in f and not f.endswith(('.stream','.gpu_resources'))]
entries,_=P.scan_entries(tocs[0])
e=[x for x in entries if x['type_id']==U.UNIT_ID][6]
pl=P.entry_payload(e,tocs[0]); u=U.Unit.read(pl['toc'],pl['gpu'])
ti=u.transform_info
bi=u.bone_infos[0]
print('unit 0x%016x  boneinfo[0] num_bones=%d' % (e['file_id'], bi.num_bones))
for k in range(bi.num_bones):
    t = bi.real_indices[k]
    raw = bi.bones[k]
    print('  slot %2d -> xform %2d %-14s' % (k, t, names.get(ti.hashes[t],'?')))
    print('     bi   rows: %s' % [[round(v,4) for v in raw[0:4]],[round(v,4) for v in raw[4:8]],[round(v,4) for v in raw[8:12]],[round(v,4) for v in raw[12:16]]])
    m = ti.matrices[t]
    print('     xform m : %s' % [[round(v,4) for v in m[0:4]],[round(v,4) for v in m[4:8]],[round(v,4) for v in m[8:12]],[round(v,4) for v in m[12:16]]])
print('remaps[0] =', bi.remaps[0])
print('remaps[1] =', bi.remaps[1])
print('unit materials:', ['0x%x'%m for m in u.material_ids], 'section ids:', ['0x%x'%s for s in u.section_ids])
for i,mi in enumerate(u.mesh_infos):
    print(' mesh[%d] id=0x%08x %-34s lod=%d stream=%d nsec=%d mats=%s' % (i, mi.mesh_id, names.get(mi.mesh_id,'?'), mi.lod_index, mi.stream_index, mi.num_sections, ['0x%x'%m for m in mi.material_ids]))
